using Ark.Ydl.Options;
using Microsoft.Extensions.Options;

namespace Ark.Ydl.Jobs;

/// <summary>Drains the queue with MaxConcurrentJobs parallel workers.</summary>
public sealed class JobWorker(JobManager jobs, JobRunner runner, IOptions<YdlOptions> opt, ILogger<JobWorker> log) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var n = Math.Clamp(opt.Value.MaxConcurrentJobs, 1, 16);
        log.LogInformation("Starting {Count} download worker(s)", n);
        return Task.WhenAll(Enumerable.Range(0, n).Select(_ => Task.Run(() => LoopAsync(stoppingToken), stoppingToken)));
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var job in jobs.Reader.ReadAllAsync(ct))
            {
                if (job.Status != JobStatus.Queued) continue; // cancelled while waiting
                try
                {
                    await runner.RunAsync(job, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    log.LogError(ex, "Job {Id} crashed", job.Id);
                    job.Mutate(j =>
                    {
                        j.Status = JobStatus.Failed;
                        j.Stage = "Failed";
                        j.Error = "Something went wrong on the server.";
                        j.ErrorDetail = ex.Message;
                        j.FinishedAt = DateTimeOffset.UtcNow;
                        j.ExpiresAt = j.FinishedAt + jobs.Retention;
                    });
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }
}

/// <summary>Deletes expired jobs and anything a previous run left behind.</summary>
public sealed class JobJanitor(JobManager jobs, YdlPaths paths, ILogger<JobJanitor> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        SweepOrphans();
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(2));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var job in jobs.All.Where(j => j.IsTerminal && j.ExpiresAt < now).ToList())
            {
                jobs.Remove(job);
                log.LogInformation("Job {Id} expired and was deleted", job.Id);
            }
            SweepOrphans();
        }
    }

    /// <summary>
    /// The job store lives in memory, so folders from an earlier run are orphans. Only folders
    /// named like a job id are touched — a mis-pointed DownloadRoot never loses anything else.
    /// </summary>
    private void SweepOrphans()
    {
        try
        {
            var known = jobs.All.Select(j => j.Id).ToHashSet();
            foreach (var dir in Directory.EnumerateDirectories(paths.Downloads))
            {
                var name = Path.GetFileName(dir);
                if (JobManager.LooksLikeJobId(name) && !known.Contains(name))
                    JobManager.TryDeleteDir(dir);
            }
            foreach (var dir in Directory.EnumerateDirectories(paths.Cache, "probe-*"))
                if (Directory.GetCreationTimeUtc(dir) < DateTime.UtcNow.AddHours(-1))
                    JobManager.TryDeleteDir(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "Cleanup sweep failed");
        }
    }
}
