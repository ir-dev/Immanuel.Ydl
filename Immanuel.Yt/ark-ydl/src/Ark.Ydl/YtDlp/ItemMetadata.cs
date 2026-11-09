using System.Text.Json;

namespace Ark.Ydl.YtDlp;

/// <summary>
/// One downloaded item's metadata, as yt-dlp appended it with --print-to-file (one JSON object per line).
/// For a single video it is the same information the probe had; for a playlist it is the only place each
/// item's genre and channel come from, because playlist probes are flat.
/// </summary>
public sealed record ItemMeta
{
    public string? Id { get; init; }
    public string? Title { get; init; }
    public string? Channel { get; init; }
    public string? ChannelId { get; init; }
    public double? Duration { get; init; }
    public long? Views { get; init; }
    public long? Likes { get; init; }
    public List<string> Categories { get; init; } = [];
    public List<string> Tags { get; init; } = [];
    public string? Genre { get; init; }
    public string? Language { get; init; }
    public int? AgeLimit { get; init; }
    public string? UploadDate { get; init; }
    public int? PlaylistIndex { get; init; }
    public string? LiveStatus { get; init; }
    public long? FilesizeApprox { get; init; }
    public int? Height { get; init; }
}

public static class ItemMetadata
{
    public const string Folder = ".meta";
    public const string FileName = "items.jsonl";

    /// <summary>The fields written per item; `.{…}` keeps only these keys of the info dict (yt-dlp output template syntax).</summary>
    public const string Template =
        "%(.{id,title,channel,uploader,channel_id,duration,view_count,like_count,categories,tags,genre,genres," +
        "language,age_limit,upload_date,playlist_index,live_status,filesize_approx,height})j";

    public static string PathFor(string jobDir) => System.IO.Path.Combine(jobDir, Folder, FileName);

    /// <summary>Reads what yt-dlp wrote, skipping anything malformed. Bounded: a huge playlist can't balloon memory.</summary>
    public static List<ItemMeta> Read(string jobDir, int max = 500)
    {
        var list = new List<ItemMeta>();
        var file = PathFor(jobDir);
        if (!File.Exists(file)) return list;
        try
        {
            foreach (var line in File.ReadLines(file))
            {
                if (list.Count >= max) break;
                var t = line.Trim();
                if (t.Length < 2 || t[0] != '{') continue;
                try
                {
                    using var doc = JsonDocument.Parse(t);
                    var e = doc.RootElement;
                    var id = ProbeMapper.Str(e, "id");
                    if (id is not null && list.Any(x => x.Id == id)) continue; // a re-try of the same item
                    list.Add(new ItemMeta
                    {
                        Id = id,
                        Title = Clip(ProbeMapper.Str(e, "title"), 300),
                        Channel = Clip(ProbeMapper.Str(e, "channel") ?? ProbeMapper.Str(e, "uploader"), 200),
                        ChannelId = Clip(ProbeMapper.Str(e, "channel_id"), 64),
                        Duration = ProbeMapper.Num(e, "duration"),
                        Views = ProbeMapper.Num(e, "view_count") is { } v ? (long)v : null,
                        Likes = ProbeMapper.Num(e, "like_count") is { } l ? (long)l : null,
                        Categories = Strings(e, "categories", 5),
                        Tags = Strings(e, "tags", 12),
                        Genre = Clip(ProbeMapper.Str(e, "genre") ?? Strings(e, "genres", 1).FirstOrDefault(), 100),
                        Language = Clip(ProbeMapper.Str(e, "language"), 20),
                        AgeLimit = ProbeMapper.Num(e, "age_limit") is { } a ? (int)a : null,
                        UploadDate = ProbeMapper.Str(e, "upload_date") is { Length: 8 } d ? $"{d[..4]}-{d[4..6]}-{d[6..]}" : null,
                        PlaylistIndex = ProbeMapper.Num(e, "playlist_index") is { } p ? (int)p : null,
                        LiveStatus = Clip(ProbeMapper.Str(e, "live_status"), 30),
                        FilesizeApprox = ProbeMapper.Num(e, "filesize_approx") is { } f ? (long)f : null,
                        Height = ProbeMapper.Num(e, "height") is { } h ? (int)h : null,
                    });
                }
                catch (JsonException)
                {
                }
            }
        }
        catch (IOException)
        {
        }
        return list;
    }

    internal static List<string> Strings(JsonElement e, string name, int max) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Array
            ? p.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!)
                .Where(s => s.Length is > 0 and <= 100).Take(max).ToList()
            : [];

    private static string? Clip(string? s, int max) => s is null ? null : s.Length > max ? s[..max] : s;
}
