using System.Text;
using Ark.Ydl.Audit;
using Ark.Ydl.Options;
using Ark.Ydl.Releases;
using Ark.Ydl.Tools;
using Microsoft.Extensions.Options;

namespace Ark.Ydl.Admin;

public sealed record AdminKeyInput(string? Key);

public static class AdminEndpoints
{
    public static void MapAdminApi(this WebApplication app, bool authEnabled)
    {
        // The console page itself is harmless (all data comes from the API below), but it is never cached or indexed.
        app.MapGet("/admin", (IWebHostEnvironment env, HttpContext ctx) =>
        {
            NoStore(ctx);
            return Results.File(Path.Combine(env.WebRootPath, "admin.html"), "text/html; charset=utf-8");
        });

        var session = app.MapGroup("/api/admin/session");

        session.MapGet("", (AdminAccess access, AuditRecorder recorder, HttpContext ctx) =>
        {
            NoStore(ctx);
            var who = access.Check(ctx, authEnabled);
            return Results.Ok(new
            {
                // Days are counted in the audit time zone, not the browser's: the console builds its ranges from this.
                today = recorder.Clock.Today,
                timeZone = recorder.Clock.Zone.Id,
                auditEnabled = recorder.Enabled,
                signedIn = who is not null,
                method = who?.Method,
                name = who?.Name,
                keyConfigured = access.KeyEnabled,
                keyProblem = access.KeyProblem,
                roleSignIn = authEnabled,
                roles = authEnabled ? access.Roles : [],
                user = ctx.User.Identity?.IsAuthenticated == true ? ctx.User.FindFirst("name")?.Value ?? ctx.User.Identity.Name : null,
            });
        });

        session.MapPost("", (AdminKeyInput input, AdminAccess access, HttpContext ctx) =>
        {
            NoStore(ctx);
            if (!access.KeyEnabled)
                return Results.Json(new { error = access.KeyProblem ?? "Key sign-in is off on this server (Ydl:Admin:Key is not set)." }, statusCode: StatusCodes.Status403Forbidden);
            if (!access.KeyMatches(input.Key))
            {
                AuditRecorder.Annotate(ctx, detail: "admin key rejected");
                return Results.Json(new { error = "That key isn't right." }, statusCode: StatusCodes.Status401Unauthorized);
            }
            access.IssueCookie(ctx);
            AuditRecorder.Annotate(ctx, detail: "admin signed in with the key");
            return Results.Ok(new { signedIn = true, method = "key" });
        }).RequireRateLimiting("admin-login");

        session.MapDelete("", (HttpContext ctx) =>
        {
            AdminAccess.ClearCookie(ctx);
            return Results.NoContent();
        });

        var api = app.MapGroup("/api/admin").AddEndpointFilter(async (ic, next) =>
        {
            var ctx = ic.HttpContext;
            NoStore(ctx);
            var access = ctx.RequestServices.GetRequiredService<AdminAccess>();
            if (access.Check(ctx, authEnabled) is null)
                return Results.Json(new { error = "Sign in to the admin console first." }, statusCode: StatusCodes.Status401Unauthorized);
            return await next(ic);
        });

        api.MapGet("/overview", async (string? from, string? to, AuditReports reports, AuditWriter writer) =>
        {
            if (await Unavailable(writer) is { } no) return no;
            var range = DateRange.Parse(from, to, reports.Today);
            var prev = range.Previous();
            return Results.Ok(new
            {
                range,
                previous = prev,
                today = reports.Today,
                kpis = await reports.KpisAsync(range),
                previousKpis = await reports.KpisAsync(prev),
                daily = await reports.DailyAsync(range),
                hourly = range.Days <= 2 ? await reports.HourlyAsync(range) : null,
            });
        });

        api.MapGet("/breakdowns", async (string? from, string? to, AuditReports reports, AuditWriter writer) =>
        {
            if (await Unavailable(writer) is { } no) return no;
            var range = DateRange.Parse(from, to, reports.Today);
            return Results.Ok(new { range, data = await reports.BreakdownsAsync(range) });
        });

        api.MapGet("/downloads", async (string? from, string? to, string? q, string? status, string? mode, string? category, string? country,
            string? ip, int? page, int? size, AuditReports reports, AuditWriter writer) =>
        {
            if (await Unavailable(writer) is { } no) return no;
            var range = DateRange.Parse(from, to, reports.Today);
            var f = new LogFilter { Q = q, Status = status, Mode = mode, Category = category, Country = country, Ip = ip, Page = page ?? 1, Size = size ?? 50 };
            var (total, rows) = await reports.DownloadsAsync(range, f);
            return Results.Ok(new { range, total, page = Math.Max(1, f.Page), size = Math.Clamp(f.Size, 10, 200), rows = Rows(rows) });
        });

        api.MapGet("/downloads/{jobId}", async (string jobId, AuditReports reports, AuditWriter writer) =>
        {
            if (await Unavailable(writer) is { } no) return no;
            if (jobId.Length > 40) return Results.NotFound();
            var row = await reports.DownloadAsync(jobId);
            return row is null
                ? Results.Json(new { error = "No such download in the audit log." }, statusCode: StatusCodes.Status404NotFound)
                : Results.Ok(new { row = Plain(row), requests = (await reports.RequestsForJobAsync(jobId)).Select(Plain) });
        });

        api.MapGet("/requests", async (string? from, string? to, string? q, string? kind, string? status, string? ip, int? page, int? size,
            AuditReports reports, AuditWriter writer) =>
        {
            if (await Unavailable(writer) is { } no) return no;
            var range = DateRange.Parse(from, to, reports.Today);
            var f = new LogFilter { Q = q, Kind = kind, Status = status, Ip = ip, Page = page ?? 1, Size = size ?? 50 };
            var (total, rows) = await reports.RequestsAsync(range, f);
            return Results.Ok(new { range, total, page = Math.Max(1, f.Page), size = Math.Clamp(f.Size, 10, 200), rows = Rows(rows) });
        });

        api.MapGet("/export/{table}", async (string table, string? from, string? to, AuditReports reports, AuditWriter writer) =>
        {
            if (await Unavailable(writer) is { } no) return no;
            var name = table switch { "downloads.csv" => AuditSchema.Downloads, "requests.csv" => AuditSchema.Requests, "daily.csv" => AuditSchema.Daily, _ => null };
            if (name is null) return Results.NotFound();
            var range = DateRange.Parse(from, to, reports.Today);
            var csv = await reports.CsvAsync(name, range);
            return Results.File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv)).ToArray(), "text/csv; charset=utf-8",
                $"ark-ydl-{table[..^4]}-{range.From}-to-{range.To}.csv");
        });

        api.MapGet("/status", async (AuditWriter writer, AuditMaintenance maintenance, AuditRecorder recorder, AuditReports reports,
            GeoIpService geo, ReleaseCatalog catalog, LatestReleaseService latest, ToolLocator tools, IOptions<YdlOptions> opt,
            IOptions<ArkDatabaseOptions> dbOpt, YdlPaths paths) =>
        {
            var o = opt.Value.Audit;
            var file = ArkDatabaseServiceCollectionExtensions.SqliteFile(dbOpt, paths);
            long? size = null;
            if (file is not null)
                size = new[] { file, file + "-wal", file + "-shm" }.Where(File.Exists).Sum(f => new FileInfo(f).Length);
            object? tables = null;
            string? schema = null;
            if (writer.IsReady)
            {
                try
                {
                    tables = await reports.TableStatsAsync();
                    schema = await AuditSchema.GetMetaAsync(writer.Db, "schema_version");
                }
                catch (Exception ex)
                {
                    tables = new { error = ex.Message };
                }
            }
            var s = tools.Status;
            return Results.Ok(new
            {
                audit = new
                {
                    enabled = o.Enabled,
                    logRequests = o.LogRequests,
                    includeStaticFiles = o.IncludeStaticFiles,
                    excludePaths = o.ExcludePaths,
                    retentionDays = o.RetentionDays,
                    ipMode = o.IpMode.ToString(),
                    timeZone = recorder.Clock.Zone.Id,
                    timeZoneNote = recorder.Clock.Note,
                    captureItemMetadata = o.CaptureItemMetadata,
                    countryHeader = o.CountryHeader,
                    provider = dbOpt.Value.Databases.TryGetValue(ArkDatabaseOptions.Audit, out var db) ? db.Provider : "Sqlite",
                    file,
                    size,
                    schema,
                    writer = writer.Status,
                    maintenance = maintenance.Status,
                    tables,
                },
                geoip = geo.Status,
                release = ReleaseEndpoints.VersionInfo(catalog, latest),
                matrix = catalog.Matrix(),
                tools = new
                {
                    ytDlp = new { s.YtDlp.Ok, s.YtDlp.Version, s.YtDlp.Note },
                    ffmpeg = new { s.Ffmpeg.Ok, s.Ffmpeg.Version, s.Ffmpeg.Note },
                    jsRuntime = new { s.JsRuntime.Ok, s.JsRuntime.Name, s.JsRuntime.Version, s.JsRuntime.Note },
                },
                server = new { time = DateTimeOffset.UtcNow, machine = Environment.MachineName, os = System.Runtime.InteropServices.RuntimeInformation.OSDescription, dotnet = Environment.Version.ToString() },
            });
        });

        api.MapPost("/release/check", async (LatestReleaseService latest, ReleaseCatalog catalog, CancellationToken ct) =>
        {
            await latest.CheckAsync(ct);
            return Results.Ok(ReleaseEndpoints.VersionInfo(catalog, latest));
        });

        api.MapPost("/maintenance", async (AuditMaintenance maintenance, AuditWriter writer) =>
        {
            if (await Unavailable(writer) is { } no) return no;
            await writer.FlushAsync(TimeSpan.FromSeconds(5));
            await maintenance.RunOnceAsync(backfill: true);
            return Results.Ok(maintenance.Status);
        });
    }

    private static void NoStore(HttpContext ctx)
    {
        ctx.Response.Headers.CacheControl = "no-store";
        ctx.Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
    }

    private static async Task<IResult?> Unavailable(AuditWriter writer)
    {
        try
        {
            await writer.Ready.WaitAsync(TimeSpan.FromSeconds(5));
            return null;
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException)
        {
            var why = writer.Status.LastError ?? ex.Message;
            return Results.Json(new { error = $"The audit store isn't available: {why}" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    /// <summary>Dapper rows as plain dictionaries, so the JSON serializer never has to guess at its row type.</summary>
    private static IEnumerable<Dictionary<string, object?>> Rows(IEnumerable<dynamic> rows) =>
        rows.Cast<IDictionary<string, object>>().Select(Plain);

    private static Dictionary<string, object?> Plain(IDictionary<string, object> row) =>
        row.ToDictionary(kv => kv.Key, kv => kv.Value is DBNull ? null : (object?)kv.Value);
}
