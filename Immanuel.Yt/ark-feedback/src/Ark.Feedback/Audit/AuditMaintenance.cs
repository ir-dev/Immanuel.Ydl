using Ark.Feedback.Options;
using Microsoft.Extensions.Options;

namespace Ark.Feedback.Audit;

public sealed record AuditMaintenanceStatus(DateTimeOffset? LastRunAt, DateTimeOffset? LastPruneAt, IReadOnlyList<string> PrunedMonths, string? LastError);

/// <summary>
/// Keeps audit_days current (row counts of today's and yesterday's tables every 10 minutes, every day of the open
/// month at start-up) and, once a day, deletes whole month files older than Feedback:Audit:RetentionMonths.
/// </summary>
public sealed class AuditMaintenance(AuditWriter writer, AuditDatabases dbs, AuditClock clock, IOptions<FeedbackOptions> opt, ILogger<AuditMaintenance> log) : BackgroundService
{
    private DateTimeOffset? _lastRun, _lastPrune;
    private readonly List<string> _pruned = [];
    private string? _lastError;

    public AuditMaintenanceStatus Status => new(_lastRun, _lastPrune, _pruned.ToList(), _lastError);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!opt.Value.Audit.Enabled) return;
        try
        {
            while (!writer.IsReady) await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
            await RunOnceAsync(wholeMonth: true);
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(10));
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await RunOnceAsync(wholeMonth: false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    internal async Task RunOnceAsync(bool wholeMonth, bool forcePrune = false)
    {
        try
        {
            var today = clock.Today;
            var yesterday = clock.Day(DateTime.UtcNow.AddDays(-1));
            var days = new SortedSet<string>(StringComparer.Ordinal) { today, yesterday };
            if (wholeMonth)
            {
                var month = clock.ThisMonth;
                if (dbs.Exists(month)) foreach (var d in await AuditSchema.DaysInFileAsync(dbs.For(month))) days.Add(d);
            }
            foreach (var day in days)
            {
                var month = day[..7];
                if (!dbs.Exists(month)) continue;
                var db = dbs.For(month);
                var day8 = AuditClock.Day8OfDay(day);
                var existing = await AuditSchema.DaysInFileAsync(db);
                if (!existing.Contains(day)) continue;
                var r = AuditSchema.Name(AuditTable.Requests, day8);
                var a = AuditSchema.Name(AuditTable.Analyses, day8);
                var c = AuditSchema.Name(AuditTable.Comments, day8);
                var requests = await db.ExecuteCountAsync($"SELECT COUNT(*) FROM {r};");
                var analyses = await db.ExecuteCountAsync($"SELECT COUNT(*) FROM {a};");
                var completed = await db.ExecuteCountAsync($"SELECT COUNT(*) FROM {a} WHERE status = 'completed';");
                var comments = await db.ExecuteCountAsync($"SELECT COUNT(*) FROM {c};");
                var feedback = await db.ExecuteCountAsync($"SELECT COUNT(*) FROM {a} WHERE liked IS NOT NULL;");
                var liked = await db.ExecuteCountAsync($"SELECT COUNT(*) FROM {a} WHERE liked = 1;");
                await db.UpdateTableAsync(AuditSchema.Days, new Dictionary<string, object>
                {
                    ["requests"] = requests, ["analyses"] = analyses, ["completed"] = completed, ["comments"] = comments, ["feedback"] = feedback, ["liked"] = liked,
                    ["updated_utc"] = AuditClock.Format(DateTime.UtcNow),
                }, new Dictionary<string, object> { ["day"] = day });
            }
            _lastRun = DateTimeOffset.UtcNow;

            var keep = opt.Value.Audit.RetentionMonths;
            if (keep > 0 && (forcePrune || _lastPrune is null || DateTimeOffset.UtcNow - _lastPrune > TimeSpan.FromHours(23)))
            {
                var cutoff = clock.Local(DateTime.UtcNow).AddMonths(-keep).ToString("yyyy-MM");
                foreach (var m in dbs.Months().Where(m => string.CompareOrdinal(m.Month, cutoff) < 0))
                {
                    dbs.Forget(m.Month);
                    foreach (var f in new[] { m.Path, m.Path + "-wal", m.Path + "-shm" })
                        try { if (File.Exists(f)) File.Delete(f); } catch (IOException ex) { log.LogWarning(ex, "Couldn't delete {File}", f); }
                    _pruned.Add(m.Month);
                    log.LogInformation("Audit retention: removed {Month} ({Bytes} bytes)", m.Month, m.Bytes);
                }
                _lastPrune = DateTimeOffset.UtcNow;
            }
            _lastError = null;
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            log.LogWarning(ex, "Audit maintenance failed");
        }
    }
}
