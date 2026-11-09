using System.Text.RegularExpressions;

namespace Ark.Ydl.YtDlp;

/// <summary>Turns yt-dlp's ERROR lines into something an end user can act on.</summary>
public static partial class YtDlpErrors
{
    [GeneratedRegex(@"^(?:ERROR:\s*)?(?:\[[^\]]+\]\s*)?(?:[A-Za-z0-9_-]{6,}:\s*)?")]
    private static partial Regex PrefixRx();

    private static readonly (string[] Needles, string Message)[] Rules =
    [
        (["Sign in to confirm your age", "age-restricted", "inappropriate for some users"],
            "This video is age-restricted. The server needs signed-in YouTube cookies to download it."),
        (["confirm you're not a bot", "confirm you’re not a bot", "not a bot"],
            "YouTube asked this server to prove it isn't a bot. Try again in a few minutes; if it keeps happening the server admin needs to add YouTube cookies (see README → Bot checks)."),
        (["HTTP Error 429", "Too Many Requests", "content isn't available, try again later", "content isn’t available, try again later"],
            "YouTube is rate-limiting this server right now. Wait a few minutes and try again."),
        (["members-only", "Join this channel", "available to this channel's members"],
            "This is members-only content."),
        (["Private video", "This video is private"],
            "This video is private."),
        (["This live event will begin", "Premieres in", "is_upcoming", "This live stream recording is not available"],
            "This premiere or live stream hasn't started (or hasn't finished processing) yet."),
        (["does not pass filter (!is_live", "is currently live", "live event"],
            "This is a live stream that's still running. Try again once it has ended."),
        (["not made this video available in your country", "geo restrict", "not available in your country", "blocked it in your country"],
            "This video is blocked in the server's region."),
        (["copyright"],
            "This video was removed after a copyright claim."),
        (["account associated with this video has been terminated", "account has been terminated"],
            "The channel that uploaded this video has been terminated."),
        (["Video unavailable", "This video is unavailable", "This video has been removed", "does not exist"],
            "This video is unavailable — it may have been removed or made private."),
        (["The playlist does not exist", "playlist does not exist"],
            "That playlist doesn't exist or is private."),
        (["Requested format is not available"],
            "That quality isn't available for this video. Pick \"Best available\" and try again."),
        (["larger than max-filesize", "File is larger than"],
            "The file is bigger than this server allows."),
        (["Unsupported URL"],
            "That link isn't a supported video page."),
        (["No supported JavaScript runtime", "JavaScript runtime", "n challenge", "nsig extraction failed", "Signature extraction failed"],
            "The server couldn't solve YouTube's player challenge. It needs a JavaScript runtime (deno) and an up-to-date yt-dlp — the admin can check /api/system."),
        (["ffmpeg not found", "ffmpeg is not installed", "ffprobe and ffmpeg not found", "ffprobe not found"],
            "The server is missing ffmpeg, which is needed to merge, cut and convert."),
        (["Unable to download webpage", "timed out", "Connection reset", "Temporary failure in name resolution", "Network is unreachable"],
            "The server couldn't reach YouTube. Try again in a moment."),
        (["Postprocessing:", "Conversion failed", "Error opening output"],
            "Converting the file failed. Try a different format or turn off precise cuts."),
    ];

    /// <summary>A friendly sentence plus the raw detail (the last few ERROR lines).</summary>
    public static (string Message, string? Detail) Translate(IReadOnlyList<string> errors, string? stderr = null)
    {
        var all = string.Join('\n', errors) + "\n" + (stderr ?? "");
        foreach (var (needles, message) in Rules)
            if (needles.Any(n => all.Contains(n, StringComparison.OrdinalIgnoreCase)))
                return (message, Detail(errors, stderr));

        var first = errors.FirstOrDefault()
                    ?? stderr?.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("ERROR:"));
        if (first is null) return ("Something went wrong while downloading.", Detail(errors, stderr));
        var clean = PrefixRx().Replace(first, "").Trim();
        if (clean.Length > 220) clean = clean[..220] + "…";
        return (clean.Length > 0 ? clean : "Something went wrong while downloading.", Detail(errors, stderr));
    }

    private static string? Detail(IReadOnlyList<string> errors, string? stderr)
    {
        var lines = errors.Count > 0
            ? errors.TakeLast(4)
            : (stderr ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).TakeLast(4);
        var text = string.Join('\n', lines);
        return text.Length == 0 ? null : text.Length > 1500 ? text[^1500..] : text;
    }
}
