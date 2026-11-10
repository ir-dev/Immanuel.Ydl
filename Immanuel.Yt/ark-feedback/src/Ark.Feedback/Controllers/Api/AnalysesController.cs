using System.Text.Json;
using Ark.Feedback.Analysis;
using Ark.Feedback.Audit;
using Ark.Feedback.Nlp;
using Ark.Feedback.Options;
using Ark.Feedback.YouTube;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Ark.Feedback.Controllers.Api;

public sealed record AnalyseRequest(string? Url, string? Profile, bool Fresh = false);

/// <summary>Start, watch and cancel analyses.</summary>
[ApiController]
[Route("api/analyses")]
public sealed class AnalysesController(AnalysisManager manager, AuditRecorder audit, ModelCatalog catalog, IOptions<FeedbackOptions> opt) : ControllerBase
{
    private static readonly JsonSerializerOptions Json = ReportStore.Json;

    /// <summary>Queues an analysis. Answers 202 with the job, or 200 with a stored report's id when a fresh-enough one exists.</summary>
    [HttpPost]
    [EnableRateLimiting("analyses")]
    public IActionResult Start([FromBody] AnalyseRequest? body)
    {
        if (!VideoUrl.TryParse(body?.Url, out var url, out var problem))
        {
            AuditRecorder.Annotate(HttpContext, detail: "rejected: " + problem);
            return BadRequest(new { error = problem });
        }
        var client = audit.Client(HttpContext);
        try
        {
            var result = manager.Enqueue(url!, body?.Profile, body?.Fresh ?? false, client);
            AuditRecorder.Annotate(HttpContext, videoId: url!.VideoId, analysisId: result.Job.Id, detail: result.Cached ? "served from cache" : $"queued ({result.Job.Profile.Id})");
            var payload = Snapshot(result.Job, result.Cached);
            return result.Cached ? Ok(payload) : Accepted(payload);
        }
        catch (TooBusyException ex)
        {
            AuditRecorder.Annotate(HttpContext, videoId: url!.VideoId, detail: "refused: busy");
            Response.Headers.RetryAfter = "60";
            return StatusCode(StatusCodes.Status429TooManyRequests, new { error = ex.Message });
        }
    }

    [HttpGet("{id}")]
    public IActionResult Get(string id)
    {
        if (manager.Get(id) is not { } job) return NotFound(new { error = "No such analysis (it may have finished more than two hours ago — open the report instead)." });
        return Ok(Snapshot(job, job.Report?.Compute.FromCache == true));
    }

    /// <summary>Server-sent events: one JSON snapshot per change, then the stream ends.</summary>
    [HttpGet("{id}/events")]
    public async Task Events(string id, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";
        Response.ContentType = "text/event-stream";
        if (manager.Get(id) is not { } job)
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            await Response.WriteAsync("event: gone\ndata: {}\n\n", ct);
            return;
        }
        await Response.Body.FlushAsync(ct);
        try
        {
            await foreach (var e in job.EventsAsync(ct))
            {
                await Response.WriteAsync($"data: {JsonSerializer.Serialize(Snapshot(job, false, e), Json)}\n\n", ct);
                await Response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
            // the page went away
        }
    }

    [HttpDelete("{id}")]
    public IActionResult Cancel(string id)
    {
        var client = audit.Client(HttpContext);
        if (!manager.Cancel(id, client)) return NotFound(new { error = "Nothing to cancel." });
        AuditRecorder.Annotate(HttpContext, analysisId: id, detail: "canceled by the user");
        return NoContent();
    }

    /// <summary>The profiles a visitor may choose, with what each one does.</summary>
    [HttpGet("/api/profiles")]
    public IActionResult Profiles()
    {
        var o = opt.Value.Analysis;
        var allowed = o.AllowedProfiles.Count == 0 ? ModelCatalog.Profiles.Select(p => p.Id).ToList() : o.AllowedProfiles;
        var def = catalog.Resolve(null, o);
        return Ok(new
        {
            @default = def.Id,
            profiles = ModelCatalog.Profiles.Where(p => allowed.Contains(p.Id, StringComparer.OrdinalIgnoreCase)).Select(p =>
            {
                var r = catalog.Resolve(p.Id, o);
                return new
                {
                    r.Id, r.Name, r.Blurb, r.MaxComments, r.Themes, r.Toxicity, r.Emotion,
                    sentimentModel = catalog[r.SentimentModel].DisplayName,
                    models = r.Roles.Select(role => new { role = role.ToString(), catalog[role].Slug, catalog[role].DisplayName }),
                };
            }),
        });
    }

    private static object Snapshot(AnalysisJob job, bool cached, JobEvent? e = null)
    {
        e ??= job.Current;
        return new
        {
            id = job.Id,
            videoId = job.Url.VideoId,
            profile = job.Profile.Id,
            profileName = job.Profile.Name,
            state = e.State.ToString().ToLowerInvariant(),
            e.Stage, e.Message, e.Progress, e.Fetched, e.Total, e.Analysed,
            reportId = e.ReportId,
            e.Error, e.ErrorDetail, e.Retryable,
            queuePosition = e.QueuePosition,
            cached,
            at = e.At,
            createdAt = job.CreatedAt,
        };
    }
}
