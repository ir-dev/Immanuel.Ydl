using System.Globalization;
using System.Net;
using System.Text.Json;
using Ark.Feedback.Analysis;
using Ark.Feedback.Options;
using Microsoft.Extensions.Options;

namespace Ark.Feedback.Audit;

/// <summary>
/// Turns requests, analysis jobs, reports and feedback into audit rows and hands them to the <see cref="AuditWriter"/>.
/// Everything here is cheap and synchronous; nothing here ever throws into a request.
/// </summary>
public sealed class AuditRecorder
{
    private const string ClientItem = "audit.client";
    private const string NoteItem = "audit.note";

    private readonly FeedbackOptions _o;
    private readonly AuditWriter _writer;
    private readonly GeoIpService _geo;
    private readonly FeedbackPaths _paths;
    private readonly ILogger<AuditRecorder> _log;
    private readonly Lazy<string> _salt;

    public AuditRecorder(IOptions<FeedbackOptions> opt, AuditWriter writer, GeoIpService geo, AuditClock clock, FeedbackPaths paths, ILogger<AuditRecorder> log)
    {
        _o = opt.Value;
        _writer = writer;
        _geo = geo;
        _paths = paths;
        _log = log;
        Clock = clock;
        if (clock.Note is not null) log.LogWarning("{Note}", clock.Note);
        _salt = new Lazy<string>(LoadSalt);
    }

    public bool Enabled => _o.Audit.Enabled;

    public AuditClock Clock { get; }

    public static string AppVersion => typeof(AuditRecorder).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    // ── who is asking ────────────────────────────────────────────────────────

    public ClientInfo Client(HttpContext ctx)
    {
        if (ctx.Items.TryGetValue(ClientItem, out var cached) && cached is ClientInfo c) return c;
        c = Resolve(ctx);
        ctx.Items[ClientItem] = c;
        return c;
    }

    private ClientInfo Resolve(HttpContext ctx)
    {
        var h = ctx.Request.Headers;
        var ip = ctx.Connection.RemoteIpAddress;
        GeoResult? geo = null;
        var headerCountry = string.IsNullOrWhiteSpace(_o.Audit.CountryHeader) ? null : h[_o.Audit.CountryHeader].ToString().Trim();
        if (headerCountry is { Length: 2 } && char.IsAsciiLetter(headerCountry[0]) && char.IsAsciiLetter(headerCountry[1]))
            geo = new GeoResult(headerCountry.ToUpperInvariant(), CountryName(headerCountry), null, null);
        else if (ip is not null)
            geo = _geo.Lookup(ip);
        if (geo is null && ip is not null && IpTools.IsPrivate(ip))
            geo = new GeoResult(null, "Local network", null, null);

        var (userId, userName) = ClientIdentity.User(ctx.User);
        var ua = ClientIdentity.Clip(h.UserAgent.ToString(), 512);
        var accept = ClientIdentity.Clip(h.AcceptLanguage.ToString(), 200);
        var (tz, screen, pwa, version) = ClientHeader.Parse(h[ClientHeader.Name].ToString());
        var stored = StoreIp(ip);
        var key = ClientIdentity.ClientKey(ctx);
        if (key.StartsWith("ip:", StringComparison.Ordinal)) key = "ip:" + (stored ?? "unknown");

        return new ClientInfo
        {
            Ip = stored,
            Country = geo?.Country,
            CountryName = geo?.CountryName,
            Region = geo?.Region,
            City = geo?.City,
            UserId = userId,
            UserName = userName,
            ClientKey = key,
            UserAgent = ua,
            Agent = UserAgentParser.Parse(ua, h["Sec-CH-UA"].ToString(), h["Sec-CH-UA-Mobile"].ToString(), h["Sec-CH-UA-Platform"].ToString()),
            Language = ClientIdentity.PrimaryLanguage(accept),
            AcceptLanguage = accept,
            Referrer = ClientIdentity.Referrer(h.Referer.ToString()),
            TimeZone = tz,
            Screen = screen,
            Pwa = pwa,
            ClientVersion = version,
        };
    }

    internal string? StoreIp(IPAddress? ip)
    {
        if (ip is null) return null;
        return _o.Audit.IpMode switch
        {
            AuditIpMode.Masked => IpTools.Mask(ip),
            AuditIpMode.Hashed => IpTools.Hash(ip, _salt.Value),
            _ => IpTools.Normalize(ip).ToString(),
        };
    }

    private static string? CountryName(string code)
    {
        try { return new RegionInfo(code).EnglishName; }
        catch (ArgumentException) { return null; }
    }

    private string LoadSalt()
    {
        var file = Path.Combine(_paths.Audit, "ip-salt.txt");
        try
        {
            if (File.Exists(file))
            {
                var s = File.ReadAllText(file).Trim();
                if (s.Length >= 16) return s;
            }
            Directory.CreateDirectory(_paths.Audit);
            var salt = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));
            File.WriteAllText(file, salt);
            return salt;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Couldn't keep the IP hash salt in {File}; hashes will change on restart", file);
            return Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));
        }
    }

    // ── requests ─────────────────────────────────────────────────────────────

    private sealed class Note
    {
        public string? VideoId;
        public string? AnalysisId;
        public string? Detail;
    }

    public static void Annotate(HttpContext ctx, string? videoId = null, string? analysisId = null, string? detail = null)
    {
        if (ctx.Items[NoteItem] is not Note n) ctx.Items[NoteItem] = n = new Note();
        if (videoId is not null) n.VideoId = videoId;
        if (analysisId is not null) n.AnalysisId = analysisId;
        if (detail is not null) n.Detail = detail;
    }

    public void Request(HttpContext ctx, long elapsedMs)
    {
        if (!_o.Audit.Enabled || !_o.Audit.LogRequests) return;
        try
        {
            var path = ctx.Request.Path.Value ?? "/";
            if (_o.Audit.ExcludePaths.Any(p => !string.IsNullOrEmpty(p) && path.StartsWith(p, StringComparison.OrdinalIgnoreCase))) return;
            var kind = Kind(ctx.Request.Method, path);
            if (kind == "static" && !_o.Audit.IncludeStaticFiles) return;

            var now = DateTime.UtcNow;
            var (day, hour, weekday) = Clock.Bucket(now);
            var note = ctx.Items[NoteItem] as Note;
            var analysisId = note?.AnalysisId ?? (ctx.Request.RouteValues["id"] as string is { } rid && ReportStore.IsValidId(rid) ? rid : null);
            var row = new Dictionary<string, object?>
            {
                ["ts_utc"] = AuditClock.Format(now),
                ["day"] = day,
                ["hour"] = hour,
                ["weekday"] = weekday,
                ["method"] = ctx.Request.Method,
                ["path"] = ClientIdentity.Clip(path, 300),
                ["query"] = ClientIdentity.Clip(ctx.Request.QueryString.Value, 600),
                ["kind"] = kind,
                ["status"] = ctx.Response.StatusCode,
                ["duration_ms"] = elapsedMs,
                ["bytes"] = ctx.Response.ContentLength,
                ["analysis_id"] = analysisId,
                ["video_id"] = note?.VideoId,
                ["detail"] = ClientIdentity.Clip(note?.Detail, 300),
                ["app_version"] = AppVersion,
            };
            Client(ctx).WriteTo(row);
            _writer.Enqueue(new InsertOp(AuditClock.Day8OfDay(day), AuditTable.Requests, row));
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Request audit skipped");
        }
    }

    private static readonly HashSet<string> StaticExt =
        [".js", ".mjs", ".css", ".svg", ".png", ".jpg", ".jpeg", ".gif", ".webp", ".ico", ".webmanifest", ".woff", ".woff2", ".map", ".txt", ".xml"];

    public static string Kind(string method, string path)
    {
        var p = path.ToLowerInvariant();
        if (p.StartsWith("/api/admin")) return "admin";
        if (p == "/api/analyses" && method == "POST") return "analyse";
        if (p.StartsWith("/api/analyses/")) return p.EndsWith("/events") ? "events" : "job-status";
        if (p.StartsWith("/api/reports/"))
        {
            if (p.EndsWith("/feedback")) return "feedback";
            if (p.EndsWith(".csv") || p.EndsWith("/csv")) return "export";
            return "report";
        }
        if (p == "/api/reports") return "recent";
        if (p == "/api/system") return "system";
        if (p.StartsWith("/api/")) return "api";
        if (p.StartsWith("/signin-oidc") || p.StartsWith("/signout") || p.StartsWith("/auth/") || p.StartsWith("/ark/")) return "auth";
        if (p == "/healthz") return "health";
        if (p is "/" or "/index.html") return "page";
        if (p.StartsWith("/r/")) return "report-page";
        if (p is "/admin" or "/admin/") return "admin-page";
        if (StaticExt.Contains(Path.GetExtension(p))) return "static";
        return "other";
    }

    // ── analyses ─────────────────────────────────────────────────────────────

    private static string Day8Of(string analysisId) => analysisId[..8];

    public void Attach(AnalysisManager manager)
    {
        if (!_o.Audit.Enabled) return;
        manager.JobCreated += JobCreated;
        manager.JobCompleted += JobCompleted;
    }

    private void JobCreated(AnalysisJob job)
    {
        try
        {
            var created = job.CreatedAt.UtcDateTime;
            var (day, hour, weekday) = Clock.Bucket(created);
            var row = new Dictionary<string, object?>
            {
                ["analysis_id"] = job.Id,
                ["day"] = day,
                ["hour"] = hour,
                ["weekday"] = weekday,
                ["created_utc"] = created,
                ["status"] = "queued",
                ["profile"] = job.Profile.Id,
                ["fresh"] = job.Fresh,
                ["cached"] = 0,
                ["url"] = job.Url.Canonical,
                ["video_id"] = job.Url.VideoId,
                ["app_version"] = AppVersion,
                ["updated_utc"] = created,
            };
            job.Client.WriteTo(row);
            _writer.Enqueue(new InsertOp(Day8Of(job.Id), AuditTable.Analyses, row));
            job.Changed += JobChanged;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Couldn't audit analysis {Id}", job.Id);
        }
    }

    private void JobChanged(AnalysisJob job)
    {
        if (job.Current.State is JobState.Completed) return; // the completed row is written with the report
        _writer.Enqueue(new UpdateOp(Day8Of(job.Id), AuditTable.Analyses, "analysis_id", job.Id, () => StateRow(job)));
    }

    private static Dictionary<string, object?> StateRow(AnalysisJob job)
    {
        var c = job.Current;
        var row = new Dictionary<string, object?>
        {
            ["status"] = c.State.ToString().ToLowerInvariant(),
            ["started_utc"] = job.StartedAt,
            ["finished_utc"] = job.FinishedAt,
            ["queue_ms"] = job.StartedAt is { } s ? (long)(s - job.CreatedAt).TotalMilliseconds : null,
            ["run_ms"] = job.StartedAt is { } s2 && job.FinishedAt is { } f ? (long)(f - s2).TotalMilliseconds : null,
            ["error"] = c.Error,
            ["error_detail"] = ClientIdentity.Clip(c.ErrorDetail, 2000),
            ["fetched"] = c.Fetched,
            ["updated_utc"] = DateTime.UtcNow,
        };
        return row;
    }

    private void JobCompleted(AnalysisJob job, Report r)
    {
        try
        {
            _writer.Enqueue(new UpdateOp(Day8Of(job.Id), AuditTable.Analyses, "analysis_id", job.Id, () => ReportRow(job, r)));
            if (_o.Analysis.StoreComments)
            {
                var now = DateTime.UtcNow;
                foreach (var c in r.Comments)
                    _writer.Enqueue(new InsertOp(Day8Of(job.Id), AuditTable.Comments, CommentRow(job.Id, r.Video.Id, c, now)));
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Couldn't audit report {Id}", job.Id);
        }
    }

    internal static Dictionary<string, object?> ReportRow(AnalysisJob job, Report r)
    {
        var row = StateRow(job);
        row["status"] = "completed";
        var s = r.Sentiment;
        var top = r.Comments.Where(c => c.DuplicateOf is null).OrderByDescending(c => c.Likes).FirstOrDefault();
        var pinned = r.Threads.PinnedId is { } pid ? r.Comments.FirstOrDefault(c => c.Id == pid) : null;
        var primary = r.Languages.FirstOrDefault(l => l.Key != "und") ?? r.Languages.FirstOrDefault();
        foreach (var (k, v) in new Dictionary<string, object?>
                 {
                     ["source"] = r.Fetch.Source, ["tool_version"] = r.Fetch.ToolVersion, ["fetch_ms"] = r.Compute.FetchMs, ["infer_ms"] = r.Compute.InferMs,
                     ["title"] = ClientIdentity.Clip(r.Video.Title, 500), ["channel"] = ClientIdentity.Clip(r.Video.Channel, 200), ["channel_id"] = r.Video.ChannelId,
                     ["channel_followers"] = r.Video.ChannelFollowers, ["duration_sec"] = r.Video.DurationSeconds, ["view_count"] = r.Video.ViewCount, ["like_count"] = r.Video.LikeCount,
                     ["comment_count_total"] = r.Video.CommentCount, ["upload_date"] = r.Video.UploadedAt?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                     ["category"] = r.Video.Category, ["tags"] = r.Video.Tags.Count > 0 ? JsonSerializer.Serialize(r.Video.Tags) : null, ["video_language"] = r.Video.Language, ["is_short"] = r.Video.IsShort,
                     ["fetched"] = r.Fetch.Fetched, ["analysed"] = r.Fetch.Analysed, ["unique_count"] = r.Fetch.Unique, ["repeats"] = r.Fetch.ExactRepeats, ["top_level"] = r.Fetch.TopLevel,
                     ["replies"] = r.Fetch.Replies, ["sampled"] = r.Fetch.Sampled, ["sample_note"] = r.Fetch.SampleNote,
                     ["positive"] = s.Positive, ["neutral"] = s.Neutral, ["negative"] = s.Negative, ["positive_pct"] = s.PositivePct, ["neutral_pct"] = s.NeutralPct, ["negative_pct"] = s.NegativePct,
                     ["score"] = s.Score, ["weighted_score"] = s.WeightedScore, ["mean_score"] = s.MeanScore, ["polarization"] = s.Polarization, ["confidence"] = s.MeanConfidence, ["verdict"] = s.Verdict,
                     ["language_count"] = r.Languages.Count(l => l.Key != "und"), ["primary_language"] = primary?.Key, ["primary_language_pct"] = primary?.Pct,
                     ["languages"] = JsonSerializer.Serialize(r.Languages.Take(8).Select(l => new { l.Key, l.Count, l.Score })),
                     ["themes"] = r.Themes.Count > 0 ? JsonSerializer.Serialize(r.Themes.Select(t => new { t.Label, t.Count, t.Score })) : null,
                     ["keywords_positive"] = r.PositiveKeywords.Count > 0 ? JsonSerializer.Serialize(r.PositiveKeywords.Take(10).Select(k => k.Key)) : null,
                     ["keywords_negative"] = r.NegativeKeywords.Count > 0 ? JsonSerializer.Serialize(r.NegativeKeywords.Take(10).Select(k => k.Key)) : null,
                     ["emoji"] = r.Emoji.Count > 0 ? JsonSerializer.Serialize(r.Emoji.Take(8).Select(e => new { e.Key, e.Count })) : null,
                     ["toxic_count"] = r.Toxicity?.Flagged, ["toxic_pct"] = r.Toxicity?.Pct,
                     ["top_emotion"] = r.Emotions?.FirstOrDefault(e => e.Key != "neutral")?.Key,
                     ["emotions"] = r.Emotions is { Count: > 0 } em ? JsonSerializer.Serialize(em.Take(8).Select(e => new { e.Key, e.Count })) : null,
                     ["uploader_comments"] = r.Threads.UploaderComments, ["hearted"] = r.Threads.Hearted, ["pinned_text"] = ClientIdentity.Clip(pinned?.Text, 300),
                     ["top_comment"] = ClientIdentity.Clip(top?.Text, 300), ["top_comment_likes"] = top?.Likes,
                     ["insights"] = JsonSerializer.Serialize(r.Insights.Select(i => i.Title)),
                     ["models"] = JsonSerializer.Serialize(r.Models.Select(m => new { m.Role, m.Slug, m.Items, m.Ms })),
                     ["threads"] = r.Compute.Threads, ["ms_per_comment"] = r.Compute.MsPerComment, ["app_version"] = r.AppVersion,
                 })
            row[k] = v;
        return row;
    }

    internal static Dictionary<string, object?> CommentRow(string analysisId, string videoId, ScoredComment c, DateTime now) => new()
    {
        ["analysis_id"] = analysisId,
        ["video_id"] = videoId,
        ["comment_id"] = c.Id,
        ["parent_id"] = c.ParentId,
        ["is_reply"] = c.IsReply,
        ["author"] = ClientIdentity.Clip(c.Author, 200),
        ["author_id"] = c.AuthorId,
        ["author_is_uploader"] = c.AuthorIsUploader,
        ["author_is_verified"] = c.AuthorIsVerified,
        ["is_pinned"] = c.IsPinned,
        ["is_hearted"] = c.IsHearted,
        ["like_count"] = c.Likes,
        ["published_utc"] = c.PublishedAt,
        ["text"] = c.Text,
        ["text_len"] = c.Text.Length,
        ["lang"] = c.Lang,
        ["lang_conf"] = c.LangConfidence,
        ["sentiment"] = c.Sentiment,
        ["score"] = c.Score,
        ["p_pos"] = c.PPos,
        ["p_neu"] = c.PNeu,
        ["p_neg"] = c.PNeg,
        ["toxic"] = c.Toxic,
        ["emotion"] = c.Emotion,
        ["emotion_p"] = c.EmotionP,
        ["theme"] = c.Theme,
        ["duplicate_of"] = c.DuplicateOf,
        ["repeats"] = c.Repeats,
        ["created_utc"] = now,
    };

    // ── feedback & views ─────────────────────────────────────────────────────

    /// <summary>"Did the user like the report?" — thumbs up/down plus an optional note, written onto the analysis row.</summary>
    public void Feedback(string analysisId, bool liked, string? note, ClientInfo client)
    {
        if (!_o.Audit.Enabled || !ReportStore.IsValidId(analysisId)) return;
        _writer.Enqueue(new FeedbackOp(Day8Of(analysisId), analysisId, liked, ClientIdentity.Clip(note, 1000), DateTime.UtcNow));
    }

    public void ReportViewed(string analysisId)
    {
        if (!_o.Audit.Enabled || !ReportStore.IsValidId(analysisId)) return;
        _writer.Enqueue(new ViewedOp(Day8Of(analysisId), analysisId, DateTime.UtcNow));
    }
}
