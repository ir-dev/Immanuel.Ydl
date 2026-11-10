using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Ark.Feedback.Admin;
using Ark.Feedback.Analysis;
using Ark.Feedback.Audit;
using Ark.Feedback.Controllers.Api;
using Ark.Feedback.Nlp;
using Ark.Feedback.Options;
using Ark.Feedback.Tools;
using Ark.Feedback.YouTube;
using Ark.oAuth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// ── options & paths ──────────────────────────────────────────────────────────
builder.Services.Configure<FeedbackOptions>(builder.Configuration.GetSection(FeedbackOptions.Section));
builder.Services.AddSingleton(sp => new FeedbackPaths(sp.GetRequiredService<IOptions<FeedbackOptions>>().Value, builder.Environment.ContentRootPath));
builder.Services.AddSingleton(sp => new ModelCatalog(sp.GetRequiredService<IOptions<FeedbackOptions>>().Value.Analysis));
builder.Services.AddSingleton(sp => new AuditClock(sp.GetRequiredService<IOptions<FeedbackOptions>>().Value.Audit.TimeZone));

var userAgent = "ark-feedback/" + AnalysisPipeline.AppVersion;
builder.Services.AddHttpClient("downloads", c =>
{
    c.Timeout = TimeSpan.FromMinutes(30); // a 280 MB model on a slow host
    c.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
});
builder.Services.AddHttpClient("geoip", c =>
{
    c.Timeout = Timeout.InfiniteTimeSpan;
    c.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
});
builder.Services.AddHttpClient("youtube-api", c =>
{
    c.Timeout = TimeSpan.FromSeconds(30);
    c.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
});
builder.Services.AddDataProtection();
builder.Services.AddMemoryCache();

// ── tools, models, analysis ─────────────────────────────────────────────────
builder.Services.AddSingleton<HttpDownloader>();
builder.Services.AddSingleton<ToolLocator>();
builder.Services.AddHostedService<ToolMaintenanceService>();
builder.Services.AddSingleton<ModelStore>();
builder.Services.AddHostedService<ModelMaintenanceService>();
builder.Services.AddSingleton<ModelHost>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ModelHost>());
builder.Services.AddSingleton<LanguageDetector>();
builder.Services.AddSingleton<YtDlpCommentSource>();
builder.Services.AddSingleton<DataApiCommentSource>();
builder.Services.AddSingleton<CommentSourceSelector>();
builder.Services.AddSingleton<AnalysisPipeline>();
builder.Services.AddSingleton<ReportStore>();
builder.Services.AddSingleton<AnalysisManager>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AnalysisManager>());

// ── audit log (one SQLite file per month, a table set per day) & admin ─────
builder.Services.AddSingleton<AuditDatabases>();
builder.Services.AddSingleton<AuditWriter>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AuditWriter>());
builder.Services.AddSingleton<GeoIpService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<GeoIpService>());
builder.Services.AddSingleton<AuditRecorder>();
builder.Services.AddSingleton<AuditReports>();
builder.Services.AddSingleton<AuditMaintenance>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AuditMaintenance>());
builder.Services.AddSingleton<AdminAccess>();

builder.Services.AddControllersWithViews().AddJsonOptions(o =>
{
    o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
    o.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});
builder.Services.AddProblemDetails();

// ── rate limiting (per signed-in user, else per IP) ─────────────────────────
var limits = builder.Configuration.GetSection(FeedbackOptions.Section).Get<FeedbackOptions>()?.RateLimit ?? new RateLimitOptions();
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.OnRejected = async (ctx, ct) =>
    {
        ctx.HttpContext.Response.ContentType = "application/json";
        await ctx.HttpContext.Response.WriteAsync("""{"error":"You're going a bit fast — wait a minute and try again."}""", ct);
    };
    o.AddPolicy("analyses", http => RateLimitPartition.GetFixedWindowLimiter(ClientIdentity.ClientKey(http),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = Math.Max(1, limits.AnalysesPer10Minutes), Window = TimeSpan.FromMinutes(10) }));
    o.AddPolicy("admin-login", http => RateLimitPartition.GetFixedWindowLimiter("ip:" + (http.Connection.RemoteIpAddress?.ToString() ?? "unknown"),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(5) }));
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http =>
    {
        var path = http.Request.Path.Value ?? "";
        if (!path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) || path.EndsWith("/events", StringComparison.OrdinalIgnoreCase))
            return RateLimitPartition.GetNoLimiter("none");
        return RateLimitPartition.GetFixedWindowLimiter(ClientIdentity.ClientKey(http),
            _ => new FixedWindowRateLimiterOptions { PermitLimit = Math.Max(10, limits.ApiPerMinute), Window = TimeSpan.FromMinutes(1) });
    });
});

// ── optional sign-in against the Ark identity server ────────────────────────
var ark = builder.Configuration.GetSection("ark_oauth_client");
var authEnabled = !string.IsNullOrWhiteSpace(ark["Authority"]) && !string.IsNullOrWhiteSpace(ark["ClientId"]);
string? authorityOrigin = null;
if (authEnabled)
{
    builder.Services.AddArkOidcClient(builder.Configuration);
    builder.Services.AddAuthorization(o => o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
    if (Uri.TryCreate(ark["Authority"], UriKind.Absolute, out var authority)) authorityOrigin = authority.GetLeftPart(UriPartial.Authority);
}
AdminApiController.AuthEnabled = authEnabled;

var app = builder.Build();

// ── audit: every request, outermost, so the row has the final status ─────────
var audit = app.Services.GetRequiredService<AuditRecorder>();
audit.Attach(app.Services.GetRequiredService<AnalysisManager>());
app.Use(async (ctx, next) =>
{
    var started = Stopwatch.GetTimestamp();
    try { await next(); }
    finally { audit.Request(ctx, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds); }
});

app.UseExceptionHandler();

// ── security headers ────────────────────────────────────────────────────────
var csp = string.Join("; ",
    "default-src 'self'",
    "script-src 'self'",
    "img-src 'self' data: https://i.ytimg.com https://yt3.ggpht.com https://*.ytimg.com https://*.ggpht.com",
    "style-src 'self' 'unsafe-inline'",
    "connect-src 'self'",
    "font-src 'self'",
    "object-src 'none'",
    "base-uri 'self'",
    "form-action 'self'" + (authorityOrigin is null ? "" : " " + authorityOrigin),
    "frame-ancestors 'none'",
    "manifest-src 'self'");
app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h["X-Content-Type-Options"] = "nosniff";
    h["Referrer-Policy"] = "strict-origin-when-cross-origin";
    h["X-Frame-Options"] = "DENY";
    h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
    h["Content-Security-Policy"] = csp;
    await next();
});

var staticFiles = new StaticFileOptions
{
    ContentTypeProvider = new FileExtensionContentTypeProvider { Mappings = { [".webmanifest"] = "application/manifest+json" } },
    OnPrepareResponse = c =>
    {
        var ext = Path.GetExtension(c.File.Name).ToLowerInvariant();
        c.Context.Response.Headers.CacheControl = ext is ".svg" or ".png" or ".ico" ? "public, max-age=86400" : "no-cache";
    },
};

if (!authEnabled) app.UseStaticFiles(staticFiles);
app.UseRouting();
app.UseRateLimiter();
if (authEnabled)
{
    app.UseAuthentication();
    app.UseAuthorization();
    app.UseStaticFiles(staticFiles);
    app.MapPost("/auth/signout", (HttpContext ctx) => ctx.ArkSignOutEverywhereAsync("/"));
}
app.MapControllers();

app.Run();

public partial class Program;
