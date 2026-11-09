using System.Text.RegularExpressions;
using Ark.Ydl.Options;

namespace Ark.Ydl.YtDlp;

public enum DownloadMode { Video, Audio, Subtitles, Thumbnail }

/// <summary>
/// Auto = highest resolution, H.264 + AAC whenever it exists at that resolution (plays everywhere);
/// Best = yt-dlp's own pick (AV1/VP9 + Opus, smallest for the quality); the rest force a codec.
/// </summary>
public enum VideoCodec { Auto, Best, H264, Vp9, Av1 }

public enum VideoContainer { Mp4, Mkv, Webm }

public enum AudioFormat { Mp3, M4a, Opus, Flac, Wav, Ogg, Best }

public enum SubtitleMode { None, Embed, File, Both }

public enum SponsorAction { None, Remove, Mark }

public enum FileNameStyle { Title, TitleId, ChannelTitle, DateTitle }

/// <summary>What the user asked for. Every field is whitelisted before it becomes an argument.</summary>
public sealed class DownloadRequest
{
    public string Url { get; set; } = "";
    public bool Playlist { get; set; }
    public DownloadMode Mode { get; set; } = DownloadMode.Video;
    public VideoOptions Video { get; set; } = new();
    public AudioOptions Audio { get; set; } = new();
    public ClipOptions? Clip { get; set; }
    public SubtitleOptions Subtitles { get; set; } = new();
    public EmbedOptions Embed { get; set; } = new();
    public SponsorBlockOptions SponsorBlock { get; set; } = new();

    /// <summary>1-based playlist positions to fetch; null = all (up to the server limit).</summary>
    public List<int>? PlaylistItems { get; set; }

    public FileNameStyle FileName { get; set; } = FileNameStyle.Title;

    /// <summary>Prefix playlist files with "001 - ".</summary>
    public bool NumberFiles { get; set; } = true;

    /// <summary>Shown in the downloads list only.</summary>
    public string? Title { get; set; }

    /// <summary>Shown in the downloads list only.</summary>
    public string? Thumbnail { get; set; }

    /// <summary>Short human label, e.g. "MP4 · 1080p" (shown in the downloads list).</summary>
    public string? Label { get; set; }
}

public sealed class VideoOptions
{
    /// <summary>Cap on the smaller dimension (so a vertical Short at 1080x1920 is "1080p"). Null = best.</summary>
    public int? MaxHeight { get; set; }
    public VideoCodec Codec { get; set; } = VideoCodec.Auto;
    public VideoContainer Container { get; set; } = VideoContainer.Mp4;
    /// <summary>Allow HDR streams. Off = prefer SDR, which looks right on every screen.</summary>
    public bool Hdr { get; set; }
}

public sealed class AudioOptions
{
    public AudioFormat Format { get; set; } = AudioFormat.Mp3;
    /// <summary>kbps for lossy re-encodes; null or 0 = best VBR.</summary>
    public int? Bitrate { get; set; }
    /// <summary>Crop the cover art to a square (music players expect 1:1).</summary>
    public bool SquareCover { get; set; }
    /// <summary>Read "Artist - Title" out of video titles; album/track from the playlist.</summary>
    public bool SmartTags { get; set; } = true;
}

public sealed class ClipOptions
{
    public List<ClipSection> Sections { get; set; } = [];
    /// <summary>Re-encode around the cut points so the clip starts exactly where asked.</summary>
    public bool Precise { get; set; } = true;
    /// <summary>Download the whole thing once and split it into one file per chapter.</summary>
    public bool SplitChapters { get; set; }
}

public sealed class ClipSection
{
    public double Start { get; set; }
    /// <summary>Null = to the end.</summary>
    public double? End { get; set; }
    /// <summary>When set, the section is this chapter (matched by exact title) and the file is named after it.</summary>
    public string? Chapter { get; set; }
}

public sealed class SubtitleOptions
{
    public SubtitleMode Mode { get; set; } = SubtitleMode.None;
    /// <summary>Uploaded subtitle languages.</summary>
    public List<string> Langs { get; set; } = [];
    /// <summary>Auto-generated caption languages.</summary>
    public List<string> AutoLangs { get; set; } = [];
}

public sealed class EmbedOptions
{
    public bool Metadata { get; set; } = true;
    public bool Thumbnail { get; set; } = true;
    public bool Chapters { get; set; } = true;
}

public sealed class SponsorBlockOptions
{
    public SponsorAction Action { get; set; } = SponsorAction.None;
    public List<string> Categories { get; set; } = [];
}

public static partial class DownloadRequestValidator
{
    public static readonly string[] SponsorCategories =
        ["sponsor", "intro", "outro", "selfpromo", "preview", "filler", "interaction", "music_offtopic", "hook"];

    private static readonly int[] Bitrates = [64, 96, 128, 160, 192, 256, 320];

    [GeneratedRegex(@"^[A-Za-z]{2,3}(?:[-_][A-Za-z0-9]{1,8}){0,3}$")]
    private static partial Regex LangRx();

    /// <summary>Null when valid, otherwise a sentence for the user.</summary>
    public static string? Validate(DownloadRequest r, YdlOptions o)
    {
        if (string.IsNullOrWhiteSpace(r.Url)) return "Paste a link first.";
        if (!Enum.IsDefined(r.Mode)) return "Unknown download mode.";
        if (!Enum.IsDefined(r.Video.Codec) || !Enum.IsDefined(r.Video.Container)) return "Unknown video format.";
        if (!Enum.IsDefined(r.Audio.Format)) return "Unknown audio format.";
        if (!Enum.IsDefined(r.Subtitles.Mode) || !Enum.IsDefined(r.SponsorBlock.Action) || !Enum.IsDefined(r.FileName))
            return "Unknown option.";

        if (r.Video.MaxHeight is { } h && (h < 144 || h > 4320)) return "Pick a quality between 144p and 4320p.";
        if (r.Audio.Bitrate is { } br && br != 0 && !Bitrates.Contains(br)) return "Unsupported audio bitrate.";

        if (r.Clip is { } clip)
        {
            if (r.Playlist) return "Clips work on single videos, not playlists.";
            if (r.Mode is not (DownloadMode.Video or DownloadMode.Audio)) return "Clips need video or audio mode.";
            if (!clip.SplitChapters)
            {
                if (clip.Sections.Count == 0) return "Add at least one time range.";
                if (clip.Sections.Count > o.MaxClipSections) return $"At most {o.MaxClipSections} ranges per download.";
                foreach (var s in clip.Sections)
                {
                    if (double.IsNaN(s.Start) || s.Start < 0) return "A clip can't start before 0:00.";
                    if (s.End is { } e && (double.IsNaN(e) || e <= s.Start + 0.05)) return "Each clip must end after it starts.";
                    if (s.Chapter is { Length: > 300 }) return "Chapter title too long.";
                    if (o.MaxDurationMinutes > 0 && s.End is { } e2 && e2 - s.Start > o.MaxDurationMinutes * 60)
                        return $"Clips are limited to {o.MaxDurationMinutes} minutes on this server.";
                }
            }
        }

        var langs = r.Subtitles.Langs.Concat(r.Subtitles.AutoLangs).ToList();
        if (langs.Count > 20) return "Pick at most 20 subtitle languages.";
        if (langs.Any(l => !LangRx().IsMatch(l))) return "Unsupported subtitle language code.";
        if (r.Mode == DownloadMode.Subtitles && langs.Count == 0) return "Choose at least one subtitle language.";

        if (r.SponsorBlock.Categories.Any(c => !SponsorCategories.Contains(c))) return "Unknown SponsorBlock category.";

        if (r.PlaylistItems is { } items)
        {
            if (items.Count == 0) return "Select at least one video.";
            if (items.Count > o.MaxPlaylistItems) return $"At most {o.MaxPlaylistItems} videos per download on this server.";
            if (items.Any(i => i < 1 || i > 100_000)) return "Invalid playlist position.";
        }

        if (r.Title is { Length: > 500 } || r.Label is { Length: > 120 } || r.Thumbnail is { Length: > 2048 })
            return "Request too large.";
        return null;
    }

    /// <summary>"1:3,7,9:12" from [1,2,3,7,9,10,11,12] (yt-dlp --playlist-items, inclusive ranges).</summary>
    public static string CompactItems(IEnumerable<int> items)
    {
        var sorted = items.Distinct().Order().ToList();
        var parts = new List<string>();
        for (var i = 0; i < sorted.Count;)
        {
            var j = i;
            while (j + 1 < sorted.Count && sorted[j + 1] == sorted[j] + 1) j++;
            parts.Add(j == i ? sorted[i].ToString() : $"{sorted[i]}:{sorted[j]}");
            i = j + 1;
        }
        return string.Join(',', parts);
    }
}
