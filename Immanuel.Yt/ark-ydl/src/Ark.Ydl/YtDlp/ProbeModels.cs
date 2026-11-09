namespace Ark.Ydl.YtDlp;

/// <summary>What the UI needs to render a video or a playlist — a trimmed view of yt-dlp's -J output.</summary>
public sealed class ProbeResult
{
    public string Kind { get; set; } = "video"; // video | playlist
    public string Url { get; set; } = "";
    public string? Id { get; set; }
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? Uploader { get; set; }
    public string? ChannelUrl { get; set; }
    public long? ChannelFollowers { get; set; }
    public string? Thumbnail { get; set; }
    public double? Duration { get; set; }
    public long? ViewCount { get; set; }
    public long? LikeCount { get; set; }
    public string? UploadDate { get; set; } // yyyy-MM-dd
    public string? LiveStatus { get; set; }
    public int? AgeLimit { get; set; }
    public string? WebpageUrl { get; set; }
    public string? Extractor { get; set; }
    public bool IsYouTube { get; set; }
    public bool IsShort { get; set; }
    public bool IsMusic { get; set; }
    public string? Artist { get; set; }
    public string? Track { get; set; }
    public string? Album { get; set; }
    public List<string> Tags { get; set; } = [];
    /// <summary>YouTube's category for the video ("Music", "Gaming", "Education"…) — what the audit log calls its genre.</summary>
    public List<string> Categories { get; set; } = [];
    /// <summary>A music genre, where the site gives one.</summary>
    public string? Genre { get; set; }
    public string? ChannelId { get; set; }
    public string? Language { get; set; }

    public List<ChapterInfo> Chapters { get; set; } = [];
    public List<HeatPoint> Heatmap { get; set; } = [];
    public List<SubtitleInfo> Subtitles { get; set; } = [];
    public List<SubtitleInfo> AutoCaptions { get; set; } = [];
    public List<VideoQuality> VideoQualities { get; set; } = [];
    public List<AudioTrack> AudioTracks { get; set; } = [];

    /// <summary>The link said which part to clip (t= / start= / end=).</summary>
    public double? HintStart { get; set; }
    public double? HintEnd { get; set; }

    /// <summary>A single video that also belongs to a playlist (watch?v=…&amp;list=…).</summary>
    public string? ListId { get; set; }

    // playlist only
    public int? PlaylistCount { get; set; }
    public bool Truncated { get; set; }
    public List<PlaylistEntry> Entries { get; set; } = [];

    /// <summary>A shallow copy carrying the per-link hints (the cached result is shared).</summary>
    public ProbeResult WithHints(double? start, double? end, string? listId)
    {
        var c = (ProbeResult)MemberwiseClone();
        if (c.Kind == "video")
        {
            c.HintStart = start;
            c.HintEnd = end;
            c.ListId = listId;
        }
        return c;
    }
}

public sealed record ChapterInfo(double Start, double End, string Title);

public sealed record HeatPoint(double Start, double End, double Value);

public sealed record SubtitleInfo(string Lang, string Name);

/// <summary>One row in the quality picker. Res is the smaller dimension, as yt-dlp's "res" sort uses it.</summary>
public sealed class VideoQuality
{
    public int Res { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public double? Fps { get; set; }
    public bool Hdr { get; set; }
    public List<string> Codecs { get; set; } = [];
    /// <summary>Approximate video-stream bytes per codec family (h264, vp9, av1, …).</summary>
    public Dictionary<string, long> Sizes { get; set; } = [];
}

public sealed class AudioTrack
{
    public string Codec { get; set; } = "";
    public double? Abr { get; set; }
    public long? Size { get; set; }
    public string? Language { get; set; }
}

public sealed class PlaylistEntry
{
    public int Index { get; set; }
    public string? Id { get; set; }
    public string? Title { get; set; }
    public string? Url { get; set; }
    public double? Duration { get; set; }
    public string? Thumbnail { get; set; }
    public string? Uploader { get; set; }
    public long? ViewCount { get; set; }
    public string? LiveStatus { get; set; }
    public bool Unavailable { get; set; }
}
