using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Ark.Ydl.Options;
using Ark.Ydl.Util;
using Ark.Ydl.YtDlp;
using Microsoft.Extensions.Options;

namespace Ark.Ydl.Jobs;

/// <summary>In-memory job store plus the FIFO queue the workers drain.</summary>
public sealed partial class JobManager(IOptions<YdlOptions> opt, YdlPaths paths, ILogger<JobManager> log)
{
    private readonly ConcurrentDictionary<string, DownloadJob> _jobs = new();
    private readonly Channel<DownloadJob> _queue = Channel.CreateUnbounded<DownloadJob>();
    private long _sequence;

    [GeneratedRegex("^[A-Za-z0-9_-]{22}$")]
    private static partial Regex JobIdRx();

    public ChannelReader<DownloadJob> Reader => _queue.Reader;
    public IEnumerable<DownloadJob> All => _jobs.Values;
    public TimeSpan Retention => TimeSpan.FromMinutes(Math.Max(5, opt.Value.RetentionMinutes));

    public (DownloadJob? Job, string? Error) Enqueue(DownloadRequest request, UrlAnalysis url, string owner)
    {
        var o = opt.Value;
        var active = _jobs.Values.Count(j => j.Owner == owner && !j.IsTerminal);
        if (active >= Math.Max(1, o.MaxActiveJobsPerClient))
            return (null, $"You already have {active} downloads going. Let one finish (or cancel it) first.");
        if (_jobs.Values.Count(j => !j.IsTerminal) >= 200)
            return (null, "The server is very busy right now. Try again in a few minutes.");

        var id = NewId();
        var dir = Path.Combine(paths.Downloads, id);
        Directory.CreateDirectory(dir);
        var job = new DownloadJob(id, Interlocked.Increment(ref _sequence), request, url, dir, owner);
        _jobs[id] = job;
        _queue.Writer.TryWrite(job);
        log.LogInformation("Job {Id} queued: {Mode} {Url}", id, request.Mode, url.Url);
        return (job, null);
    }

    public DownloadJob? Get(string id) => JobIdRx().IsMatch(id) && _jobs.TryGetValue(id, out var j) ? j : null;

    public int? QueuePosition(DownloadJob job) =>
        job.Status == JobStatus.Queued
            ? 1 + _jobs.Values.Count(j => j.Status == JobStatus.Queued && j.Sequence < job.Sequence)
            : null;

    public JobSnapshot Snapshot(DownloadJob job) => job.Snapshot(QueuePosition(job));

    /// <summary>A job left the queue: every waiting job's position changed.</summary>
    public void NotifyQueueMoved()
    {
        foreach (var j in _jobs.Values.Where(j => j.Status == JobStatus.Queued)) j.Touch();
    }

    public void Cancel(DownloadJob job)
    {
        if (job.TryCancelQueued(Retention))
        {
            TryDeleteDir(job.Directory);
            NotifyQueueMoved();
            return;
        }
        if (!job.IsTerminal) job.Cts.Cancel();
    }

    /// <summary>Cancel if needed, forget the job and delete its files.</summary>
    public void Remove(DownloadJob job)
    {
        if (!job.IsTerminal) Cancel(job);
        _jobs.TryRemove(job.Id, out _);
        TryDeleteDir(job.Directory);
    }

    /// <summary>Builds (once) and returns a ZIP of every file in a finished job. Stored, not compressed: media is already compressed.</summary>
    public async Task<(string Path, string Name)?> EnsureZipAsync(DownloadJob job, CancellationToken ct)
    {
        if (job.Status != JobStatus.Completed || job.Files.Count == 0) return null;
        var name = SafeFileName(job.PlaylistTitle ?? job.Request.Title ?? "ark-ydl") + ".zip";
        var zipDir = Path.Combine(job.Directory, ".zip");
        var zipPath = Path.Combine(zipDir, "bundle.zip");
        await job.ZipLock.WaitAsync(ct);
        try
        {
            if (File.Exists(zipPath)) return (zipPath, name);
            Directory.CreateDirectory(zipDir);
            var building = zipPath + ".building";
            var files = job.Files.ToList();
            await Task.Run(() =>
            {
                using var fs = File.Create(building);
                using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
                foreach (var f in files)
                {
                    ct.ThrowIfCancellationRequested();
                    zip.CreateEntryFromFile(Path.Combine(job.Directory, f.RelativePath),
                        f.RelativePath.Replace('\\', '/'), CompressionLevel.NoCompression);
                }
            }, ct);
            File.Move(building, zipPath, overwrite: true);
            return (zipPath, name);
        }
        finally
        {
            job.ZipLock.Release();
        }
    }

    public static string NewId()
    {
        Span<byte> b = stackalloc byte[16];
        RandomNumberGenerator.Fill(b);
        return Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static bool LooksLikeJobId(string name) => JobIdRx().IsMatch(name);

    public static string SafeFileName(string name)
    {
        var bad = Path.GetInvalidFileNameChars().Concat(['<', '>', ':', '"', '/', '\\', '|', '?', '*']).ToHashSet();
        var clean = new string(name.Select(c => bad.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim().Trim('.');
        if (clean.Length > 120) clean = clean[..120].Trim();
        return clean.Length == 0 ? "download" : clean;
    }

    internal static void TryDeleteDir(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // a file may still be open (a download in flight); the janitor retries
        }
    }
}
