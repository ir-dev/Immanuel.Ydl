using System.Globalization;
using System.IO.Compression;
using System.Net;
using Ark.Ydl.Options;
using MaxMind.Db;
using Microsoft.Extensions.Options;

namespace Ark.Ydl.Audit;

public sealed record GeoResult(string? Country, string? CountryName, string? Region, string? City);

public sealed record GeoIpStatus(
    bool Enabled, string State, string? Edition, string? File, string? Month, string? DatabaseType, DateTime? BuildDate,
    DateTimeOffset? LoadedAt, string? Error, DateTimeOffset? NextCheck, string Attribution);

/// <summary>
/// IP → country / region / city from DB-IP's free "lite" database (CC BY 4.0, https://db-ip.com). The .mmdb is
/// downloaded into {DataRoot}/geoip, checked by opening it and looking up a known address, then swapped in; a new
/// month's file is fetched in the background. Failures are retried (10 min, doubling, up to 6 h) and never block a
/// request: until a database is loaded, lookups just return nothing.
/// </summary>
public sealed class GeoIpService(IOptions<YdlOptions> opt, YdlPaths paths, IHttpClientFactory http, ILogger<GeoIpService> log) : BackgroundService
{
    public const string Attribution = "IP geolocation by DB-IP (https://db-ip.com), licensed under CC BY 4.0.";
    private static readonly IPAddress Probe = IPAddress.Parse("8.8.8.8");

    private volatile Reader? _reader;
    private string _state = "starting";
    private string? _file, _month, _error;
    private DateTimeOffset? _loadedAt, _nextCheck;

    private GeoIpOptions O => opt.Value.Audit.GeoIp;
    private bool Enabled => opt.Value.Audit.Enabled && O.Enabled;
    private string Edition => O.Edition?.Trim().ToLowerInvariant() is "country" ? "country" : "city";

    public bool Ready => _reader is not null;

    public GeoIpStatus Status
    {
        get
        {
            var r = _reader;
            return new GeoIpStatus(Enabled, Enabled ? _state : "off", Enabled ? Edition : null, _file, _month,
                r?.Metadata.DatabaseType, r?.Metadata.BuildDate, _loadedAt, _error, _nextCheck, Attribution);
        }
    }

    public GeoResult? Lookup(IPAddress ip)
    {
        var r = _reader;
        if (r is null || IpTools.IsPrivate(ip)) return null;
        try
        {
            var d = r.Find<Dictionary<string, object>>(IpTools.Normalize(ip));
            return d is null ? null : Map(d);
        }
        catch (Exception ex) when (ex is InvalidDatabaseException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    internal static GeoResult Map(Dictionary<string, object> d)
    {
        var country = Obj(d, "country") ?? Obj(d, "registered_country");
        var sub = d.TryGetValue("subdivisions", out var s) && s is IList<object> list && list.Count > 0 ? list[0] as Dictionary<string, object> : null;
        return new GeoResult(
            Str(country, "iso_code"),
            Name(country),
            Name(sub),
            Name(Obj(d, "city")));
    }

    private static Dictionary<string, object>? Obj(Dictionary<string, object>? d, string key) =>
        d is not null && d.TryGetValue(key, out var v) ? v as Dictionary<string, object> : null;

    private static string? Str(Dictionary<string, object>? d, string key) =>
        d is not null && d.TryGetValue(key, out var v) && v is string s && s.Length > 0 ? s : null;

    private static string? Name(Dictionary<string, object>? d)
    {
        var names = Obj(d, "names");
        return Str(names, "en") ?? names?.Values.OfType<string>().FirstOrDefault();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Enabled) return;
        try
        {
            if (!string.IsNullOrWhiteSpace(O.DatabasePath))
            {
                // An operator-supplied file: open it, never download.
                if (!TryOpen(Path.GetFullPath(O.DatabasePath), month: null, out var err))
                {
                    _state = "failed";
                    _error = err;
                }
                return;
            }

            var existing = LocalFiles().FirstOrDefault();
            if (existing is not null && !TryOpen(existing.Path, existing.Month, out var openErr))
            {
                log.LogWarning("GeoIP file {File} couldn't be opened ({Error}); fetching a new one", existing.Path, openErr);
                TryDelete(existing.Path);
                existing = null;
            }
            if (!O.AutoDownload)
            {
                if (existing is null)
                {
                    _state = "missing";
                    _error = $"No GeoIP database in {paths.GeoIp} and Ydl:Audit:GeoIp:AutoDownload is off.";
                }
                return;
            }

            var delay = TimeSpan.FromMinutes(10);
            await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); // let the tools bootstrap go first
            while (!stoppingToken.IsCancellationRequested)
            {
                var ok = await RefreshAsync(stoppingToken);
                var wait = ok ? TimeSpan.FromHours(24) : delay;
                if (!ok) delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, TimeSpan.FromHours(6).Ticks));
                else delay = TimeSpan.FromMinutes(10);
                _nextCheck = DateTimeOffset.UtcNow + wait;
                await Task.Delay(wait, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>Makes sure this month's file (or, early in a month, last month's) is loaded. True when nothing is left to do.</summary>
    internal async Task<bool> RefreshAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var thisMonth = now.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        var lastMonth = now.AddMonths(-1).ToString("yyyy-MM", CultureInfo.InvariantCulture);
        if (_reader is not null && _month == thisMonth) return true;

        foreach (var month in new[] { thisMonth, lastMonth })
        {
            if (_reader is not null && _month == month) return true; // last month's is loaded and this month's isn't out yet
            var result = await DownloadAsync(month, ct);
            if (result is null) return true;
            if (result == "404") continue; // not published yet: try the month before
            _error = result;
            _state = _reader is null ? "failed" : "ready";
            log.LogWarning("GeoIP download failed: {Error}", result);
            return false;
        }
        // Neither month exists on the server: keep whatever is loaded.
        _error = $"No DB-IP {Edition} lite file for {thisMonth} or {lastMonth} at {O.ReleaseBase}.";
        _state = _reader is null ? "failed" : "ready";
        return _reader is not null;
    }

    /// <summary>Null on success, "404" when the month isn't published, otherwise the reason it failed.</summary>
    private async Task<string?> DownloadAsync(string month, CancellationToken ct)
    {
        var name = $"dbip-{Edition}-lite-{month}.mmdb";
        var url = O.ReleaseBase.TrimEnd('/') + "/" + name + ".gz";
        Directory.CreateDirectory(paths.GeoIp);
        var gz = Path.Combine(paths.GeoIp, name + ".gz.part");
        var mmdb = Path.Combine(paths.GeoIp, name + ".part");
        var final = Path.Combine(paths.GeoIp, name);
        _state = _reader is null ? "downloading" : "updating";
        try
        {
            using var client = http.CreateClient("geoip");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMinutes(30));
            using var res = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (res.StatusCode == HttpStatusCode.NotFound) return "404";
            if (!res.IsSuccessStatusCode) return $"{url} answered {(int)res.StatusCode}.";
            if (res.Content.Headers.ContentLength > 400_000_000) return $"{url} is unexpectedly large.";
            log.LogInformation("Downloading GeoIP database {Url}", url);
            await using (var body = await res.Content.ReadAsStreamAsync(cts.Token))
            await using (var file = File.Create(gz))
                await body.CopyToAsync(file, cts.Token);
            await using (var input = File.OpenRead(gz))
            await using (var unzip = new GZipStream(input, CompressionMode.Decompress))
            await using (var output = File.Create(mmdb))
                await unzip.CopyToAsync(output, cts.Token);
            TryDelete(gz);

            // Validate before it replaces anything: it must open, and a well-known address must resolve to a country.
            using (var check = Open(mmdb))
            {
                var d = check.Find<Dictionary<string, object>>(Probe);
                if (d is null || Map(d).Country is null) return $"{name} opened but holds no country data.";
            }
            File.Move(mmdb, final, overwrite: true);
            if (!TryOpen(final, month, out var err)) return err;
            foreach (var old in LocalFiles().Where(f => !f.Path.Equals(final, StringComparison.OrdinalIgnoreCase)))
                TryDelete(old.Path);
            log.LogInformation("GeoIP database {Name} is ready", name);
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or InvalidDatabaseException or UnauthorizedAccessException
                                       || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            return $"{url}: {ex.Message}";
        }
        finally
        {
            TryDelete(gz);
            TryDelete(mmdb);
        }
    }

    private bool TryOpen(string file, string? month, out string? error)
    {
        error = null;
        try
        {
            if (!File.Exists(file))
            {
                error = $"{file} does not exist.";
                return false;
            }
            var reader = Open(file);
            var old = _reader;
            _reader = reader;
            _file = file;
            _month = month;
            _loadedAt = DateTimeOffset.UtcNow;
            _state = "ready";
            _error = null;
            if (old is not null)
                _ = Task.Delay(TimeSpan.FromMinutes(2)).ContinueWith(_ => old.Dispose(), TaskScheduler.Default); // let in-flight lookups finish
            return true;
        }
        catch (Exception ex) when (ex is InvalidDatabaseException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Memory-mapped (pages in only what lookups touch); a host that refuses mappings gets the whole file in memory.</summary>
    private static Reader Open(string file)
    {
        try
        {
            return new Reader(file, FileAccessMode.MemoryMapped);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return new Reader(file, FileAccessMode.Memory);
        }
    }

    private sealed record LocalFile(string Path, string Month);

    /// <summary>Downloaded files of the configured edition, newest month first.</summary>
    private IEnumerable<LocalFile> LocalFiles()
    {
        if (!Directory.Exists(paths.GeoIp)) return [];
        var prefix = $"dbip-{Edition}-lite-";
        return Directory.EnumerateFiles(paths.GeoIp, prefix + "*.mmdb")
            .Select(f => new LocalFile(f, System.IO.Path.GetFileNameWithoutExtension(f)[prefix.Length..]))
            .Where(f => f.Month.Length == 7)
            .OrderByDescending(f => f.Month, StringComparer.Ordinal)
            .ToList();
    }

    private static void TryDelete(string file)
    {
        try { if (File.Exists(file)) File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public override void Dispose()
    {
        base.Dispose();
        _reader?.Dispose();
    }
}
