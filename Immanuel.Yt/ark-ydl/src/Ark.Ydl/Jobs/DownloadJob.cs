using Ark.Ydl.Util;
using Ark.Ydl.YtDlp;

namespace Ark.Ydl.Jobs;

public enum JobStatus { Queued, Running, Completed, Failed, Canceled }

public sealed record JobFile(int Index, string Name, string RelativePath, long Size, string Kind);

public sealed record JobFileDto(int Index, string Name, long Size, string Kind, string Url, string InlineUrl);

/// <summary>What the browser sees of a job (polled or streamed over SSE).</summary>
public sealed class JobSnapshot
{
    public string Id { get; init; } = "";
    public JobStatus Status { get; init; }
    public string Stage { get; init; } = "";
    public double? Progress { get; init; }
    public long? Downloaded { get; init; }
    public long? Total { get; init; }
    public double? Speed { get; init; }
    public double? Eta { get; init; }
    public int ItemIndex { get; init; }
    public int ItemCount { get; init; }
    public string? Title { get; init; }
    public string? Thumbnail { get; init; }
    public string? Label { get; init; }
    public DownloadMode Mode { get; init; }
    public bool Playlist { get; init; }
    public string? Error { get; init; }
    public string? ErrorDetail { get; init; }
    public List<string> Warnings { get; init; } = [];
    public List<JobFileDto> Files { get; init; } = [];
    public long TotalSize { get; init; }
    public string? ZipUrl { get; init; }
    public int? QueuePosition { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public long Version { get; init; }
}

/// <summary>
/// One download. State changes go through <see cref="Mutate"/>, which bumps a version and wakes
/// anyone waiting in <see cref="WaitForChangeAsync"/> (the SSE stream).
/// </summary>
public sealed class DownloadJob(string id, long sequence, DownloadRequest request, UrlAnalysis url, string directory, string owner)
{
    private readonly object _gate = new();
    private readonly Queue<string> _log = new();
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _version;

    public string Id { get; } = id;
    public long Sequence { get; } = sequence;
    public DownloadRequest Request { get; } = request;
    public UrlAnalysis Url { get; } = url;
    public string Directory { get; } = directory;
    public string Owner { get; } = owner;
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
    public CancellationTokenSource Cts { get; } = new();
    public SemaphoreSlim ZipLock { get; } = new(1, 1);

    public JobStatus Status { get; internal set; } = JobStatus.Queued;
    public string Stage { get; internal set; } = "Waiting in queue";
    public double? Progress { get; internal set; }
    public long? Downloaded { get; internal set; }
    public long? Total { get; internal set; }
    public double? Speed { get; internal set; }
    public double? Eta { get; internal set; }
    public int ItemIndex { get; internal set; }
    public int ItemCount { get; internal set; }
    public string? PlaylistTitle { get; internal set; }
    public string? Error { get; internal set; }
    public string? ErrorDetail { get; internal set; }
    public List<string> Warnings { get; internal set; } = [];
    public List<JobFile> Files { get; internal set; } = [];
    public DateTimeOffset? StartedAt { get; internal set; }
    public DateTimeOffset? FinishedAt { get; internal set; }
    public DateTimeOffset? ExpiresAt { get; internal set; }

    public bool IsTerminal => Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Canceled;

    public void Mutate(Action<DownloadJob> change)
    {
        lock (_gate)
        {
            change(this);
            Signal();
        }
    }

    /// <summary>Wake SSE listeners without changing state (e.g. the queue moved).</summary>
    public void Touch()
    {
        lock (_gate) Signal();
    }

    /// <summary>Queued → Running, unless it was cancelled while waiting.</summary>
    public bool TryStart()
    {
        lock (_gate)
        {
            if (Status != JobStatus.Queued) return false;
            Status = JobStatus.Running;
            Stage = "Starting";
            StartedAt = DateTimeOffset.UtcNow;
            Signal();
            return true;
        }
    }

    /// <summary>Queued → Canceled. False when it is already running (cancel the token instead).</summary>
    public bool TryCancelQueued(TimeSpan retention)
    {
        lock (_gate)
        {
            if (Status != JobStatus.Queued) return false;
            Status = JobStatus.Canceled;
            Stage = "Canceled";
            FinishedAt = DateTimeOffset.UtcNow;
            ExpiresAt = FinishedAt + retention;
            Signal();
            return true;
        }
    }

    public void AddLog(string line)
    {
        lock (_log)
        {
            _log.Enqueue(line.Length > 600 ? line[..600] : line);
            while (_log.Count > 200) _log.Dequeue();
        }
    }

    public IReadOnlyList<string> LogTail(int n)
    {
        lock (_log) return _log.TakeLast(n).ToList();
    }

    public async Task<bool> WaitForChangeAsync(long seenVersion, TimeSpan timeout, CancellationToken ct)
    {
        Task changed;
        lock (_gate)
        {
            if (_version != seenVersion) return true;
            changed = _changed.Task;
        }
        var winner = await Task.WhenAny(changed, Task.Delay(timeout, ct));
        ct.ThrowIfCancellationRequested();
        return winner == changed;
    }

    public JobSnapshot Snapshot(int? queuePosition)
    {
        lock (_gate)
        {
            var files = Files.Select(f => new JobFileDto(f.Index, f.Name, f.Size, f.Kind,
                $"/api/jobs/{Id}/files/{f.Index}", $"/api/jobs/{Id}/files/{f.Index}?inline=1")).ToList();
            return new JobSnapshot
            {
                Id = Id,
                Status = Status,
                Stage = Stage,
                Progress = Progress is { } p ? Math.Round(p, 4) : null,
                Downloaded = Downloaded,
                Total = Total,
                Speed = Speed is { } s ? Math.Round(s) : null,
                Eta = Eta,
                ItemIndex = ItemIndex,
                ItemCount = ItemCount,
                Title = PlaylistTitle ?? Request.Title,
                Thumbnail = Request.Thumbnail,
                Label = Request.Label,
                Mode = Request.Mode,
                Playlist = Request.Playlist,
                Error = Error,
                ErrorDetail = ErrorDetail,
                Warnings = [.. Warnings],
                Files = files,
                TotalSize = Files.Sum(f => f.Size),
                ZipUrl = Status == JobStatus.Completed && Files.Count > 1 ? $"/api/jobs/{Id}/zip" : null,
                QueuePosition = Status == JobStatus.Queued ? queuePosition : null,
                CreatedAt = CreatedAt,
                StartedAt = StartedAt,
                FinishedAt = FinishedAt,
                ExpiresAt = ExpiresAt,
                Version = _version,
            };
        }
    }

    private void Signal()
    {
        _version++;
        var old = _changed;
        _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        old.TrySetResult();
    }
}
