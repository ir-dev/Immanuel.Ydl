using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ark.Feedback.Options;
using Microsoft.Extensions.Options;

namespace Ark.Feedback.Tools;

public sealed record ToolInfo(string Name, string? Path, string? Version, string? Note = null)
{
    public bool Ok => Path is not null;
}

public sealed record ToolStatus(ToolInfo YtDlp, ToolInfo JsRuntime, bool Preparing, DateTimeOffset CheckedAt)
{
    public bool Ready => YtDlp.Ok;
}

/// <summary>
/// Finds yt-dlp (configured path, the app's own copy, PATH), downloads the official release when it is missing
/// (SHA-256 verified against the release's SHA2-256SUMS, fetched through GitHub's API because github.com's
/// release-download route has been timing out) and keeps it current with "yt-dlp -U". YouTube changes often
/// and a stale extractor is the usual reason comment fetching "stops working".
/// </summary>
public sealed partial class ToolLocator
{
    private readonly ToolOptions _opt;
    private readonly FeedbackPaths _paths;
    private readonly HttpDownloader _http;
    private readonly ILogger<ToolLocator> _log;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ToolLocator(IOptions<FeedbackOptions> opt, FeedbackPaths paths, HttpDownloader http, ILogger<ToolLocator> log)
    {
        _opt = opt.Value.Tools;
        _paths = paths;
        _http = http;
        _log = log;
        Status = new ToolStatus(new("yt-dlp", null, null), new("js", null, null), true, DateTimeOffset.UtcNow);
    }

    public ToolStatus Status { get; private set; }

    /// <summary>Finds an installed tool by name (PATH and the usual install folders). Tests swap it out.</summary>
    internal Func<string, string?> Which { get; init; } = FindOnPath;

    public Task WhenInitialized => _ready.Task;

    private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    private static string Exe(string name) => IsWindows ? name + ".exe" : name;
    private string OwnedYtDlpPath => Path.Combine(_paths.Tools, Exe("yt-dlp"));

    public bool OwnsYtDlp => Status.YtDlp.Path is { } p && string.Equals(Path.GetFullPath(p), OwnedYtDlpPath, StringComparison.Ordinal);

    public async Task InitializeAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var yt = await LocateYtDlpAsync(ct);
            var js = await LocateJsRuntimeAsync(ct);
            Status = new ToolStatus(yt, js, false, DateTimeOffset.UtcNow);
            _log.LogInformation("Tools: yt-dlp {Version} ({Path}); js runtime {Js}", yt.Version, yt.Path, js.Ok ? js.Path : "none");
        }
        finally
        {
            _gate.Release();
            _ready.TrySetResult();
        }
    }

    /// <summary>Looks again when yt-dlp is still missing (a failed start-up download).</summary>
    public async Task RetryMissingAsync(CancellationToken ct)
    {
        if (Status.YtDlp.Ok) return;
        await _gate.WaitAsync(ct);
        try
        {
            var yt = await LocateYtDlpAsync(ct);
            Status = Status with { YtDlp = yt, CheckedAt = DateTimeOffset.UtcNow };
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Runs "yt-dlp -U" on a binary we own and refreshes the reported version.</summary>
    public async Task<string?> UpdateYtDlpAsync(CancellationToken ct)
    {
        if (Status.YtDlp.Path is not { } path || !OwnsYtDlp) return null;
        var r = await ProcessRunner.RunCaptureAsync(path, ["--ignore-config", "-U"], TimeSpan.FromMinutes(3), ct);
        var output = (r.StdOut + "\n" + r.StdErr).Trim();
        _log.LogInformation("yt-dlp -U exit {Code}: {Output}", r.ExitCode, output.Length > 600 ? output[..600] : output);
        var version = await VersionOfAsync(path, ct);
        Status = Status with { YtDlp = Status.YtDlp with { Version = version ?? Status.YtDlp.Version }, CheckedAt = DateTimeOffset.UtcNow };
        return version;
    }

    /// <summary>--js-runtimes for every yt-dlp call, when a runtime is known.</summary>
    public IReadOnlyList<string> ToolArgs()
    {
        var js = Status.JsRuntime;
        return js.Path is { } p ? ["--js-runtimes", $"{js.Name}:{p}"] : [];
    }

    private async Task<ToolInfo> LocateYtDlpAsync(CancellationToken ct)
    {
        var candidates = new List<string?> { _opt.YtDlpPath, File.Exists(OwnedYtDlpPath) ? OwnedYtDlpPath : null, Which("yt-dlp") };
        foreach (var c in candidates.Where(c => !string.IsNullOrWhiteSpace(c)))
        {
            if (!File.Exists(c))
            {
                _log.LogWarning("yt-dlp was not found at {Path}", c);
                continue;
            }
            var v = await VersionOfAsync(c!, ct);
            if (v is not null) return new ToolInfo("yt-dlp", c, v);
            _log.LogWarning("yt-dlp at {Path} did not answer --version", c);
        }

        if (!_opt.AutoDownloadYtDlp)
            return new ToolInfo("yt-dlp", null, null, "Not found. Install yt-dlp, set Feedback:Tools:YtDlpPath, or enable Feedback:Tools:AutoDownloadYtDlp.");

        try
        {
            var path = await DownloadYtDlpAsync(ct);
            var v = await VersionOfAsync(path, ct);
            return v is not null
                ? new ToolInfo("yt-dlp", path, v, "Downloaded from the official GitHub release (SHA-256 verified).")
                : new ToolInfo("yt-dlp", null, null, "Downloaded yt-dlp but it does not run on this machine.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log.LogError(ex, "Could not download yt-dlp");
            return new ToolInfo("yt-dlp", null, null, "Not found and the automatic download failed: " + ex.Message);
        }
    }

    private async Task<ToolInfo> LocateJsRuntimeAsync(CancellationToken ct)
    {
        var configured = _opt.JsRuntimePath?.Trim();
        var candidates = new List<(string Name, string Path)>();
        if (!string.IsNullOrEmpty(configured))
        {
            var name = JsRuntimeFromFileName(configured);
            if (name is null || !File.Exists(configured))
                return new ToolInfo("none", null, null, $"Feedback:Tools:JsRuntimePath ({configured}) is not a deno/node/bun/quickjs executable that exists.");
            candidates.Add((name, configured));
        }
        else
        {
            if (Which("deno") is { } deno) candidates.Add(("deno", deno));
            if (Which("node") is { } node) candidates.Add(("node", node));
        }
        foreach (var (name, path) in candidates)
        {
            var v = await VersionOfAsync(path, ct);
            if (v is null) continue;
            var m = SemverRx().Match(v);
            if (m.Success && name == "node" && int.Parse(m.Groups[1].Value) < 22) continue; // yt-dlp wants node >= 22
            return new ToolInfo(name, path, m.Success ? m.Value : v);
        }
        return new ToolInfo("none", null, null, "No JavaScript runtime (optional: comments work without one; yt-dlp just warns).");
    }

    [GeneratedRegex(@"(\d+)\.(\d+)(?:\.(\d+))?")]
    private static partial Regex SemverRx();

    internal static string? JsRuntimeFromFileName(string path) =>
        Path.GetFileNameWithoutExtension(path.Trim().Replace('\\', '/').Split('/')[^1]).ToLowerInvariant() switch
        {
            "deno" => "deno",
            "node" or "nodejs" => "node",
            "bun" => "bun",
            "qjs" or "quickjs" or "qjs-ng" or "quickjs-ng" => "quickjs",
            _ => null,
        };

    internal static string? ReleaseAssetName()
    {
        var arch = RuntimeInformation.OSArchitecture;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "yt-dlp_macos";
        if (IsWindows) return arch == Architecture.X86 ? "yt-dlp_x86.exe" : "yt-dlp.exe";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return arch switch
            {
                Architecture.X64 => "yt-dlp_linux",
                Architecture.Arm64 => "yt-dlp_linux_aarch64",
                Architecture.Arm => "yt-dlp_linux_armv7l",
                _ => null,
            };
        return null;
    }

    /// <summary>Parses a GNU-style SHA2-256SUMS file ("&lt;hex&gt;  [*]&lt;name&gt;").</summary>
    internal static string? ExpectedHash(string sums, string asset)
    {
        foreach (var raw in sums.Split('\n'))
        {
            var line = raw.Trim();
            var sp = line.IndexOfAny([' ', '\t']);
            if (sp <= 0) continue;
            var name = line[sp..].Trim().TrimStart('*');
            if (name == asset) return line[..sp].ToLowerInvariant();
        }
        return null;
    }

    [GeneratedRegex(@"^https://github\.com/([^/]+)/([^/]+)/releases/(?:download/([^/]+)|latest/download)/([^/?#]+)$", RegexOptions.IgnoreCase)]
    private static partial Regex GitHubFileRx();

    /// <summary>A github.com release file URL → the same file's REST API URL (which still works while the download route 504s).</summary>
    private async Task<string> ViaGitHubApiAsync(string url, Dictionary<string, Dictionary<string, string>> cache, CancellationToken ct)
    {
        var m = GitHubFileRx().Match(url);
        if (!m.Success) return url;
        var (owner, repo, tag, name) = (m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Success ? m.Groups[3].Value : null, Uri.UnescapeDataString(m.Groups[4].Value));
        var api = tag is null ? $"https://api.github.com/repos/{owner}/{repo}/releases/latest" : $"https://api.github.com/repos/{owner}/{repo}/releases/tags/{tag}";
        if (!cache.TryGetValue(api, out var assets))
        {
            var json = await _http.GetStringAsync(api, ct, "application/vnd.github+json");
            using var doc = JsonDocument.Parse(json);
            assets = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var a in doc.RootElement.GetProperty("assets").EnumerateArray())
                if (a.GetProperty("name").GetString() is { } n && a.GetProperty("url").GetString() is { } u) assets[n] = u;
            cache[api] = assets;
        }
        return assets.TryGetValue(name, out var found) ? found : throw new InvalidOperationException($"{name} is not in the {owner}/{repo} release \"{tag ?? "latest"}\".");
    }

    private async Task<string> DownloadYtDlpAsync(CancellationToken ct)
    {
        var asset = ReleaseAssetName() ?? throw new PlatformNotSupportedException("No official yt-dlp build for this platform.");
        var baseUrl = _opt.YtDlpReleaseBase.TrimEnd('/') + "/";
        _log.LogInformation("Downloading yt-dlp ({Asset}) from {Base}", asset, baseUrl);
        var cache = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var sumsUrl = await ViaGitHubApiAsync(baseUrl + "SHA2-256SUMS", cache, ct);
        var fileUrl = await ViaGitHubApiAsync(baseUrl + asset, cache, ct);
        var expected = ExpectedHash(await _http.GetStringAsync(sumsUrl, ct), asset)
                       ?? throw new InvalidOperationException($"{asset} is not listed in SHA2-256SUMS.");
        var sha = await _http.DownloadAsync(fileUrl, OwnedYtDlpPath, expected, null, ct);
        MakeExecutable(OwnedYtDlpPath);
        _log.LogInformation("yt-dlp saved to {Path} (sha256 {Hash})", OwnedYtDlpPath, sha);
        return OwnedYtDlpPath;
    }

    private static void MakeExecutable(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

    /// <summary>PATH lookup plus the usual install folders (IDE-launched apps often get a thin PATH on macOS).</summary>
    internal static string? FindOnPath(string name)
    {
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).ToList();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!IsWindows) dirs.AddRange(["/opt/homebrew/bin", "/usr/local/bin", "/usr/bin", "/bin"]);
        if (!string.IsNullOrEmpty(home)) dirs.AddRange([Path.Combine(home, ".local", "bin"), Path.Combine(home, ".deno", "bin")]);
        var exts = IsWindows ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';', StringSplitOptions.RemoveEmptyEntries) : [""];
        foreach (var d in dirs.Distinct())
            foreach (var e in exts)
            {
                try
                {
                    var p = Path.Combine(d.Trim('"'), name + e);
                    if (File.Exists(p)) return p;
                }
                catch (ArgumentException) { /* malformed PATH entry */ }
            }
        return null;
    }

    /// <summary>First non-empty output line of "--version", or null if the tool would not run.</summary>
    private async Task<string?> VersionOfAsync(string path, CancellationToken ct)
    {
        try
        {
            var r = await ProcessRunner.RunCaptureAsync(path, ["--version"], TimeSpan.FromSeconds(30), ct);
            if (r.TimedOut || r.ExitCode != 0) return null;
            return (r.StdOut + "\n" + r.StdErr).Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug(ex, "{Path} did not run", path);
            return null;
        }
    }
}

/// <summary>Detects yt-dlp at start-up without blocking it, retries a failed download (10 min, doubling to 6 h), and self-updates it daily.</summary>
public sealed class ToolMaintenanceService(ToolLocator tools, IOptions<FeedbackOptions> opt, ILogger<ToolMaintenanceService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await tools.InitializeAsync(stoppingToken);
        var o = opt.Value.Tools;
        var interval = TimeSpan.FromHours(Math.Max(1, o.UpdateIntervalHours));
        var nextUpdate = o.AutoUpdateYtDlp ? DateTimeOffset.UtcNow + interval : DateTimeOffset.MaxValue;
        var retryWait = TimeSpan.FromMinutes(10);
        var nextRetry = DateTimeOffset.UtcNow + retryWait;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!tools.Status.YtDlp.Ok && o.AutoDownloadYtDlp && DateTimeOffset.UtcNow >= nextRetry)
                {
                    await tools.RetryMissingAsync(stoppingToken);
                    if (!tools.Status.YtDlp.Ok) retryWait = TimeSpan.FromTicks(Math.Min(retryWait.Ticks * 2, TimeSpan.FromHours(6).Ticks));
                    nextRetry = DateTimeOffset.UtcNow + retryWait;
                }
                if (DateTimeOffset.UtcNow >= nextUpdate)
                {
                    if (tools.OwnsYtDlp) await tools.UpdateYtDlpAsync(stoppingToken);
                    nextUpdate = DateTimeOffset.UtcNow + interval;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Tool maintenance failed; will try again");
            }
            var next = tools.Status.YtDlp.Ok ? nextUpdate : (nextRetry < nextUpdate ? nextRetry : nextUpdate);
            if (next == DateTimeOffset.MaxValue) return;
            var wait = next - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, stoppingToken);
        }
    }
}
