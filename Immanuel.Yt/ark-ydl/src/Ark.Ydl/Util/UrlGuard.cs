using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Web;

namespace Ark.Ydl.Util;

public enum UrlKind { Video, Playlist, Channel, Other }

/// <summary>The outcome of looking at what a user pasted.</summary>
public sealed record UrlAnalysis
{
    public bool Ok => Error is null;
    public string? Error { get; init; }
    /// <summary>The normalised URL handed to yt-dlp.</summary>
    public string Url { get; init; } = "";
    public UrlKind Kind { get; init; }
    public string? VideoId { get; init; }
    public string? ListId { get; init; }
    /// <summary>Clip start from t= / start= in the link.</summary>
    public double? Start { get; init; }
    /// <summary>Clip end from end= in the link.</summary>
    public double? End { get; init; }
    public bool IsYouTube { get; init; }
    public bool IsMusic { get; init; }

    public static UrlAnalysis Fail(string error) => new() { Error = error };
}

/// <summary>
/// Turns pasted text into a URL yt-dlp should fetch, or a reason it will not.
/// Only allow-listed hosts pass unless AllowAnySite is on; even then loopback and private
/// addresses are refused so the server cannot be pointed at its own network.
/// </summary>
public static partial class UrlGuard
{
    [GeneratedRegex(@"^[A-Za-z0-9_-]{11}$")]
    private static partial Regex VideoIdRx();

    [GeneratedRegex(@"^(?:PL|UU|LL|FL|OL|RD|UL|OLAK5uy_)[A-Za-z0-9_-]{8,}$")]
    private static partial Regex ListIdRx();

    [GeneratedRegex(@"https?://[^\s<>""']+", RegexOptions.IgnoreCase)]
    private static partial Regex FirstUrlRx();

    [GeneratedRegex(@"^(?:www\.|m\.)?(?:youtube\.com|youtu\.be|youtube-nocookie\.com|music\.youtube\.com)(?:[/?#]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex SchemelessYouTubeRx();

    public static UrlAnalysis Analyze(string? input, IReadOnlyCollection<string> allowedSites, bool allowAnySite, bool? preferPlaylist = null)
    {
        var text = (input ?? "").Trim();
        if (text.Length == 0) return UrlAnalysis.Fail("Paste a link first.");
        if (text.Length > 2048) return UrlAnalysis.Fail("That link is too long.");

        string candidate;
        var found = FirstUrlRx().Match(text);
        if (found.Success) candidate = found.Value.TrimEnd('.', ',', ')', ']', ';');
        else if (VideoIdRx().IsMatch(text)) candidate = $"https://www.youtube.com/watch?v={text}";
        else if (ListIdRx().IsMatch(text)) candidate = $"https://www.youtube.com/playlist?list={text}";
        else if (SchemelessYouTubeRx().IsMatch(text)) candidate = "https://" + text;
        else return UrlAnalysis.Fail("That doesn't look like a link. Paste a full YouTube URL.");

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            return UrlAnalysis.Fail("Only http(s) links are supported.");

        var host = uri.IdnHost.ToLowerInvariant();
        var bareHost = host.StartsWith("www.") ? host[4..] : host.StartsWith("m.") ? host[2..] : host;
        var isYouTube = bareHost is "youtube.com" or "youtu.be" or "youtube-nocookie.com"
                        || bareHost.EndsWith(".youtube.com") || bareHost.EndsWith(".youtube-nocookie.com");

        var sites = allowedSites.Count > 0 ? allowedSites : Options.YdlOptions.DefaultSites;
        var listed = sites.Any(s => HostMatches(bareHost, s) || HostMatches(host, s));
        if (!listed && !allowAnySite)
            return UrlAnalysis.Fail("Only YouTube links are supported on this server.");

        if (!isYouTube)
        {
            if (IsInternalHost(host)) return UrlAnalysis.Fail("That address is not reachable from here.");
            return new UrlAnalysis { Url = uri.AbsoluteUri, Kind = UrlKind.Other, IsYouTube = false };
        }

        return AnalyzeYouTube(uri, bareHost, preferPlaylist);
    }

    private static UrlAnalysis AnalyzeYouTube(Uri uri, string bareHost, bool? preferPlaylist)
    {
        var q = HttpUtility.ParseQueryString(uri.Query);
        var segs = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var isMusic = bareHost == "music.youtube.com";

        string? videoId = null;
        if (bareHost == "youtu.be" && segs.Length >= 1) videoId = segs[0];
        else if (segs.Length >= 1 && segs[0] == "watch") videoId = q["v"];
        else if (segs.Length >= 2 && segs[0] is "shorts" or "live" or "embed" or "v" or "e") videoId = segs[1];
        if (videoId is not null && !VideoIdRx().IsMatch(videoId)) videoId = null;

        var listId = q["list"];
        if (listId is not null && !Regex.IsMatch(listId, "^[A-Za-z0-9_-]{2,64}$")) listId = null;

        double? start = null, end = null;
        var t = q["t"] ?? q["start"] ?? FragmentValue(uri.Fragment, "t");
        if (TimeCode.TryParse(t, out var ts) && ts > 0) start = ts;
        if (TimeCode.TryParse(q["end"], out var te) && te > 0 && (start is null || te > start)) end = te;

        // Channels: /@handle, /channel/UC…, /c/name, /user/name → the uploads tab.
        if (videoId is null && segs.Length >= 1 && (segs[0].StartsWith('@') || segs[0] is "channel" or "c" or "user"))
        {
            var baseSegs = segs[0].StartsWith('@') ? 1 : 2;
            if (segs.Length < baseSegs) return UrlAnalysis.Fail("That channel link looks incomplete.");
            var path = "/" + string.Join('/', segs); // already percent-encoded by Uri
            if (segs.Length == baseSegs) path += "/videos";
            return new UrlAnalysis
            {
                Url = $"https://www.youtube.com{path}", Kind = UrlKind.Channel,
                IsYouTube = true,
            };
        }

        var playlistHost = isMusic ? "music.youtube.com" : "www.youtube.com";
        if (listId is not null && (videoId is null || preferPlaylist == true))
        {
            // Mixes (RD…) only resolve together with a seed video.
            var url = listId.StartsWith("RD") && videoId is not null
                ? $"https://www.youtube.com/watch?v={videoId}&list={listId}"
                : $"https://{playlistHost}/playlist?list={listId}";
            return new UrlAnalysis
            {
                Url = url, Kind = UrlKind.Playlist, VideoId = videoId, ListId = listId,
                IsYouTube = true, IsMusic = isMusic,
            };
        }

        if (videoId is null)
            return UrlAnalysis.Fail("That YouTube link doesn't point to a video, playlist or channel.");

        return new UrlAnalysis
        {
            Url = $"https://{(isMusic ? "music.youtube.com" : "www.youtube.com")}/watch?v={videoId}",
            Kind = UrlKind.Video, VideoId = videoId, ListId = listId,
            Start = start, End = end, IsYouTube = true, IsMusic = isMusic,
        };
    }

    private static string? FragmentValue(string fragment, string key)
    {
        if (string.IsNullOrEmpty(fragment)) return null;
        return HttpUtility.ParseQueryString(fragment.TrimStart('#'))[key];
    }

    private static bool HostMatches(string host, string site)
    {
        site = site.Trim().TrimStart('.').ToLowerInvariant();
        return site.Length > 0 && (host == site || host.EndsWith("." + site));
    }

    /// <summary>Loopback, link-local, private and unique-local literals plus "localhost".</summary>
    internal static bool IsInternalHost(string host)
    {
        host = host.Trim('[', ']');
        if (host is "localhost" || host.EndsWith(".localhost") || host.EndsWith(".internal") || host.EndsWith(".local"))
            return true;
        if (!IPAddress.TryParse(host, out var ip)) return false;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) return true;
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal;
        var b = ip.GetAddressBytes();
        return b[0] == 10
               || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
               || (b[0] == 192 && b[1] == 168)
               || (b[0] == 169 && b[1] == 254)
               || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
               || b[0] == 0;
    }
}
