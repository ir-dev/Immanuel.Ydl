using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ark.Feedback.Options;
using Microsoft.Extensions.Options;

namespace Ark.Feedback.Analysis;

/// <summary>
/// Reports as JSON files under {DataRoot}/reports/{yyyy-MM}/{id}.json with a small {id}.meta.json beside each, so the
/// home page's "recent" list and the cache lookup never read a full report. Old months are pruned by the maintenance job.
/// </summary>
public sealed class ReportStore
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly FeedbackPaths _paths;
    private readonly IOptions<FeedbackOptions> _opt;
    private readonly ILogger<ReportStore> _log;
    private readonly ConcurrentDictionary<string, ReportMeta> _meta = new(StringComparer.Ordinal);
    private readonly Lazy<bool> _indexed;

    public ReportStore(FeedbackPaths paths, IOptions<FeedbackOptions> opt, ILogger<ReportStore> log)
    {
        _paths = paths;
        _opt = opt;
        _log = log;
        _indexed = new Lazy<bool>(LoadIndex);
    }

    private static string Month(string id) => id.Length >= 6 ? $"{id[..4]}-{id[4..6]}" : "unknown";

    public static bool IsValidId(string? id) =>
        id is { Length: >= 17 and <= 24 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') && id[8] == '-';

    public string PathOf(string id) => Path.Combine(_paths.Reports, Month(id), id + ".json");

    private bool LoadIndex()
    {
        try
        {
            if (!Directory.Exists(_paths.Reports)) return true;
            foreach (var f in Directory.EnumerateFiles(_paths.Reports, "*.meta.json", SearchOption.AllDirectories))
            {
                try
                {
                    var m = JsonSerializer.Deserialize<ReportMeta>(File.ReadAllText(f), Json);
                    if (m is not null) _meta[m.Id] = m;
                }
                catch (Exception ex) when (ex is JsonException or IOException)
                {
                    _log.LogDebug(ex, "Skipping unreadable report meta {File}", f);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Couldn't index stored reports");
        }
        return true;
    }

    public async Task SaveAsync(Report report, CancellationToken ct)
    {
        _ = _indexed.Value;
        var path = PathOf(report.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        await using (var fs = File.Create(tmp))
            await JsonSerializer.SerializeAsync(fs, report, Json, ct);
        File.Move(tmp, path, overwrite: true);
        await SaveMetaAsync(report, ct);
    }

    private async Task SaveMetaAsync(Report report, CancellationToken ct)
    {
        var meta = new ReportMeta
        {
            Id = report.Id, CreatedAt = report.CreatedAt, VideoId = report.Video.Id, Title = report.Video.Title, Channel = report.Video.Channel,
            Thumbnail = report.Video.Thumbnail, Profile = report.Profile, Analysed = report.Fetch.Analysed, Score = report.Sentiment.Score,
            Verdict = report.Sentiment.Verdict, PrimaryLanguage = report.Languages.FirstOrDefault()?.Key,
            Genre = report.Audience?.Genre.Label, GenreIcon = report.Audience?.Genre.Icon, AgeGroup = report.Audience?.Age.Label, AgeRange = report.Audience?.Age.Range,
            Reach = report.Audience?.Reach.Label, Family = report.Audience?.Family.Rating,
        };
        await File.WriteAllTextAsync(Path.ChangeExtension(PathOf(report.Id), ".meta.json"), JsonSerializer.Serialize(meta, Json), ct);
        _meta[report.Id] = meta;
    }

    public async Task<Report?> LoadAsync(string id, CancellationToken ct)
    {
        if (!IsValidId(id)) return null;
        var path = PathOf(id);
        if (!File.Exists(path)) return null;
        try
        {
            await using var fs = File.OpenRead(path);
            var report = await JsonSerializer.DeserializeAsync<Report>(fs, Json, ct);
            if (report is not null && (report.Audience is null || report.Summary is null))
            {
                // a report from before schema 2: the audience profile is computed from what was stored (the report file is left as it is);
                // the small meta file beside it is refreshed so the home page cards and share previews pick the new fields up
                try
                {
                    ReportBuilder.Enrich(report);
                    if (Meta(id) is { Genre: null }) await SaveMetaAsync(report, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogWarning(ex, "Couldn't profile the audience of report {Id}", id); }
            }
            return report;
        }
        catch (JsonException ex)
        {
            _log.LogWarning(ex, "Report {Id} is unreadable", id);
            return null;
        }
    }

    public ReportMeta? Meta(string id)
    {
        _ = _indexed.Value;
        return _meta.TryGetValue(id, out var m) ? m : null;
    }

    /// <summary>A report of the same video and profile younger than Feedback:Analysis:CacheHours, newest first.</summary>
    public ReportMeta? FindRecent(string videoId, string profile)
    {
        _ = _indexed.Value;
        var hours = _opt.Value.Analysis.CacheHours;
        if (hours <= 0) return null;
        var cutoff = DateTimeOffset.UtcNow.AddHours(-hours);
        return _meta.Values.Where(m => m.VideoId == videoId && m.Profile.Equals(profile, StringComparison.OrdinalIgnoreCase) && m.CreatedAt >= cutoff)
            .OrderByDescending(m => m.CreatedAt).FirstOrDefault();
    }

    public IReadOnlyList<ReportMeta> Recent(int count, string? videoId = null)
    {
        _ = _indexed.Value;
        var q = _meta.Values.AsEnumerable();
        if (videoId is not null) q = q.Where(m => m.VideoId == videoId);
        return q.OrderByDescending(m => m.CreatedAt).Take(Math.Clamp(count, 1, 100)).ToList();
    }

    public int Count
    {
        get { _ = _indexed.Value; return _meta.Count; }
    }

    /// <summary>Deletes report files older than the retention. Returns how many were removed.</summary>
    public int Prune(int retentionDays)
    {
        _ = _indexed.Value;
        if (retentionDays <= 0) return 0;
        var cutoff = DateTimeOffset.UtcNow.AddDays(-retentionDays);
        var removed = 0;
        foreach (var m in _meta.Values.Where(m => m.CreatedAt < cutoff).ToList())
        {
            try
            {
                var p = PathOf(m.Id);
                if (File.Exists(p)) File.Delete(p);
                var mp = Path.ChangeExtension(p, ".meta.json");
                if (File.Exists(mp)) File.Delete(mp);
                _meta.TryRemove(m.Id, out _);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.LogDebug(ex, "Couldn't delete report {Id}", m.Id);
            }
        }
        if (Directory.Exists(_paths.Reports))
            foreach (var dir in Directory.EnumerateDirectories(_paths.Reports))
                if (!Directory.EnumerateFileSystemEntries(dir).Any()) { try { Directory.Delete(dir); } catch (IOException) { } }
        return removed;
    }

    /// <summary>Report ids are {yyyyMMdd}-{8 hex}: the day (in the audit time zone) locates the audit table, the rest is random.</summary>
    public static string NewId(string day8)
    {
        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(4);
        return day8 + "-" + Convert.ToHexStringLower(bytes);
    }

    public static string Day8(DateOnly d) => d.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
}
