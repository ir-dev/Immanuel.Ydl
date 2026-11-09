using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Ark.Ydl.Options;
using Microsoft.Extensions.Options;

namespace Ark.Ydl.Tools;

public sealed record ToolInfo(string Name, string? Path, string? Version, string? Note = null)
{
    public bool Ok => Path is not null;
}

public sealed record ToolStatus(ToolInfo YtDlp, ToolInfo Ffmpeg, ToolInfo JsRuntime, bool Preparing, DateTimeOffset CheckedAt)
{
    public bool Ready => YtDlp.Ok && Ffmpeg.Ok;
}

/// <summary>
/// Finds yt-dlp, ffmpeg and a JavaScript runtime (YouTube extraction has needed one since
/// yt-dlp 2025.11), downloads the official yt-dlp release when it is missing and keeps it
/// current: YouTube changes often, and an out-of-date extractor is the usual reason a
/// downloader "stops working".
/// </summary>
public sealed partial class ToolLocator
{
    private readonly YdlOptions _opt;
    private readonly YdlPaths _paths;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<ToolLocator> _log;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ToolLocator(IOptions<YdlOptions> opt, YdlPaths paths, IHttpClientFactory http, ILogger<ToolLocator> log)
    {
        _opt = opt.Value;
        _paths = paths;
        _http = http;
        _log = log;
        Status = new ToolStatus(new("yt-dlp", null, null), new("ffmpeg", null, null), new("js", null, null), true, DateTimeOffset.UtcNow);
    }

    public ToolStatus Status { get; private set; }

    /// <summary>Completes once the first detection pass (and any bootstrap download) has finished.</summary>
    public Task WhenInitialized => _ready.Task;

    private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    private string OwnedYtDlpPath => Path.Combine(_paths.Tools, IsWindows ? "yt-dlp.exe" : "yt-dlp");

    /// <summary>True when the yt-dlp in use lives in our tools folder, so self-update is ours to run.</summary>
    public bool OwnsYtDlp => Status.YtDlp.Path is { } p &&
                             string.Equals(Path.GetFullPath(p), OwnedYtDlpPath, StringComparison.Ordinal);

    public async Task InitializeAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var yt = await LocateYtDlpAsync(ct);
            var ff = await LocateFfmpegAsync(ct);
            var js = await LocateJsRuntimeAsync(ct);
            Status = new ToolStatus(yt, ff, js, false, DateTimeOffset.UtcNow);
            _log.LogInformation("Tools: yt-dlp {YtVer} ({YtPath}); ffmpeg {FfVer} ({FfPath}); js {Js} {JsVer}",
                yt.Version, yt.Path, ff.Version, ff.Path, js.Name, js.Version);
            if (!js.Ok)
                _log.LogWarning("No JavaScript runtime found. YouTube downloads need deno (recommended) or node >= 22. {Note}", js.Note);
        }
        finally
        {
            _gate.Release();
            _ready.TrySetResult();
        }
    }

    /// <summary>Runs "yt-dlp -U" on a binary we own and refreshes the reported version.</summary>
    public async Task<string?> UpdateYtDlpAsync(CancellationToken ct)
    {
        if (Status.YtDlp.Path is not { } path || !OwnsYtDlp) return null;
        var r = await ProcessRunner.RunCaptureAsync(path, ["--ignore-config", "-U"], TimeSpan.FromMinutes(3), ct);
        var output = (r.StdOut + "\n" + r.StdErr).Trim();
        _log.LogInformation("yt-dlp -U exit {Code}: {Output}", r.ExitCode, output.Length > 600 ? output[..600] : output);
        var version = await VersionOfAsync(path, ["--version"], ct);
        Status = Status with { YtDlp = Status.YtDlp with { Version = version ?? Status.YtDlp.Version }, CheckedAt = DateTimeOffset.UtcNow };
        return version;
    }

    /// <summary>--ffmpeg-location / --js-runtimes for every yt-dlp call.</summary>
    public IReadOnlyList<string> ToolArgs()
    {
        var s = Status;
        var a = new List<string>();
        if (s.Ffmpeg.Path is { } ff) a.AddRange(["--ffmpeg-location", ff]);
        if (s.JsRuntime.Path is { } js) a.AddRange(["--js-runtimes", $"{s.JsRuntime.Name}:{js}"]);
        return a;
    }

    // ── yt-dlp ──────────────────────────────────────────────────────────────

    private async Task<ToolInfo> LocateYtDlpAsync(CancellationToken ct)
    {
        var candidates = new List<string?>
        {
            _opt.Tools.YtDlpPath,
            File.Exists(OwnedYtDlpPath) ? OwnedYtDlpPath : null,
            FindOnPath("yt-dlp"),
        };
        foreach (var c in candidates.Where(c => !string.IsNullOrWhiteSpace(c)))
        {
            var v = await VersionOfAsync(c!, ["--version"], ct);
            if (v is not null) return new ToolInfo("yt-dlp", c, v);
            _log.LogWarning("yt-dlp at {Path} did not answer --version", c);
        }

        if (!_opt.Tools.AutoDownloadYtDlp)
            return new ToolInfo("yt-dlp", null, null, "Not found. Install yt-dlp or enable Ydl:Tools:AutoDownloadYtDlp.");

        try
        {
            var path = await DownloadYtDlpAsync(ct);
            var v = await VersionOfAsync(path, ["--version"], ct);
            return v is not null
                ? new ToolInfo("yt-dlp", path, v, "Downloaded from the official GitHub release (SHA-256 verified).")
                : new ToolInfo("yt-dlp", null, null, "Downloaded yt-dlp but it does not run on this machine.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex, "Could not download yt-dlp");
            return new ToolInfo("yt-dlp", null, null, "Not found and the automatic download failed: " + ex.Message);
        }
    }

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

    private async Task<string> DownloadYtDlpAsync(CancellationToken ct)
    {
        var asset = ReleaseAssetName() ?? throw new PlatformNotSupportedException("No official yt-dlp build for this platform.");
        var baseUrl = _opt.Tools.YtDlpReleaseBase.TrimEnd('/') + "/";
        var client = _http.CreateClient("tools");
        _log.LogInformation("Downloading yt-dlp ({Asset}) from {Base}", asset, baseUrl);

        var sums = await client.GetStringAsync(baseUrl + "SHA2-256SUMS", ct);
        var expected = ExpectedHash(sums, asset) ?? throw new InvalidOperationException($"{asset} is not listed in SHA2-256SUMS.");

        var tmp = OwnedYtDlpPath + ".download";
        using (var resp = await client.GetAsync(baseUrl + asset, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(tmp);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buf = new byte[81920];
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                hash.AppendData(buf, 0, n);
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
            }
            var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (actual != expected)
            {
                dst.Close();
                File.Delete(tmp);
                throw new InvalidOperationException($"Checksum mismatch for {asset}: expected {expected}, got {actual}.");
            }
        }

        File.Move(tmp, OwnedYtDlpPath, overwrite: true);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(OwnedYtDlpPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        _log.LogInformation("yt-dlp saved to {Path} (sha256 {Hash})", OwnedYtDlpPath, expected);
        return OwnedYtDlpPath;
    }

    // ── ffmpeg ──────────────────────────────────────────────────────────────

    [GeneratedRegex(@"version\s+(\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex VersionWordRx();

    private async Task<ToolInfo> LocateFfmpegAsync(CancellationToken ct)
    {
        string? broken = null;
        foreach (var c in new[] { _opt.Tools.FfmpegPath, FindOnPath("ffmpeg") }.Where(c => !string.IsNullOrWhiteSpace(c)).Distinct())
        {
            var (line, failure) = await ProbeToolAsync(c!, ["-hide_banner", "-version"], ct);
            if (line is null)
            {
                broken ??= $"ffmpeg at {c} is installed but won't run ({failure}). Reinstall it (e.g. \"brew reinstall ffmpeg\").";
                continue;
            }
            var m = VersionWordRx().Match(line);
            var hasProbe = File.Exists(Path.Combine(Path.GetDirectoryName(c!)!, IsWindows ? "ffprobe.exe" : "ffprobe"));
            return new ToolInfo("ffmpeg", c, m.Success ? m.Groups[1].Value : line,
                hasProbe ? null : "ffprobe was not found next to ffmpeg; some post-processing may fail.");
        }
        return new ToolInfo("ffmpeg", null, null,
            broken ?? "Not found. Install ffmpeg (needed to merge video+audio, cut clips and convert audio).");
    }

    // ── JavaScript runtime ──────────────────────────────────────────────────

    [GeneratedRegex(@"(\d+)\.(\d+)(?:\.(\d+))?")]
    private static partial Regex SemverRx();

    private async Task<ToolInfo> LocateJsRuntimeAsync(CancellationToken ct)
    {
        var wanted = (_opt.Tools.JsRuntime ?? "auto").Trim().ToLowerInvariant();
        if (wanted == "none") return new ToolInfo("none", null, null, "Disabled by configuration.");

        // yt-dlp's own priority order; node must be >= 22, deno >= 2.3.
        var order = wanted == "auto" ? new[] { "deno", "node", "bun", "quickjs" } : [wanted];
        foreach (var name in order)
        {
            var exe = name == "quickjs" ? "qjs" : name;
            var path = wanted != "auto" && !string.IsNullOrWhiteSpace(_opt.Tools.JsRuntimePath)
                ? _opt.Tools.JsRuntimePath
                : FindOnPath(exe) ?? (name == "deno" ? HomeBin(".deno", "bin", "deno") : null);
            if (path is null) continue;
            var line = await VersionOfAsync(path, name == "quickjs" ? ["--help"] : ["--version"], ct, anyExitCode: name == "quickjs");
            if (line is null) continue;
            var m = SemverRx().Match(line);
            var version = m.Success ? m.Value : line;
            var major = m.Success ? int.Parse(m.Groups[1].Value) : 0;
            var minor = m.Success ? int.Parse(m.Groups[2].Value) : 0;
            if (name == "node" && major < 22) { _log.LogWarning("node {Version} is too old for yt-dlp (needs >= 22)", version); continue; }
            if (name == "deno" && (major < 2 || (major == 2 && minor < 3))) { _log.LogWarning("deno {Version} is too old (needs >= 2.3)", version); continue; }
            return new ToolInfo(name, path, version);
        }
        return new ToolInfo("none", null, null, "Install deno (https://deno.com) or node >= 22 so yt-dlp can solve YouTube's JavaScript challenges.");
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static string? HomeBin(params string[] parts)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home)) return null;
        var p = Path.Combine([home, .. parts]) + (IsWindows ? ".exe" : "");
        return File.Exists(p) ? p : null;
    }

    /// <summary>PATH lookup plus the usual install folders (IDE-launched apps often get a thin PATH on macOS).</summary>
    internal static string? FindOnPath(string name)
    {
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).ToList();
        if (!IsWindows)
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            dirs.AddRange(["/opt/homebrew/bin", "/usr/local/bin", "/usr/bin", "/bin", Path.Combine(home, ".local", "bin"), Path.Combine(home, ".deno", "bin")]);
        }
        var exts = IsWindows
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [""];
        foreach (var d in dirs.Distinct())
        {
            foreach (var e in exts)
            {
                try
                {
                    var p = Path.Combine(d.Trim('"'), name + e);
                    if (File.Exists(p)) return p;
                }
                catch (ArgumentException)
                {
                    // malformed PATH entry
                }
            }
        }
        return null;
    }

    /// <summary>First non-empty output line of a version command, or null if it would not run.</summary>
    private async Task<string?> VersionOfAsync(string path, IReadOnlyList<string> args, CancellationToken ct, bool anyExitCode = false) =>
        (await ProbeToolAsync(path, args, ct, anyExitCode)).Version;

    /// <summary>
    /// Runs a version command. A non-zero exit (e.g. a Homebrew ffmpeg whose dylibs were removed by an
    /// upgrade) is a failure, and its first output line is returned as the reason.
    /// </summary>
    private async Task<(string? Version, string? Failure)> ProbeToolAsync(string path, IReadOnlyList<string> args, CancellationToken ct, bool anyExitCode = false)
    {
        try
        {
            var r = await ProcessRunner.RunCaptureAsync(path, args, TimeSpan.FromSeconds(30), ct);
            if (r.TimedOut) return (null, "did not answer within 30 s");
            var first = (r.StdOut + "\n" + r.StdErr).Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
            if (r.ExitCode == 0 || (anyExitCode && first is not null)) return (first ?? "unknown", null);
            var reason = (r.StdErr + "\n" + r.StdOut).Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
            _log.LogWarning("{Path} exited with {Code}: {Reason}", path, r.ExitCode, reason);
            return (null, reason is null ? $"exit code {r.ExitCode}" : reason.Length > 200 ? reason[..200] + "…" : reason);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug(ex, "{Path} did not run", path);
            return (null, ex.Message);
        }
    }
}

/// <summary>Detects tools at start-up (without blocking it) and keeps yt-dlp updated.</summary>
public sealed class ToolMaintenanceService(ToolLocator tools, IOptions<YdlOptions> opt, ILogger<ToolMaintenanceService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await tools.InitializeAsync(stoppingToken);
        var o = opt.Value.Tools;
        if (!o.AutoUpdateYtDlp) return;

        var interval = TimeSpan.FromHours(Math.Max(1, o.UpdateIntervalHours));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (tools.OwnsYtDlp) await tools.UpdateYtDlpAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "yt-dlp self-update failed; will retry in {Interval}", interval);
            }
            await Task.Delay(interval, stoppingToken);
        }
    }
}
