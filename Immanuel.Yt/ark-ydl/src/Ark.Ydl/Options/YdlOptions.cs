namespace Ark.Ydl.Options;

/// <summary>Everything under the "Ydl" configuration section.</summary>
public sealed class YdlOptions
{
    public const string Section = "Ydl";

    /// <summary>Root for runtime data. Default: {ContentRoot}/App_Data.</summary>
    public string? DataRoot { get; set; }

    /// <summary>Where job folders are created. Default: {DataRoot}/downloads. Use fast local disk.</summary>
    public string? DownloadRoot { get; set; }

    /// <summary>yt-dlp processes downloading at the same time; the rest wait in the queue.</summary>
    public int MaxConcurrentJobs { get; set; } = 2;

    /// <summary>yt-dlp metadata lookups running at the same time.</summary>
    public int MaxConcurrentProbes { get; set; } = 4;

    /// <summary>Queued + running jobs one client (IP, or signed-in user) may have.</summary>
    public int MaxActiveJobsPerClient { get; set; } = 4;

    /// <summary>How long finished files stay downloadable before they are deleted.</summary>
    public int RetentionMinutes { get; set; } = 120;

    /// <summary>Upper bound on playlist entries listed and downloaded in one go.</summary>
    public int MaxPlaylistItems { get; set; } = 100;

    /// <summary>Upper bound on time ranges / chapters in one clip job.</summary>
    public int MaxClipSections { get; set; } = 10;

    /// <summary>Refuse whole-video downloads longer than this. 0 = no limit.</summary>
    public int MaxDurationMinutes { get; set; }

    /// <summary>Passed to --max-filesize, e.g. "4G". Empty = no limit.</summary>
    public string? MaxFileSize { get; set; }

    /// <summary>--concurrent-fragments for DASH/HLS downloads.</summary>
    public int ConcurrentFragments { get; set; } = 4;

    /// <summary>Probe results are cached this long (a lookup costs several seconds).</summary>
    public int ProbeCacheMinutes { get; set; } = 15;

    /// <summary>Hosts (and their sub-domains) users may paste. Empty = the YouTube defaults.</summary>
    public List<string> AllowedSites { get; set; } = [];

    /// <summary>Accept any site yt-dlp supports, not only <see cref="AllowedSites"/>. Off by default (SSRF surface).</summary>
    public bool AllowAnySite { get; set; }

    /// <summary>Netscape cookies.txt passed with --cookies; needed for age-restricted / members videos or bot checks.</summary>
    public string? CookiesFile { get; set; }

    /// <summary>Passed with --proxy (e.g. a residential proxy when the server IP gets bot-checked).</summary>
    public string? Proxy { get; set; }

    /// <summary>Admin-only extra arguments appended to every yt-dlp call (e.g. --extractor-args).</summary>
    public List<string> ExtraArgs { get; set; } = [];

    public RateLimitOptions RateLimit { get; set; } = new();

    public ToolOptions Tools { get; set; } = new();

    public static readonly string[] DefaultSites = ["youtube.com", "youtu.be", "youtube-nocookie.com"];
}

public sealed class RateLimitOptions
{
    public int ProbesPerMinute { get; set; } = 30;
    public int JobsPerMinute { get; set; } = 20;
}

public sealed class ToolOptions
{
    /// <summary>Folder for tools this app downloads and owns. Default: {DataRoot}/tools.</summary>
    public string? Directory { get; set; }

    /// <summary>Explicit yt-dlp path. Default: the tools folder, then PATH, then download.</summary>
    public string? YtDlpPath { get; set; }

    /// <summary>Explicit ffmpeg path (ffprobe is expected next to it). Default: {Tools}/ffmpeg, then PATH, then download.</summary>
    public string? FfmpegPath { get; set; }

    /// <summary>
    /// When no working ffmpeg is found, download a self-contained static ffmpeg + ffprobe
    /// (SHA-256 verified) into {Tools}/ffmpeg.
    /// </summary>
    public bool AutoDownloadFfmpeg { get; set; } = true;

    /// <summary>macOS / Linux static builds: {base}{macos|linux}/{amd64|arm64}/release/{ffmpeg,ffprobe}.zip, each with a .sha256.</summary>
    public string FfmpegReleaseBase { get; set; } = "https://ffmpeg.martin-riedl.de/redirect/latest/";

    /// <summary>
    /// Windows static builds (yt-dlp/FFmpeg-Builds): ffmpeg-master-latest-{win64|winarm64|win32}-gpl.zip + checksums.sha256.
    /// A github.com/{owner}/{repo}/releases/download/{tag}/ base is fetched through the GitHub REST API instead,
    /// because github.com answers that route with 504 for this repo. Empty = skip this source.
    /// </summary>
    public string? FfmpegWindowsReleaseBase { get; set; } = "https://github.com/yt-dlp/FFmpeg-Builds/releases/download/latest/";

    /// <summary>
    /// Tried on Windows x64 when the source above fails: a zip holding ffmpeg.exe + ffprobe.exe, with the
    /// .sha256 beside the file it redirects to (gyan.dev, linked from ffmpeg.org). Empty = no fallback.
    /// </summary>
    public string? FfmpegWindowsFallbackUrl { get; set; } = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";

    /// <summary>auto | deno | node | bun | quickjs | none. YouTube needs one since yt-dlp 2025.11.</summary>
    public string JsRuntime { get; set; } = "auto";

    /// <summary>Explicit path to the JS runtime binary.</summary>
    public string? JsRuntimePath { get; set; }

    /// <summary>Download the official yt-dlp release (SHA-256 verified) when none is found.</summary>
    public bool AutoDownloadYtDlp { get; set; } = true;

    /// <summary>Run "yt-dlp -U" on start and every <see cref="UpdateIntervalHours"/> when the binary is ours.</summary>
    public bool AutoUpdateYtDlp { get; set; } = true;

    public int UpdateIntervalHours { get; set; } = 24;

    public string YtDlpReleaseBase { get; set; } = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/";
}

/// <summary>Resolved absolute folders, created at start-up.</summary>
public sealed class YdlPaths
{
    public YdlPaths(YdlOptions o, string contentRoot)
    {
        DataRoot = Path.GetFullPath(o.DataRoot ?? Path.Combine(contentRoot, "App_Data"));
        Downloads = Path.GetFullPath(o.DownloadRoot ?? Path.Combine(DataRoot, "downloads"));
        Tools = Path.GetFullPath(o.Tools.Directory ?? Path.Combine(DataRoot, "tools"));
        Cache = Path.Combine(DataRoot, "cache");
        foreach (var d in new[] { DataRoot, Downloads, Tools, Cache })
            System.IO.Directory.CreateDirectory(d);
    }

    public string DataRoot { get; }
    public string Downloads { get; }
    public string Tools { get; }
    public string Cache { get; }
}
