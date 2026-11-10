using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ark.Feedback.Options;
using Ark.Feedback.Tools;
using Microsoft.Extensions.Options;

namespace Ark.Feedback.YouTube;

/// <summary>
/// Metadata + comments through yt-dlp: one process per fetch, "--dump-single-json --write-comments", output parsed from
/// stdout, progress parsed from the verbose stderr ("Downloading ~N comments", "comment API JSON page k (m/~N)").
/// No API key, no quota; YouTube's own total is approximate.
/// </summary>
public sealed partial class YtDlpCommentSource(ToolLocator tools, IOptions<FeedbackOptions> opt, ILogger<YtDlpCommentSource> log) : ICommentSource
{
    public string Name => "yt-dlp";

    /// <summary>True while yt-dlp works — and while the start-up detection/download is still running, because a fetch waits for it.</summary>
    public bool Available => tools.Status.YtDlp.Ok || tools.Status.Preparing;

    public string? Unavailability => tools.Status.YtDlp.Ok ? null : tools.Status.Preparing ? "yt-dlp is still being set up on this server." : tools.Status.YtDlp.Note ?? "yt-dlp is not available yet.";

    [GeneratedRegex(@"Downloading ~?(\d[\d,]*) comments", RegexOptions.IgnoreCase)]
    private static partial Regex TotalRx();

    [GeneratedRegex(@"comment API JSON (?:page|reply thread) \d+ \((\d+)/~?(\d+)\)", RegexOptions.IgnoreCase)]
    private static partial Regex PageRx();

    internal IReadOnlyList<string> BuildArgs(FetchRequest r)
    {
        var o = opt.Value.YouTube;
        var replies = Math.Max(0, r.RepliesPerThread);
        // max_comments = total, parents, total replies, replies per thread — replies capped at half the total so threads don't crowd out top-level voices
        var extractor = $"youtube:max_comments={r.MaxComments},all,{(replies == 0 ? 0 : Math.Max(replies, r.MaxComments / 2))},{replies};comment_sort={(r.Sort == "new" ? "new" : "top")};player_skip=js;skip=hls,dash,translated_subs";
        if (!string.IsNullOrWhiteSpace(o.ExtraExtractorArgs)) extractor += ";" + o.ExtraExtractorArgs.Trim().TrimStart(';');
        var args = new List<string>
        {
            "--ignore-config", "--no-playlist", "--skip-download", "--write-comments", "--dump-single-json", "--no-cache-dir",
            "--verbose", "--no-color", "--socket-timeout", "30", "--retries", "3", "--extractor-retries", "3",
            "--extractor-args", extractor,
        };
        if (!string.IsNullOrWhiteSpace(o.CookiesFile)) args.AddRange(["--cookies", o.CookiesFile]);
        if (!string.IsNullOrWhiteSpace(o.Proxy)) args.AddRange(["--proxy", o.Proxy]);
        args.AddRange(tools.ToolArgs());
        args.Add(r.Url.Canonical);
        return args;
    }

    public async Task<CommentFetchResult> FetchAsync(FetchRequest request, IProgress<FetchProgress>? progress, CancellationToken ct)
    {
        // An analysis queued in the first seconds after start-up waits for the tool detection (or first download) instead of failing.
        if (tools.Status.Preparing)
        {
            progress?.Report(new FetchProgress(0, null, "Waiting for yt-dlp to be ready"));
            await tools.WhenInitialized.WaitAsync(ct);
        }
        var path = tools.Status.YtDlp.Path ?? throw new CommentSourceException("yt-dlp is not available on this server.", tools.Status.YtDlp.Note, retryable: true);
        var args = BuildArgs(request);
        long? total = null;
        var fetched = 0;
        var warnings = new List<string>();
        progress?.Report(new FetchProgress(0, null, "Contacting YouTube"));
        var result = await ProcessRunner.RunAsync(path, args, TimeSpan.FromSeconds(Math.Max(60, opt.Value.YouTube.FetchTimeoutSeconds)), line =>
        {
            if (TotalRx().Match(line) is { Success: true } t && long.TryParse(t.Groups[1].Value.Replace(",", ""), NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            {
                total = n;
                progress?.Report(new FetchProgress(fetched, total, "Fetching comments"));
            }
            else if (PageRx().Match(line) is { Success: true } p && int.TryParse(p.Groups[1].Value, out var f))
            {
                fetched = Math.Max(fetched, f);
                if (long.TryParse(p.Groups[2].Value, out var n2)) total ??= n2;
                progress?.Report(new FetchProgress(fetched, total, "Fetching comments"));
            }
            else if (line.StartsWith("WARNING:", StringComparison.Ordinal) && !line.Contains("JavaScript runtime", StringComparison.OrdinalIgnoreCase) && warnings.Count < 10)
                warnings.Add(line["WARNING:".Length..].Trim());
            else if (line.Contains("Downloading webpage", StringComparison.Ordinal))
                progress?.Report(new FetchProgress(0, null, "Reading the video page"));
        }, ct);

        if (result.TimedOut)
            throw new CommentSourceException("YouTube took too long to answer. Try again in a minute, or pick a smaller profile.", retryable: true);
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StdOut))
            throw Translate(result);

        try
        {
            using var doc = JsonDocument.Parse(result.StdOut);
            var root = doc.RootElement;
            var video = ParseVideo(root, request.Url.VideoId);
            var comments = ParseComments(root);
            // yt-dlp overwrites comment_count with the number it fetched; the stderr total is YouTube's figure.
            if (total is { } yt && (video.CommentCount is null || video.CommentCount <= comments.Count)) video = video with { CommentCount = yt };
            progress?.Report(new FetchProgress(comments.Count, video.CommentCount, "Comments fetched"));
            return new CommentFetchResult(video, comments, Name, tools.Status.YtDlp.Version, warnings);
        }
        catch (JsonException ex)
        {
            log.LogWarning(ex, "yt-dlp output was not JSON");
            throw new CommentSourceException("yt-dlp returned something that isn't JSON.", ex.Message, retryable: true);
        }
    }

    internal static VideoInfo ParseVideo(JsonElement root, string fallbackId)
    {
        string? S(string k) => root.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        long? L(string k) => root.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;
        double? D(string k) => root.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
        bool? B(string k) => root.TryGetProperty(k, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
        var tags = root.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Array
            ? t.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).Take(40).ToList() : [];
        var categories = root.TryGetProperty("categories", out var c) && c.ValueKind == JsonValueKind.Array
            ? c.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList() : [];
        DateTimeOffset? uploaded = null;
        if (L("timestamp") is { } ts) uploaded = DateTimeOffset.FromUnixTimeSeconds(ts);
        else if (S("upload_date") is { Length: 8 } ud && DateTime.TryParseExact(ud, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d))
            uploaded = d;
        var duration = D("duration");
        var url = S("webpage_url") ?? "";
        return new VideoInfo
        {
            Id = S("id") ?? fallbackId,
            Title = S("title") ?? S("fulltitle"),
            Description = Clip(S("description"), 2000),
            Channel = S("channel") ?? S("uploader"),
            ChannelId = S("channel_id"),
            ChannelUrl = S("channel_url") ?? S("uploader_url"),
            ChannelFollowers = L("channel_follower_count"),
            ChannelVerified = B("channel_is_verified"),
            DurationSeconds = duration,
            ViewCount = L("view_count"),
            LikeCount = L("like_count"),
            CommentCount = L("comment_count"),
            UploadedAt = uploaded,
            Category = categories.FirstOrDefault(),
            Tags = tags,
            Language = S("language"),
            LiveStatus = S("live_status"),
            IsShort = url.Contains("/shorts/", StringComparison.OrdinalIgnoreCase) || (duration is <= 60 && (S("original_url") ?? "").Contains("/shorts/", StringComparison.OrdinalIgnoreCase)),
            AgeLimit = (int?)L("age_limit"),
            Thumbnail = $"https://i.ytimg.com/vi/{S("id") ?? fallbackId}/hqdefault.jpg",
        };
    }

    internal static List<RawComment> ParseComments(JsonElement root)
    {
        var list = new List<RawComment>();
        if (!root.TryGetProperty("comments", out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
        foreach (var c in arr.EnumerateArray())
        {
            string? S(string k) => c.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            long? L(string k) => c.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;
            bool Bf(string k) => c.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;
            var id = S("id");
            var text = S("text");
            if (id is null || string.IsNullOrWhiteSpace(text)) continue;
            var parent = S("parent");
            // a reply to a reply is listed under its root thread id: "<root>.<reply>" → keep the thread root as parent
            if (parent is not null && parent != "root" && parent.Contains('.')) parent = parent.Split('.')[0];
            list.Add(new RawComment
            {
                Id = id,
                ParentId = parent is null or "root" ? null : parent,
                Text = text.Trim(),
                Author = S("author"),
                AuthorId = S("author_id"),
                AuthorIsUploader = Bf("author_is_uploader"),
                AuthorIsVerified = Bf("author_is_verified"),
                IsPinned = Bf("is_pinned"),
                IsFavorited = Bf("is_favorited"),
                LikeCount = L("like_count") ?? 0,
                PublishedAt = L("timestamp") is { } ts ? DateTimeOffset.FromUnixTimeSeconds(ts) : null,
            });
        }
        return list;
    }

    private static CommentSourceException Translate(ProcessResult r) => Translate(r.StdErr);

    /// <summary>Turns yt-dlp's stderr into one honest sentence for the visitor, with the raw ERROR line as the detail.</summary>
    internal static CommentSourceException Translate(string stderr)
    {
        var lines = stderr.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var errorLine = lines.LastOrDefault(l => l.StartsWith("ERROR:", StringComparison.Ordinal) || l.StartsWith("yt-dlp: error:", StringComparison.Ordinal));
        var all = stderr.ToLowerInvariant();
        string? reason = null;
        if (errorLine is not null)
        {
            reason = errorLine[(errorLine.IndexOf(':') + 1)..].Trim();
            var m = Regex.Match(reason, @"^\[youtube\]\s+[A-Za-z0-9_-]{11}:\s*(.*)$");
            if (m.Success) reason = m.Groups[1].Value.Trim();
            else if (reason.StartsWith("[youtube]", StringComparison.Ordinal)) reason = reason["[youtube]".Length..].Trim();
        }
        var detail = reason ?? lines.LastOrDefault(l => l.StartsWith("WARNING:", StringComparison.Ordinal)) ?? lines.LastOrDefault();
        var lower = (reason ?? "").ToLowerInvariant();

        if (lower.Contains("not a bot") || lower.Contains("429") || lower.Contains("too many requests") || all.Contains("http error 429"))
            return new CommentSourceException("YouTube is rate-limiting this server right now and asks it to prove it isn't a bot. Wait a while and try again; an operator can configure a cookies file or a proxy.", detail, retryable: true);
        if (reason is null) return new CommentSourceException("YouTube didn't answer the way yt-dlp expected.", detail, retryable: true);
        if (lower.Contains("private video")) return new CommentSourceException("This video is private, so its comments can't be read.", reason);
        if (lower.Contains("comments are turned off") || lower.Contains("comments disabled") || lower.Contains("disabled comments")) return new CommentSourceException("Comments are turned off for this video.", reason);
        if (lower.Contains("video unavailable") || lower.Contains("is not available") || lower.Contains("has been removed") || lower.Contains("does not exist")) return new CommentSourceException("That video isn't available (removed, blocked or the id is wrong).", reason);
        if (lower.Contains("age-restricted") || lower.Contains("age restricted") || lower.Contains("confirm your age") || lower.Contains("members-only") || lower.Contains("members only") || lower.Contains("join this channel"))
            return new CommentSourceException("YouTube wants a sign-in for this video (age-restricted or members only). An operator can configure a cookies file.", reason);
        if (lower.Contains("sign in")) return new CommentSourceException("YouTube wants a sign-in to read this video. An operator can configure a cookies file.", reason, retryable: true);
        if (lower.Contains("premiere") || lower.Contains("live event will begin")) return new CommentSourceException("This video hasn't premiered yet, so there are no comments to read.", reason);
        if (lower.Contains("timed out") || lower.Contains("connection") || lower.Contains("network")) return new CommentSourceException("The connection to YouTube dropped. Try again in a minute.", reason, retryable: true);
        return new CommentSourceException("YouTube refused: " + reason, detail, retryable: true);
    }

    private static string? Clip(string? s, int max) => string.IsNullOrEmpty(s) ? null : s.Length > max ? s[..max] + "…" : s;
}
