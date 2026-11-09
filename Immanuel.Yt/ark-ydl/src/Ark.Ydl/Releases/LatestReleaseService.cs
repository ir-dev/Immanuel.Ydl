using System.Net.Http.Headers;
using Ark.Ydl.Options;
using Microsoft.Extensions.Options;

namespace Ark.Ydl.Releases;

/// <summary>What the app knows about the newest published release.</summary>
public sealed record LatestRelease
{
    public string Version { get; init; } = "";
    public string? Title { get; init; }
    public string? Date { get; init; }
    /// <summary>feed = read from Ydl:Release:FeedUrl; local = this build's own manifest (no feed, or not reached yet).</summary>
    public string Source { get; init; } = "local";
    public DateTimeOffset? CheckedAt { get; init; }
    /// <summary>Why the feed couldn't be read, when it couldn't.</summary>
    public string? Error { get; init; }
    /// <summary>Releases in the feed that are newer than this build (newest first), so the app can say what an update brings.</summary>
    public IReadOnlyList<ReleaseEntry> Newer { get; init; } = [];
}

/// <summary>
/// Reads the release feed in the background (once shortly after start, then every CheckIntervalHours) and keeps
/// the answer in memory, so showing "latest release" never costs a request a network call.
/// </summary>
public sealed class LatestReleaseService(
    ReleaseCatalog catalog, IOptions<YdlOptions> opt, IHttpClientFactory http, ILogger<LatestReleaseService> log) : BackgroundService
{
    private LatestRelease? _feed;
    private string? _lastError;
    private DateTimeOffset? _lastAttempt;

    public LatestRelease Current
    {
        get
        {
            var local = Local();
            var feed = _feed;
            if (feed is null) return local with { Error = _lastError, CheckedAt = _lastAttempt };
            // A feed that is behind this build (e.g. deployed before the push) never hides the newer local release.
            return SemVer.Parse(feed.Version) >= SemVer.Parse(local.Version) ? feed with { Error = _lastError } : local with { CheckedAt = feed.CheckedAt };
        }
    }

    public bool UpdateAvailable => SemVer.Parse(Current.Version) > SemVer.Parse(catalog.AppVersion);

    public bool FeedConfigured => !string.IsNullOrWhiteSpace(opt.Value.Release.FeedUrl);

    private LatestRelease Local()
    {
        var newest = catalog.Newest;
        return new LatestRelease
        {
            Version = newest?.Version ?? catalog.AppVersion,
            Title = newest?.Title,
            Date = newest?.Date,
            Source = "local",
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!FeedConfigured) return;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                await CheckAsync(stoppingToken);
                await Task.Delay(TimeSpan.FromHours(Math.Clamp(opt.Value.Release.CheckIntervalHours, 1, 24 * 7)), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>One read of the feed. Public so the admin console can ask for a fresh answer.</summary>
    public async Task<LatestRelease> CheckAsync(CancellationToken ct)
    {
        var url = opt.Value.Release.FeedUrl;
        if (string.IsNullOrWhiteSpace(url)) return Current;
        _lastAttempt = DateTimeOffset.UtcNow;
        try
        {
            using var client = http.CreateClient("tools");
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            using var res = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!res.IsSuccessStatusCode)
            {
                _lastError = $"The release feed answered {(int)res.StatusCode}.";
                log.LogInformation("Release feed {Url} answered {Status}", url, (int)res.StatusCode);
                return Current;
            }
            if (res.Content.Headers.ContentLength > 2_000_000)
            {
                _lastError = "The release feed is unexpectedly large.";
                return Current;
            }
            await using var s = await res.Content.ReadAsStreamAsync(cts.Token);
            var manifest = ReleaseCatalog.Parse(s);
            var latest = SemVer.TryParse(manifest.Current, out _) ? manifest.Current
                : manifest.Releases.Select(r => r.Version).OrderByDescending(SemVer.Parse).FirstOrDefault();
            if (latest is null)
            {
                _lastError = "The release feed names no version.";
                return Current;
            }
            var entry = manifest.Releases.FirstOrDefault(r => SemVer.Parse(r.Version) == SemVer.Parse(latest));
            var mine = SemVer.Parse(catalog.AppVersion);
            _feed = new LatestRelease
            {
                Version = latest,
                Title = entry?.Title,
                Date = entry?.Date,
                Source = "feed",
                CheckedAt = DateTimeOffset.UtcNow,
                Newer = manifest.Releases.Where(r => SemVer.Parse(r.Version) > mine)
                    .OrderByDescending(r => SemVer.Parse(r.Version)).Take(10).ToList(),
            };
            _lastError = null;
            if (SemVer.Parse(latest) > mine)
                log.LogInformation("A newer release is out: {Latest} (running {Mine})", latest, catalog.AppVersion);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidDataException)
        {
            if (ct.IsCancellationRequested) throw;
            _lastError = "Couldn't read the release feed: " + ex.Message;
            log.LogInformation("Release feed {Url} failed: {Message}", url, ex.Message);
        }
        return Current;
    }
}
