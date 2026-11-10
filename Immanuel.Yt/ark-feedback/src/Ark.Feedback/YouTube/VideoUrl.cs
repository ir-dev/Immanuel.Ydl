using System.Text.RegularExpressions;

namespace Ark.Feedback.YouTube;

/// <summary>A YouTube link the user pasted, reduced to the one thing that matters: the 11-character video id.</summary>
public sealed partial record VideoUrl(string VideoId, string Canonical, string Kind)
{
    [GeneratedRegex(@"^[A-Za-z0-9_-]{11}$")]
    private static partial Regex IdRx();

    private static readonly HashSet<string> Hosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "youtube.com", "www.youtube.com", "m.youtube.com", "music.youtube.com", "youtube-nocookie.com", "www.youtube-nocookie.com", "youtu.be", "www.youtu.be",
    };

    public static bool IsValidId(string? id) => id is not null && IdRx().IsMatch(id);

    /// <summary>
    /// Accepts watch, youtu.be, shorts, live, embed, v and music links (with or without a scheme), and a bare video id.
    /// Playlists and channels are refused: a report is about one video's comments.
    /// </summary>
    public static bool TryParse(string? input, out VideoUrl? url, out string? problem)
    {
        url = null;
        problem = null;
        var s = input?.Trim();
        if (string.IsNullOrEmpty(s))
        {
            problem = "Paste a YouTube link first.";
            return false;
        }
        if (s.Length > 2048)
        {
            problem = "That link is too long to be a YouTube link.";
            return false;
        }
        if (IsValidId(s))
        {
            url = Make(s, "id");
            return true;
        }
        if (!s.Contains("://", StringComparison.Ordinal)) s = "https://" + s;
        if (!Uri.TryCreate(s, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps))
        {
            problem = "That doesn't look like a web link.";
            return false;
        }
        if (!Hosts.Contains(u.Host))
        {
            problem = "Only YouTube links are supported (youtube.com, youtu.be, music.youtube.com).";
            return false;
        }
        var segs = u.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var q = System.Web.HttpUtility.ParseQueryString(u.Query);
        string? id = null, kind = null;
        if (u.Host.EndsWith("youtu.be", StringComparison.OrdinalIgnoreCase) && segs.Length >= 1) (id, kind) = (segs[0], "short-link");
        else if (segs.Length >= 1 && segs[0].Equals("watch", StringComparison.OrdinalIgnoreCase)) (id, kind) = (q["v"], "watch");
        else if (segs.Length >= 2 && segs[0] is var k && k.ToLowerInvariant() is "shorts" or "live" or "embed" or "v" or "e")
            (id, kind) = (segs[1], k.ToLowerInvariant() == "shorts" ? "shorts" : k.ToLowerInvariant() == "live" ? "live" : "embed");
        else if (segs.Length == 0 && q["v"] is { } v) (id, kind) = (v, "watch");
        else if (segs.Length >= 1 && segs[0].Equals("playlist", StringComparison.OrdinalIgnoreCase))
        {
            problem = "That's a playlist. Paste the link of one video — a report covers one video's comments.";
            return false;
        }
        else if (segs.Length >= 1 && (segs[0].StartsWith('@') || segs[0].Equals("channel", StringComparison.OrdinalIgnoreCase) || segs[0].Equals("c", StringComparison.OrdinalIgnoreCase)))
        {
            problem = "That's a channel. Paste the link of one of its videos.";
            return false;
        }
        if (id is not null && id.Contains('?')) id = id.Split('?')[0];
        if (!IsValidId(id))
        {
            problem = "Couldn't find a video id in that link.";
            return false;
        }
        url = Make(id!, kind ?? "watch");
        return true;
    }

    private static VideoUrl Make(string id, string kind) => new(id, "https://www.youtube.com/watch?v=" + id, kind);

    public string Thumbnail => $"https://i.ytimg.com/vi/{VideoId}/hqdefault.jpg";
}
