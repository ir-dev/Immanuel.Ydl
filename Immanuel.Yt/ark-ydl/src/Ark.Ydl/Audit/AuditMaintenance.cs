using Ark.Ydl.Options;
using Microsoft.Extensions.Options;

namespace Ark.Ydl.Audit;

public sealed record AuditMaintenanceStatus(DateTimeOffset? LastRollupAt, DateTimeOffset? LastPruneAt, long LastPruned, string? LastError);

/// <summary>
/// Keeps audit_daily current (today and yesterday every 10 minutes, any day missing a row at start-up) and deletes raw
/// rows older than Ydl:Audit:RetentionDays once a day — only after their day has a rollup, so the totals outlive them.
/// </summary>
public sealed class AuditMaintenance(AuditWriter writer, AuditRecorder recorder, IOptions<YdlOptions> opt, ILogger<AuditMaintenance> log)
    : BackgroundService
{
    private DateTimeOffset? _lastRollup, _lastPrune;
    private long _lastPruned;
    private string? _lastError;

    public AuditMaintenanceStatus Status => new(_lastRollup, _lastPrune, _lastPruned, _lastError);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!opt.Value.Audit.Enabled) return;
        try
        {
            while (!writer.IsReady) await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            await RunOnceAsync(backfill: true);
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(10));
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await RunOnceAsync(backfill: false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <param name="forcePrune">Apply retention now even if it ran in the last 23 hours.</param>
    internal async Task RunOnceAsync(bool backfill, bool forcePrune = false)
    {
        try
        {
            var db = writer.Db;
            var today = recorder.Clock.Today;
            var yesterday = recorder.Clock.Day(DateTime.UtcNow.AddDays(-1));
            var days = new SortedSet<string>(StringComparer.Ordinal) { yesterday, today };
            if (backfill)
            {
                var missing = await db.ExecuteSelectAsync<string>($@"
                    SELECT day FROM {AuditSchema.Requests} WHERE day NOT IN (SELECT day FROM {AuditSchema.Daily}) GROUP BY day
                    UNION
                    SELECT day FROM {AuditSchema.Downloads} WHERE day NOT IN (SELECT day FROM {AuditSchema.Daily}) GROUP BY day;");
                foreach (var d in missing) days.Add(d);
            }
            foreach (var day in days) await AuditReports.RollupAsync(db, day);
            _lastRollup = DateTimeOffset.UtcNow;

            var keep = opt.Value.Audit.RetentionDays;
            if (keep > 0 && (forcePrune || _lastPrune is null || DateTimeOffset.UtcNow - _lastPrune > TimeSpan.FromHours(23)))
            {
                var cutoff = recorder.Clock.Day(DateTime.UtcNow.AddDays(-keep));
                var c = await db.GetSqlValueAsync(cutoff);
                // Roll up whatever is about to go, in case a day never got its row.
                var going = await db.ExecuteSelectAsync<string>(
                    $"SELECT day FROM {AuditSchema.Requests} WHERE day < {c} AND day NOT IN (SELECT day FROM {AuditSchema.Daily}) GROUP BY day;");
                foreach (var d in going) await AuditReports.RollupAsync(db, d);
                var before = await db.ExecuteCountAsync($"SELECT COUNT(*) FROM {AuditSchema.Requests} WHERE day < {c};")
                             + await db.ExecuteCountAsync($"SELECT COUNT(*) FROM {AuditSchema.Downloads} WHERE day < {c};");
                if (before > 0)
                {
                    await db.ExecuteAsync($"BEGIN; DELETE FROM {AuditSchema.Requests} WHERE day < {c}; DELETE FROM {AuditSchema.Downloads} WHERE day < {c}; COMMIT;");
                    log.LogInformation("Audit retention: removed {Rows} rows from before {Cutoff}", before, cutoff);
                }
                _lastPruned = before;
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
