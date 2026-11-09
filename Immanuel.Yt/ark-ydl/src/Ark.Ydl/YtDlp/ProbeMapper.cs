using System.Globalization;
using System.Text.Json;
using Ark.Ydl.Util;

namespace Ark.Ydl.YtDlp;

/// <summary>Maps yt-dlp's -J JSON onto <see cref="ProbeResult"/>. Tolerant of missing fields everywhere.</summary>
public static class ProbeMapper
{
    public static ProbeResult Map(JsonElement root, UrlAnalysis url)
    {
        var isPlaylist = Str(root, "_type") is "playlist" or "multi_video";
        var r = isPlaylist ? MapPlaylist(root) : MapVideo(root);
        r.Url = url.Url;
        r.IsYouTube = url.IsYouTube;
        r.IsMusic = url.IsMusic || (Str(root, "webpage_url") ?? "").Contains("music.youtube.com");
        if (!isPlaylist)
        {
            r.HintStart = url.Start;
            r.HintEnd = url.End;
            r.ListId = url.ListId;
        }
        return r;
    }

    private static ProbeResult MapVideo(JsonElement v)
    {
        var r = new ProbeResult { Kind = "video" };
        FillCommon(r, v);
        r.Duration = Num(v, "duration");
        r.LikeCount = Long(v, "like_count");
        r.LiveStatus = Str(v, "live_status") ?? (Bool(v, "is_live") == true ? "is_live" : null);
        r.AgeLimit = (int?)Num(v, "age_limit");
        r.Artist = Str(v, "artist") ?? FirstStr(v, "artists") ?? Str(v, "creator");
        r.Track = Str(v, "track");
        r.Album = Str(v, "album");

        var page = Str(v, "webpage_url") ?? Str(v, "original_url") ?? "";
        var width = Num(v, "width");
        var height = Num(v, "height");
        r.IsShort = page.Contains("/shorts/") || (width > 0 && height > 0 && height > width && r.Duration is <= 180);

        if (v.TryGetProperty("chapters", out var ch) && ch.ValueKind == JsonValueKind.Array)
            foreach (var c in ch.EnumerateArray())
            {
                var s = Num(c, "start_time");
                var e = Num(c, "end_time");
                if (s is null || e is null) continue;
                r.Chapters.Add(new ChapterInfo(s.Value, e.Value, Str(c, "title") ?? $"Chapter {r.Chapters.Count + 1}"));
            }

        if (v.TryGetProperty("heatmap", out var hm) && hm.ValueKind == JsonValueKind.Array)
            foreach (var p in hm.EnumerateArray())
            {
                var s = Num(p, "start_time");
                var e = Num(p, "end_time");
                var val = Num(p, "value");
                if (s is not null && e is not null && val is not null)
                    r.Heatmap.Add(new HeatPoint(s.Value, e.Value, Math.Round(val.Value, 4)));
            }

        r.Subtitles = Subs(v, "subtitles");
        r.AutoCaptions = Subs(v, "automatic_captions");
        MapFormats(r, v);
        return r;
    }

    private static ProbeResult MapPlaylist(JsonElement p)
    {
        var r = new ProbeResult { Kind = "playlist" };
        FillCommon(r, p);
        r.PlaylistCount = (int?)Num(p, "playlist_count");
        if (p.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
        {
            var i = 0;
            foreach (var e in entries.EnumerateArray())
            {
                i++;
                if (e.ValueKind != JsonValueKind.Object) continue;
                var id = Str(e, "id");
                var title = Str(e, "title");
                var duration = Num(e, "duration");
                var availability = Str(e, "availability");
                var unavailable = title is "[Private video]" or "[Deleted video]" or "[Unavailable video]"
                                  || availability is "private" or "needs_auth" or "subscriber_only" or "premium_only";
                r.Entries.Add(new PlaylistEntry
                {
                    Index = (int?)Num(e, "playlist_index") ?? i,
                    Id = id,
                    Title = title,
                    Url = Str(e, "url") ?? Str(e, "webpage_url"),
                    Duration = duration,
                    Thumbnail = PickThumb(e, 480) ?? (id is { Length: 11 } ? $"https://i.ytimg.com/vi/{id}/mqdefault.jpg" : null),
                    Uploader = Str(e, "channel") ?? Str(e, "uploader"),
                    ViewCount = Long(e, "view_count"),
                    LiveStatus = Str(e, "live_status"),
                    Unavailable = unavailable,
                });
            }
        }
        r.PlaylistCount ??= r.Entries.Count;
        r.Truncated = r.PlaylistCount > r.Entries.Count;
        r.Thumbnail ??= r.Entries.FirstOrDefault(e => !e.Unavailable)?.Thumbnail;
        r.Duration = r.Entries.All(e => e.Duration is null) ? null : r.Entries.Sum(e => e.Duration ?? 0);
        return r;
    }

    private static void FillCommon(ProbeResult r, JsonElement v)
    {
        r.Id = Str(v, "id");
        r.Title = Str(v, "title") ?? Str(v, "fulltitle");
        var desc = Str(v, "description");
        r.Description = desc is { Length: > 5000 } ? desc[..5000] + "…" : desc;
        r.Uploader = Str(v, "channel") ?? Str(v, "uploader");
        r.ChannelUrl = Str(v, "channel_url") ?? Str(v, "uploader_url");
        r.ChannelFollowers = Long(v, "channel_follower_count");
        r.Thumbnail = Str(v, "thumbnail") ?? PickThumb(v, 1280);
        r.ViewCount = Long(v, "view_count");
        r.WebpageUrl = Str(v, "webpage_url") ?? Str(v, "original_url");
        r.Extractor = Str(v, "extractor_key") ?? Str(v, "extractor");
        var date = Str(v, "upload_date") ?? Str(v, "modified_date");
        if (date is { Length: 8 } && DateOnly.TryParseExact(date, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            r.UploadDate = d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (v.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array)
            r.Tags = tags.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.String).Select(t => t.GetString()!).Take(12).ToList();
        r.Categories = ItemMetadata.Strings(v, "categories", 5);
        r.Genre = Str(v, "genre") ?? ItemMetadata.Strings(v, "genres", 1).FirstOrDefault();
        r.ChannelId = Str(v, "channel_id");
        r.Language = Str(v, "language");
    }

    private static void MapFormats(ProbeResult r, JsonElement v)
    {
        if (!v.TryGetProperty("formats", out var formats) || formats.ValueKind != JsonValueKind.Array) return;
        var duration = r.Duration ?? 0;
        var videos = new List<(int res, int w, int h, double? fps, bool hdr, string family, long? size, double tbr)>();
        var audios = new List<AudioTrack>();

        foreach (var f in formats.EnumerateArray())
        {
            var protocol = Str(f, "protocol") ?? "";
            if (protocol.Contains("mhtml") || Str(f, "format_note") == "storyboard") continue;
            var vcodec = Str(f, "vcodec");
            var acodec = Str(f, "acodec");
            var tbr = Num(f, "tbr") ?? 0;
            var size = Long(f, "filesize") ?? Long(f, "filesize_approx")
                       ?? (tbr > 0 && duration > 0 ? (long)(tbr * 125 * duration) : null);

            var hasVideo = vcodec is not null && vcodec != "none";
            var hasAudio = acodec is not null && acodec != "none";
            if (hasVideo)
            {
                var w = (int)(Num(f, "width") ?? 0);
                var h = (int)(Num(f, "height") ?? 0);
                if (w <= 0 && h <= 0) continue;
                var res = w > 0 && h > 0 ? Math.Min(w, h) : Math.Max(w, h);
                var dr = Str(f, "dynamic_range");
                videos.Add((res, w, h, Num(f, "fps"), dr is not null && dr != "SDR", Family(vcodec!), size, tbr));
            }
            else if (hasAudio && vcodec == "none")
            {
                audios.Add(new AudioTrack
                {
                    Codec = Family(acodec!),
                    Abr = Num(f, "abr") ?? (tbr > 0 ? tbr : null),
                    Size = size,
                    Language = Str(f, "language"),
                });
            }
        }

        r.VideoQualities = videos
            .GroupBy(x => NormalizeRes(x.res))
            .OrderByDescending(g => g.Key)
            .Select(g =>
            {
                var best = g.OrderByDescending(x => x.fps ?? 0).ThenByDescending(x => x.tbr).First();
                return new VideoQuality
                {
                    Res = g.Key,
                    Width = best.w,
                    Height = best.h,
                    Fps = g.Max(x => x.fps),
                    Hdr = g.Any(x => x.hdr),
                    Codecs = g.Select(x => x.family).Distinct().OrderBy(CodecOrder).ToList(),
                    Sizes = g.Where(x => x.size is > 0 && !x.hdr)
                        .GroupBy(x => x.family)
                        .ToDictionary(c => c.Key, c => c.OrderByDescending(x => x.fps ?? 0).ThenByDescending(x => x.tbr).First().size!.Value),
                };
            })
            .ToList();

        r.AudioTracks = audios
            .Where(a => a.Abr is not null || a.Size is not null)
            .GroupBy(a => a.Codec)
            .Select(g => g.OrderByDescending(a => a.Abr ?? 0).First())
            .OrderByDescending(a => a.Abr ?? 0)
            .ToList();
    }

    /// <summary>Snap odd sizes (e.g. 1076 for letterboxed films) to the standard ladder for display.</summary>
    internal static int NormalizeRes(int res)
    {
        int[] ladder = [144, 240, 360, 480, 720, 1080, 1440, 2160, 4320];
        foreach (var step in ladder)
            if (res <= step && res >= step * 0.85) return step;
        return res;
    }

    private static int CodecOrder(string family) => family switch
    {
        "h264" => 0, "vp9" => 1, "av1" => 2, "h265" => 3, _ => 9,
    };

    internal static string Family(string codec)
    {
        var c = codec.ToLowerInvariant();
        if (c.StartsWith("avc") || c.StartsWith("h264")) return "h264";
        if (c.StartsWith("vp09") || c.StartsWith("vp9")) return "vp9";
        if (c.StartsWith("av01") || c.StartsWith("av1")) return "av1";
        if (c.StartsWith("hev") || c.StartsWith("hvc") || c.StartsWith("h265")) return "h265";
        if (c.StartsWith("vp8")) return "vp8";
        if (c.StartsWith("mp4a") || c.StartsWith("aac")) return "aac";
        if (c.StartsWith("opus")) return "opus";
        if (c.StartsWith("vorbis")) return "vorbis";
        if (c.StartsWith("mp3")) return "mp3";
        if (c.Contains("ac-3") || c.StartsWith("ac3") || c.StartsWith("ec-3") || c.StartsWith("eac3")) return "ac3";
        var dot = c.IndexOf('.');
        return dot > 0 ? c[..dot] : c;
    }

    private static List<SubtitleInfo> Subs(JsonElement v, string prop)
    {
        var list = new List<SubtitleInfo>();
        if (!v.TryGetProperty(prop, out var subs) || subs.ValueKind != JsonValueKind.Object) return list;
        foreach (var lang in subs.EnumerateObject())
        {
            if (lang.Name is "live_chat" or "rechat" || lang.Value.ValueKind != JsonValueKind.Array) continue;
            var name = lang.Value.EnumerateArray().Select(x => Str(x, "name")).FirstOrDefault(n => !string.IsNullOrEmpty(n));
            list.Add(new SubtitleInfo(lang.Name, name ?? lang.Name));
        }
        return list;
    }

    private static string? PickThumb(JsonElement v, int targetWidth)
    {
        if (!v.TryGetProperty("thumbnails", out var t) || t.ValueKind != JsonValueKind.Array) return null;
        string? best = null;
        var bestScore = double.MaxValue;
        string? last = null;
        foreach (var th in t.EnumerateArray())
        {
            var url = Str(th, "url");
            if (url is null) continue;
            last = url;
            var w = Num(th, "width");
            if (w is null) continue;
            var score = Math.Abs(w.Value - targetWidth) + (w < targetWidth ? 200 : 0);
            if (score < bestScore) { bestScore = score; best = url; }
        }
        return best ?? last;
    }

    // ── JSON helpers ────────────────────────────────────────────────────────

    internal static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static string? FirstStr(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Array
            ? p.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()).FirstOrDefault()
            : null;

    internal static double? Num(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out var d) ? d : null;

    private static long? Long(JsonElement e, string name) => Num(e, name) is { } d ? (long)d : null;

    private static bool? Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind is JsonValueKind.True or JsonValueKind.False ? p.GetBoolean() : null;
}
