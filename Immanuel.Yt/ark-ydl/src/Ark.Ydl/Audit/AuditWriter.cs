using System.Text;
using System.Threading.Channels;
using Ark.Rapid;
using Ark.Ydl.Options;
using Microsoft.Extensions.Options;

namespace Ark.Ydl.Audit;

public abstract record AuditOp;

public sealed record InsertOp(string Table, Dictionary<string, object?> Row) : AuditOp;

/// <summary>An UPDATE whose values are computed when it is written, so it always carries the latest state.</summary>
public sealed record UpdateOp(string Table, string KeyColumn, string Key, Func<Dictionary<string, object?>> Values) : AuditOp;

/// <summary>A finished job's file was saved once more.</summary>
public sealed record ServedOp(string JobId, long Bytes, DateTime AtUtc) : AuditOp;

public sealed record AuditWriterStatus(
    string State, long Written, long Dropped, long Failed, int Queued, DateTimeOffset? LastWriteAt, string? LastError, DateTimeOffset? LastErrorAt);

/// <summary>
/// The only thing that writes audit rows. Requests and job events drop rows into a bounded queue and move on; this
/// drains it in batches (one transaction per batch) on its own thread, so a slow disk never slows a request down.
/// If the database can't be opened (no write access, a 32-bit pool without the native library…) downloads carry on
/// and the admin console says why.
/// </summary>
public sealed class AuditWriter : BackgroundService
{
    private const int MaxBatch = 250;
    private const int MaxText = 64 * 1024;

    private readonly Channel<AuditOp> _queue;
    private readonly Func<string, ArkDbManager> _dbs;
    private readonly ILogger<AuditWriter> _log;
    private readonly bool _enabled;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _written, _dropped, _failed;
    private string _state;
    private string? _lastError;
    private DateTimeOffset? _lastWriteAt, _lastErrorAt;

    public AuditWriter(IOptions<YdlOptions> opt, Func<string, ArkDbManager> dbs, ILogger<AuditWriter> log)
    {
        _dbs = dbs;
        _log = log;
        _enabled = opt.Value.Audit.Enabled;
        _state = _enabled ? "starting" : "off";
        _queue = Channel.CreateBounded<AuditOp>(new BoundedChannelOptions(Math.Clamp(opt.Value.Audit.QueueCapacity, 100, 1_000_000))
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait, // TryWrite then fails, and the row is counted as dropped
        });
    }

    public ArkDbManager Db => _dbs(ArkDatabaseOptions.Audit);

    /// <summary>Completes once the schema exists (or faults if it never will). Readers wait on it.</summary>
    public Task Ready => _ready.Task;

    public bool IsReady => _ready.Task.IsCompletedSuccessfully;

    public AuditWriterStatus Status => new(_state, Interlocked.Read(ref _written), Interlocked.Read(ref _dropped),
        Interlocked.Read(ref _failed), _queue.Reader.CanCount ? _queue.Reader.Count : 0, _lastWriteAt, _lastError, _lastErrorAt);

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

    private volatile bool _busy;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            _ready.TrySetException(new InvalidOperationException("Auditing is off (Ydl:Audit:Enabled=false)."));
            return;
        }

        // Create the schema, retrying: a locked or not-yet-writable folder on first deploy shouldn't disable auditing for good.
        var wait = TimeSpan.FromSeconds(5);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await AuditSchema.EnsureCreatedAsync(Db);
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
                    // Let a burst gather briefly, then take everything that's there (up to a batch).
                    await Task.Delay(50, stoppingToken);
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

        // Shutting down: write what is still queued, briefly.
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
        var db = Db;
        var statements = new List<string>(batch.Count);
        foreach (var op in batch)
        {
            try { statements.Add(await ToSqlAsync(db, op)); }
            catch (Exception ex) { Interlocked.Increment(ref _failed); Fail(ex, "An audit row couldn't be prepared"); }
        }
        if (statements.Count == 0) return;
        try
        {
            var sql = new StringBuilder("BEGIN;\n");
            foreach (var s in statements) sql.Append(s).Append('\n');
            sql.Append("COMMIT;");
            await db.ExecuteAsync(sql.ToString());
            Interlocked.Add(ref _written, statements.Count);
            _lastWriteAt = DateTimeOffset.UtcNow;
            if (_state != "ready") _state = "ready";
        }
        catch (Exception ex)
        {
            // One bad row must not sink the batch: replay row by row through the library's own DML.
            Fail(ex, "An audit batch failed; retrying row by row");
            foreach (var op in batch)
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

    private static async Task WriteOneAsync(ArkDbManager db, AuditOp op)
    {
        switch (op)
        {
            case InsertOp i:
                await db.InsertTableAsync(i.Table, Clean(i.Row));
                break;
            case UpdateOp u:
                await db.UpdateTableAsync(u.Table, Clean(u.Values()), new Dictionary<string, object> { [u.KeyColumn] = u.Key });
                break;
            default:
                await db.ExecuteAsync(await ToSqlAsync(db, op));
                break;
        }
    }

    /// <summary>
    /// One statement per op, values inlined only through the manager's own GetSqlValueAsync (AI-SPEC.md §7: this
    /// library has no parameters) and identifiers only from this code, never from a request.
    /// </summary>
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
                return $"INSERT INTO {await db.GetSafeTableName(i.Table)} ({string.Join(", ", cols)}) VALUES ({string.Join(", ", vals)});";
            }
            case UpdateOp u:
            {
                var row = Clean(u.Values());
                var sets = new List<string>(row.Count);
                foreach (var (k, v) in row) sets.Add($"{await db.GetSafeColumnName(k)} = {await db.GetSqlValueAsync(v!)}");
                return $"UPDATE {await db.GetSafeTableName(u.Table)} SET {string.Join(", ", sets)} WHERE {await db.GetSafeColumnName(u.KeyColumn)} = {await db.GetSqlValueAsync(u.Key)};";
            }
            case ServedOp s:
            {
                var at = await db.GetSqlValueAsync(AuditClock.Format(s.AtUtc));
                return $"UPDATE {AuditSchema.Downloads} SET served_count = served_count + 1, " +
                       $"served_bytes = served_bytes + {await db.GetSqlValueAsync(Math.Max(0, s.Bytes))}, " +
                       $"first_served_utc = {db.Util.Qry_IfNull("first_served_utc", at)}, last_served_utc = {at} " +
                       $"WHERE job_id = {await db.GetSqlValueAsync(s.JobId)};";
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
                double x => double.IsFinite(x) ? Math.Round(x, 3) : null!,
                float x => float.IsFinite(x) ? Math.Round(x, 3) : null!,
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
