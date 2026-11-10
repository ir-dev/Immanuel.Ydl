using System.Runtime.InteropServices;
using System.Text;
using Ark.Feedback.Admin;
using Ark.Feedback.Analysis;
using Ark.Feedback.Audit;
using Ark.Feedback.Nlp;
using Ark.Feedback.Options;
using Ark.Feedback.Tools;
using Ark.Feedback.YouTube;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Ark.Feedback.Controllers.Api;

public sealed record AdminKeyInput(string? Key);

/// <summary>Everything the admin console reads. Every action but the session ones requires <see cref="AdminAccess"/> to say yes.</summary>
[ApiController]
[Route("api/admin")]
public sealed class AdminApiController(AdminAccess access, AuditReports reports, AuditWriter writer, AuditRecorder recorder, AuditMaintenance maintenance,
    AuditDatabases dbs, GeoIpService geo, ModelStore modelStore, ModelHost host, ModelCatalog catalog, ToolLocator tools, CommentSourceSelector sources,
    AnalysisManager manager, ReportStore reportStore, LanguageDetector lid, IOptions<FeedbackOptions> opt, FeedbackPaths paths) : ControllerBase, IActionFilter
{
    public static bool AuthEnabled { get; set; }

    public void OnActionExecuting(ActionExecutingContext context)
    {
        Response.Headers.CacheControl = "no-store";
        var action = context.ActionDescriptor.RouteValues["action"];
        if (action is nameof(Session) or nameof(SignInAdmin) or nameof(SignOutAdmin)) return;
        if (access.Check(HttpContext, AuthEnabled) is null)
            context.Result = StatusCode(StatusCodes.Status401Unauthorized, new { error = "Sign in to the admin console first." });
    }

    public void OnActionExecuted(ActionExecutedContext context)
    {
    }

    // ── session ──────────────────────────────────────────────────────────────

    [HttpGet("session")]
    public IActionResult Session()
    {
        var who = access.Check(HttpContext, AuthEnabled);
        return Ok(new
        {
            today = recorder.Clock.Today,
            timeZone = recorder.Clock.Zone.Id,
            auditEnabled = recorder.Enabled,
            signedIn = who is not null,
            method = who?.Method,
            name = who?.Name,
            keyConfigured = access.KeyEnabled,
            keyProblem = access.KeyProblem,
            roleSignIn = AuthEnabled,
            roles = AuthEnabled ? access.Roles : [],
            user = User.Identity?.IsAuthenticated == true ? User.FindFirst("name")?.Value ?? User.Identity.Name : null,
            app = opt.Value.AppName,
        });
    }

    [HttpPost("session")]
    [EnableRateLimiting("admin-login")]
    public IActionResult SignInAdmin([FromBody] AdminKeyInput? input)
    {
        if (!access.KeyEnabled)
            return StatusCode(StatusCodes.Status403Forbidden, new { error = access.KeyProblem ?? "Key sign-in is off on this server (Feedback:Admin:Key is not set)." });
        if (!access.KeyMatches(input?.Key))
        {
            AuditRecorder.Annotate(HttpContext, detail: "admin key rejected");
            return Unauthorized(new { error = "That key isn't right." });
        }
        access.IssueCookie(HttpContext);
        AuditRecorder.Annotate(HttpContext, detail: "admin signed in with the key");
        return Ok(new { signedIn = true, method = "key" });
    }

    [HttpDelete("session")]
    public IActionResult SignOutAdmin()
    {
        AdminAccess.ClearCookie(HttpContext);
        return NoContent();
    }

    // ── reports ──────────────────────────────────────────────────────────────

    private IActionResult? Unavailable()
    {
        if (writer.IsReady) return null;
        var s = writer.Status;
        return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = s.State == "off" ? "Auditing is off (Feedback:Audit:Enabled=false)." : "The audit database isn't available: " + (s.LastError ?? "still opening") });
    }

    [HttpGet("overview")]
    public async Task<IActionResult> Overview(string? from, string? to)
    {
        if (Unavailable() is { } no) return no;
        var range = DateRange.Parse(from, to, reports.Today);
        var prev = range.Previous();
        return Ok(new
        {
            range, previous = prev, today = reports.Today,
            kpis = await reports.KpisAsync(range),
            previousKpis = await reports.KpisAsync(prev),
            daily = await reports.DailyAsync(range),
        });
    }

    [HttpGet("breakdowns")]
    public async Task<IActionResult> Breakdowns(string? from, string? to)
    {
        if (Unavailable() is { } no) return no;
        var range = DateRange.Parse(from, to, reports.Today);
        return Ok(new { range, data = await reports.BreakdownsAsync(range) });
    }

    [HttpGet("analyses")]
    public async Task<IActionResult> Analyses(string? from, string? to, string? q, string? status, string? verdict, string? profile, string? country, string? ip, string? liked, int? page, int? size)
    {
        if (Unavailable() is { } no) return no;
        var range = DateRange.Parse(from, to, reports.Today);
        var f = new LogFilter { Q = q, Status = status, Verdict = verdict, Profile = profile, Country = country, Ip = ip, Liked = liked, Page = page ?? 1, Size = size ?? 50 };
        var (total, rows) = await reports.AnalysesAsync(range, f);
        return Ok(new { range, total, page = Math.Max(1, f.Page), size = Math.Clamp(f.Size, 10, 200), rows = rows.Select(Plain) });
    }

    [HttpGet("analyses/{id}")]
    public async Task<IActionResult> Analysis(string id, string? sentiment, int? page, int? size)
    {
        if (Unavailable() is { } no) return no;
        if (!ReportStore.IsValidId(id)) return NotFound();
        var row = await reports.AnalysisAsync(id);
        if (row is null) return NotFound(new { error = "No such analysis in the audit log." });
        var (total, comments) = await reports.CommentsAsync(id, sentiment, page ?? 1, size ?? 50);
        return Ok(new
        {
            row = Plain(row),
            comments = new { total, page = Math.Max(1, page ?? 1), rows = comments.Select(Plain) },
            requests = (await reports.RequestsForAnalysisAsync(id)).Select(Plain),
            report = reportStore.Meta(id),
            live = manager.Get(id) is { } job ? new { state = job.Current.State.ToString().ToLowerInvariant(), job.Current.Message } : null,
        });
    }

    [HttpGet("requests")]
    public async Task<IActionResult> Requests(string? from, string? to, string? q, string? kind, string? status, string? ip, int? page, int? size)
    {
        if (Unavailable() is { } no) return no;
        var range = DateRange.Parse(from, to, reports.Today);
        var f = new LogFilter { Q = q, Kind = kind, Status = status, Ip = ip, Page = page ?? 1, Size = size ?? 50 };
        var (total, rows) = await reports.RequestsAsync(range, f);
        return Ok(new { range, total, page = Math.Max(1, f.Page), size = Math.Clamp(f.Size, 10, 200), rows = rows.Select(Plain) });
    }

    /// <summary>One day's table as CSV: /api/admin/export/analyses/2026-11-10.csv</summary>
    [HttpGet("export/{table}/{day}.csv")]
    public async Task<IActionResult> Export(string table, string day)
    {
        if (Unavailable() is { } no) return no;
        AuditTable? t = table switch { "analyses" => AuditTable.Analyses, "requests" => AuditTable.Requests, "comments" => AuditTable.Comments, _ => null };
        if (t is null) return NotFound();
        var csv = await reports.CsvAsync(t.Value, day);
        if (csv is null) return NotFound(new { error = "No table for that day." });
        return File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv)).ToArray(), "text/csv; charset=utf-8", $"ark-feedback-{table}-{day}.csv");
    }

    [HttpGet("store")]
    public async Task<IActionResult> Store()
    {
        if (Unavailable() is { } no) return no;
        return Ok(new { directory = dbs.Directory, months = await reports.StoreAsync() });
    }

    // ── system ───────────────────────────────────────────────────────────────

    [HttpGet("status")]
    public IActionResult Status()
    {
        var o = opt.Value;
        var states = modelStore.States();
        var t = tools.Status;
        long modelBytes = 0;
        try { modelBytes = modelStore.BytesOnDisk(); } catch (IOException) { }
        return Ok(new
        {
            audit = new
            {
                enabled = o.Audit.Enabled, logRequests = o.Audit.LogRequests, includeStaticFiles = o.Audit.IncludeStaticFiles, excludePaths = o.Audit.ExcludePaths,
                retentionMonths = o.Audit.RetentionMonths, ipMode = o.Audit.IpMode.ToString(), timeZone = recorder.Clock.Zone.Id, timeZoneNote = recorder.Clock.Note,
                storeComments = o.Analysis.StoreComments, directory = dbs.Directory, filePrefix = o.Audit.FilePrefix,
                months = dbs.Months(), writer = writer.Status, maintenance = maintenance.Status,
            },
            geoip = geo.Status,
            models = catalog.All.Select(s => new
            {
                role = s.Role.ToString(), s.Slug, s.DisplayName, s.Repo, s.Revision, file = s.Model.Path, s.Model.Sha256, s.Enabled, s.License, s.Languages, s.Note,
                sizeMb = Math.Round(s.DownloadBytes / 1_000_000.0), state = states[s.Role], loaded = host.Loaded.FirstOrDefault(l => l.Role == s.Role),
                path = modelStore.ModelPath(s),
            }),
            modelBytes,
            analysis = new
            {
                o.Analysis.DefaultProfile, o.Analysis.AllowedProfiles, o.Analysis.MaxConcurrentAnalyses, o.Analysis.MaxQueued, threads = host.Threads, o.Analysis.MaxComments,
                o.Analysis.RepliesPerThread, o.Analysis.TimeBudgetSeconds, o.Analysis.MaxSequenceTokens, o.Analysis.BatchSize, o.Analysis.UnloadIdleMinutes, o.Analysis.CacheHours,
                o.Analysis.ReportRetentionDays, queued = manager.Queued, running = manager.Running, storedReports = reportStore.Count,
                active = manager.Active.Select(j => new { j.Id, j.Url.VideoId, profile = j.Profile.Id, state = j.Current.State.ToString().ToLowerInvariant(), j.Current.Message, j.Current.Progress, j.CreatedAt }),
                languageDetection = lid.Engine,
            },
            youtube = new { source = sources.Primary.Name, available = sources.Primary.Available, problem = sources.Primary.Unavailability, apiKeyConfigured = !string.IsNullOrWhiteSpace(o.YouTube.ApiKey), mode = o.YouTube.Source, fallback = sources.Fallback?.Name },
            tools = new { ytDlp = new { t.YtDlp.Ok, t.YtDlp.Version, t.YtDlp.Path, t.YtDlp.Note, owned = tools.OwnsYtDlp }, jsRuntime = new { t.JsRuntime.Ok, t.JsRuntime.Name, t.JsRuntime.Version, t.JsRuntime.Path, t.JsRuntime.Note } },
            paths = new { paths.DataRoot, paths.Models, paths.Reports, paths.Audit, paths.Tools },
            server = new
            {
                time = DateTimeOffset.UtcNow, machine = Environment.MachineName, os = RuntimeInformation.OSDescription, arch = RuntimeInformation.ProcessArchitecture.ToString(),
                dotnet = Environment.Version.ToString(), cores = Environment.ProcessorCount, workingSetMb = Environment.WorkingSet / 1_000_000, version = AnalysisPipeline.AppVersion,
                is64Bit = Environment.Is64BitProcess,
            },
        });
    }

    /// <summary>Releases loaded models now (gives memory back without a restart).</summary>
    [HttpPost("models/unload")]
    public IActionResult Unload()
    {
        host.UnloadAll("admin");
        return Ok(new { unloaded = true });
    }

    /// <summary>Starts downloading a missing model now instead of waiting for the retry timer.</summary>
    [HttpPost("models/{role}/download")]
    public IActionResult Download(string role)
    {
        if (!Enum.TryParse<ModelRole>(role, true, out var r)) return NotFound();
        _ = Task.Run(() => modelStore.EnsureAsync(r, CancellationToken.None));
        return Accepted(new { started = true });
    }

    [HttpPost("tools/update")]
    public async Task<IActionResult> UpdateYtDlp(CancellationToken ct)
    {
        if (!tools.OwnsYtDlp) return BadRequest(new { error = "yt-dlp isn't managed by this app (configured or on PATH), so it can't be updated from here." });
        var v = await tools.UpdateYtDlpAsync(ct);
        return Ok(new { version = v });
    }

    [HttpPost("audit/refresh")]
    public async Task<IActionResult> Refresh()
    {
        await writer.FlushAsync(TimeSpan.FromSeconds(10));
        await maintenance.RunOnceAsync(wholeMonth: true);
        return Ok(new { ok = true, writer = writer.Status, maintenance = maintenance.Status });
    }

    private static Dictionary<string, object?> Plain(IDictionary<string, object> row)
    {
        var d = new Dictionary<string, object?>(row.Count);
        foreach (var (k, v) in row) d[k] = v;
        return d;
    }
}
