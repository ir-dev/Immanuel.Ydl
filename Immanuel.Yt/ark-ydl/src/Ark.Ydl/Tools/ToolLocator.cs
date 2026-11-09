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
/// downloader "stops working". When no working ffmpeg or JavaScript runtime is installed, it
/// downloads a self-contained static ffmpeg, and deno, into the tools folder. A deployment then needs
/// nothing installed on the machine, which is what shared hosting allows.
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

    /// <summary>Finds an installed tool by name (PATH and the usual install folders). Tests swap it out.</summary>
    internal Func<string, string?> Which { get; init; } = FindOnPath;

    /// <summary>Completes once the first detection pass (and any bootstrap download) has finished.</summary>
    public Task WhenInitialized => _ready.Task;

    private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    private static string Exe(string name) => IsWindows ? name + ".exe" : name;
    private string OwnedYtDlpPath => Path.Combine(_paths.Tools, Exe("yt-dlp"));
    private string OwnedFfmpegDir => Path.Combine(_paths.Tools, "ffmpeg");
    private string OwnedFfmpegPath => Path.Combine(OwnedFfmpegDir, Exe("ffmpeg"));
    private string OwnedDenoDir => Path.Combine(_paths.Tools, "deno");
    private string OwnedDenoPath => Path.Combine(OwnedDenoDir, Exe("deno"));

    /// <summary>True when the yt-dlp in use lives in our tools folder, so self-update is ours to run.</summary>
    public bool OwnsYtDlp => Status.YtDlp.Path is { } p &&
                             string.Equals(Path.GetFullPath(p), OwnedYtDlpPath, StringComparison.Ordinal);

    public async Task InitializeAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            PrepareChildEnvironment();
            // Independent of each other, and on a first start all three may be downloading.
            var ytTask = LocateYtDlpAsync(ct);
            var ffTask = LocateFfmpegAsync(ct);
            var jsTask = LocateJsRuntimeAsync(ct);
            await Task.WhenAll(ytTask, ffTask, jsTask);
            var (yt, ff, js) = (await ytTask, await ffTask, await jsTask);
            Status = new ToolStatus(yt, ff, js, false, DateTimeOffset.UtcNow);
            _log.LogInformation("Tools: yt-dlp {YtVer} ({YtPath}); ffmpeg {FfVer} ({FfPath}); js {Js} {JsVer} ({JsPath})",
                yt.Version, yt.Path, ff.Version, ff.Path, js.Name, js.Version, js.Path);
            if (!js.Ok)
                _log.LogWarning("No JavaScript runtime. YouTube downloads need deno (recommended) or node >= 22. {Note}", js.Note);
        }
        finally
        {
            _gate.Release();
            _ready.TrySetResult();
        }
    }

    /// <summary>
    /// yt-dlp starts deno, and deno inherits this process's environment. Under IIS, and on shared hosting
    /// in particular, the app-pool account often has no loaded user profile, so deno's default cache
    /// folder (%LOCALAPPDATA%\deno, ~/.cache/deno) is missing or read-only. Point it at the app's own cache
    /// folder instead, and keep deno from checking for updates or waiting for a prompt nobody can answer.
    /// A value the host has already set wins.
    /// </summary>
    private void PrepareChildEnvironment()
    {
        SetDefault("DENO_DIR", Path.Combine(_paths.Cache, "deno"));
        SetDefault("DENO_NO_UPDATE_CHECK", "1");
        SetDefault("DENO_NO_PROMPT", "1");

        static void SetDefault(string name, string value)
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name)))
                Environment.SetEnvironmentVariable(name, value);
        }
    }

    /// <summary>Tools that are missing although the app may download them itself ("ffmpeg", "deno").</summary>
    public IReadOnlyList<string> MissingDownloads()
    {
        var s = Status;
        var missing = new List<string>();
        if (_opt.Tools.AutoDownloadFfmpeg && !s.Ffmpeg.Ok) missing.Add("ffmpeg");
        if (DenoDownloadAllowed && !s.JsRuntime.Ok) missing.Add("deno");
        return missing;
    }

    /// <summary>
    /// Looks again for each tool in <see cref="MissingDownloads"/> and downloads the ones still missing.
    /// The maintenance service calls this, so one failed download at start-up doesn't last until the next
    /// restart. Returns what is still missing afterwards.
    /// </summary>
    public async Task<IReadOnlyList<string>> RetryMissingAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var missing = MissingDownloads();
            if (missing.Contains("ffmpeg"))
            {
                var ff = await LocateFfmpegAsync(ct);
                Status = Status with { Ffmpeg = ff, CheckedAt = DateTimeOffset.UtcNow };
                if (ff.Ok) _log.LogInformation("ffmpeg {Version} ({Path}) is available now", ff.Version, ff.Path);
            }
            if (missing.Contains("deno"))
            {
                var js = await LocateJsRuntimeAsync(ct);
                Status = Status with { JsRuntime = js, CheckedAt = DateTimeOffset.UtcNow };
                if (js.Ok) _log.LogInformation("JavaScript runtime {Name} {Version} ({Path}) is available now", js.Name, js.Version, js.Path);
            }
            return MissingDownloads();
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
            Which("yt-dlp"),
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
        var archive = (await ViaGitHubApiAsync(client, [new ToolArchive(baseUrl + asset, baseUrl + "SHA2-256SUMS", asset)], ct))[0];
        var expected = await DownloadVerifiedAsync(client, archive, tmp, ct);
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
        var candidates = new[] { _opt.Tools.FfmpegPath, File.Exists(OwnedFfmpegPath) ? OwnedFfmpegPath : null, Which("ffmpeg") }
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
    internal async Task<ToolInfo> InstallFfmpegAsync(CancellationToken ct, IReadOnlyList<ToolSource>? sources = null)
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

    /// <summary>One place to get a tool from (e.g. ffmpeg + ffprobe): a short label for messages, and the archives that together hold it.</summary>
    internal sealed record ToolSource(string Label, IReadOnlyList<ToolArchive> Archives);

    /// <summary>
    /// Where this platform's static ffmpeg + ffprobe come from, in the order to try them. macOS and Linux
    /// use ffmpeg.martin-riedl.de, which has one self-contained zip per tool with a .sha256 file beside it
    /// ("latest" redirects to a versioned folder). Windows uses yt-dlp's FFmpeg-Builds (one zip, listed
    /// in checksums.sha256), then on x64 gyan.dev (one zip, bare .sha256 beside it). Empty when there is
    /// no build for the platform.
    /// </summary>
    internal static IReadOnlyList<ToolSource> FfmpegSources(ToolOptions o, OSPlatform os, Architecture arch)
    {
        if (os == OSPlatform.Windows)
        {
            var list = new List<ToolSource>();
            var flavor = arch switch { Architecture.X64 => "win64", Architecture.Arm64 => "winarm64", Architecture.X86 => "win32", _ => null };
            if (flavor is not null && !string.IsNullOrWhiteSpace(o.FfmpegWindowsReleaseBase))
            {
                var b = o.FfmpegWindowsReleaseBase.Trim().TrimEnd('/') + "/";
                var zip = $"ffmpeg-master-latest-{flavor}-gpl.zip";
                list.Add(new ToolSource(SourceLabel(b + zip), [new ToolArchive(b + zip, b + "checksums.sha256", zip)]));
            }
            if (arch == Architecture.X64 && !string.IsNullOrWhiteSpace(o.FfmpegWindowsFallbackUrl))
            {
                var url = o.FfmpegWindowsFallbackUrl.Trim();
                list.Add(new ToolSource(SourceLabel(url), [new ToolArchive(url, null, Path.GetFileName(new Uri(url).AbsolutePath))]));
            }
            return list;
        }
        var system = os == OSPlatform.OSX ? "macos" : os == OSPlatform.Linux ? "linux" : null;
        var cpu = arch switch { Architecture.X64 => "amd64", Architecture.Arm64 => "arm64", _ => null };
        if (system is null || cpu is null) return [];
        var root = $"{o.FfmpegReleaseBase.TrimEnd('/')}/{system}/{cpu}/release/";
        return [new ToolSource(SourceLabel(root), [new ToolArchive(root + "ffmpeg.zip", null, "ffmpeg.zip"), new ToolArchive(root + "ffprobe.zip", null, "ffprobe.zip")])];
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
    private async Task<string> DownloadFfmpegAsync(IReadOnlyList<ToolSource> sources, CancellationToken ct)
    {
        if (sources.Count == 0)
            throw new PlatformNotSupportedException($"No static ffmpeg build is published for {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture}).");
        var client = _http.CreateClient("tools");
        var failures = new List<string>();
        foreach (var s in sources)
        {
            try
            {
                await InstallFromAsync(client, s, OwnedFfmpegDir, [Exe("ffmpeg"), Exe("ffprobe")], ct);
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

    /// <summary>
    /// Downloads and verifies every archive of <paramref name="source"/>, extracts the <paramref name="wanted"/>
    /// files from them, and makes those files (and nothing else) the content of <paramref name="targetDir"/>.
    /// </summary>
    private async Task InstallFromAsync(HttpClient client, ToolSource source, string targetDir, string[] wanted, CancellationToken ct)
    {
        var archives = await ViaGitHubApiAsync(client, source.Archives, ct);

        // Build the folder under a temporary name and move it in at the end, so the app never
        // sees half an install.
        var staging = targetDir + ".download";
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var a in archives)
            {
                _log.LogInformation("Downloading {Tool} from {Source}: {Url}", wanted[0], source.Label, a.Url);
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
            if (Directory.Exists(targetDir)) Directory.Delete(targetDir, true);
            // On Windows, antivirus often scans a freshly written .exe for a moment, and the rename
            // fails until it lets go.
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    Directory.Move(staging, targetDir);
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
        _log.LogInformation("{Tool} saved to {Dir}", wanted[0], targetDir);
    }

    // ── GitHub release files ────────────────────────────────────────────────

    /// <summary>A file of a GitHub release; a null <paramref name="Tag"/> is the repo's latest release.</summary>
    internal sealed record GitHubFile(string Owner, string Repo, string? Tag, string Name)
    {
        public string ReleaseApiUrl => Tag is null
            ? $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest"
            : $"https://api.github.com/repos/{Owner}/{Repo}/releases/tags/{Tag}";
    }

    [GeneratedRegex(@"^https://github\.com/([^/]+)/([^/]+)/releases/(?:download/([^/]+)|latest/download)/([^/?#]+)$", RegexOptions.IgnoreCase)]
    private static partial Regex GitHubFileRx();

    /// <summary>
    /// "https://github.com/{owner}/{repo}/releases/download/{tag}/{file}" or ".../releases/latest/download/{file}"
    /// → its parts; anything else → null.
    /// </summary>
    internal static GitHubFile? GitHubReleaseFile(string? url) =>
        url is not null && GitHubFileRx().Match(url) is { Success: true } m
            ? new GitHubFile(m.Groups[1].Value, m.Groups[2].Value,
                m.Groups[3].Success ? Uri.UnescapeDataString(m.Groups[3].Value) : null, Uri.UnescapeDataString(m.Groups[4].Value))
            : null;

    /// <summary>
    /// Swaps each github.com release download URL for the same file's REST API URL. Since 2026-11-09 github.com
    /// answers /releases/download/… with 504 Gateway Time-out, first for yt-dlp/FFmpeg-Builds (thousands of
    /// daily releases) and then for yt-dlp and deno too, while the API serves the same releases. That costs
    /// one API call per release; the files themselves still come from GitHub's download CDN.
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
                : throw new InvalidOperationException($"{f.Name} is not in the {f.Owner}/{f.Repo} release \"{f.Tag ?? "latest"}\".");
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

    /// <summary>The runtimes yt-dlp supports, in its own order of preference.</summary>
    internal static readonly string[] JsRuntimes = ["deno", "node", "bun", "quickjs"];

    private string JsWanted => (_opt.Tools.JsRuntime ?? "auto").Trim().ToLowerInvariant();

    /// <summary>deno may be downloaded only when the configuration would use it.</summary>
    private bool DenoDownloadAllowed => _opt.Tools.AutoDownloadJsRuntime && JsWanted is "auto" or "deno";

    /// <summary>"C:\tools\deno.exe" → deno, "/usr/bin/nodejs" → node, "qjs" → quickjs; null when the name says nothing.</summary>
    internal static string? JsRuntimeFromFileName(string path) =>
        Path.GetFileNameWithoutExtension(path.Trim().Replace('\\', '/').Split('/')[^1]).ToLowerInvariant() switch
        {
            "deno" => "deno",
            "node" or "nodejs" => "node",
            "bun" => "bun",
            "qjs" or "quickjs" or "qjs-ng" or "quickjs-ng" => "quickjs",
            _ => null,
        };

    /// <summary>
    /// Tries, in order: Ydl:Tools:JsRuntimePath; the app's own deno ({Tools}/deno); whatever runtime is
    /// installed (deno, node ≥ 22, bun, quickjs, as yt-dlp prefers them). If none of them works, it
    /// downloads deno into the tools folder, which is the only option on a host where nothing can be
    /// installed.
    /// </summary>
    private async Task<ToolInfo> LocateJsRuntimeAsync(CancellationToken ct)
    {
        var wanted = JsWanted;
        if (wanted == "none") return new ToolInfo("none", null, null, "Disabled by configuration (Ydl:Tools:JsRuntime is \"none\").");
        if (wanted != "auto" && !JsRuntimes.Contains(wanted))
            return new ToolInfo("none", null, null, $"Ydl:Tools:JsRuntime \"{wanted}\" is not one of auto, {string.Join(", ", JsRuntimes)}, none.");

        string? problem = null;
        if (_opt.Tools.JsRuntimePath?.Trim() is { Length: > 0 } configured)
        {
            var name = wanted != "auto" ? wanted : JsRuntimeFromFileName(configured);
            if (name is null) problem = $"Can't tell which runtime {configured} is from its name; set Ydl:Tools:JsRuntime as well.";
            else if (!File.Exists(configured)) problem = $"There is no file at {configured} (Ydl:Tools:JsRuntimePath).";
            else
            {
                var info = await JsRuntimeInfoAsync(name, configured, ct);
                if (info.Ok) return info;
                problem = info.Note;
            }
            _log.LogWarning("Not using Ydl:Tools:JsRuntimePath: {Problem}", problem);
        }

        foreach (var name in wanted == "auto" ? JsRuntimes : [wanted])
        {
            string?[] candidates = name switch
            {
                "deno" => [File.Exists(OwnedDenoPath) ? OwnedDenoPath : null, Which("deno")],
                "quickjs" => [Which("qjs")],
                _ => [Which(name)],
            };
            foreach (var path in candidates.OfType<string>().Distinct())
            {
                var info = await JsRuntimeInfoAsync(name, path, ct);
                if (info.Ok) return info;
                _log.LogInformation("Skipping a JavaScript runtime: {Problem}", info.Note); // e.g. the old node a shared host ships
                problem ??= info.Note;
            }
        }

        if (!DenoDownloadAllowed)
            return new ToolInfo("none", null, null, (problem is null ? "" : problem + " ") + (_opt.Tools.AutoDownloadJsRuntime
                ? $"Install {wanted} so yt-dlp can solve YouTube's JavaScript challenges, or set Ydl:Tools:JsRuntimePath."
                : "Install deno (https://deno.com) or node >= 22 so yt-dlp can solve YouTube's JavaScript challenges, or enable Ydl:Tools:AutoDownloadJsRuntime."));

        var installed = await InstallDenoAsync(ct);
        return installed.Ok || problem is null ? installed : installed with { Note = problem + " " + installed.Note };
    }

    /// <summary>Runs the runtime's version command; a runtime that won't run, or is older than yt-dlp accepts, has no path.</summary>
    private async Task<ToolInfo> JsRuntimeInfoAsync(string name, string path, CancellationToken ct, string? note = null)
    {
        var (line, failure) = await ProbeToolAsync(path, name == "quickjs" ? ["--help"] : ["--version"], ct, anyExitCode: name == "quickjs");
        if (line is null) return new ToolInfo(name, null, null, $"{name} at {path} won't run ({failure}).");
        var m = SemverRx().Match(line);
        var version = m.Success ? m.Value : line;
        var (major, minor) = m.Success ? (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)) : (0, 0);
        // yt-dlp's minimums.
        if (name == "node" && major < 22) return new ToolInfo(name, null, version, $"node {version} at {path} is too old (yt-dlp needs node 22 or later).");
        if (name == "deno" && (major, minor) is (< 2, _) or (2, < 3)) return new ToolInfo(name, null, version, $"deno {version} at {path} is too old (yt-dlp needs deno 2.3 or later).");
        return new ToolInfo(name, path, version, note);
    }

    /// <summary>deno's target triple for a platform (its release files are deno-{target}.zip); null when deno publishes no build.</summary>
    internal static string? DenoTarget(OSPlatform os, Architecture arch)
    {
        var cpu = arch switch { Architecture.X64 => "x86_64", Architecture.Arm64 => "aarch64", _ => null };
        if (cpu is null) return null;
        if (os == OSPlatform.Windows) return $"{cpu}-pc-windows-msvc";
        if (os == OSPlatform.OSX) return $"{cpu}-apple-darwin";
        if (os == OSPlatform.Linux) return $"{cpu}-unknown-linux-gnu";
        return null;
    }

    [GeneratedRegex(@"^v\d+\.\d+\.\d+(?:-[0-9A-Za-z.]+)?$")]
    private static partial Regex DenoVersionRx();

    /// <summary>
    /// Downloads deno from <see cref="ToolOptions.DenoReleaseBase"/> (dl.deno.land, the server deno's own
    /// install scripts use, not github.com, whose release route has been timing out) into {Tools}/deno,
    /// checks its SHA-256 against the .sha256sum beside it, and checks that it runs. A 32-bit app pool on
    /// 64-bit Windows still gets the 64-bit build, because deno runs as a separate process.
    /// </summary>
    internal async Task<ToolInfo> InstallDenoAsync(CancellationToken ct)
    {
        var host = "deno's download server";
        try
        {
            var target = DenoTarget(CurrentOs, RuntimeInformation.OSArchitecture)
                ?? throw new PlatformNotSupportedException($"deno publishes no build for {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture}).");
            var b = _opt.Tools.DenoReleaseBase.Trim().TrimEnd('/') + "/";
            host = new Uri(b).Host;
            var client = _http.CreateClient("tools");

            var version = _opt.Tools.DenoVersion?.Trim() is { Length: > 0 } pin && !pin.Equals("latest", StringComparison.OrdinalIgnoreCase)
                ? (pin.StartsWith('v') ? pin : "v" + pin)
                : await ReadTextAsync(client, b + "release-latest.txt", ct);
            if (!DenoVersionRx().IsMatch(version))
                throw new InvalidOperationException($"\"{(version.Length > 40 ? version[..40] + "…" : version)}\" is not a deno version.");

            var zip = $"deno-{target}.zip";
            var url = $"{b}release/{version}/{zip}";
            await InstallFromAsync(client, new ToolSource(host, [new ToolArchive(url, url + ".sha256sum", zip)]), OwnedDenoDir, [Exe("deno")], ct);
            var info = await JsRuntimeInfoAsync("deno", OwnedDenoPath, ct, $"Downloaded deno {version} from {host} (SHA-256 verified).");
            return info.Ok ? info : info with { Name = "none", Note = "Downloaded deno, but " + info.Note };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log.LogError(ex, "Could not download deno");
            return new ToolInfo("none", null, null,
                $"The automatic deno download from {host} failed ({ex.Message}). The app keeps retrying in the background. " +
                $"You can also upload deno yourself: put {Exe("deno")} from https://github.com/denoland/deno/releases into {OwnedDenoDir}, " +
                "or set Ydl:Tools:JsRuntimePath.");
        }
    }

    private async Task<string> ReadTextAsync(HttpClient client, string url, CancellationToken ct)
    {
        using var resp = await GetAsync(client, url, ct);
        return (await resp.Content.ReadAsStringAsync(ct)).Trim();
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

    /// <summary>PATH lookup plus the usual install folders (IDE-launched apps often get a thin PATH on macOS).</summary>
    internal static string? FindOnPath(string name)
    {
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).ToList();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!IsWindows)
            dirs.AddRange(["/opt/homebrew/bin", "/usr/local/bin", "/usr/bin", "/bin"]);
        if (!string.IsNullOrEmpty(home))
            dirs.AddRange([Path.Combine(home, ".local", "bin"), Path.Combine(home, ".deno", "bin")]); // deno's installer puts it in ~/.deno/bin
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
/// Detects tools at start-up (without blocking it), keeps yt-dlp updated, and while ffmpeg or deno is
/// missing tries the download again: 10 minutes after a failure, then twice as long each time, up to 6 hours.
/// </summary>
public sealed class ToolMaintenanceService(ToolLocator tools, IOptions<YdlOptions> opt, ILogger<ToolMaintenanceService> log) : BackgroundService
{
    internal static readonly TimeSpan RetryFirst = TimeSpan.FromMinutes(10);
    internal static readonly TimeSpan RetryMax = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await tools.InitializeAsync(stoppingToken);
        var o = opt.Value.Tools;
        var interval = TimeSpan.FromHours(Math.Max(1, o.UpdateIntervalHours));
        var nextUpdate = o.AutoUpdateYtDlp ? DateTimeOffset.UtcNow : DateTimeOffset.MaxValue;
        var retryWait = RetryFirst;
        var nextRetry = DateTimeOffset.UtcNow + retryWait;

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

            var missing = tools.MissingDownloads();
            if (missing.Count > 0 && DateTimeOffset.UtcNow >= nextRetry)
            {
                log.LogInformation("Still missing: {Tools}; trying the download again", string.Join(", ", missing));
                try
                {
                    missing = await tools.RetryMissingAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogWarning(ex, "Tool download retry failed");
                }
                if (missing.Count > 0)
                {
                    retryWait = TimeSpan.FromTicks(Math.Min(retryWait.Ticks * 2, RetryMax.Ticks));
                    log.LogWarning("Still missing: {Tools}; next try in {Wait}", string.Join(", ", missing), retryWait);
                }
                nextRetry = DateTimeOffset.UtcNow + retryWait;
            }

            var next = missing.Count > 0 && nextRetry < nextUpdate ? nextRetry : nextUpdate;
            if (next == DateTimeOffset.MaxValue) return; // nothing left to do
            var wait = next - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, stoppingToken);
        }
    }
}
