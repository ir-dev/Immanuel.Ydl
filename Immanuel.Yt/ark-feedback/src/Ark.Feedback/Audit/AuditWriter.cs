using System.Text;
using System.Threading.Channels;
using Ark.Feedback.Options;
using Ark.Rapid;
using Microsoft.Extensions.Options;

namespace Ark.Feedback.Audit;

/// <summary>A pending write. Day8 says which day's table (and so which month's file) it belongs to.</summary>
public abstract record AuditOp(string Day8);

public sealed record InsertOp(string Day8, AuditTable Table, Dictionary<string, object?> Row) : AuditOp(Day8);

/// <summary>An UPDATE whose values are computed when it is written, so it always carries the latest state.</summary>
public sealed record UpdateOp(string Day8, AuditTable Table, string KeyColumn, string Key, Func<Dictionary<string, object?>> Values) : AuditOp(Day8);

/// <summary>A report was opened once more.</summary>
public sealed record ViewedOp(string Day8, string AnalysisId, DateTime AtUtc) : AuditOp(Day8);

/// <summary>The asker said whether the report was useful; the last opinion wins, every opinion is counted.</summary>
public sealed record FeedbackOp(string Day8, string AnalysisId, bool Liked, string? Note, DateTime AtUtc) : AuditOp(Day8);

public sealed record AuditWriterStatus(string State, long Written, long Dropped, long Failed, int Queued, DateTimeOffset? LastWriteAt, string? LastError, DateTimeOffset? LastErrorAt, IReadOnlyList<string> OpenMonths);

/// <summary>
/// The only thing that writes audit rows. Requests and job events drop rows into a bounded queue and move on; this
/// drains it in batches on its own thread — one transaction per month file per batch — so a slow disk never slows a
/// request down. Tables are created the first time a day is seen. If a file can't be opened (no write access, a 32-bit
/// pool without the native SQLite library…) analyses carry on and the admin console says why.
/// </summary>
public sealed class AuditWriter : BackgroundService
{
    private const int MaxBatch = 300;
    private const int MaxText = 64 * 1024;

    private readonly Channel<AuditOp> _queue;
    private readonly AuditDatabases _dbs;
    private readonly ILogger<AuditWriter> _log;
    private readonly bool _enabled;
    private readonly HashSet<string> _ensuredFiles = new(StringComparer.Ordinal);
    private readonly HashSet<string> _ensuredDays = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _written, _dropped, _failed;
    private string _state;
    private string? _lastError;
    private DateTimeOffset? _lastWriteAt, _lastErrorAt;
    private volatile bool _busy;

    public AuditWriter(IOptions<FeedbackOptions> opt, AuditDatabases dbs, ILogger<AuditWriter> log)
    {
        _dbs = dbs;
        _log = log;
        _enabled = opt.Value.Audit.Enabled;
        _state = _enabled ? "starting" : "off";
        _queue = Channel.CreateBounded<AuditOp>(new BoundedChannelOptions(Math.Clamp(opt.Value.Audit.QueueCapacity, 100, 1_000_000))
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
    }

    public bool Enabled => _enabled;

    /// <summary>Completes once the current month's file is open and its per-file tables exist.</summary>
    public Task Ready => _ready.Task;

    public bool IsReady => _ready.Task.IsCompletedSuccessfully;

    public AuditWriterStatus Status => new(_state, Interlocked.Read(ref _written), Interlocked.Read(ref _dropped),
        Interlocked.Read(ref _failed), _queue.Reader.CanCount ? _queue.Reader.Count : 0, _lastWriteAt, _lastError, _lastErrorAt,
        _ensuredFiles.OrderByDescending(m => m, StringComparer.Ordinal).ToList());

    public void Enqueue(AuditOp op)
    {
        if (!_enabled) return;
        if (!_queue.Writer.TryWrite(op)) Interlocked.Increment(ref _dropped);
    }

    /// <summary>Resolves once every row queued before the call is on disk. For tests and the admin "refresh".</summary>
    public async Task FlushAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && (_queue.Reader.Count > 0 || _busy))
            await Task.Delay(25);
    }

    /// <summary>Makes sure a month file and a day's tables exist (readers that race the first write call this too).</summary>
    public async Task EnsureAsync(string day8)
    {
        var month = AuditClock.MonthOfDay8(day8);
        var db = _dbs.For(month);
        if (!_ensuredFiles.Contains(month))
        {
            await AuditSchema.EnsureFileAsync(db);
            lock (_ensuredFiles) _ensuredFiles.Add(month);
        }
        if (!_ensuredDays.Contains(day8))
        {
            await AuditSchema.EnsureDayAsync(db, day8);
            lock (_ensuredDays) _ensuredDays.Add(day8);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            _ready.TrySetException(new InvalidOperationException("Auditing is off (Feedback:Audit:Enabled=false)."));
            return;
        }

        var wait = TimeSpan.FromSeconds(5);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var month = DateTime.UtcNow.ToString("yyyy-MM"); // the real zone-aware month is ensured on first write; this just proves the folder works
                await AuditSchema.EnsureFileAsync(_dbs.For(month));
                lock (_ensuredFiles) _ensuredFiles.Add(month);
                _state = "ready";
                _ready.TrySetResult();
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _state = "unavailable";
                Fail(ex, "Couldn't open the audit database");
                try { await Task.Delay(wait, stoppingToken); }
                catch (OperationCanceledException) { return; }
                wait = TimeSpan.FromTicks(Math.Min(wait.Ticks * 2, TimeSpan.FromMinutes(5).Ticks));
            }
        }

        var batch = new List<AuditOp>(MaxBatch);
        try
        {
            while (await _queue.Reader.WaitToReadAsync(stoppingToken))
            {
                _busy = true;
                try
                {
                    await Task.Delay(50, stoppingToken); // let a burst gather
                    while (batch.Count < MaxBatch && _queue.Reader.TryRead(out var op)) batch.Add(op);
                    await WriteBatchAsync(batch);
                }
                finally
                {
                    batch.Clear();
                    _busy = false;
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }

        while (_queue.Reader.TryRead(out var op))
        {
            batch.Add(op);
            if (batch.Count == MaxBatch) { await WriteBatchAsync(batch); batch.Clear(); }
        }
        if (batch.Count > 0) await WriteBatchAsync(batch);
    }

    internal async Task WriteBatchAsync(IReadOnlyList<AuditOp> batch)
    {
        if (batch.Count == 0) return;
        foreach (var group in batch.GroupBy(op => AuditClock.MonthOfDay8(op.Day8)))
        {
            ArkDbManager db;
            try
            {
                db = _dbs.For(group.Key);
                foreach (var day in group.Select(o => o.Day8).Distinct()) await EnsureAsync(day);
            }
            catch (Exception ex)
            {
                Interlocked.Add(ref _failed, group.Count());
                Fail(ex, $"Couldn't open the audit file for {group.Key}");
                continue;
            }
            var statements = new List<string>(group.Count());
            foreach (var op in group)
            {
                try { statements.Add(await ToSqlAsync(db, op)); }
                catch (Exception ex) { Interlocked.Increment(ref _failed); Fail(ex, "An audit row couldn't be prepared"); }
            }
            if (statements.Count == 0) continue;
            try
            {
                var sql = new StringBuilder("BEGIN;\n");
                foreach (var s in statements) sql.Append(s).Append('\n');
                sql.Append("COMMIT;");
                await db.ExecuteAsync(sql.ToString());
                Interlocked.Add(ref _written, statements.Count);
                _lastWriteAt = DateTimeOffset.UtcNow;
                _state = "ready";
            }
            catch (Exception ex)
            {
                // One bad row must not sink the batch: replay row by row through the library's own DML.
                Fail(ex, "An audit batch failed; retrying row by row");
                foreach (var op in group)
                {
                    try
                    {
                        await WriteOneAsync(db, op);
                        Interlocked.Increment(ref _written);
                        _lastWriteAt = DateTimeOffset.UtcNow;
                    }
                    catch (Exception one)
                    {
                        Interlocked.Increment(ref _failed);
                        Fail(one, "An audit row couldn't be written");
                    }
                }
            }
        }
    }

    private static async Task WriteOneAsync(ArkDbManager db, AuditOp op)
    {
        switch (op)
        {
            case InsertOp i:
                await db.InsertTableAsync(AuditSchema.Name(i.Table, i.Day8), Clean(i.Row));
                break;
            case UpdateOp u:
                await db.UpdateTableAsync(AuditSchema.Name(u.Table, u.Day8), Clean(u.Values()), new Dictionary<string, object> { [u.KeyColumn] = u.Key });
                break;
            default:
                await db.ExecuteAsync(await ToSqlAsync(db, op));
                break;
        }
    }

    /// <summary>One statement per op; values only through GetSqlValueAsync (AI-SPEC.md §7), identifiers only from this code.</summary>
    internal static async Task<string> ToSqlAsync(ArkDbManager db, AuditOp op)
    {
        switch (op)
        {
            case InsertOp i:
            {
                var row = Clean(i.Row);
                var cols = new List<string>(row.Count);
                var vals = new List<string>(row.Count);
                foreach (var (k, v) in row)
                {
                    cols.Add(await db.GetSafeColumnName(k));
                    vals.Add(await db.GetSqlValueAsync(v!));
                }
                return $"INSERT INTO {AuditSchema.Name(i.Table, i.Day8)} ({string.Join(", ", cols)}) VALUES ({string.Join(", ", vals)});";
            }
            case UpdateOp u:
            {
                var row = Clean(u.Values());
                var sets = new List<string>(row.Count);
                foreach (var (k, v) in row) sets.Add($"{await db.GetSafeColumnName(k)} = {await db.GetSqlValueAsync(v!)}");
                return $"UPDATE {AuditSchema.Name(u.Table, u.Day8)} SET {string.Join(", ", sets)} WHERE {await db.GetSafeColumnName(u.KeyColumn)} = {await db.GetSqlValueAsync(u.Key)};";
            }
            case ViewedOp v:
            {
                var at = await db.GetSqlValueAsync(AuditClock.Format(v.AtUtc));
                return $"UPDATE {AuditSchema.Name(AuditTable.Analyses, v.Day8)} SET report_views = report_views + 1, last_viewed_utc = {at} WHERE analysis_id = {await db.GetSqlValueAsync(v.AnalysisId)};";
            }
            case FeedbackOp f:
            {
                var at = await db.GetSqlValueAsync(AuditClock.Format(f.AtUtc));
                return $"UPDATE {AuditSchema.Name(AuditTable.Analyses, f.Day8)} SET liked = {(f.Liked ? 1 : 0)}, feedback_note = {await db.GetSqlValueAsync(f.Note!)}, " +
                       $"feedback_utc = {at}, feedback_count = feedback_count + 1, updated_utc = {at} WHERE analysis_id = {await db.GetSqlValueAsync(f.AnalysisId)};";
            }
            default:
                throw new NotSupportedException(op.GetType().Name);
        }
    }

    /// <summary>Values the SQL literal encoder handles safely: no NaN, no NUL characters, bounded text, flags as 0/1.</summary>
    internal static Dictionary<string, object> Clean(Dictionary<string, object?> row)
    {
        var d = new Dictionary<string, object>(row.Count);
        foreach (var (k, v) in row)
        {
            d[k] = v switch
            {
                null => null!,
                string s => s.Length == 0 ? null! : Text(s),
                bool b => b ? 1 : 0,
                double x => double.IsFinite(x) ? Math.Round(x, 4) : null!,
                float x => float.IsFinite(x) ? Math.Round(x, 4) : null!,
                DateTime t => AuditClock.Format(t.Kind == DateTimeKind.Local ? t.ToUniversalTime() : t),
                DateTimeOffset t => AuditClock.Format(t),
                Enum e => e.ToString().ToLowerInvariant(),
                _ => v,
            };
        }
        return d;
    }

    private static string Text(string s)
    {
        if (s.Contains('\0')) s = s.Replace("\0", "");
        return s.Length > MaxText ? s[..MaxText] : s;
    }

    private void Fail(Exception ex, string what)
    {
        _lastError = $"{what}: {ex.Message}";
        _lastErrorAt = DateTimeOffset.UtcNow;
        _log.LogWarning(ex, "{What}", what);
    }
}
