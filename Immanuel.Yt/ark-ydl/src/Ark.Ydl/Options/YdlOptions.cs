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

    public AuditOptions Audit { get; set; } = new();

    public AdminOptions Admin { get; set; } = new();

    public ReleaseOptions Release { get; set; } = new();

    public static readonly string[] DefaultSites = ["youtube.com", "youtu.be", "youtube-nocookie.com"];
}

public enum AuditIpMode { Full, Masked, Hashed }

/// <summary>Ydl:Audit — the request / download log kept in a local SQLite file.</summary>
public sealed class AuditOptions
{
    /// <summary>Master switch. Off = nothing is written and the admin console shows no data.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Log every HTTP request (pages and API calls), not only downloads.</summary>
    public bool LogRequests { get; set; } = true;

    /// <summary>Also log scripts, styles, images and icons. Off by default: a page load fetches a dozen of them.</summary>
    public bool IncludeStaticFiles { get; set; }

    /// <summary>
    /// Paths never logged (prefix match). appsettings.json lists /healthz, whose probes would otherwise drown everything
    /// else. Empty here because configuration binding appends to a list that already has items.
    /// </summary>
    public List<string> ExcludePaths { get; set; } = [];

    /// <summary>Raw rows older than this are deleted; the per-day totals stay. 0 = keep everything.</summary>
    public int RetentionDays { get; set; } = 365;

    /// <summary>Full = as received; Masked = last IPv4 octet / last 80 IPv6 bits zeroed; Hashed = salted SHA-256.</summary>
    public AuditIpMode IpMode { get; set; } = AuditIpMode.Full;

    /// <summary>Time zone (IANA or Windows id) that decides which day and hour a row belongs to. Default UTC.</summary>
    public string TimeZone { get; set; } = "UTC";

    /// <summary>Have yt-dlp append each downloaded item's metadata (genre, channel, duration…) to a file in the job folder.</summary>
    public bool CaptureItemMetadata { get; set; } = true;

    /// <summary>A header a trusted CDN sets with the visitor's country code (e.g. CF-IPCountry). Used before GeoIP.</summary>
    public string? CountryHeader { get; set; }

    /// <summary>Rows waiting to be written. When full, new rows are dropped (and counted) rather than slowing requests down.</summary>
    public int QueueCapacity { get; set; } = 20000;

    public GeoIpOptions GeoIp { get; set; } = new();
}

/// <summary>Ydl:Audit:GeoIp — IP to country / region / city, from the free DB-IP lite database (CC BY 4.0).</summary>
public sealed class GeoIpOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>city (region + city, about 60 MB download / 130 MB on disk) or country (about 4 MB / 8 MB).</summary>
    public string Edition { get; set; } = "city";

    /// <summary>Download the database into {DataRoot}/geoip and refresh it monthly.</summary>
    public bool AutoDownload { get; set; } = true;

    /// <summary>An .mmdb of your own (DB-IP or MaxMind GeoLite2 layout). When set, nothing is downloaded.</summary>
    public string? DatabasePath { get; set; }

    /// <summary>{base}dbip-{edition}-lite-{yyyy-MM}.mmdb.gz</summary>
    public string ReleaseBase { get; set; } = "https://download.db-ip.com/free/";
}

/// <summary>Ydl:Admin — who may open /admin.</summary>
public sealed class AdminOptions
{
    /// <summary>Shared secret typed into the admin console (16+ characters). Null = key sign-in off.</summary>
    public string? Key { get; set; }

    /// <summary>
    /// With Ark sign-in on, any of these Ark claims (projected as roles) opens the console without the key.
    /// appsettings.json lists ark-ydl.admin; empty here because configuration binding appends to a non-empty list.
    /// </summary>
    public List<string> Roles { get; set; } = [];

    /// <summary>How long a key sign-in lasts.</summary>
    public int SessionHours { get; set; } = 12;

    /// <summary>In the Development environment, a request from this machine needs no key when none is configured.</summary>
    public bool AllowLoopbackInDevelopment { get; set; } = true;
}

/// <summary>Ydl:Release — where the "latest release" shown in the app comes from.</summary>
public sealed class ReleaseOptions
{
    /// <summary>
    /// A releases.json published with each release; its "current" is the latest release. Default: this file on
    /// the repository's master branch. Empty = only the release manifest built into this app.
    /// </summary>
    public string? FeedUrl { get; set; } =
        "https://raw.githubusercontent.com/ir-dev/Immanuel.Ydl/master/Immanuel.Yt/ark-ydl/src/Ark.Ydl/releases.json";

    public int CheckIntervalHours { get; set; } = 12;
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

    /// <summary>
    /// Explicit path to the JS runtime binary, tried first. With JsRuntime "auto" the runtime is told
    /// by the file name (deno.exe → deno, node → node, qjs → quickjs).
    /// </summary>
    public string? JsRuntimePath { get; set; }

    /// <summary>
    /// When no JS runtime is found (and JsRuntime is auto or deno), download deno (one self-contained
    /// file, SHA-256 verified) into {Tools}/deno. It needs no install and no admin rights, so it also
    /// works on shared hosting.
    /// </summary>
    public bool AutoDownloadJsRuntime { get; set; } = true;

    /// <summary>
    /// deno's own download server (what its install scripts use): {base}release-latest.txt names the newest
    /// version, and {base}release/{version}/deno-{target}.zip has a .zip.sha256sum beside it.
    /// </summary>
    public string DenoReleaseBase { get; set; } = "https://dl.deno.land/";

    /// <summary>"latest", or a version to pin, e.g. "2.9.8".</summary>
    public string? DenoVersion { get; set; } = "latest";

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
        Audit = Path.Combine(DataRoot, "audit");
        GeoIp = Path.Combine(DataRoot, "geoip");
        foreach (var d in new[] { DataRoot, Downloads, Tools, Cache })
            System.IO.Directory.CreateDirectory(d);
    }

    public string DataRoot { get; }
    public string Downloads { get; }
    public string Tools { get; }
    public string Cache { get; }
    /// <summary>The audit database (created on first use).</summary>
    public string Audit { get; }
    /// <summary>The downloaded GeoIP database (created on first use).</summary>
    public string GeoIp { get; }
}
