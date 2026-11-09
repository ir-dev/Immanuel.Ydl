using System.ComponentModel;
using System.Text.RegularExpressions;
using Ark.Ydl.Options;
using Ark.Ydl.Tools;
using Ark.Ydl.YtDlp;
using Microsoft.Extensions.Options;

namespace Ark.Ydl.Jobs;

/// <summary>Runs one job: yt-dlp with live progress, then collects (and tidies) the output.</summary>
public sealed class JobRunner(ToolLocator tools, JobManager jobs, IOptions<YdlOptions> opt, YdlPaths paths, ILogger<JobRunner> log)
{
    public async Task RunAsync(DownloadJob job, CancellationToken stoppingToken)
    {
        if (!job.TryStart()) return;
        jobs.NotifyQueueMoved();
        var o = opt.Value;
        var retention = jobs.Retention;

        try
        {
            await tools.WhenInitialized.WaitAsync(TimeSpan.FromMinutes(3), stoppingToken);
        }
        catch (TimeoutException)
        {
        }
        if (tools.Status.YtDlp.Path is not { } ytDlp)
        {
            Fail(job, "The download engine (yt-dlp) isn't installed on this server.", tools.Status.YtDlp.Note, retention);
            return;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, job.Cts.Token);
        var cookies = CookieJar.CopyInto(o, Path.Combine(job.Directory, ".tmp"));
        var common = YtDlpArgs.Common(o, paths.Cache, tools.ToolArgs(), cookies);
        var args = YtDlpArgs.Download(job.Request, job.Url, common, job.Directory, o);
        var tracker = new ProgressTracker(job.Request.Mode == DownloadMode.Audio);
        var gate = new object();
        log.LogDebug("Job {Id}: yt-dlp {Args}", job.Id, string.Join(' ', args));

        int exit;
        try
        {
            exit = await ProcessRunner.RunStreamingAsync(ytDlp, args, (line, _) =>
            {
                lock (gate)
                {
                    job.AddLog(line);
                    if (!tracker.Feed(line)) return;
                    job.Mutate(j =>
                    {
                        j.Stage = tracker.Stage;
                        j.Progress = tracker.Overall;
                        j.Downloaded = tracker.Downloaded;
                        j.Total = tracker.Total;
                        j.Speed = tracker.Speed;
                        j.Eta = tracker.Eta;
                        j.ItemIndex = tracker.ItemIndex;
                        j.ItemCount = tracker.ItemCount;
                        j.PlaylistTitle ??= tracker.PlaylistTitle;
                    });
                }
            }, job.Directory, linked.Token);
        }
        catch (OperationCanceledException)
        {
            if (stoppingToken.IsCancellationRequested)
            {
                Fail(job, "The server restarted during this download. Please try again.", null, retention);
                throw;
            }
            job.Mutate(j =>
            {
                j.Status = JobStatus.Canceled;
                j.Stage = "Canceled";
                j.Progress = null;
                j.FinishedAt = DateTimeOffset.UtcNow;
                j.ExpiresAt = j.FinishedAt + retention;
            });
            JobManager.TryDeleteDir(job.Directory);
            log.LogInformation("Job {Id} canceled", job.Id);
            return;
        }
        catch (Win32Exception ex)
        {
            Fail(job, "Couldn't start the download engine on this server.", ex.Message, retention);
            return;
        }

        Tidy(job);
        var files = JobFiles.Collect(job.Directory);
        List<string> errors, warnings;
        int filtered;
        lock (gate)
        {
            errors = [.. tracker.Errors];
            warnings = [.. tracker.Warnings];
            filtered = tracker.FilteredOut;
        }

        if (files.Count > 0)
        {
            var notes = new List<string>();
            if (exit != 0 && errors.Count > 0)
            {
                var playlist = YtDlpArgs.IsPlaylist(job.Request, job.Url);
                notes.Add(playlist
                    ? $"{errors.Count} item(s) couldn't be downloaded — the rest are ready."
                    : "Finished with errors: " + YtDlpErrors.Translate(errors).Message);
            }
            if (filtered > 0 && YtDlpArgs.IsPlaylist(job.Request, job.Url))
                notes.Add($"{filtered} live or over-long item(s) were skipped.");
            if (warnings.Any(w => w.Contains("Requested formats are incompatible", StringComparison.OrdinalIgnoreCase)))
                notes.Add("The chosen container didn't fit these streams, so a different one was used.");

            job.Mutate(j =>
            {
                j.Status = JobStatus.Completed;
                j.Stage = "Ready";
                j.Progress = 1;
                j.Speed = j.Eta = null;
                j.Files = files;
                j.Warnings = notes;
                j.FinishedAt = DateTimeOffset.UtcNow;
                j.ExpiresAt = j.FinishedAt + retention;
            });
            log.LogInformation("Job {Id} completed: {Count} file(s), {Bytes} bytes", job.Id, files.Count, files.Sum(f => f.Size));
            return;
        }

        string message;
        string? detail;
        if (filtered > 0 && errors.Count == 0)
        {
            message = o.MaxDurationMinutes > 0
                ? $"This is a live stream that's still running, or it's longer than this server allows ({o.MaxDurationMinutes} min)."
                : "This is a live stream that's still running. Try again once it has ended.";
            detail = null;
        }
        else if (errors.Count == 0 && exit == 0)
        {
            message = job.Request.Mode switch
            {
                DownloadMode.Subtitles => "No subtitles were found in the chosen languages.",
                DownloadMode.Thumbnail => "This video has no thumbnail to download.",
                _ => "Nothing was downloaded.",
            };
            detail = string.Join('\n', job.LogTail(6));
        }
        else
        {
            (message, detail) = YtDlpErrors.Translate(errors, string.Join('\n', job.LogTail(25)));
        }
        Fail(job, message, detail, retention);
        log.LogInformation("Job {Id} failed (exit {Exit}): {Message} | {Detail}", job.Id, exit, message, detail);
    }

    private static void Fail(DownloadJob job, string message, string? detail, TimeSpan retention) =>
        job.Mutate(j =>
        {
            j.Status = JobStatus.Failed;
            j.Stage = "Failed";
            j.Progress = null;
            j.Speed = j.Eta = null;
            j.Error = message;
            j.ErrorDetail = detail;
            j.FinishedAt = DateTimeOffset.UtcNow;
            j.ExpiresAt = j.FinishedAt + retention;
        });

    /// <summary>Removes scratch folders and, for "embed only" subtitles, the loose subtitle files.</summary>
    private static void Tidy(DownloadJob job)
    {
        JobManager.TryDeleteDir(Path.Combine(job.Directory, ".tmp"));
        var r = job.Request;
        var full = Path.Combine(job.Directory, ".full");
        if (r.Clip is { SplitChapters: true } && System.IO.Directory.Exists(full))
        {
            var chapters = System.IO.Directory.EnumerateFiles(job.Directory, "*", SearchOption.TopDirectoryOnly)
                .Any(f => JobFiles.KindOf(f) is "video" or "audio" && !JobFiles.IsPartial(Path.GetFileName(f)));
            if (!chapters)
            {
                // No chapters to split on: hand over the whole file rather than nothing.
                foreach (var f in System.IO.Directory.EnumerateFiles(full))
                    if (!JobFiles.IsPartial(Path.GetFileName(f)))
                        File.Move(f, Path.Combine(job.Directory, Path.GetFileName(f)), overwrite: true);
            }
            JobManager.TryDeleteDir(full);
        }

        if (r.Mode == DownloadMode.Video && r.Subtitles.Mode == SubtitleMode.Embed)
            foreach (var f in System.IO.Directory.EnumerateFiles(job.Directory, "*", SearchOption.AllDirectories))
                if (JobFiles.KindOf(f) == "subtitle")
                    TryDelete(f);
    }

    private static void TryDelete(string file)
    {
        try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

public static partial class JobFiles
{
    [GeneratedRegex(@"\.f[0-9]+(?:-[A-Za-z0-9]+)*\.[A-Za-z0-9]+$")]
    private static partial Regex IntermediateRx();

    private static readonly HashSet<string> Video = [".mp4", ".mkv", ".webm", ".mov", ".avi", ".flv", ".m4v", ".3gp"];
    private static readonly HashSet<string> Audio = [".mp3", ".m4a", ".opus", ".flac", ".wav", ".ogg", ".aac", ".mka", ".alac", ".aiff", ".oga"];
    private static readonly HashSet<string> Subtitle = [".srt", ".vtt", ".ass", ".ssa", ".lrc", ".ttml", ".srv1", ".srv2", ".srv3", ".json3"];
    private static readonly HashSet<string> Image = [".jpg", ".jpeg", ".png", ".webp", ".gif"];

    /// <summary>Finished files in a job folder; dot-folders (.tmp, .full, .zip) and partials are skipped.</summary>
    public static List<JobFile> Collect(string dir)
    {
        var list = new List<JobFile>();
        if (!System.IO.Directory.Exists(dir)) return list;
        foreach (var path in System.IO.Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var rel = Path.GetRelativePath(dir, path);
            if (rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(s => s.StartsWith('.'))) continue;
            var name = Path.GetFileName(path);
            if (IsPartial(name)) continue;
            list.Add(new JobFile(list.Count, name, rel, new FileInfo(path).Length, KindOf(name)));
        }
        // media first, then subtitles/images, keeping name order inside each group
        var ordered = list.OrderBy(f => f.Kind switch { "video" => 0, "audio" => 0, "subtitle" => 1, "image" => 2, _ => 3 })
            .ThenBy(f => f.RelativePath, StringComparer.Ordinal)
            .Select((f, i) => f with { Index = i })
            .ToList();
        return ordered;
    }

    public static bool IsPartial(string name) =>
        name.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".ytdl", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".temp", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
        || name.Contains(".part-Frag", StringComparison.OrdinalIgnoreCase)
        || name.Contains(".temp.", StringComparison.OrdinalIgnoreCase)
        || name.Equals("cookies.txt", StringComparison.OrdinalIgnoreCase)
        || IntermediateRx().IsMatch(name);

    public static string KindOf(string name)
    {
        var ext = Path.GetExtension(name).ToLowerInvariant();
        if (Video.Contains(ext)) return "video";
        if (Audio.Contains(ext)) return "audio";
        if (Subtitle.Contains(ext)) return "subtitle";
        if (Image.Contains(ext)) return "image";
        return "other";
    }

    public static string ContentType(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".mp4" or ".m4v" => "video/mp4",
        ".webm" => "video/webm",
        ".mkv" => "video/x-matroska",
        ".mov" => "video/quicktime",
        ".mp3" => "audio/mpeg",
        ".m4a" => "audio/mp4",
        ".opus" => "audio/ogg",
        ".ogg" or ".oga" => "audio/ogg",
        ".flac" => "audio/flac",
        ".wav" => "audio/wav",
        ".aac" => "audio/aac",
        ".mka" => "audio/x-matroska",
        ".srt" => "application/x-subrip",
        ".vtt" => "text/vtt",
        ".ass" or ".ssa" or ".lrc" => "text/plain; charset=utf-8",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".zip" => "application/zip",
        _ => "application/octet-stream",
    };
}
