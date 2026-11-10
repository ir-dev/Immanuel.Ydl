using Ark.Feedback.Analysis;
using Ark.Feedback.Nlp;
using Ark.Feedback.Options;
using Ark.Feedback.Tools;
using Ark.Feedback.YouTube;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Ark.Feedback.Controllers.Api;

/// <summary>What the page needs to know about the server: are the models and yt-dlp ready, which profiles exist, who is signed in.</summary>
[ApiController]
public sealed class SystemController(ModelStore models, ModelHost host, ModelCatalog catalog, ToolLocator tools, CommentSourceSelector sources,
    AnalysisManager manager, LanguageDetector lid, IOptions<FeedbackOptions> opt) : ControllerBase
{
    [HttpGet("/api/system")]
    public IActionResult System()
    {
        var o = opt.Value;
        var def = catalog.Resolve(null, o.Analysis);
        var states = models.States();
        var needed = def.Roles.ToList();
        var ready = needed.All(r => states[r].Ready);
        var downloading = states.Where(kv => kv.Value.State == "downloading").Select(kv => new { role = kv.Key.ToString(), kv.Value.Fraction, kv.Value.Bytes, kv.Value.Total }).ToList();
        var source = sources.Primary;
        Response.Headers.CacheControl = "no-store";
        return Ok(new
        {
            app = o.AppName,
            version = AnalysisPipeline.AppVersion,
            ready = ready && source.Available && !tools.Status.Preparing,
            preparing = tools.Status.Preparing || (!ready && (downloading.Count > 0 || states.Any(kv => needed.Contains(kv.Key) && kv.Value.State == "missing" && o.Analysis.AutoDownloadModels))),
            source = new { source.Name, source.Available, problem = source.Unavailability },
            ytDlp = new { tools.Status.YtDlp.Ok, tools.Status.YtDlp.Version, tools.Status.YtDlp.Note },
            models = catalog.All.Select(s => new
            {
                role = s.Role.ToString(), s.Slug, s.DisplayName, s.Enabled, s.Languages, s.License, s.Note, s.Repo,
                sizeMb = Math.Round(s.DownloadBytes / 1_000_000.0),
                state = states[s.Role].State, states[s.Role].Fraction, states[s.Role].Error,
                loaded = host.Loaded.Any(l => l.Role == s.Role),
            }),
            downloading,
            defaultProfile = def.Id,
            queue = new { queued = manager.Queued, running = manager.Running, maxConcurrent = Math.Max(1, o.Analysis.MaxConcurrentAnalyses) },
            cpu = new { threads = host.Threads, cores = Environment.ProcessorCount, budgetSeconds = o.Analysis.TimeBudgetSeconds, maxComments = o.Analysis.MaxComments },
            languageDetection = lid.Engine,
            signedIn = User.Identity?.IsAuthenticated == true,
            user = User.Identity?.IsAuthenticated == true ? User.FindFirst("name")?.Value ?? User.FindFirst("email")?.Value ?? User.Identity.Name : null,
            showRecent = o.Analysis.ShowRecentReports,
        });
    }

    [HttpGet("/healthz")]
    [AllowAnonymous]
    public IActionResult Health()
    {
        var def = catalog.Resolve(null, opt.Value.Analysis);
        var ok = def.Roles.All(models.IsReady) && sources.Primary.Available;
        return Ok(new { status = ok ? "ok" : tools.Status.Preparing ? "starting" : "degraded", version = AnalysisPipeline.AppVersion });
    }
}
