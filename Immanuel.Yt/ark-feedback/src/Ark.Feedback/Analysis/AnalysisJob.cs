using System.Threading.Channels;
using Ark.Feedback.Audit;
using Ark.Feedback.Nlp;
using Ark.Feedback.YouTube;

namespace Ark.Feedback.Analysis;

public enum JobState { Queued, Fetching, Analysing, Completed, Failed, Canceled }

/// <summary>A snapshot of a job for the page: state, what it is doing, how far along, and the report id once done.</summary>
public sealed record JobEvent(
    string Id, JobState State, string Stage, string Message, double? Progress, int? Fetched, long? Total, int? Analysed,
    string? ReportId, string? Error, string? ErrorDetail, bool Retryable, int QueuePosition, DateTimeOffset At);

/// <summary>One requested analysis, from the moment it is queued to its report (or failure), with live events for the page.</summary>
public sealed class AnalysisJob
{
    private readonly List<Channel<JobEvent>> _subscribers = [];
    private readonly object _lock = new();

    public AnalysisJob(string id, VideoUrl url, AnalysisProfile profile, ClientInfo client, bool fresh)
    {
        Id = id;
        Url = url;
        Profile = profile;
        Client = client;
        Fresh = fresh;
        CreatedAt = DateTimeOffset.UtcNow;
        Current = new JobEvent(id, JobState.Queued, "queued", "Waiting for a free slot", 0, null, null, null, null, null, null, false, 0, CreatedAt);
    }

    public string Id { get; }
    public VideoUrl Url { get; }
    public AnalysisProfile Profile { get; }
    public ClientInfo Client { get; }
    /// <summary>The user asked for a fresh run, bypassing a cached report.</summary>
    public bool Fresh { get; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public CancellationTokenSource Cancellation { get; } = new();
    public JobEvent Current { get; private set; }
    public Report? Report { get; set; }
    public string? ReportId => Report?.Id ?? Current.ReportId;
    public bool IsDone => Current.State is JobState.Completed or JobState.Failed or JobState.Canceled;
    public int QueuePosition { get; set; }

    /// <summary>Fires on every change (the audit recorder listens).</summary>
    public event Action<AnalysisJob>? Changed;

    public void Update(JobState state, string stage, string message, double? progress = null, int? fetched = null, long? total = null, int? analysed = null)
    {
        JobEvent e;
        lock (_lock)
        {
            var c = Current;
            e = c with
            {
                State = state, Stage = stage, Message = message,
                Progress = progress ?? c.Progress, Fetched = fetched ?? c.Fetched, Total = total ?? c.Total, Analysed = analysed ?? c.Analysed,
                QueuePosition = state == JobState.Queued ? QueuePosition : 0, At = DateTimeOffset.UtcNow,
            };
            Current = e;
        }
        Publish(e);
    }

    public void Complete(Report report)
    {
        Report = report;
        FinishedAt = DateTimeOffset.UtcNow;
        JobEvent e;
        lock (_lock) e = Current = Current with { State = JobState.Completed, Stage = "done", Message = "Report ready", Progress = 1, ReportId = report.Id, Analysed = report.Fetch.Analysed, QueuePosition = 0, At = FinishedAt.Value };
        Publish(e, final: true);
    }

    public void Fail(string error, string? detail, bool retryable)
    {
        FinishedAt = DateTimeOffset.UtcNow;
        JobEvent e;
        lock (_lock) e = Current = Current with { State = JobState.Failed, Stage = "failed", Message = error, Error = error, ErrorDetail = detail, Retryable = retryable, QueuePosition = 0, At = FinishedAt.Value };
        Publish(e, final: true);
    }

    public void Cancel(string reason)
    {
        if (IsDone) return;
        Cancellation.Cancel();
        FinishedAt = DateTimeOffset.UtcNow;
        JobEvent e;
        lock (_lock) e = Current = Current with { State = JobState.Canceled, Stage = "canceled", Message = reason, QueuePosition = 0, At = FinishedAt.Value };
        Publish(e, final: true);
    }

    private void Publish(JobEvent e, bool final = false)
    {
        Channel<JobEvent>[] subs;
        lock (_lock) subs = _subscribers.ToArray();
        foreach (var s in subs)
        {
            s.Writer.TryWrite(e);
            if (final) s.Writer.TryComplete();
        }
        try { Changed?.Invoke(this); } catch { /* listeners must not break a job */ }
    }

    /// <summary>Events from now on, starting with the current snapshot; completes when the job ends.</summary>
    public async IAsyncEnumerable<JobEvent> EventsAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var ch = Channel.CreateBounded<JobEvent>(new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        JobEvent snapshot;
        lock (_lock)
        {
            snapshot = Current;
            if (!IsDone) _subscribers.Add(ch);
            else ch.Writer.TryComplete();
        }
        yield return snapshot;
        try
        {
            await foreach (var e in ch.Reader.ReadAllAsync(ct)) yield return e;
        }
        finally
        {
            lock (_lock) _subscribers.Remove(ch);
        }
    }
}
