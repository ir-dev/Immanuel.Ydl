namespace Ark.Feedback.Options;

/// <summary>"Feedback" section of appsettings.json: everything an operator may tune.</summary>
public sealed class FeedbackOptions
{
    public const string Section = "Feedback";

    /// <summary>Where runtime data lives (models, tools, reports, audit databases). Default: {ContentRoot}/App_Data.</summary>
    public string? DataRoot { get; set; }

    /// <summary>Public name shown in the UI and in audit rows.</summary>
    public string AppName { get; set; } = "ARK-RAPID";

    public AnalysisOptions Analysis { get; set; } = new();
    public YouTubeOptions YouTube { get; set; } = new();
    public ToolOptions Tools { get; set; } = new();
    public AuditOptions Audit { get; set; } = new();
    public AdminOptions Admin { get; set; } = new();
    public RateLimitOptions RateLimit { get; set; } = new();
}

/// <summary>How much CPU an analysis may take, and which models do the work.</summary>
public sealed class AnalysisOptions
{
    /// <summary>fast | balanced | thorough — the profile used when a request names none.</summary>
    public string DefaultProfile { get; set; } = "balanced";

    /// <summary>Profiles a visitor may pick. Remove "thorough" on a small host.</summary>
    public List<string> AllowedProfiles { get; set; } = [];

    /// <summary>Analyses running at the same time. 1 on shared hosting: every analysis saturates the inference threads.</summary>
    public int MaxConcurrentAnalyses { get; set; } = 1;

    /// <summary>Queued + running analyses the server accepts before answering 429.</summary>
    public int MaxQueued { get; set; } = 12;

    /// <summary>Threads ONNX Runtime may use inside one operator. 0 = half the cores, between 1 and 4.</summary>
    public int InferenceThreads { get; set; }

    /// <summary>Upper bound on comments fetched from YouTube, whatever the profile asks for.</summary>
    public int MaxComments { get; set; } = 800;

    /// <summary>Replies fetched per top-level comment (yt-dlp max-replies-per-thread). 0 = top-level comments only.</summary>
    public int RepliesPerThread { get; set; } = 3;

    /// <summary>Seconds of model time one analysis may spend. When the fetched comments would take longer, a sample is scored instead.</summary>
    public int TimeBudgetSeconds { get; set; } = 90;

    /// <summary>Tokens per comment fed to the models (longer comments are cut; 128 covers almost every YouTube comment).</summary>
    public int MaxSequenceTokens { get; set; } = 128;

    /// <summary>Comments per forward pass.</summary>
    public int BatchSize { get; set; } = 16;

    /// <summary>Minutes of silence after which loaded models are released, giving the memory back to the host.</summary>
    public int UnloadIdleMinutes { get; set; } = 20;

    /// <summary>A report for the same video and profile younger than this is served from disk instead of recomputed.</summary>
    public int CacheHours { get; set; } = 6;

    /// <summary>Days a stored report stays on disk (0 = forever).</summary>
    public int ReportRetentionDays { get; set; } = 365;

    /// <summary>Download models that are missing at start-up (and keep retrying) instead of only reporting them missing.</summary>
    public bool AutoDownloadModels { get; set; } = true;

    /// <summary>Where model files come from: a Hugging Face compatible host. Files are SHA-256 verified either way.</summary>
    public string ModelHost { get; set; } = "https://huggingface.co/";

    /// <summary>Per-role overrides of the model catalog (repo / file / enabled), keyed by role: sentiment, sentimentFast, toxicity, emotion, embeddings.</summary>
    public Dictionary<string, ModelOverride> Models { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Also keep every scored comment in the audit log (text included). Off = only the per-analysis summary row.</summary>
    public bool StoreComments { get; set; } = true;

    /// <summary>Show the latest reports on the home page (titles and verdicts of public videos). Off on a private deployment.</summary>
    public bool ShowRecentReports { get; set; } = true;

    /// <summary>Comments per client key that may be queued or running at once.</summary>
    public int MaxActivePerClient { get; set; } = 1;
}

public sealed class ModelOverride
{
    public bool? Enabled { get; set; }
    public string? Repo { get; set; }
    public string? Revision { get; set; }
    public string? File { get; set; }
    public string? Sha256 { get; set; }
}

public sealed class YouTubeOptions
{
    /// <summary>A YouTube Data API v3 key. When set, metadata and comments come from the official API (fast, exact totals) and yt-dlp is the fallback.</summary>
    public string? ApiKey { get; set; }

    /// <summary>ytdlp | api | auto. auto = api when a key is configured, else yt-dlp.</summary>
    public string Source { get; set; } = "auto";

    /// <summary>Extra extractor arguments appended to yt-dlp's "youtube:" extractor args (advanced).</summary>
    public string? ExtraExtractorArgs { get; set; }

    /// <summary>Netscape cookies file for yt-dlp (age-restricted or member videos).</summary>
    public string? CookiesFile { get; set; }

    public string? Proxy { get; set; }

    /// <summary>Seconds a yt-dlp run may take before it is killed.</summary>
    public int FetchTimeoutSeconds { get; set; } = 600;
}

public sealed class ToolOptions
{
    /// <summary>Folder for downloaded tools. Default: {DataRoot}/tools.</summary>
    public string? Directory { get; set; }

    /// <summary>A yt-dlp to use instead of PATH / the app's own copy.</summary>
    public string? YtDlpPath { get; set; }

    public bool AutoDownloadYtDlp { get; set; } = true;

    public bool AutoUpdateYtDlp { get; set; } = true;

    public int UpdateIntervalHours { get; set; } = 24;

    public string YtDlpReleaseBase { get; set; } = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/";

    /// <summary>deno | node | bun | quickjs executable, handed to yt-dlp as --js-runtimes. Comments don't need one; it silences a warning and completes format data.</summary>
    public string? JsRuntimePath { get; set; }
}

public enum AuditIpMode { Full, Masked, Hashed }

public sealed class AuditOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>One row per HTTP request (pages, API calls). Off = only analyses, comments and feedback.</summary>
    public bool LogRequests { get; set; } = true;

    public bool IncludeStaticFiles { get; set; }

    /// <summary>Paths never written to the request log. Defaults come from appsettings.json (binding appends to non-empty lists).</summary>
    public List<string> ExcludePaths { get; set; } = [];

    /// <summary>Whole monthly database files older than this are deleted (0 = keep forever).</summary>
    public int RetentionMonths { get; set; } = 24;

    public AuditIpMode IpMode { get; set; } = AuditIpMode.Full;

    /// <summary>The time zone that decides which day (table) and month (file) an event belongs to.</summary>
    public string TimeZone { get; set; } = "UTC";

    /// <summary>A header a proxy fills with the visitor's country code (e.g. CF-IPCountry); skips the GeoIP lookup.</summary>
    public string? CountryHeader { get; set; }

    public int QueueCapacity { get; set; } = 20000;

    /// <summary>Prefix of the monthly files: {Prefix}-{yyyy-MM}.db.</summary>
    public string FilePrefix { get; set; } = "ark-feedback-audit";

    public GeoIpOptions GeoIp { get; set; } = new();
}

public sealed class GeoIpOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>country (about 10 MB) or city (about 60 MB) edition of DB-IP lite.</summary>
    public string Edition { get; set; } = "country";

    public bool AutoDownload { get; set; } = true;

    public string? DatabasePath { get; set; }

    public string ReleaseBase { get; set; } = "https://download.db-ip.com/free/";
}

public sealed class AdminOptions
{
    /// <summary>Key for the admin console (16+ characters). Unset = console reachable only with an Ark role, or from localhost in Development.</summary>
    public string? Key { get; set; }

    /// <summary>Ark roles that open the console when sign-in is on. Defaults come from appsettings.json.</summary>
    public List<string> Roles { get; set; } = [];

    public int SessionHours { get; set; } = 12;

    public bool AllowLoopbackInDevelopment { get; set; } = true;
}

public sealed class RateLimitOptions
{
    /// <summary>New analyses one client may start per 10 minutes.</summary>
    public int AnalysesPer10Minutes { get; set; } = 6;

    /// <summary>API calls (status polls excluded) per client per minute.</summary>
    public int ApiPerMinute { get; set; } = 120;
}

/// <summary>Resolved folders. Created on first use where the app writes.</summary>
public sealed class FeedbackPaths
{
    public FeedbackPaths(FeedbackOptions o, string contentRoot)
    {
        DataRoot = Path.GetFullPath(o.DataRoot ?? Path.Combine(contentRoot, "App_Data"));
        Tools = Path.GetFullPath(o.Tools.Directory ?? Path.Combine(DataRoot, "tools"));
        Models = Path.Combine(DataRoot, "models");
        Reports = Path.Combine(DataRoot, "reports");
        Cache = Path.Combine(DataRoot, "cache");
        Audit = Path.Combine(DataRoot, "audit");
        GeoIp = Path.Combine(DataRoot, "geoip");
        foreach (var d in new[] { DataRoot, Tools, Models, Reports, Cache })
            Directory.CreateDirectory(d);
    }

    public string DataRoot { get; }
    public string Tools { get; }
    public string Models { get; }
    public string Reports { get; }
    public string Cache { get; }
    public string Audit { get; }
    public string GeoIp { get; }
}
