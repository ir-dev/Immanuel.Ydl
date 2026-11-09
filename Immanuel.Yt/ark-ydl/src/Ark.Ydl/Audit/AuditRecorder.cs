using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ark.Ydl.Endpoints;
using Ark.Ydl.Jobs;
using Ark.Ydl.Options;
using Ark.Ydl.Releases;
using Ark.Ydl.Tools;
using Ark.Ydl.Util;
using Ark.Ydl.YtDlp;
using Microsoft.Extensions.Options;

namespace Ark.Ydl.Audit;

/// <summary>
/// Turns requests and download jobs into audit rows and hands them to the <see cref="AuditWriter"/>. Everything
/// here is cheap and synchronous (a GeoIP lookup is an in-memory tree walk); nothing here ever throws into a request.
/// </summary>
public sealed partial class AuditRecorder
{
    private const string ClientItem = "audit.client";
    private const string NoteItem = "audit.note";

    private readonly YdlOptions _o;
    private readonly AuditWriter _writer;
    private readonly GeoIpService _geo;
    private readonly ReleaseCatalog _release;
    private readonly ToolLocator _tools;
    private readonly YdlPaths _paths;
    private readonly ILogger<AuditRecorder> _log;
    private readonly Lazy<string> _salt;

    public AuditRecorder(IOptions<YdlOptions> opt, AuditWriter writer, GeoIpService geo, ReleaseCatalog release, ToolLocator tools,
        YdlPaths paths, ILogger<AuditRecorder> log)
    {
        _o = opt.Value;
        _writer = writer;
        _geo = geo;
        _release = release;
        _tools = tools;
        _paths = paths;
        _log = log;
        Clock = new AuditClock(_o.Audit.TimeZone);
        if (Clock.Note is not null) log.LogWarning("{Note}", Clock.Note);
        _salt = new Lazy<string>(LoadSalt);
    }

    public bool Enabled => _o.Audit.Enabled;

    public AuditClock Clock { get; }

    // ── who is asking ────────────────────────────────────────────────────────

    /// <summary>The caller, resolved once per request.</summary>
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
        var key = ApiEndpoints.ClientKey(ctx);
        if (key.StartsWith("ip:", StringComparison.Ordinal)) key = "ip:" + (stored ?? "unknown"); // never leak a masked IP through the key

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

    /// <summary>A per-installation secret so hashed IPs can't be reversed with a rainbow table of the IPv4 space.</summary>
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
        public string? JobId;
        public string? Detail;
    }

    /// <summary>Lets an endpoint attach what the request was about (a video, a job, why it was refused) to its row.</summary>
    public static void Annotate(HttpContext ctx, string? videoId = null, string? jobId = null, string? detail = null)
    {
        if (ctx.Items[NoteItem] is not Note n) ctx.Items[NoteItem] = n = new Note();
        if (videoId is not null) n.VideoId = videoId;
        if (jobId is not null) n.JobId = jobId;
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
            var jobId = note?.JobId ?? (path.StartsWith("/api/jobs/", StringComparison.Ordinal) ? ctx.Request.RouteValues["id"] as string : null);
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
                ["job_id"] = jobId is { Length: <= 40 } ? jobId : null,
                ["video_id"] = note?.VideoId,
                ["detail"] = ClientIdentity.Clip(note?.Detail, 300),
                ["app_version"] = _release.AppVersion,
            };
            Client(ctx).WriteTo(row);
            _writer.Enqueue(new InsertOp(AuditSchema.Requests, row));
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Request audit skipped");
        }
    }

    [GeneratedRegex(@"^/(?:watch|playlist|shorts/[^/]+|live/[^/]+|embed/[^/]+|[A-Za-z0-9_-]{11}|@[A-Za-z0-9._-]{1,100})$")]
    private static partial Regex ShortcutRx();

    private static readonly HashSet<string> StaticExt =
        [".js", ".mjs", ".css", ".svg", ".png", ".jpg", ".jpeg", ".gif", ".webp", ".ico", ".webmanifest", ".woff", ".woff2", ".map", ".txt", ".xml"];

    /// <summary>What a request was, in one word — the request log's main filter.</summary>
    public static string Kind(string method, string path)
    {
        var p = path.ToLowerInvariant();
        if (p.StartsWith("/api/admin")) return "admin";
        if (p == "/api/probe") return "probe";
        if (p == "/api/jobs" && method == "POST") return "job";
        if (p.StartsWith("/api/jobs/"))
        {
            if (p.EndsWith("/events")) return "events";
            if (p.Contains("/files/")) return "file";
            if (p.EndsWith("/zip")) return "zip";
            return "job-status";
        }
        if (p == "/api/version" || p.StartsWith("/api/releases")) return "release";
        if (p == "/api/system") return "system";
        if (p.StartsWith("/api/")) return "api";
        if (p.StartsWith("/signin-oidc") || p.StartsWith("/signout") || p.StartsWith("/auth/") || p.StartsWith("/ark/")) return "auth";
        if (p == "/healthz") return "health";
        if (p is "/" or "/index.html") return "page";
        if (p is "/admin" or "/admin/" or "/admin.html") return "admin-page";
        if (StaticExt.Contains(Path.GetExtension(p))) return "static";
        if (ShortcutRx().IsMatch(path)) return "shortcut";
        return "other";
    }

    // ── downloads ────────────────────────────────────────────────────────────

    /// <summary>Listens to every job's status changes. Called once at start-up.</summary>
    public void Attach(JobManager jobs)
    {
        if (!_o.Audit.Enabled) return;
        jobs.StatusChanged += JobChanged;
    }

    public void DownloadQueued(DownloadJob job, ClientInfo client, ProbeResult? probe)
    {
        if (!_o.Audit.Enabled) return;
        try
        {
            var row = DownloadRow(job, probe, client, Clock);
            row["app_version"] = _release.AppVersion;
            row["ytdlp_version"] = _tools.Status.YtDlp.Version;
            _writer.Enqueue(new InsertOp(AuditSchema.Downloads, row));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Couldn't audit job {Id}", job.Id);
        }
    }

    private void JobChanged(DownloadJob job) =>
        _writer.Enqueue(new UpdateOp(AuditSchema.Downloads, "job_id", job.Id, () => DownloadUpdate(job)));

    /// <summary>A finished file (or the ZIP) was fetched to be saved — not a preview's range request.</summary>
    public void Served(DownloadJob job, long bytes)
    {
        if (!_o.Audit.Enabled) return;
        _writer.Enqueue(new ServedOp(job.Id, bytes, DateTime.UtcNow));
    }

    internal static Dictionary<string, object?> DownloadRow(DownloadJob job, ProbeResult? probe, ClientInfo client, AuditClock clock)
    {
        var r = job.Request;
        var url = job.Url;
        var created = job.CreatedAt.UtcDateTime;
        var (day, hour, weekday) = clock.Bucket(created);
        var playlist = YtDlpArgs.IsPlaylist(r, url);
        var clip = r.Mode is DownloadMode.Video or DownloadMode.Audio && !playlist ? r.Clip : null;
        var duration = probe?.Duration;

        var row = new Dictionary<string, object?>
        {
            ["job_id"] = job.Id,
            ["day"] = day,
            ["hour"] = hour,
            ["weekday"] = weekday,
            ["created_utc"] = created,
            ["status"] = Status(job.Status),
            ["url"] = ClientIdentity.Clip(url.Url, 600),
            ["site"] = Uri.TryCreate(url.Url, UriKind.Absolute, out var u) ? u.Host.ToLowerInvariant() : null,
            ["url_kind"] = url.Kind.ToString().ToLowerInvariant(),
            ["video_id"] = url.VideoId ?? (probe?.Kind == "video" ? probe.Id : null),
            ["list_id"] = url.ListId,
            ["is_youtube"] = url.IsYouTube,
            ["is_music"] = url.IsMusic || probe?.IsMusic == true,
            ["is_short"] = probe?.IsShort,
            ["live_status"] = probe?.LiveStatus,
            ["title"] = ClientIdentity.Clip(probe?.Title ?? r.Title, 500),
            ["channel"] = ClientIdentity.Clip(probe?.Uploader, 200),
            ["channel_id"] = probe?.ChannelId,
            ["channel_url"] = ClientIdentity.Clip(probe?.ChannelUrl, 300),
            ["channel_followers"] = probe?.ChannelFollowers,
            ["duration_sec"] = duration,
            ["view_count"] = probe?.ViewCount,
            ["like_count"] = probe?.LikeCount,
            ["upload_date"] = probe?.UploadDate,
            ["age_limit"] = probe?.AgeLimit,
            ["category"] = probe?.Categories.FirstOrDefault(),
            ["categories"] = probe is { Categories.Count: > 0 } ? JsonSerializer.Serialize(probe.Categories) : null,
            ["genre"] = probe?.Genre,
            ["tags"] = probe is { Tags.Count: > 0 } ? JsonSerializer.Serialize(probe.Tags) : null,
            ["video_language"] = probe?.Language,
            ["artist"] = ClientIdentity.Clip(probe?.Artist, 200),
            ["track"] = ClientIdentity.Clip(probe?.Track, 200),
            ["album"] = ClientIdentity.Clip(probe?.Album, 200),
            ["mode"] = r.Mode.ToString().ToLowerInvariant(),
            ["format"] = Format(r),
            ["scope"] = Scope(r, playlist, clip),
            ["quality"] = r.Mode == DownloadMode.Video ? r.Video.MaxHeight : null,
            ["codec"] = r.Mode == DownloadMode.Video ? r.Video.Codec.ToString().ToLowerInvariant() : null,
            ["container"] = r.Mode == DownloadMode.Video ? r.Video.Container.ToString().ToLowerInvariant() : null,
            ["hdr"] = r.Mode == DownloadMode.Video ? r.Video.Hdr : null,
            ["audio_format"] = r.Mode == DownloadMode.Audio ? r.Audio.Format.ToString().ToLowerInvariant() : null,
            ["audio_bitrate"] = r.Mode == DownloadMode.Audio && r.Audio.Bitrate is > 0 ? r.Audio.Bitrate : null,
            ["clip_sections"] = clip is { Sections.Count: > 0 }
                ? JsonSerializer.Serialize(clip.Sections.Select(s => new { s.Start, s.End, s.Chapter }))
                : null,
            ["clip_seconds"] = ClipSeconds(clip, duration),
            ["playlist"] = playlist,
            ["playlist_items"] = playlist ? r.PlaylistItems?.Count : null,
            ["playlist_count"] = playlist ? probe?.PlaylistCount : null,
            ["subtitles"] = r.Subtitles.Mode == SubtitleMode.None ? null : r.Subtitles.Mode.ToString().ToLowerInvariant(),
            ["subtitle_langs"] = r.Subtitles.Langs.Count + r.Subtitles.AutoLangs.Count == 0 ? null
                : string.Join(',', r.Subtitles.Langs.Concat(r.Subtitles.AutoLangs.Select(l => l + "(auto)"))),
            ["sponsorblock"] = r.SponsorBlock.Action == SponsorAction.None ? null
                : $"{r.SponsorBlock.Action.ToString().ToLowerInvariant()}:{string.Join(',', r.SponsorBlock.Categories)}",
            ["embed"] = string.Join(',', new[] { r.Embed.Metadata ? "metadata" : null, r.Embed.Thumbnail ? "thumbnail" : null, r.Embed.Chapters ? "chapters" : null }.Where(x => x is not null)),
            ["file_name_style"] = r.FileName.ToString().ToLowerInvariant(),
            ["label"] = ClientIdentity.Clip(r.Label, 120),
            ["estimated_bytes"] = Estimate(r, probe, clip, duration),
            ["updated_utc"] = DateTime.UtcNow,
        };
        client.WriteTo(row);
        return row;
    }

    /// <summary>The columns that change as a job runs; computed when the update is written, so it is always current.</summary>
    internal static Dictionary<string, object?> DownloadUpdate(DownloadJob job)
    {
        var started = job.StartedAt?.UtcDateTime;
        var finished = job.FinishedAt?.UtcDateTime;
        var row = new Dictionary<string, object?>
        {
            ["status"] = Status(job.Status),
            ["started_utc"] = started,
            ["finished_utc"] = finished,
            ["queue_ms"] = started is { } s ? (long)(s - job.CreatedAt.UtcDateTime).TotalMilliseconds : null,
            ["run_ms"] = started is { } s2 && finished is { } f ? (long)(f - s2).TotalMilliseconds : null,
            ["error"] = ClientIdentity.Clip(job.Error, 500),
            ["error_detail"] = ClientIdentity.Clip(job.ErrorDetail, 4000),
            ["updated_utc"] = DateTime.UtcNow,
        };
        if (job.IsTerminal)
        {
            var files = job.Files;
            row["file_count"] = files.Count;
            row["total_bytes"] = files.Sum(x => x.Size);
            row["files"] = files.Count == 0 ? null : JsonSerializer.Serialize(files.Take(300).Select(x => new { n = x.Name, s = x.Size, k = x.Kind }));
        }

        var items = job.Items;
        if (items.Count > 0)
        {
            row["item_count"] = items.Count;
            row["items"] = JsonSerializer.Serialize(items.Take(300).Select(i => new
            {
                id = i.Id, t = i.Title, c = i.Channel, cat = i.Categories.FirstOrDefault(), g = i.Genre, d = i.Duration, v = i.Views, i = i.PlaylistIndex,
            }));
            // yt-dlp's own record of what it fetched: authoritative for one video, and the only source for a playlist's items.
            var cats = items.SelectMany(i => i.Categories.Take(1)).ToList();
            if (cats.Count > 0)
            {
                row["category"] = Mode(cats);
                row["categories"] = JsonSerializer.Serialize(cats.GroupBy(x => x).OrderByDescending(g => g.Count()).Select(g => g.Key).Take(5));
            }
            if (Mode(items.Select(i => i.Genre)) is { } genre) row["genre"] = genre;
            if (Mode(items.Select(i => i.Channel)) is { } channel) row["channel"] = channel;
            if (Mode(items.Select(i => i.ChannelId)) is { } channelId) row["channel_id"] = channelId;
            if (Mode(items.Select(i => i.Language)) is { } lang) row["video_language"] = lang;
            var durations = items.Where(i => i.Duration is > 0).Select(i => i.Duration!.Value).ToList();
            if (items.Count == 1)
            {
                var one = items[0];
                if (one.Id is not null) row["video_id"] = one.Id;
                if (durations.Count == 1 && job.Request.Clip is null) row["duration_sec"] = durations[0];
                if (one.Views is not null) row["view_count"] = one.Views;
                if (one.Likes is not null) row["like_count"] = one.Likes;
                if (one.UploadDate is not null) row["upload_date"] = one.UploadDate;
                if (one.AgeLimit is not null) row["age_limit"] = one.AgeLimit;
                if (one.Tags.Count > 0) row["tags"] = JsonSerializer.Serialize(one.Tags);
                if (one.LiveStatus is not null) row["live_status"] = one.LiveStatus;
            }
            else if (durations.Count > 0)
            {
                row["duration_sec"] = durations.Sum();
            }
        }
        return row;
    }

    private static string? Mode(IEnumerable<string?> values) =>
        values.Where(v => !string.IsNullOrWhiteSpace(v)).GroupBy(v => v!).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();

    internal static string Status(JobStatus s) => s.ToString().ToLowerInvariant();

    /// <summary>"MP4 1080p", "MP4 best", "MP3 192k", "Original audio", "Subtitles", "Thumbnail".</summary>
    internal static string Format(DownloadRequest r) => r.Mode switch
    {
        DownloadMode.Video => $"{r.Video.Container.ToString().ToUpperInvariant()} {(r.Video.MaxHeight is { } h ? h + "p" : "best")}",
        DownloadMode.Audio => r.Audio.Format == AudioFormat.Best
            ? "Original audio"
            : r.Audio.Format.ToString().ToUpperInvariant() + (r.Audio.Bitrate is > 0 and var br ? $" {br}k" : ""),
        DownloadMode.Subtitles => "Subtitles",
        _ => "Thumbnail",
    };

    /// <summary>whole | clip | chapters | playlist.</summary>
    internal static string Scope(DownloadRequest r, bool playlist, ClipOptions? clip)
    {
        if (playlist) return "playlist";
        if (clip is null) return "whole";
        if (clip.SplitChapters || clip.Sections.Any(s => s.Chapter is not null)) return "chapters";
        return "clip";
    }

    internal static double? ClipSeconds(ClipOptions? clip, double? duration)
    {
        if (clip is null) return null;
        if (clip.SplitChapters) return duration;
        double total = 0;
        foreach (var s in clip.Sections)
        {
            var end = s.End ?? duration;
            if (end is null) return null;
            total += Math.Max(0, end.Value - s.Start);
        }
        return total;
    }

    /// <summary>The same rough size the page shows before a download: best stream at the cap, plus audio, scaled to a clip.</summary>
    internal static long? Estimate(DownloadRequest r, ProbeResult? p, ClipOptions? clip, double? duration)
    {
        if (p is null || p.Kind != "video") return null;
        var audio = p.AudioTracks.FirstOrDefault(a => a.Codec == (r.Video.Codec is VideoCodec.Auto or VideoCodec.H264 ? "aac" : "opus"))?.Size
                    ?? p.AudioTracks.FirstOrDefault()?.Size;
        long? bytes = null;
        if (r.Mode == DownloadMode.Video)
        {
            var q = p.VideoQualities.Where(x => r.Video.MaxHeight is not { } cap || x.Res <= cap).FirstOrDefault() ?? p.VideoQualities.LastOrDefault();
            if (q is null || q.Sizes.Count == 0) return null;
            var order = r.Video.Codec switch
            {
                VideoCodec.Auto => new[] { "h264", "vp9", "av1" },
                VideoCodec.Best => ["av1", "vp9", "h264"],
                VideoCodec.H264 => ["h264"],
                VideoCodec.Vp9 => ["vp9"],
                _ => ["av1"],
            };
            long v = order.Select(c => q.Sizes.TryGetValue(c, out var s) ? s : 0).FirstOrDefault(s => s > 0);
            if (v == 0) v = q.Sizes.Values.Min();
            bytes = v + (audio ?? 0);
        }
        else if (r.Mode == DownloadMode.Audio)
        {
            bytes = audio;
        }
        if (bytes is null) return null;
        if (clip is not null && duration is > 0 && ClipSeconds(clip, duration) is { } secs)
            bytes = (long)(bytes.Value * Math.Min(1, secs / duration.Value));
        return bytes;
    }
}
