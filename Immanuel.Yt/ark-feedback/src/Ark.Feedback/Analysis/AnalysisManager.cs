using System.Collections.Concurrent;
using System.Threading.Channels;
using Ark.Feedback.Audit;
using Ark.Feedback.Nlp;
using Ark.Feedback.Options;
using Ark.Feedback.YouTube;
using Microsoft.Extensions.Options;

namespace Ark.Feedback.Analysis;

public sealed record EnqueueResult(AnalysisJob Job, bool Cached, int Position);

public sealed class TooBusyException(string message) : Exception(message);

/// <summary>
/// The queue: accepts requests (serving a fresh-enough stored report instead of recomputing), runs them one or two
/// at a time on worker tasks, keeps finished jobs around for two hours so a page can reconnect, and tells the audit
/// recorder about every change.
/// </summary>
public sealed class AnalysisManager : BackgroundService
{
    private readonly ConcurrentDictionary<string, AnalysisJob> _jobs = new(StringComparer.Ordinal);
    private readonly Channel<AnalysisJob> _queue = Channel.CreateUnbounded<AnalysisJob>(new UnboundedChannelOptions { SingleReader = false });
    private readonly AnalysisPipeline _pipeline;
    private readonly ReportStore _store;
    private readonly ModelCatalog _catalog;
    private readonly AuditClock _clock;
    private readonly IOptions<FeedbackOptions> _opt;
    private readonly ILogger<AnalysisManager> _log;
    private int _queued;

    public AnalysisManager(AnalysisPipeline pipeline, ReportStore store, ModelCatalog catalog, AuditClock clock, IOptions<FeedbackOptions> opt, ILogger<AnalysisManager> log)
    {
        _pipeline = pipeline;
        _store = store;
        _catalog = catalog;
        _clock = clock;
        _opt = opt;
        _log = log;
    }

    /// <summary>A job was created (queued) — the audit recorder inserts its row here.</summary>
    public event Action<AnalysisJob>? JobCreated;

    /// <summary>A job finished with a report.</summary>
    public event Action<AnalysisJob, Report>? JobCompleted;

    public int Queued => Volatile.Read(ref _queued);

    public int Running => _jobs.Values.Count(j => j.Current.State is JobState.Fetching or JobState.Analysing);

    public AnalysisJob? Get(string id) => _jobs.TryGetValue(id, out var j) ? j : null;

    public IEnumerable<AnalysisJob> Active => _jobs.Values.Where(j => !j.IsDone);

    /// <summary>Queues an analysis, or answers with a stored report when one is fresh enough and the caller didn't ask for a fresh run.</summary>
    public EnqueueResult Enqueue(VideoUrl url, string? profileId, bool fresh, ClientInfo client)
    {
        var o = _opt.Value.Analysis;
        var profile = _catalog.Resolve(profileId, o);
        var active = _jobs.Values.Count(j => !j.IsDone && j.Client.ClientKey == client.ClientKey);
        if (active >= Math.Max(1, o.MaxActivePerClient))
            throw new TooBusyException("You already have an analysis running. Wait for it to finish before starting another.");

        if (!fresh && _store.FindRecent(url.VideoId, profile.Id) is { } recent)
        {
            var cached = new AnalysisJob(ReportStore.NewId(Day8()), url, profile, client, fresh: false);
            cached.Update(JobState.Analysing, "cache", "Found a recent report", 1);
            _jobs[cached.Id] = cached;
            cached.Complete(new Report
            {
                Id = recent.Id, CreatedAt = recent.CreatedAt, Profile = recent.Profile, ProfileName = ModelCatalog.Profile(recent.Profile)?.Name ?? recent.Profile,
                AppVersion = AnalysisPipeline.AppVersion, Video = new VideoInfo { Id = recent.VideoId, Title = recent.Title, Channel = recent.Channel, Thumbnail = recent.Thumbnail },
                Fetch = new FetchSummary { Source = "cache", Analysed = recent.Analysed }, Sentiment = new SentimentSummary { Verdict = recent.Verdict, VerdictText = "", Score = recent.Score },
                Compute = new ComputeSummary { FromCache = true },
            });
            return new EnqueueResult(cached, Cached: true, 0);
        }

        // Already queued or running for the same video + profile? Join it instead of doubling the work.
        var twin = _jobs.Values.FirstOrDefault(j => !j.IsDone && j.Url.VideoId == url.VideoId && j.Profile.Id == profile.Id);
        if (twin is not null) return new EnqueueResult(twin, Cached: false, twin.QueuePosition);

        if (Queued + Running >= Math.Max(1, o.MaxQueued))
            throw new TooBusyException("The server is busy with other analyses right now. Try again in a few minutes.");

        var job = new AnalysisJob(ReportStore.NewId(Day8()), url, profile, client, fresh);
        _jobs[job.Id] = job;
        var position = Interlocked.Increment(ref _queued);
        job.QueuePosition = position;
        job.Update(JobState.Queued, "queued", position > 1 ? $"Waiting for a free slot ({position - 1} ahead)" : "Waiting for a free slot", 0);
        try { JobCreated?.Invoke(job); } catch (Exception ex) { _log.LogDebug(ex, "JobCreated listener failed"); }
        _queue.Writer.TryWrite(job);
        return new EnqueueResult(job, Cached: false, position);
    }

    public bool Cancel(string id, ClientInfo client)
    {
        if (!_jobs.TryGetValue(id, out var job) || job.IsDone) return false;
        if (job.Client.ClientKey != client.ClientKey) return false;
        job.Cancel("Canceled by the user");
        return true;
    }

    private string Day8() => ReportStore.Day8(DateOnly.FromDateTime(_clock.Local(DateTime.UtcNow)));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workers = Math.Max(1, _opt.Value.Analysis.MaxConcurrentAnalyses) + 1; // one extra so a fetch can overlap an inference
        var tasks = Enumerable.Range(0, workers).Select(_ => WorkerAsync(stoppingToken)).ToList();
        tasks.Add(JanitorAsync(stoppingToken));
        await Task.WhenAll(tasks);
    }

    private async Task WorkerAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var job in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                Interlocked.Decrement(ref _queued);
                foreach (var other in _jobs.Values.Where(j => j.Current.State == JobState.Queued)) other.QueuePosition = Math.Max(0, other.QueuePosition - 1);
                if (job.IsDone) continue;
                job.StartedAt = DateTimeOffset.UtcNow;
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, job.Cancellation.Token);
                try
                {
                    var report = await _pipeline.RunAsync(job, linked.Token);
                    await _store.SaveAsync(report, CancellationToken.None);
                    job.Complete(report);
                    try { JobCompleted?.Invoke(job, report); } catch (Exception ex) { _log.LogDebug(ex, "JobCompleted listener failed"); }
                }
                catch (OperationCanceledException) when (job.Cancellation.IsCancellationRequested)
                {
                    // already marked canceled
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    job.Fail("The server is shutting down.", null, retryable: true);
                }
                catch (CommentSourceException ex)
                {
                    job.Fail(ex.Message, ex.Detail, ex.Retryable);
                }
                catch (ModelUnavailableException ex)
                {
                    job.Fail(ex.Message, null, retryable: true);
                }
                catch (TimeoutException ex)
                {
                    job.Fail("The analysis took too long and was stopped.", ex.Message, retryable: true);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Analysis {Id} failed", job.Id);
                    job.Fail("Something went wrong while analysing this video.", ex.Message, retryable: true);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task JanitorAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var cutoff = DateTimeOffset.UtcNow.AddHours(-2);
                foreach (var j in _jobs.Values.Where(j => j.IsDone && (j.FinishedAt ?? j.CreatedAt) < cutoff).ToList())
                    _jobs.TryRemove(j.Id, out _);
                var days = _opt.Value.Analysis.ReportRetentionDays;
                if (days > 0)
                {
                    var removed = _store.Prune(days);
                    if (removed > 0) _log.LogInformation("Report retention: removed {Count} reports older than {Days} days", removed, days);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
