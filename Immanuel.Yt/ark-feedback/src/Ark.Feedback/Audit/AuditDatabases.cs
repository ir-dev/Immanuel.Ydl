using System.Collections.Concurrent;
using System.Globalization;
using Ark.Feedback.Options;
using Ark.Rapid;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Ark.Feedback.Audit;

public sealed record AuditMonthFile(string Month, string Path, long Bytes, DateTime ModifiedUtc);

/// <summary>
/// One SQLite database per month — {Audit}/{Prefix}-{yyyy-MM}.db — each opened through Ark.Rapid.Database's
/// SqliteManager (IMPLEMENTATION.md §2: managers only wrap a connection string, so one per month is cheap). Old months
/// are deleted as whole files by the maintenance job, which is why the log is split this way: retention never needs a
/// DELETE, and a month's history can be copied or archived as one file.
/// </summary>
public sealed class AuditDatabases(IOptions<FeedbackOptions> opt, FeedbackPaths paths)
{
    private readonly ConcurrentDictionary<string, ArkDbManager> _managers = new(StringComparer.Ordinal);

    private AuditOptions O => opt.Value.Audit;

    public string Directory => paths.Audit;

    public string FileFor(string month) => Path.Combine(paths.Audit, $"{O.FilePrefix}-{month}.db");

    /// <summary>The manager for a month ("yyyy-MM"), creating the folder on first use.</summary>
    public ArkDbManager For(string month)
    {
        if (!AuditClock.IsMonth(month)) throw new ArgumentException($"'{month}' is not a yyyy-MM month.", nameof(month));
        return _managers.GetOrAdd(month, m => new SqliteManager(ConnectionString(FileFor(m))));
    }

    /// <summary>
    /// Pooling is off so a failed batch can never hand a connection with an open transaction back to a pool; the
    /// default timeout makes a writer wait for a reader (and the other way round) instead of failing with "database is locked".
    /// </summary>
    internal static string ConnectionString(string file)
    {
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        return new SqliteConnectionStringBuilder { DataSource = file, Pooling = false, DefaultTimeout = 30, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();
    }

    /// <summary>Month files on disk, newest first.</summary>
    public IReadOnlyList<AuditMonthFile> Months()
    {
        if (!System.IO.Directory.Exists(paths.Audit)) return [];
        var prefix = O.FilePrefix + "-";
        var list = new List<AuditMonthFile>();
        foreach (var f in System.IO.Directory.EnumerateFiles(paths.Audit, prefix + "*.db"))
        {
            var name = Path.GetFileNameWithoutExtension(f);
            var month = name[prefix.Length..];
            if (!AuditClock.IsMonth(month)) continue;
            var bytes = new[] { f, f + "-wal", f + "-shm" }.Where(File.Exists).Sum(x => new FileInfo(x).Length);
            list.Add(new AuditMonthFile(month, f, bytes, File.GetLastWriteTimeUtc(f)));
        }
        return list.OrderByDescending(m => m.Month, StringComparer.Ordinal).ToList();
    }

    public bool Exists(string month) => File.Exists(FileFor(month));

    /// <summary>Months between two days inclusive, oldest first, whether or not a file exists yet.</summary>
    public static IEnumerable<string> MonthsBetween(string fromDay, string toDay)
    {
        if (!AuditClock.TryParseDay(fromDay, out var f) || !AuditClock.TryParseDay(toDay, out var t)) yield break;
        var m = new DateOnly(f.Year, f.Month, 1);
        var end = new DateOnly(t.Year, t.Month, 1);
        while (m <= end)
        {
            yield return m.ToString("yyyy-MM", CultureInfo.InvariantCulture);
            m = m.AddMonths(1);
        }
    }

    /// <summary>Forgets a month's manager (before its file is deleted).</summary>
    public void Forget(string month) => _managers.TryRemove(month, out _);
}
