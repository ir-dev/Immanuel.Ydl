using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Ark.oAuth;
using Ark.Ydl.Admin;
using Ark.Ydl.Audit;
using Ark.Ydl.Endpoints;
using Ark.Ydl.Releases;
using Ark.Ydl.Jobs;
using Ark.Ydl.Options;
using Ark.Ydl.Tools;
using Ark.Ydl.YtDlp;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// ── options & core services ─────────────────────────────────────────────────
builder.Services.Configure<YdlOptions>(builder.Configuration.GetSection(YdlOptions.Section));
builder.Services.PostConfigure<YdlOptions>(o =>
{
    if (o.AllowedSites.Count == 0) o.AllowedSites.AddRange(YdlOptions.DefaultSites);
});
builder.Services.AddSingleton(sp =>
    new YdlPaths(sp.GetRequiredService<IOptions<YdlOptions>>().Value, builder.Environment.ContentRootPath));
var userAgent = "ark-ydl/" + (typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "1");
builder.Services.AddHttpClient("tools", c =>
{
    c.Timeout = TimeSpan.FromMinutes(5);
    c.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
});
builder.Services.AddHttpClient("geoip", c =>
{
    c.Timeout = Timeout.InfiniteTimeSpan; // a 60 MB file on a slow host; GeoIpService bounds it itself
    c.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
});
builder.Services.AddMemoryCache();
builder.Services.AddDataProtection();
builder.Services.AddSingleton<ToolLocator>();
builder.Services.AddSingleton<ProbeService>();
builder.Services.AddSingleton<JobManager>();
builder.Services.AddSingleton<JobRunner>();
builder.Services.AddHostedService<ToolMaintenanceService>();
builder.Services.AddHostedService<JobWorker>();
builder.Services.AddHostedService<JobJanitor>();

// ── release info, audit log, admin console ──────────────────────────────────
builder.Services.AddSingleton<ReleaseCatalog>();
builder.Services.AddSingleton<LatestReleaseService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<LatestReleaseService>());
builder.Services.AddArkRapidDatabase(builder.Configuration);   // "ArkDatabase:Databases"; "audit" defaults to {DataRoot}/audit/ark-ydl-audit.db
builder.Services.AddSingleton<AuditWriter>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AuditWriter>());
builder.Services.AddSingleton<GeoIpService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<GeoIpService>());
builder.Services.AddSingleton<AuditRecorder>();
builder.Services.AddSingleton<AuditReports>();
builder.Services.AddSingleton<AuditMaintenance>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AuditMaintenance>());
builder.Services.AddSingleton<AdminAccess>();

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});
builder.Services.AddProblemDetails();

// ── rate limiting (per signed-in user, else per IP) ─────────────────────────
var limits = builder.Configuration.GetSection(YdlOptions.Section).Get<YdlOptions>()?.RateLimit ?? new RateLimitOptions();
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.OnRejected = async (ctx, ct) =>
    {
        ctx.HttpContext.Response.ContentType = "application/json";
        await ctx.HttpContext.Response.WriteAsync(
            """{"error":"You're going a bit fast — wait a minute and try again."}""", ct);
    };
    o.AddPolicy("probe", http => RateLimitPartition.GetFixedWindowLimiter(ApiEndpoints.ClientKey(http),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = Math.Max(1, limits.ProbesPerMinute), Window = TimeSpan.FromMinutes(1) }));
    o.AddPolicy("jobs", http => RateLimitPartition.GetFixedWindowLimiter(ApiEndpoints.ClientKey(http),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = Math.Max(1, limits.JobsPerMinute), Window = TimeSpan.FromMinutes(1) }));
    // Admin key guesses: per IP, whoever is signed in.
    o.AddPolicy("admin-login", http => RateLimitPartition.GetFixedWindowLimiter("ip:" + (http.Connection.RemoteIpAddress?.ToString() ?? "unknown"),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(5) }));
});

// ── optional sign-in against the Ark identity server ────────────────────────
// Off unless ark_oauth_client:Authority and :ClientId are set. Then every page and API call needs a
// signed-in user — worth it on a public host, where an open downloader is soon found and abused.
var ark = builder.Configuration.GetSection("ark_oauth_client");
var authEnabled = !string.IsNullOrWhiteSpace(ark["Authority"]) && !string.IsNullOrWhiteSpace(ark["ClientId"]);
string? authorityOrigin = null;
if (authEnabled)
{
    builder.Services.AddArkOidcClient(builder.Configuration);
    builder.Services.AddAuthorization(o =>
        o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
    if (Uri.TryCreate(ark["Authority"], UriKind.Absolute, out var authority))
        authorityOrigin = authority.GetLeftPart(UriPartial.Authority);
}

var app = builder.Build();

// ── audit: every request, outermost, so the row has the final status (500s from the handler below included) ──
var audit = app.Services.GetRequiredService<AuditRecorder>();
audit.Attach(app.Services.GetRequiredService<JobManager>());
app.Use(async (ctx, next) =>
{
    var started = Stopwatch.GetTimestamp();
    try
    {
        await next();
    }
    finally
    {
        audit.Request(ctx, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }
});

app.UseExceptionHandler();

// ── security headers ────────────────────────────────────────────────────────
// The YouTube IFrame player (clip preview) needs youtube.com scripts and a youtube-nocookie frame;
// the referrer must not be stripped or embeds fail with "error 153".
var csp = string.Join("; ",
    "default-src 'self'",
    "script-src 'self' https://www.youtube.com https://s.ytimg.com",
    "frame-src https://www.youtube-nocookie.com https://www.youtube.com",
    "img-src 'self' data: https:",
    "media-src 'self' blob:",
    "style-src 'self' 'unsafe-inline'",
    "connect-src 'self'",
    "font-src 'self'",
    "object-src 'none'",
    "base-uri 'self'",
    "form-action 'self'" + (authorityOrigin is null ? "" : " " + authorityOrigin),
    "frame-ancestors 'none'",
    "manifest-src 'self'",
    "worker-src 'self'");
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
        // Same URLs across releases, so make browsers revalidate (ETag) instead of serving stale JS.
        var ext = Path.GetExtension(c.File.Name).ToLowerInvariant();
        c.Context.Response.Headers.CacheControl = ext is ".svg" or ".png" or ".ico" ? "public, max-age=86400" : "no-cache";
    },
};

app.UseDefaultFiles();
if (!authEnabled) app.UseStaticFiles(staticFiles);
app.UseRouting();
app.UseRateLimiter();
if (authEnabled)
{
    app.UseAuthentication();
    app.UseAuthorization();
    app.UseStaticFiles(staticFiles); // after UseAuthorization, so the fallback policy covers the page too
    app.MapPost("/auth/signout", (HttpContext ctx) => ctx.ArkSignOutEverywhereAsync("/"));
}

app.MapGet("/healthz", (ToolLocator tools) => Results.Ok(new
{
    status = tools.Status.Ready ? "ok" : tools.Status.Preparing ? "starting" : "degraded",
    ytDlp = tools.Status.YtDlp.Version,
    ffmpeg = tools.Status.Ffmpeg.Version,
    js = tools.Status.JsRuntime.Ok ? tools.Status.JsRuntime.Name : null,
})).AllowAnonymous();

app.MapYdlApi(authEnabled);
app.MapReleaseApi();
app.MapAdminApi(authEnabled);
app.MapYouTubeShortcuts();

app.Run();

public partial class Program;
