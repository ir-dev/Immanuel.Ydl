using System.Text.Json;
using Ark.Ydl.Options;
using Ark.Ydl.Tools;
using Ark.Ydl.Util;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Ark.Ydl.YtDlp;

public sealed record ProbeOutcome(ProbeResult? Result, int Status, string? Error, string? Detail)
{
    public static ProbeOutcome Success(ProbeResult r) => new(r, 200, null, null);
    public static ProbeOutcome Fail(int status, string error, string? detail = null) => new(null, status, error, detail);
}

/// <summary>Looks a link up with "yt-dlp -J" (cached, and bounded so a burst cannot exhaust the box).</summary>
public sealed class ProbeService(ToolLocator tools, IOptions<YdlOptions> opt, YdlPaths paths, IMemoryCache cache, ILogger<ProbeService> log)
{
    private readonly SemaphoreSlim _slots = new(Math.Max(1, opt.Value.MaxConcurrentProbes));

    public async Task<ProbeOutcome> ProbeAsync(UrlAnalysis url, bool playlist, CancellationToken ct)
    {
        var o = opt.Value;
        var key = $"probe:{(playlist ? "pl" : "v")}:{url.Url}";
        if (cache.TryGetValue(key, out ProbeResult? cached) && cached is not null)
            return ProbeOutcome.Success(cached.WithHints(url.Start, url.End, url.ListId));

        try
        {
            await tools.WhenInitialized.WaitAsync(TimeSpan.FromMinutes(2), ct);
        }
        catch (TimeoutException)
        {
            return ProbeOutcome.Fail(503, "The server is still getting ready. Try again in a minute.");
        }
        if (tools.Status.YtDlp.Path is not { } ytDlp)
            return ProbeOutcome.Fail(503, "The download engine (yt-dlp) isn't installed on this server.", tools.Status.YtDlp.Note);

        await _slots.WaitAsync(ct);
        var tmp = Path.Combine(paths.Cache, "probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cookies = CookieJar.CopyInto(o, tmp);
            var common = YtDlpArgs.Common(o, paths.Cache, tools.ToolArgs(), cookies);
            var args = YtDlpArgs.Probe(common, url, playlist, o.MaxPlaylistItems);
            var started = DateTime.UtcNow;
            var r = await ProcessRunner.RunCaptureAsync(ytDlp, args, TimeSpan.FromSeconds(playlist ? 150 : 90), ct);
            if (r.TimedOut) return ProbeOutcome.Fail(504, "YouTube took too long to answer. Try again.");

            var json = r.StdOut.Trim();
            if (r.ExitCode != 0 || json.Length == 0 || json[0] != '{')
            {
                var errors = r.StdErr.Split('\n').Select(l => l.Trim())
                    .Where(l => l.StartsWith("ERROR:")).Select(l => l[6..].Trim()).ToList();
                var (message, detail) = YtDlpErrors.Translate(errors, r.StdErr);
                log.LogInformation("Probe failed for {Url} (exit {Code}): {Detail}", url.Url, r.ExitCode, detail);
                return ProbeOutcome.Fail(422, message, detail);
            }

            using var doc = JsonDocument.Parse(json);
            var result = ProbeMapper.Map(doc.RootElement, url);
            log.LogInformation("Probed {Url} ({Kind}) in {Ms} ms", url.Url, result.Kind, (int)(DateTime.UtcNow - started).TotalMilliseconds);
            cache.Set(key, result, TimeSpan.FromMinutes(Math.Max(1, o.ProbeCacheMinutes)));
            return ProbeOutcome.Success(result.WithHints(url.Start, url.End, url.ListId));
        }
        catch (JsonException ex)
        {
            log.LogWarning(ex, "yt-dlp returned unreadable JSON for {Url}", url.Url);
            return ProbeOutcome.Fail(502, "Couldn't read the video details. Try again.");
        }
        finally
        {
            _slots.Release();
            TryDelete(tmp);
        }
    }

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

public static class CookieJar
{
    /// <summary>
    /// yt-dlp rewrites its cookie file on exit, so each run gets a private copy of the configured
    /// one: parallel runs never fight over it and a read-only mounted file stays usable.
    /// </summary>
    public static string? CopyInto(YdlOptions o, string dir)
    {
        if (string.IsNullOrWhiteSpace(o.CookiesFile) || !File.Exists(o.CookiesFile)) return null;
        Directory.CreateDirectory(dir);
        var dst = Path.Combine(dir, "cookies.txt");
        File.Copy(o.CookiesFile, dst, overwrite: true);
        return dst;
    }
}
