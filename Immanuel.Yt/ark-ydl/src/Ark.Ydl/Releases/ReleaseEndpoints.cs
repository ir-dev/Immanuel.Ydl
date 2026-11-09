namespace Ark.Ydl.Releases;

public static class ReleaseEndpoints
{
    /// <summary>The compact version block: shown on every page load, so it carries no release notes.</summary>
    public static object VersionInfo(ReleaseCatalog catalog, LatestReleaseService latest)
    {
        var l = latest.Current;
        var current = catalog.CurrentRelease;
        return new
        {
            product = catalog.Manifest.Product,
            app = catalog.Manifest.App,
            version = catalog.AppVersion,
            revision = catalog.Revision,
            builtAt = catalog.BuiltAt,
            manifest = catalog.WebManifestVersion,
            manifestMatches = catalog.WebManifestVersion is null || SemVer.Parse(catalog.WebManifestVersion) == SemVer.Parse(catalog.AppVersion),
            title = current?.Title,
            date = current?.Date,
            compatibility = current?.Compatibility.Level,
            latest = new { version = l.Version, title = l.Title, date = l.Date, source = l.Source, checkedAt = l.CheckedAt, error = l.Error },
            updateAvailable = latest.UpdateAvailable,
        };
    }

    public static void MapReleaseApi(this WebApplication app)
    {
        app.MapGet("/api/version", (ReleaseCatalog catalog, LatestReleaseService latest, HttpContext ctx) =>
        {
            ctx.Response.Headers.CacheControl = "no-cache";
            return Results.Ok(VersionInfo(catalog, latest));
        }).AllowAnonymous();

        app.MapGet("/api/releases", (ReleaseCatalog catalog, LatestReleaseService latest, HttpContext ctx) =>
        {
            ctx.Response.Headers.CacheControl = "no-cache";
            return Results.Json(new
            {
                product = catalog.Manifest.Product,
                current = catalog.AppVersion,
                latest = latest.Current.Version,
                updateAvailable = latest.UpdateAvailable,
                releases = catalog.Manifest.Releases,
                newer = latest.Current.Newer,
                legacy = catalog.Manifest.Legacy,
                matrix = catalog.Matrix(),
            }, ReleaseCatalog.Json);
        });

        app.MapGet("/api/releases/migration", (string? from, ReleaseCatalog catalog) =>
        {
            var plan = catalog.PlanFrom(from);
            return plan.Status == "unknown"
                ? Results.Json(new { error = plan.Recommendation }, ReleaseCatalog.Json, statusCode: StatusCodes.Status400BadRequest)
                : Results.Json(plan, ReleaseCatalog.Json);
        });
    }
}
