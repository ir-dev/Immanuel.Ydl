using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Xml;
using Ark.Feedback.Options;
using Microsoft.Extensions.Options;

namespace Ark.Feedback.YouTube;

/// <summary>
/// Metadata + comments through the YouTube Data API v3 (videos.list + commentThreads.list with replies). Needs an API
/// key (Feedback:YouTube:ApiKey); costs 1 quota unit per 100 comments, so the free 10,000 units/day go a long way.
/// Exact totals, fast pages, official terms.
/// </summary>
public sealed class DataApiCommentSource(IHttpClientFactory http, IOptions<FeedbackOptions> opt, ILogger<DataApiCommentSource> log) : ICommentSource
{
    private const string Base = "https://www.googleapis.com/youtube/v3/";

    public string Name => "youtube-data-api";

    public bool Available => !string.IsNullOrWhiteSpace(opt.Value.YouTube.ApiKey);

    public string? Unavailability => Available ? null : "No YouTube Data API key is configured (Feedback:YouTube:ApiKey).";

    public async Task<CommentFetchResult> FetchAsync(FetchRequest request, IProgress<FetchProgress>? progress, CancellationToken ct)
    {
        var key = opt.Value.YouTube.ApiKey ?? throw new CommentSourceException("No YouTube Data API key is configured.");
        var client = http.CreateClient("youtube-api");
        progress?.Report(new FetchProgress(0, null, "Reading the video"));
        var video = await GetVideoAsync(client, key, request.Url.VideoId, ct);
        var comments = new List<RawComment>();
        string? page = null;
        var warnings = new List<string>();
        do
        {
            var url = $"{Base}commentThreads?part=snippet,replies&videoId={request.Url.VideoId}&maxResults=100&order={(request.Sort == "new" ? "time" : "relevance")}&textFormat=plainText&key={Uri.EscapeDataString(key)}"
                      + (page is null ? "" : "&pageToken=" + Uri.EscapeDataString(page));
            using var doc = await GetJsonAsync(client, url, ct, commentsCall: true);
            var root = doc.RootElement;
            if (root.TryGetProperty("items", out var items))
            {
                foreach (var item in items.EnumerateArray())
                {
                    if (!item.TryGetProperty("snippet", out var sn) || !sn.TryGetProperty("topLevelComment", out var top)) continue;
                    var parsed = ParseComment(top, null);
                    if (parsed is not null)
                    {
                        parsed = parsed with { ReplyCount = sn.TryGetProperty("totalReplyCount", out var rc) ? rc.GetInt32() : null };
                        comments.Add(parsed);
                    }
                    if (request.RepliesPerThread > 0 && item.TryGetProperty("replies", out var rep) && rep.TryGetProperty("comments", out var rcs))
                        foreach (var r in rcs.EnumerateArray().Take(request.RepliesPerThread))
                            if (ParseComment(r, parsed?.Id) is { } reply) comments.Add(reply);
                    if (comments.Count >= request.MaxComments) break;
                }
            }
            progress?.Report(new FetchProgress(comments.Count, video.CommentCount, "Fetching comments"));
            page = root.TryGetProperty("nextPageToken", out var np) ? np.GetString() : null;
        } while (page is not null && comments.Count < request.MaxComments && !ct.IsCancellationRequested);

        if (comments.Count > request.MaxComments) comments.RemoveRange(request.MaxComments, comments.Count - request.MaxComments);
        return new CommentFetchResult(video, comments, Name, "v3", warnings);
    }

    private async Task<VideoInfo> GetVideoAsync(HttpClient client, string key, string id, CancellationToken ct)
    {
        var url = $"{Base}videos?part=snippet,statistics,contentDetails,status&id={id}&key={Uri.EscapeDataString(key)}";
        using var doc = await GetJsonAsync(client, url, ct, commentsCall: false);
        var items = doc.RootElement.TryGetProperty("items", out var arr) ? arr : default;
        if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() == 0)
            throw new CommentSourceException("That video isn't available (removed, private or the id is wrong).");
        var v = items[0];
        var sn = v.GetProperty("snippet");
        var st = v.TryGetProperty("statistics", out var s) ? s : default;
        long? Stat(string k) => st.ValueKind == JsonValueKind.Object && st.TryGetProperty(k, out var x) && long.TryParse(x.GetString(), out var n) ? n : null;
        string? Str(JsonElement e, string k) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var x) && x.ValueKind == JsonValueKind.String ? x.GetString() : null;
        double? duration = null;
        if (v.TryGetProperty("contentDetails", out var cd) && Str(cd, "duration") is { } iso)
        {
            try { duration = XmlConvert.ToTimeSpan(iso).TotalSeconds; } catch (FormatException) { }
        }
        var tags = sn.TryGetProperty("tags", out var tg) && tg.ValueKind == JsonValueKind.Array ? tg.EnumerateArray().Select(x => x.GetString()!).Take(40).ToList() : [];
        return new VideoInfo
        {
            Id = id,
            Title = Str(sn, "title"),
            Description = Str(sn, "description") is { } d && d.Length > 2000 ? d[..2000] + "…" : Str(sn, "description"),
            Channel = Str(sn, "channelTitle"),
            ChannelId = Str(sn, "channelId"),
            ChannelUrl = Str(sn, "channelId") is { } cid ? "https://www.youtube.com/channel/" + cid : null,
            DurationSeconds = duration,
            ViewCount = Stat("viewCount"),
            LikeCount = Stat("likeCount"),
            CommentCount = Stat("commentCount"),
            UploadedAt = DateTimeOffset.TryParse(Str(sn, "publishedAt"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var p) ? p : null,
            Category = Str(sn, "categoryId") is { } cat ? CategoryName(cat) : null,
            Tags = tags,
            Language = Str(sn, "defaultAudioLanguage") ?? Str(sn, "defaultLanguage"),
            LiveStatus = Str(sn, "liveBroadcastContent") is "none" or null ? "not_live" : Str(sn, "liveBroadcastContent"),
            IsShort = duration is <= 60,
            Thumbnail = $"https://i.ytimg.com/vi/{id}/hqdefault.jpg",
        };
    }

    private async Task<JsonDocument> GetJsonAsync(HttpClient client, string url, CancellationToken ct, bool commentsCall)
    {
        using var res = await client.GetAsync(url, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (res.IsSuccessStatusCode) return JsonDocument.Parse(body);
        string? reason = null;
        try
        {
            using var err = JsonDocument.Parse(body);
            reason = err.RootElement.GetProperty("error").GetProperty("errors")[0].GetProperty("reason").GetString();
        }
        catch (Exception) { /* not the usual error shape */ }
        log.LogWarning("YouTube Data API {Status} {Reason}", (int)res.StatusCode, reason);
        throw reason switch
        {
            "commentsDisabled" => new CommentSourceException("Comments are turned off for this video.", reason),
            "videoNotFound" => new CommentSourceException("That video isn't available (removed, private or the id is wrong).", reason),
            "forbidden" when commentsCall => new CommentSourceException("YouTube won't share this video's comments (members-only or restricted).", reason),
            "quotaExceeded" or "dailyLimitExceeded" or "rateLimitExceeded" => new CommentSourceException("The YouTube API quota for today is used up; yt-dlp will be used instead.", reason, retryable: true),
            "keyInvalid" or "forbidden" => new CommentSourceException("The YouTube API key was rejected. Check Feedback:YouTube:ApiKey.", reason),
            _ when res.StatusCode == HttpStatusCode.TooManyRequests => new CommentSourceException("YouTube is rate-limiting this server right now.", reason, retryable: true),
            _ => new CommentSourceException($"YouTube API answered {(int)res.StatusCode}.", reason ?? body[..Math.Min(body.Length, 300)], retryable: (int)res.StatusCode >= 500),
        };
    }

    internal static RawComment? ParseComment(JsonElement c, string? parentId)
    {
        if (!c.TryGetProperty("id", out var idEl) || !c.TryGetProperty("snippet", out var sn)) return null;
        string? S(string k) => sn.TryGetProperty(k, out var x) && x.ValueKind == JsonValueKind.String ? x.GetString() : null;
        var text = S("textOriginal") ?? S("textDisplay");
        if (string.IsNullOrWhiteSpace(text)) return null;
        var authorChannel = sn.TryGetProperty("authorChannelId", out var ac) && ac.TryGetProperty("value", out var acv) ? acv.GetString() : null;
        return new RawComment
        {
            Id = idEl.GetString()!,
            ParentId = parentId ?? (sn.TryGetProperty("parentId", out var p) ? p.GetString() : null),
            Text = text.Trim(),
            Author = S("authorDisplayName"),
            AuthorId = authorChannel,
            LikeCount = sn.TryGetProperty("likeCount", out var lc) && lc.ValueKind == JsonValueKind.Number ? lc.GetInt64() : 0,
            PublishedAt = DateTimeOffset.TryParse(S("publishedAt"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null,
        };
    }

    private static string CategoryName(string id) => id switch
    {
        "1" => "Film & Animation", "2" => "Autos & Vehicles", "10" => "Music", "15" => "Pets & Animals", "17" => "Sports",
        "19" => "Travel & Events", "20" => "Gaming", "22" => "People & Blogs", "23" => "Comedy", "24" => "Entertainment",
        "25" => "News & Politics", "26" => "Howto & Style", "27" => "Education", "28" => "Science & Technology", "29" => "Nonprofits & Activism",
        _ => "Category " + id,
    };
}

/// <summary>Picks the source: the Data API when a key is configured (and "auto"/"api"), else yt-dlp; falls back to yt-dlp when the API fails in a retryable way.</summary>
public sealed class CommentSourceSelector(DataApiCommentSource api, YtDlpCommentSource ytDlp, IOptions<FeedbackOptions> opt)
{
    public ICommentSource Primary
    {
        get
        {
            var mode = opt.Value.YouTube.Source?.Trim().ToLowerInvariant() ?? "auto";
            return mode switch
            {
                "api" => api,
                "ytdlp" => ytDlp,
                _ => api.Available ? api : ytDlp,
            };
        }
    }

    public ICommentSource? Fallback => ReferenceEquals(Primary, api) && ytDlp.Available ? ytDlp : null;

    public IEnumerable<ICommentSource> All => [api, ytDlp];
}
