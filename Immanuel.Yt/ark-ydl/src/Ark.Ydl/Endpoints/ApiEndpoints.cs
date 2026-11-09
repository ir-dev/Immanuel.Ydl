using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.Json;
using Ark.Ydl.Audit;
using Ark.Ydl.Jobs;
using Ark.Ydl.Options;
using Ark.Ydl.Releases;
using Ark.Ydl.Tools;
using Ark.Ydl.Util;
using Ark.Ydl.YtDlp;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Ark.Ydl.Endpoints;

public sealed record ProbeInput(string? Url, bool? Playlist);

public static class ApiEndpoints
{
    public static void MapYdlApi(this WebApplication app, bool authEnabled)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/system", (ToolLocator tools, IOptions<YdlOptions> opt, ReleaseCatalog catalog, LatestReleaseService latest, HttpContext ctx) =>
        {
            var s = tools.Status;
            var o = opt.Value;
            return Results.Ok(new
            {
                app = "ark-ydl",
                version = catalog.AppVersion,
                release = ReleaseEndpoints.VersionInfo(catalog, latest),
                audit = new { enabled = o.Audit.Enabled, requests = o.Audit.Enabled && o.Audit.LogRequests, ipMode = o.Audit.IpMode.ToString().ToLowerInvariant() },
                ready = s.Ready,
                preparing = s.Preparing,
                tools = new
                {
                    ytDlp = new { ok = s.YtDlp.Ok, version = s.YtDlp.Version, note = s.YtDlp.Note },
                    ffmpeg = new { ok = s.Ffmpeg.Ok, version = s.Ffmpeg.Version, note = s.Ffmpeg.Note },
                    jsRuntime = new { ok = s.JsRuntime.Ok, name = s.JsRuntime.Name, version = s.JsRuntime.Version, note = s.JsRuntime.Note },
                },
                limits = new
                {
                    o.MaxPlaylistItems,
                    o.MaxClipSections,
                    o.MaxDurationMinutes,
                    o.RetentionMinutes,
                    o.MaxActiveJobsPerClient,
                    o.MaxFileSize,
                },
                o.AllowAnySite,
                auth = new
                {
                    enabled = authEnabled,
                    user = ctx.User.Identity?.IsAuthenticated == true
                        ? ctx.User.FindFirst("name")?.Value ?? ctx.User.Identity.Name ?? ctx.User.FindFirst(ClaimTypes.Email)?.Value
                        : null,
                },
                sponsorCategories = DownloadRequestValidator.SponsorCategories,
            });
        });

        api.MapPost("/probe", async (ProbeInput input, ProbeService probe, IOptions<YdlOptions> opt, HttpContext ctx, CancellationToken ct) =>
        {
            var o = opt.Value;
            var url = UrlGuard.Analyze(input.Url, o.AllowedSites, o.AllowAnySite, input.Playlist);
            if (!url.Ok)
            {
                AuditRecorder.Annotate(ctx, detail: url.Error);
                return Results.BadRequest(new { error = url.Error });
            }
            var playlist = url.Kind is UrlKind.Playlist or UrlKind.Channel || (url.Kind == UrlKind.Other && input.Playlist == true);
            var outcome = await probe.ProbeAsync(url, playlist, ct);
            if (outcome.Result is { } r)
            {
                var genre = r.Categories.FirstOrDefault() ?? r.Genre;
                AuditRecorder.Annotate(ctx, videoId: r.Kind == "video" ? r.Id : url.VideoId,
                    detail: $"{(r.Kind == "playlist" ? "playlist: " : "")}{r.Title}{(genre is null ? "" : $" [{genre}]")}");
                return Results.Ok(r);
            }
            AuditRecorder.Annotate(ctx, videoId: url.VideoId, detail: outcome.Error);
            return Results.Json(new { error = outcome.Error, detail = outcome.Detail }, statusCode: outcome.Status);
        }).RequireRateLimiting("probe");

        api.MapPost("/jobs", (DownloadRequest req, JobManager jobs, ProbeService probes, AuditRecorder audit, IOptions<YdlOptions> opt, HttpContext ctx) =>
        {
            var o = opt.Value;
            var error = DownloadRequestValidator.Validate(req, o);
            if (error is not null)
            {
                AuditRecorder.Annotate(ctx, detail: error);
                return Results.BadRequest(new { error });
            }
            var url = UrlGuard.Analyze(req.Url, o.AllowedSites, o.AllowAnySite, req.Playlist);
            if (!url.Ok)
            {
                AuditRecorder.Annotate(ctx, detail: url.Error);
                return Results.BadRequest(new { error = url.Error });
            }
            if (req.Playlist && url.Kind == UrlKind.Video) req.Playlist = false;
            if (req.Thumbnail is { } t && !t.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) req.Thumbnail = null;

            // The page probed this link a moment ago: that cached result is where title, channel and genre come from.
            var probed = probes.Cached(url, YtDlpArgs.IsPlaylist(req, url)) ?? probes.Cached(url, !YtDlpArgs.IsPlaylist(req, url));
            var client = audit.Client(ctx);
            var (job, err) = jobs.Enqueue(req, url, ClientKey(ctx), created: j => audit.DownloadQueued(j, client, probed));
            AuditRecorder.Annotate(ctx, videoId: url.VideoId, jobId: job?.Id, detail: err ?? req.Label);
            return job is null
                ? Results.Json(new { error = err }, statusCode: StatusCodes.Status429TooManyRequests)
                : Results.Accepted($"/api/jobs/{job.Id}", jobs.Snapshot(job));
        }).RequireRateLimiting("jobs");

        api.MapGet("/jobs/{id}", (string id, JobManager jobs) =>
            jobs.Get(id) is { } job ? Results.Ok(jobs.Snapshot(job)) : Gone());

        api.MapGet("/jobs/{id}/events", IResult (string id, JobManager jobs, IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> json, HttpContext ctx) =>
        {
            if (jobs.Get(id) is not { } job) return Gone();
            ctx.Response.Headers["X-Accel-Buffering"] = "no"; // nginx / proxies: don't buffer the stream
            return TypedResults.ServerSentEvents(Stream(job, jobs, json.Value.SerializerOptions, ctx.RequestAborted), eventType: "job");
        });

        api.MapGet("/jobs/{id}/log", (string id, JobManager jobs) =>
            jobs.Get(id) is { } job ? Results.Ok(new { lines = job.LogTail(80) }) : Gone());

        api.MapPost("/jobs/{id}/cancel", (string id, JobManager jobs) =>
        {
            if (jobs.Get(id) is not { } job) return Gone();
            jobs.Cancel(job);
            return Results.Ok(jobs.Snapshot(job));
        });

        api.MapDelete("/jobs/{id}", (string id, JobManager jobs) =>
        {
            if (jobs.Get(id) is { } job) jobs.Remove(job);
            return Results.NoContent();
        });

        api.MapGet("/jobs/{id}/files/{index:int}", (string id, int index, string? inline, JobManager jobs, AuditRecorder audit, HttpContext ctx) =>
        {
            if (jobs.Get(id) is not { Status: JobStatus.Completed } job) return Gone();
            var file = job.Files.FirstOrDefault(f => f.Index == index);
            if (file is null) return Results.NotFound(new { error = "No such file." });
            var path = Path.Combine(job.Directory, file.RelativePath);
            if (!File.Exists(path)) return Gone();
            var type = JobFiles.ContentType(file.Name);
            AuditRecorder.Annotate(ctx, videoId: job.Url.VideoId, jobId: job.Id, detail: file.Name);
            if (inline is not ("1" or "true"))
            {
                if (IsWholeFileRequest(ctx)) audit.Served(job, file.Size); // a save, not a resumed chunk
                return Results.File(path, type, file.Name, enableRangeProcessing: true);
            }
            ctx.Response.Headers.ContentDisposition = new ContentDispositionHeaderValue("inline") { FileNameStar = file.Name }.ToString();
            return Results.File(path, type, enableRangeProcessing: true);
        });

        api.MapGet("/jobs/{id}/zip", async (string id, JobManager jobs, AuditRecorder audit, HttpContext ctx, CancellationToken ct) =>
        {
            if (jobs.Get(id) is not { } job) return Gone();
            var zip = await jobs.EnsureZipAsync(job, ct);
            if (zip is not { } z) return Results.NotFound(new { error = "Nothing to zip yet." });
            AuditRecorder.Annotate(ctx, videoId: job.Url.VideoId, jobId: job.Id, detail: z.Name);
            if (IsWholeFileRequest(ctx)) audit.Served(job, new FileInfo(z.Path).Length);
            return Results.File(z.Path, "application/zip", z.Name, enableRangeProcessing: true);
        });
    }

    /// <summary>Who owns a job, for the per-client limit: the signed-in user, else the caller's IP.</summary>
    internal static string ClientKey(HttpContext ctx)
    {
        if (ctx.User.Identity?.IsAuthenticated == true)
            return "u:" + (ctx.User.FindFirst("sub")?.Value ?? ctx.User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? ctx.User.Identity.Name);
        return "ip:" + (ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown");
    }

    /// <summary>No Range header, or one starting at byte 0: the start of a save, as opposed to a resumed or seeking request.</summary>
    internal static bool IsWholeFileRequest(HttpContext ctx)
    {
        var range = ctx.Request.Headers.Range.ToString();
        return range.Length == 0 || range.Replace(" ", "").StartsWith("bytes=0-", StringComparison.OrdinalIgnoreCase);
    }

    private static IResult Gone() =>
        Results.Json(new { error = "This download has expired or doesn't exist." }, statusCode: StatusCodes.Status410Gone);

    /// <summary>One JSON snapshot per change (coalesced), a keep-alive every 15 s, done after the final state.</summary>
    private static async IAsyncEnumerable<string> Stream(DownloadJob job, JobManager jobs, JsonSerializerOptions json,
        [EnumeratorCancellation] CancellationToken ct)
    {
        long sent = -1;
        while (!ct.IsCancellationRequested)
        {
            var snap = jobs.Snapshot(job);
            if (snap.Version != sent)
            {
                sent = snap.Version;
                yield return JsonSerializer.Serialize(snap, json);
                if (job.IsTerminal) yield break;
            }

            var changed = false;
            var aborted = false;
            try
            {
                changed = await job.WaitForChangeAsync(sent, TimeSpan.FromSeconds(15), ct);
                if (changed) await Task.Delay(150, ct); // coalesce bursts of updates
            }
            catch (OperationCanceledException)
            {
                aborted = true;
            }
            if (aborted) yield break;
            if (!changed) sent = -1; // keep-alive: resend the current state
        }
    }
}

/// <summary>
/// "Swap the domain": youtube.com/watch?v=… → {this host}/watch?v=… lands on the app with the
/// link pre-filled. Works for watch, shorts, live, embed, playlists, channels and youtu.be ids.
/// </summary>
public static class ShortcutEndpoints
{
    public static void MapYouTubeShortcuts(this WebApplication app)
    {
        app.MapGet("/watch", (HttpRequest r) => To("https://www.youtube.com/watch" + r.QueryString.Value));
        app.MapGet("/playlist", (HttpRequest r) => To("https://www.youtube.com/playlist" + r.QueryString.Value));
        app.MapGet("/shorts/{id}", (string id) => To($"https://www.youtube.com/shorts/{Uri.EscapeDataString(id)}"));
        app.MapGet("/live/{id}", (string id) => To($"https://www.youtube.com/live/{Uri.EscapeDataString(id)}"));
        app.MapGet("/embed/{id}", (string id) => To($"https://www.youtube.com/embed/{Uri.EscapeDataString(id)}"));
        app.MapGet("/{id:regex(^[A-Za-z0-9_-]{{11}}$)}", (string id, HttpRequest r) => To($"https://youtu.be/{id}{r.QueryString.Value}"));
        app.MapGet("/{handle:regex(^@[A-Za-z0-9._-]{{1,100}}$)}", (string handle) => To($"https://www.youtube.com/{handle}"));
    }

    private static IResult To(string youtubeUrl) => Results.Redirect("/?url=" + Uri.EscapeDataString(youtubeUrl));
}
