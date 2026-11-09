using System.Globalization;
using System.Text.RegularExpressions;

namespace Ark.Ydl.YtDlp;

/// <summary>
/// Reads yt-dlp's console output line by line and keeps a human-friendly picture of where the
/// download is: which item of a playlist, which stream (video, then audio), bytes, speed, ETA
/// and the post-processing step. Not thread-safe; the caller serialises <see cref="Feed"/>.
/// </summary>
public sealed partial class ProgressTracker(bool audioOnly)
{
    [GeneratedRegex(@"^\[download\] Downloading (?:item|video) (\d+) of (\d+)")]
    private static partial Regex ItemRx();

    [GeneratedRegex(@"^\[download\] Downloading playlist: (.+)$")]
    private static partial Regex PlaylistRx();

    [GeneratedRegex(@"^\[info\] [^:]+: Downloading (\d+) format\(s\): (\S+)")]
    private static partial Regex FormatsRx();

    [GeneratedRegex(@"^\[download\] Destination: ")]
    private static partial Regex DestinationRx();

    [GeneratedRegex(@"^\[download\] .+ has already been downloaded")]
    private static partial Regex AlreadyRx();

    [GeneratedRegex(@"^\[download\] .+ does not pass filter")]
    private static partial Regex FilteredRx();

    [GeneratedRegex(@"^\[([A-Za-z][\w:]*)\] ")]
    private static partial Regex TagRx();

    private static readonly Dictionary<string, string> PostLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Merger"] = "Merging video and audio",
        ["ExtractAudio"] = "Converting audio",
        ["VideoRemuxer"] = "Remuxing",
        ["VideoConvertor"] = "Converting video",
        ["EmbedSubtitle"] = "Embedding subtitles",
        ["EmbedThumbnail"] = "Adding cover art",
        ["ThumbnailsConvertor"] = "Preparing the thumbnail",
        ["Metadata"] = "Writing tags and chapters",
        ["FFmpegMetadata"] = "Writing tags and chapters",
        ["MetadataParser"] = "Reading tags",
        ["SponsorBlock"] = "Looking up SponsorBlock segments",
        ["ModifyChapters"] = "Cutting out segments",
        ["SplitChapters"] = "Splitting into chapters",
        ["SubtitlesConvertor"] = "Converting subtitles",
        ["FixupM3u8"] = "Fixing up the file",
        ["FixupM4a"] = "Fixing up the file",
        ["FixupDuplicateMoov"] = "Fixing up the file",
        ["FixupTimestamp"] = "Fixing up the file",
        ["FixupStretched"] = "Fixing up the file",
        ["MoveFiles"] = "Finishing",
        ["MoveFilesAfterDownload"] = "Finishing",
    };

    public string Stage { get; private set; } = "Starting";
    public int ItemIndex { get; private set; }
    public int ItemCount { get; private set; }
    public int Streams { get; private set; } = 1;
    public int StreamIndex { get; private set; }
    public double? StreamFraction { get; private set; }
    public long? Downloaded { get; private set; }
    public long? Total { get; private set; }
    public double? Speed { get; private set; }
    public double? Eta { get; private set; }
    public bool PostProcessing { get; private set; }
    public string? PlaylistTitle { get; private set; }
    public int FilteredOut { get; private set; }
    public List<string> Errors { get; } = [];
    public List<string> Warnings { get; } = [];

    /// <summary>0..1 across the whole job, or null while the size is unknown (show an indeterminate bar).</summary>
    public double? Overall
    {
        get
        {
            var streams = Math.Max(Streams, 1);
            double? itemFrac = PostProcessing
                ? 1
                : StreamFraction is { } f
                    ? (Math.Max(StreamIndex, 1) - 1 + f) / streams
                    : StreamIndex > 1 ? (StreamIndex - 1.0) / streams : null;
            if (ItemCount > 1)
                return Math.Clamp((Math.Max(ItemIndex, 1) - 1 + (itemFrac ?? 0)) / ItemCount, 0, 1);
            return itemFrac is { } v ? Math.Clamp(v, 0, 1) : null;
        }
    }

    /// <summary>Returns true when something the user can see changed.</summary>
    public bool Feed(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;
        line = line.TrimEnd();

        if (line.StartsWith(YtDlpArgs.ProgressPrefix, StringComparison.Ordinal))
            return FeedProgress(line[YtDlpArgs.ProgressPrefix.Length..].Split('|'));

        if (line.StartsWith(YtDlpArgs.PostProgressPrefix, StringComparison.Ordinal))
        {
            var p = line[YtDlpArgs.PostProgressPrefix.Length..].Split('|');
            if (p.Length >= 2 && p[0] is "started" or "processing")
            {
                PostProcessing = true;
                Stage = PostLabel(p[1]);
                return true;
            }
            return false;
        }

        if (line.StartsWith("ERROR:", StringComparison.Ordinal))
        {
            if (Errors.Count < 30) Errors.Add(line[6..].Trim());
            return true;
        }
        if (line.StartsWith("WARNING:", StringComparison.Ordinal))
        {
            if (Warnings.Count < 30) Warnings.Add(line[8..].Trim());
            return false;
        }

        Match m;
        if ((m = ItemRx().Match(line)).Success)
        {
            ItemIndex = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            ItemCount = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            Streams = 1;
            StreamIndex = 0;
            StreamFraction = null;
            Downloaded = Total = null;
            Speed = Eta = null;
            PostProcessing = false;
            Stage = "Fetching video info";
            return true;
        }
        if ((m = PlaylistRx().Match(line)).Success)
        {
            PlaylistTitle = m.Groups[1].Value.Trim();
            Stage = "Reading the playlist";
            return true;
        }
        if ((m = FormatsRx().Match(line)).Success)
        {
            Streams = Math.Max(1, m.Groups[2].Value.Split('+').Length);
            Stage = "Starting the download";
            return true;
        }
        if (DestinationRx().IsMatch(line))
        {
            if (PostProcessing) return false; // post-processors also announce destinations
            StreamIndex++;
            StreamFraction = 0;
            Downloaded = Total = null;
            Stage = StreamLabel();
            return true;
        }
        if (AlreadyRx().IsMatch(line))
        {
            StreamIndex = Math.Max(StreamIndex, 1);
            StreamFraction = 1;
            return true;
        }
        if (FilteredRx().IsMatch(line))
        {
            FilteredOut++;
            return true;
        }
        if ((m = TagRx().Match(line)).Success)
        {
            var tag = m.Groups[1].Value;
            if (PostLabels.TryGetValue(tag, out var label))
            {
                PostProcessing = true;
                Stage = label;
                return true;
            }
            if (tag != "download" && StreamIndex == 0 && !PostProcessing && Stage is "Starting")
            {
                Stage = "Fetching video info";
                return true;
            }
        }
        return false;
    }

    private bool FeedProgress(string[] p)
    {
        if (p.Length < 6) return false;
        var status = p[0];
        if (StreamIndex == 0) StreamIndex = 1;
        PostProcessing = false;
        if (status == "finished")
        {
            StreamFraction = 1;
            Speed = Eta = null;
            return true;
        }
        if (status != "downloading") return false;

        Downloaded = Long(p[1]);
        Total = Long(p[2]) ?? Long(p[3]);
        Speed = Dbl(p[4]);
        Eta = Dbl(p[5]);
        StreamFraction = Total is > 0 && Downloaded is { } d ? Math.Clamp((double)d / Total.Value, 0, 1) : null;
        Stage = StreamLabel();
        return true;
    }

    private string StreamLabel()
    {
        if (audioOnly) return "Downloading audio";
        if (Streams >= 2) return StreamIndex <= 1 ? "Downloading video" : "Downloading audio";
        return "Downloading";
    }

    private static string PostLabel(string pp) =>
        PostLabels.TryGetValue(pp, out var l) ? l : "Processing (" + pp + ")";

    private static double? Dbl(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && !double.IsNaN(v) && !double.IsInfinity(v) ? v : null;

    private static long? Long(string s) => Dbl(s) is { } v ? (long)v : null;
}
