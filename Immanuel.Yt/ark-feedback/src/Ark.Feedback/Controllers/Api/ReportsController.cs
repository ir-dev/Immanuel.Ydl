using System.Globalization;
using System.Text;
using Ark.Feedback.Analysis;
using Ark.Feedback.Audit;
using Ark.Feedback.Options;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Ark.Feedback.Controllers.Api;

public sealed record FeedbackRequest(bool Liked, string? Note);

/// <summary>Stored reports: read one, list recent ones, export comments, say whether it helped.</summary>
[ApiController]
[Route("api/reports")]
public sealed class ReportsController(ReportStore store, AuditRecorder audit, IOptions<FeedbackOptions> opt) : ControllerBase
{
    [HttpGet]
    public IActionResult Recent([FromQuery] int count = 12, [FromQuery] string? video = null)
    {
        if (!opt.Value.Analysis.ShowRecentReports && video is null) return Ok(new { reports = Array.Empty<object>(), hidden = true });
        if (video is not null && !YouTube.VideoUrl.IsValidId(video)) return BadRequest(new { error = "Not a video id." });
        return Ok(new { reports = store.Recent(count, video) });
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> Get(string id, CancellationToken ct)
    {
        if (!ReportStore.IsValidId(id)) return NotFound(new { error = "No such report." });
        var report = await store.LoadAsync(id, ct);
        if (report is null) return NotFound(new { error = "No such report. Reports are kept for a year; this one may have expired." });
        AuditRecorder.Annotate(HttpContext, videoId: report.Video.Id, analysisId: id);
        audit.ReportViewed(id);
        Response.Headers.CacheControl = "private, max-age=300";
        return Ok(report);
    }

    /// <summary>Every analysed comment with its scores, for a spreadsheet.</summary>
    [HttpGet("{id}/comments.csv")]
    public async Task<IActionResult> Csv(string id, CancellationToken ct)
    {
        if (!ReportStore.IsValidId(id)) return NotFound();
        var report = await store.LoadAsync(id, ct);
        if (report is null) return NotFound();
        AuditRecorder.Annotate(HttpContext, videoId: report.Video.Id, analysisId: id, detail: "csv export");
        var sb = new StringBuilder();
        sb.Append("comment_id,parent_id,is_reply,author,author_is_uploader,is_pinned,is_hearted,likes,published_utc,language,sentiment,score,p_positive,p_neutral,p_negative,toxic,emotion,theme,repeats,duplicate_of,text\r\n");
        foreach (var c in report.Comments)
        {
            sb.AppendJoin(',', new[]
            {
                c.Id, c.ParentId ?? "", c.IsReply ? "1" : "0", c.Author ?? "", c.AuthorIsUploader ? "1" : "0", c.IsPinned ? "1" : "0", c.IsHearted ? "1" : "0",
                c.Likes.ToString(CultureInfo.InvariantCulture), c.PublishedAt?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "", c.Lang, c.Sentiment,
                c.Score.ToString("0.####", CultureInfo.InvariantCulture), c.PPos.ToString("0.####", CultureInfo.InvariantCulture), c.PNeu.ToString("0.####", CultureInfo.InvariantCulture),
                c.PNeg.ToString("0.####", CultureInfo.InvariantCulture), c.Toxic?.ToString("0.###", CultureInfo.InvariantCulture) ?? "", c.Emotion ?? "",
                c.Theme?.ToString(CultureInfo.InvariantCulture) ?? "", c.Repeats.ToString(CultureInfo.InvariantCulture), c.DuplicateOf ?? "", c.Text,
            }.Select(AuditReports.Csv)).Append("\r\n");
        }
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
        return File(bytes, "text/csv; charset=utf-8", $"comments-{report.Video.Id}-{id}.csv");
    }

    /// <summary>"Was this report useful?" — recorded on the analysis's audit row.</summary>
    [HttpPost("{id}/feedback")]
    public IActionResult Feedback(string id, [FromBody] FeedbackRequest? body)
    {
        if (!ReportStore.IsValidId(id) || store.Meta(id) is null) return NotFound(new { error = "No such report." });
        if (body is null) return BadRequest(new { error = "Say whether it helped." });
        var note = body.Note?.Trim();
        if (note is { Length: > 1000 }) note = note[..1000];
        audit.Feedback(id, body.Liked, note, audit.Client(HttpContext));
        AuditRecorder.Annotate(HttpContext, analysisId: id, detail: body.Liked ? "liked the report" : "did not like the report");
        return Ok(new { thanks = true });
    }
}
