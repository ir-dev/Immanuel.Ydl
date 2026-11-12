using Ark.Feedback.Analysis;
using Ark.Feedback.Options;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Ark.Feedback.Controllers;

/// <summary>The single-page app's shell. "/" and "/r/{id}" render the same page; the script reads the location.</summary>
public sealed class HomeController(IOptions<FeedbackOptions> opt, ReportStore store) : Controller
{
    [HttpGet("/")]
    public IActionResult Index()
    {
        ViewData["AppName"] = opt.Value.AppName;
        ViewData["Title"] = $"{opt.Value.AppName} · Comment sentiment";
        ViewData["Description"] = "Paste a YouTube link and read what the comments really say: sentiment, languages, themes, the voices that matter, the genre, the audience's reach and age group, and a guide for parents and teachers — computed with open models on the server's CPU.";
        return View();
    }

    /// <summary>A shareable report link. The title and description carry the verdict so a pasted link previews well.</summary>
    [HttpGet("/r/{id}")]
    public IActionResult Report(string id)
    {
        if (!ReportStore.IsValidId(id)) return NotFound();
        var meta = store.Meta(id);
        ViewData["AppName"] = opt.Value.AppName;
        ViewData["Title"] = meta is null ? $"{opt.Value.AppName} · Report" : $"{meta.Title ?? "Report"} · {Verdict(meta.Verdict)} · {opt.Value.AppName}";
        ViewData["Description"] = meta is null
            ? "A comment sentiment report."
            : string.Join(" · ", new[]
            {
                $"{meta.Analysed:N0} comments analysed: {Verdict(meta.Verdict)} (score {(meta.Score > 0 ? "+" : "")}{meta.Score:0})",
                meta.Genre, meta.AgeGroup is null ? null : $"for {meta.AgeGroup.ToLowerInvariant()} ({meta.AgeRange})",
                meta.Reach is null ? null : $"audience: {meta.Reach}",
                meta.Family is null or "unknown" ? null : $"parents & teachers: {FamilyLabel(meta.Family)}", meta.Channel,
            }.Where(x => !string.IsNullOrEmpty(x)));
        ViewData["Image"] = meta?.Thumbnail;
        ViewData["ReportId"] = id;
        return View("Index");
    }

    private static string FamilyLabel(string r) => r switch { "green" => "looks fine to share", "amber" => "preview first", "red" => "not for younger viewers", _ => r };

    private static string Verdict(string v) => v switch
    {
        "loved" => "Loved", "liked" => "Liked", "leaning-positive" => "Leaning positive", "mixed" => "Mixed", "divided" => "Divided",
        "leaning-negative" => "Leaning negative", "disliked" => "Disliked", "rejected" => "Rejected", "thin" => "Too few comments", _ => "Report",
    };
}

/// <summary>The admin console page (all data comes from /api/admin, which checks access).</summary>
public sealed class AdminController(IOptions<FeedbackOptions> opt) : Controller
{
    [HttpGet("/admin")]
    public IActionResult Index()
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
        ViewData["AppName"] = opt.Value.AppName;
        ViewData["Title"] = $"{opt.Value.AppName} · Admin";
        return View();
    }
}
