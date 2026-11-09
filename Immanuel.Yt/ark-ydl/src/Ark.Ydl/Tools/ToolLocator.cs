using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
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
/// yt-dlp 2025.11). It downloads the official yt-dlp release when it is missing and keeps it
/// current: YouTube changes often, and an out-of-date extractor is the usual reason a
/// downloader "stops working". When no working ffmpeg is installed, it downloads a
/// self-contained static build into the tools folder, so a deployment needs no system ffmpeg.
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

    /// <summary>Wait before the first retry of a download that failed with a timeout or a 408/429/5xx answer.</summary>
    internal TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Completes once the first detection pass (and any bootstrap download) has finished.</summary>
    public Task WhenInitialized => _ready.Task;

    private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    private static string Exe(string name) => IsWindows ? name + ".exe" : name;
    private string OwnedYtDlpPath => Path.Combine(_paths.Tools, Exe("yt-dlp"));
    private string OwnedFfmpegDir => Path.Combine(_paths.Tools, "ffmpeg");
    private string OwnedFfmpegPath => Path.Combine(OwnedFfmpegDir, Exe("ffmpeg"));

    /// <summary>True when the yt-dlp in use lives in our tools folder, so self-update is ours to run.</summary>
    public bool OwnsYtDlp => Status.YtDlp.Path is { } p &&
                             string.Equals(Path.GetFullPath(p), OwnedYtDlpPath, StringComparison.Ordinal);

    public async Task InitializeAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            // Independent of each other, and on a first start two of them may be downloading.
            var ytTask = LocateYtDlpAsync(ct);
            var ffTask = LocateFfmpegAsync(ct);
            var jsTask = LocateJsRuntimeAsync(ct);
            await Task.WhenAll(ytTask, ffTask, jsTask);
            var (yt, ff, js) = (await ytTask, await ffTask, await jsTask);
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

    /// <summary>
    /// Looks for ffmpeg again and, if none works, downloads it again. The maintenance service calls this
    /// while ffmpeg is missing, so one failed download at start-up doesn't last until the next restart.
    /// </summary>
    public async Task<ToolInfo> RetryFfmpegAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var ff = await LocateFfmpegAsync(ct);
            Status = Status with { Ffmpeg = ff, CheckedAt = DateTimeOffset.UtcNow };
            if (ff.Ok) _log.LogInformation("ffmpeg {Version} ({Path}) is available now", ff.Version, ff.Path);
            return ff;
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
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) // HttpClient's timeout is a cancellation too
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

    [GeneratedRegex("^[0-9a-fA-F]{64}$")]
    private static partial Regex Sha256Rx();

    /// <summary>
    /// Parses a GNU-style SHA2-256SUMS file ("&lt;hex&gt;  [*]&lt;name&gt;"). With <paramref name="bareHash"/>,
    /// a file that holds nothing but one hash (gyan.dev's "{file}.sha256") also counts: it can only be
    /// about the file it sits beside.
    /// </summary>
    internal static string? ExpectedHash(string sums, string asset, bool bareHash = false)
    {
        foreach (var raw in sums.Split('\n'))
        {
            var line = raw.Trim();
            var sp = line.IndexOfAny([' ', '\t']);
            if (sp <= 0) continue;
            var name = line[sp..].Trim().TrimStart('*');
            if (name == asset) return line[..sp].ToLowerInvariant();
        }
        var only = sums.Trim();
        return bareHash && Sha256Rx().IsMatch(only) ? only.ToLowerInvariant() : null;
    }

    private async Task<string> DownloadYtDlpAsync(CancellationToken ct)
    {
        var asset = ReleaseAssetName() ?? throw new PlatformNotSupportedException("No official yt-dlp build for this platform.");
        var baseUrl = _opt.Tools.YtDlpReleaseBase.TrimEnd('/') + "/";
        var client = _http.CreateClient("tools");
        _log.LogInformation("Downloading yt-dlp ({Asset}) from {Base}", asset, baseUrl);

        var tmp = OwnedYtDlpPath + ".download";
        var expected = await DownloadVerifiedAsync(client, new ToolArchive(baseUrl + asset, baseUrl + "SHA2-256SUMS", asset), tmp, ct);
        File.Move(tmp, OwnedYtDlpPath, overwrite: true);
        MakeExecutable(OwnedYtDlpPath);
        _log.LogInformation("yt-dlp saved to {Path} (sha256 {Hash})", OwnedYtDlpPath, expected);
        return OwnedYtDlpPath;
    }

    // ── ffmpeg ──────────────────────────────────────────────────────────────

    /// <summary>"ffmpeg version 9.0.2-https://www.martin-riedl.de Copyright…" → 9.0.2 (a builder's URL tag is dropped).</summary>
    [GeneratedRegex(@"version\s+(\S+?)(?:-https?://\S*)?(?:\s|$)", RegexOptions.IgnoreCase)]
    private static partial Regex VersionWordRx();

    internal static string FfmpegVersion(string line) => VersionWordRx().Match(line) is { Success: true } m ? m.Groups[1].Value : line;

    /// <summary>
    /// Tries the configured path, then the app's own copy ({Tools}/ffmpeg), then PATH. If none of them
    /// runs (not installed, or a Homebrew ffmpeg whose libraries an upgrade removed), it downloads a
    /// static build into the tools folder.
    /// </summary>
    private async Task<ToolInfo> LocateFfmpegAsync(CancellationToken ct)
    {
        string? problem = null;
        var candidates = new[] { _opt.Tools.FfmpegPath, File.Exists(OwnedFfmpegPath) ? OwnedFfmpegPath : null, FindOnPath("ffmpeg") }
            .Where(c => !string.IsNullOrWhiteSpace(c)).Distinct();
        foreach (var c in candidates)
        {
            if (!File.Exists(c))
            {
                problem ??= $"ffmpeg was not found at {c} (Ydl:Tools:FfmpegPath).";
                continue;
            }
            var info = await FfmpegInfoAsync(c, ct);
            if (info.Ok) return info;
            problem ??= info.Note;
        }

        if (!_opt.Tools.AutoDownloadFfmpeg)
            return new ToolInfo("ffmpeg", null, null, problem is null
                ? "Not found. Install ffmpeg (needed to merge video+audio, cut clips and convert audio) or enable Ydl:Tools:AutoDownloadFfmpeg."
                : problem + " Reinstall it (e.g. \"brew reinstall ffmpeg\") or enable Ydl:Tools:AutoDownloadFfmpeg.");

        var installed = await InstallFfmpegAsync(ct);
        return installed.Ok || problem is null ? installed : installed with { Note = problem + " " + installed.Note };
    }

    /// <summary>Runs "ffmpeg -version". If it fails, the result has no path, and the reason is in the note.</summary>
    private async Task<ToolInfo> FfmpegInfoAsync(string path, CancellationToken ct, string? note = null)
    {
        var (line, failure) = await ProbeToolAsync(path, ["-hide_banner", "-version"], ct);
        if (line is null) return new ToolInfo("ffmpeg", null, null, $"ffmpeg at {path} won't run ({failure}).");
        var hasProbe = File.Exists(Path.Combine(Path.GetDirectoryName(path) ?? "", Exe("ffprobe")));
        return new ToolInfo("ffmpeg", path, FfmpegVersion(line),
            hasProbe ? note : "ffprobe was not found next to ffmpeg; some post-processing may fail.");
    }

    /// <summary>
    /// Downloads this platform's static ffmpeg + ffprobe into {Tools}/ffmpeg and checks that it runs.
    /// Each source is tried in turn (default: this platform's own); the note names every one that failed.
    /// </summary>
    internal async Task<ToolInfo> InstallFfmpegAsync(CancellationToken ct, IReadOnlyList<FfmpegSource>? sources = null)
    {
        try
        {
            var source = await DownloadFfmpegAsync(sources ?? FfmpegSources(_opt.Tools, CurrentOs, RuntimeInformation.OSArchitecture), ct);
            var info = await FfmpegInfoAsync(OwnedFfmpegPath, ct, $"Downloaded a static build from {source} (SHA-256 verified).");
            return info.Ok ? info : info with { Note = "Downloaded ffmpeg, but " + info.Note };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log.LogError(ex, "Could not download ffmpeg");
            return new ToolInfo("ffmpeg", null, null,
                $"The automatic ffmpeg download failed ({ex.Message}). The app keeps retrying in the background; " +
                "to use an ffmpeg you installed yourself, set Ydl:Tools:FfmpegPath.");
        }
    }

    /// <summary>One file to download: its URL, the checksum file that lists it (null = "{URL after redirects}.sha256"), and the name it is listed under.</summary>
    internal sealed record ToolArchive(string Url, string? SumsUrl, string Name);

    /// <summary>One place to get ffmpeg + ffprobe from: a short label for messages, and the archives that together hold both.</summary>
    internal sealed record FfmpegSource(string Label, IReadOnlyList<ToolArchive> Archives);

    /// <summary>
    /// Where this platform's static ffmpeg + ffprobe come from, in the order to try them. macOS and Linux
    /// use ffmpeg.martin-riedl.de, which has one self-contained zip per tool with a .sha256 file beside it
    /// ("latest" redirects to a versioned folder). Windows uses yt-dlp's FFmpeg-Builds (one zip, listed
    /// in checksums.sha256), then on x64 gyan.dev (one zip, bare .sha256 beside it). Empty when there is
    /// no build for the platform.
    /// </summary>
    internal static IReadOnlyList<FfmpegSource> FfmpegSources(ToolOptions o, OSPlatform os, Architecture arch)
    {
        if (os == OSPlatform.Windows)
        {
            var list = new List<FfmpegSource>();
            var flavor = arch switch { Architecture.X64 => "win64", Architecture.Arm64 => "winarm64", Architecture.X86 => "win32", _ => null };
            if (flavor is not null && !string.IsNullOrWhiteSpace(o.FfmpegWindowsReleaseBase))
            {
                var b = o.FfmpegWindowsReleaseBase.Trim().TrimEnd('/') + "/";
                var zip = $"ffmpeg-master-latest-{flavor}-gpl.zip";
                list.Add(new FfmpegSource(SourceLabel(b + zip), [new ToolArchive(b + zip, b + "checksums.sha256", zip)]));
            }
            if (arch == Architecture.X64 && !string.IsNullOrWhiteSpace(o.FfmpegWindowsFallbackUrl))
            {
                var url = o.FfmpegWindowsFallbackUrl.Trim();
                list.Add(new FfmpegSource(SourceLabel(url), [new ToolArchive(url, null, Path.GetFileName(new Uri(url).AbsolutePath))]));
            }
            return list;
        }
        var system = os == OSPlatform.OSX ? "macos" : os == OSPlatform.Linux ? "linux" : null;
        var cpu = arch switch { Architecture.X64 => "amd64", Architecture.Arm64 => "arm64", _ => null };
        if (system is null || cpu is null) return [];
        var root = $"{o.FfmpegReleaseBase.TrimEnd('/')}/{system}/{cpu}/release/";
        return [new FfmpegSource(SourceLabel(root), [new ToolArchive(root + "ffmpeg.zip", null, "ffmpeg.zip"), new ToolArchive(root + "ffprobe.zip", null, "ffprobe.zip")])];
    }

    /// <summary>"github.com/{owner}/{repo}" for a GitHub release file, else the host.</summary>
    private static string SourceLabel(string url) =>
        GitHubReleaseFile(url) is { } g ? $"github.com/{g.Owner}/{g.Repo}" : new Uri(url).Host;

    private static OSPlatform CurrentOs =>
        IsWindows ? OSPlatform.Windows
        : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? OSPlatform.OSX
        : RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? OSPlatform.Linux
        : OSPlatform.FreeBSD;

    /// <summary>Installs from the first source that works and returns its label; throws with every source's reason if none does.</summary>
    private async Task<string> DownloadFfmpegAsync(IReadOnlyList<FfmpegSource> sources, CancellationToken ct)
    {
        if (sources.Count == 0)
            throw new PlatformNotSupportedException($"No static ffmpeg build is published for {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture}).");
        var client = _http.CreateClient("tools");
        var failures = new List<string>();
        foreach (var s in sources)
        {
            try
            {
                await InstallFromAsync(client, s, ct);
                return s.Label;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _log.LogWarning(ex, "ffmpeg download from {Source} failed", s.Label);
                failures.Add($"{s.Label}: {ex.Message}");
            }
        }
        throw new InvalidOperationException(string.Join("; ", failures));
    }

    private async Task InstallFromAsync(HttpClient client, FfmpegSource source, CancellationToken ct)
    {
        var archives = await ViaGitHubApiAsync(client, source.Archives, ct);
        string[] wanted = [Exe("ffmpeg"), Exe("ffprobe")];

        // Build the folder under a temporary name and move it in at the end, so the app never
        // sees half an install.
        var staging = OwnedFfmpegDir + ".download";
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var a in archives)
            {
                _log.LogInformation("Downloading ffmpeg from {Source}: {Url}", source.Label, a.Url);
                var zip = Path.Combine(staging, "archive.zip");
                var sha = await DownloadVerifiedAsync(client, a, zip, ct);
                ExtractNamed(zip, staging, wanted);
                File.Delete(zip);
                _log.LogInformation("{Name} verified (sha256 {Hash})", a.Name, sha);
            }
            foreach (var w in wanted)
            {
                var p = Path.Combine(staging, w);
                if (!File.Exists(p)) throw new InvalidOperationException($"{w} was not in the downloaded archive.");
                MakeExecutable(p);
            }
            if (Directory.Exists(OwnedFfmpegDir)) Directory.Delete(OwnedFfmpegDir, true);
            // On Windows, antivirus often scans a freshly written .exe for a moment, and the rename
            // fails until it lets go.
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    Directory.Move(staging, OwnedFfmpegDir);
                    break;
                }
                catch (Exception ex) when (attempt < 10 && ex is IOException or UnauthorizedAccessException)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), ct);
                }
            }
        }
        catch
        {
            try { Directory.Delete(staging, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
        _log.LogInformation("ffmpeg saved to {Dir}", OwnedFfmpegDir);
    }

    // ── GitHub release files ────────────────────────────────────────────────

    internal sealed record GitHubFile(string Owner, string Repo, string Tag, string Name)
    {
        public string ReleaseApiUrl => $"https://api.github.com/repos/{Owner}/{Repo}/releases/tags/{Tag}";
    }

    [GeneratedRegex(@"^https://github\.com/([^/]+)/([^/]+)/releases/download/([^/]+)/([^/?#]+)$", RegexOptions.IgnoreCase)]
    private static partial Regex GitHubFileRx();

    /// <summary>"https://github.com/{owner}/{repo}/releases/download/{tag}/{file}" → its parts; anything else → null.</summary>
    internal static GitHubFile? GitHubReleaseFile(string? url) =>
        url is not null && GitHubFileRx().Match(url) is { Success: true } m
            ? new GitHubFile(m.Groups[1].Value, m.Groups[2].Value, Uri.UnescapeDataString(m.Groups[3].Value), Uri.UnescapeDataString(m.Groups[4].Value))
            : null;

    /// <summary>
    /// Swaps each github.com release download URL for the same file's REST API URL. Since 2026-11-09 github.com
    /// answers /releases/download/latest/… for yt-dlp/FFmpeg-Builds (thousands of daily releases) with 504
    /// Gateway Time-out, while the API serves the same release. That costs one API call per release; the
    /// files themselves still come from GitHub's download CDN.
    /// </summary>
    private async Task<IReadOnlyList<ToolArchive>> ViaGitHubApiAsync(HttpClient client, IReadOnlyList<ToolArchive> archives, CancellationToken ct)
    {
        var releases = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        async Task<string?> ApiUrl(string? url)
        {
            if (GitHubReleaseFile(url) is not { } f) return url;
            if (!releases.TryGetValue(f.ReleaseApiUrl, out var assets))
            {
                using var resp = await GetAsync(client, f.ReleaseApiUrl, ct, "application/vnd.github+json");
                await using var body = await resp.Content.ReadAsStreamAsync(ct);
                using var doc = await JsonDocument.ParseAsync(body, cancellationToken: ct);
                assets = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var a in doc.RootElement.GetProperty("assets").EnumerateArray())
                    if (a.GetProperty("name").GetString() is { } n && a.GetProperty("url").GetString() is { } u)
                        assets[n] = u;
                releases[f.ReleaseApiUrl] = assets;
            }
            return assets.TryGetValue(f.Name, out var api)
                ? api
                : throw new InvalidOperationException($"{f.Name} is not in the {f.Owner}/{f.Repo} release \"{f.Tag}\".");
        }

        var resolved = new List<ToolArchive>();
        foreach (var a in archives)
            resolved.Add(a with { Url = (await ApiUrl(a.Url))!, SumsUrl = await ApiUrl(a.SumsUrl) });
        return resolved;
    }

    /// <summary>
    /// Extracts the zip entries whose file name is in <paramref name="names"/>, wherever they sit in the
    /// archive, flat into <paramref name="dir"/>. A file is always written under one of those names and
    /// never under the entry's own path, so a crafted entry cannot write outside the folder.
    /// </summary>
    internal static int ExtractNamed(string zipPath, string dir, IReadOnlyCollection<string> names)
    {
        var cmp = IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var done = new HashSet<string>(cmp);
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var e in zip.Entries)
        {
            var name = e.FullName.Replace('\\', '/').Split('/')[^1];
            var match = names.FirstOrDefault(n => cmp.Equals(n, name));
            if (match is null || !done.Add(match)) continue;
            e.ExtractToFile(Path.Combine(dir, match), overwrite: true);
        }
        return done.Count;
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

    /// <summary>
    /// Streams <paramref name="a"/> to <paramref name="dest"/> and checks its SHA-256 against the
    /// published checksum file. Returns the hash.
    /// </summary>
    private async Task<string> DownloadVerifiedAsync(HttpClient client, ToolArchive a, string dest, CancellationToken ct)
    {
        string actual;
        Uri final;
        using (var resp = await GetAsync(client, a.Url, ct))
        {
            final = resp.RequestMessage?.RequestUri ?? new Uri(a.Url);
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(dest);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buf = new byte[81920];
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                hash.AppendData(buf, 0, n);
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
            }
            actual = Convert.ToHexStringLower(hash.GetHashAndReset());
        }

        // A "latest" link redirects to a versioned file, and that file's .sha256 sits next to it.
        string sums;
        using (var resp = await GetAsync(client, a.SumsUrl ?? final.AbsoluteUri + ".sha256", ct))
            sums = await resp.Content.ReadAsStringAsync(ct);
        var expected = ExpectedHash(sums, a.Name, bareHash: a.SumsUrl is null);
        if (expected == actual) return expected;
        File.Delete(dest);
        throw new InvalidOperationException(expected is null
            ? $"{a.Name} is not listed in its checksum file."
            : $"Checksum mismatch for {a.Name}: expected {expected}, got {actual}.");
    }

    private const int Attempts = 3;

    private static bool IsTransient(HttpStatusCode s) =>
        s is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    /// <summary>
    /// GET that returns a successful response (headers read, body still to come) or throws. A timeout, a
    /// dropped connection or a 408/429/5xx gateway answer is tried again, up to <see cref="Attempts"/> times.
    /// A GitHub REST API file is asked for as raw bytes, which redirects to the download CDN.
    /// </summary>
    private async Task<HttpResponseMessage> GetAsync(HttpClient client, string url, CancellationToken ct, string? accept = null)
    {
        accept ??= url.StartsWith("https://api.github.com/", StringComparison.OrdinalIgnoreCase) ? "application/octet-stream" : null;
        for (var attempt = 1; ; attempt++)
        {
            var delay = RetryDelay * (attempt == 1 ? 1 : 3);
            HttpResponseMessage resp;
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Get, url);
                if (accept is not null) req.Headers.Accept.ParseAdd(accept);
                resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (Exception ex) when (attempt < Attempts && !ct.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException)
            {
                _log.LogWarning("GET {Url} failed ({Reason}); trying again in {Delay}", url, ex.Message, delay);
                await Task.Delay(delay, ct);
                continue;
            }
            if (resp.IsSuccessStatusCode) return resp;

            var (status, reason) = (resp.StatusCode, resp.ReasonPhrase);
            resp.Dispose();
            var answer = $"{new Uri(url).Host} answered {(int)status} ({reason})";
            if (attempt >= Attempts || !IsTransient(status))
                throw new HttpRequestException(attempt > 1 ? $"{answer}, {attempt} times." : answer + ".", null, status);
            _log.LogWarning("GET {Url}: {Answer}; trying again in {Delay}", url, answer, delay);
            await Task.Delay(delay, ct);
        }
    }

    private static void MakeExecutable(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

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

/// <summary>
/// Detects tools at start-up (without blocking it), keeps yt-dlp updated, and while ffmpeg is missing
/// tries its download again: 10 minutes after a failure, then twice as long each time, up to 6 hours.
/// </summary>
public sealed class ToolMaintenanceService(ToolLocator tools, IOptions<YdlOptions> opt, ILogger<ToolMaintenanceService> log) : BackgroundService
{
    internal static readonly TimeSpan FfmpegRetryFirst = TimeSpan.FromMinutes(10);
    internal static readonly TimeSpan FfmpegRetryMax = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await tools.InitializeAsync(stoppingToken);
        var o = opt.Value.Tools;
        var interval = TimeSpan.FromHours(Math.Max(1, o.UpdateIntervalHours));
        var nextUpdate = o.AutoUpdateYtDlp ? DateTimeOffset.UtcNow : DateTimeOffset.MaxValue;
        var ffmpegWait = FfmpegRetryFirst;
        var nextFfmpeg = DateTimeOffset.UtcNow + ffmpegWait;

        while (!stoppingToken.IsCancellationRequested)
        {
            if (DateTimeOffset.UtcNow >= nextUpdate)
            {
                try
                {
                    if (tools.OwnsYtDlp) await tools.UpdateYtDlpAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogWarning(ex, "yt-dlp self-update failed; will retry in {Interval}", interval);
                }
                nextUpdate = DateTimeOffset.UtcNow + interval;
            }

            var ffmpegMissing = o.AutoDownloadFfmpeg && !tools.Status.Ffmpeg.Ok;
            if (ffmpegMissing && DateTimeOffset.UtcNow >= nextFfmpeg)
            {
                log.LogInformation("ffmpeg is still missing; trying the download again");
                try
                {
                    ffmpegMissing = !(await tools.RetryFfmpegAsync(stoppingToken)).Ok;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogWarning(ex, "ffmpeg retry failed");
                }
                if (ffmpegMissing)
                {
                    ffmpegWait = TimeSpan.FromTicks(Math.Min(ffmpegWait.Ticks * 2, FfmpegRetryMax.Ticks));
                    log.LogWarning("ffmpeg is still missing; next try in {Wait}", ffmpegWait);
                }
                nextFfmpeg = DateTimeOffset.UtcNow + ffmpegWait;
            }

            var next = ffmpegMissing && nextFfmpeg < nextUpdate ? nextFfmpeg : nextUpdate;
            if (next == DateTimeOffset.MaxValue) return; // nothing left to do
            var wait = next - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, stoppingToken);
        }
    }
}
