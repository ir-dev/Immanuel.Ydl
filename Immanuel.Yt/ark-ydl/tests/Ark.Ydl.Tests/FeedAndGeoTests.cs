using System.Net;
using System.Text;
using Ark.Ydl.Audit;
using Ark.Ydl.Options;
using Ark.Ydl.Releases;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ark.Ydl.Tests;

public class LatestReleaseTests
{
    private sealed class Handler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class Factory(HttpMessageHandler h) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(h, disposeHandler: false);
    }

    private static (LatestReleaseService Svc, Handler H) Make(HttpStatusCode status, string body, string? feed = "https://feed.example/releases.json")
    {
        var manifest = new ReleaseManifest
        {
            Current = "1.1.0",
            Releases = [new ReleaseEntry { Version = "1.1.0", Title = "This build", Date = "2026-11-10" }, new ReleaseEntry { Version = "1.0.0", Title = "Older" }],
        };
        var catalog = new ReleaseCatalog(manifest, "1.1.0", typeof(ReleaseCatalog).Assembly);
        var h = new Handler(status, body);
        var opt = Microsoft.Extensions.Options.Options.Create(new YdlOptions { Release = new ReleaseOptions { FeedUrl = feed } });
        return (new LatestReleaseService(catalog, opt, new Factory(h), NullLogger<LatestReleaseService>.Instance), h);
    }

    private const string Newer = """
        { "current": "1.2.0", "releases": [
          { "version": "1.2.0", "date": "2026-12-01", "title": "Newer", "summary": "More things", "compatibility": { "level": "compatible" } },
          { "version": "1.1.0", "date": "2026-11-10", "title": "This build" } ] }
        """;

    [Fact]
    public async Task ANewerFeedReleaseMeansAnUpdateIsAvailable()
    {
        var (svc, h) = Make(HttpStatusCode.OK, Newer);
        Assert.Equal("local", svc.Current.Source);
        Assert.False(svc.UpdateAvailable);

        var latest = await svc.CheckAsync(CancellationToken.None);
        Assert.Equal(1, h.Calls);
        Assert.Equal("1.2.0", latest.Version);
        Assert.Equal("feed", latest.Source);
        Assert.Equal("Newer", latest.Title);
        Assert.True(svc.UpdateAvailable);
        Assert.Equal("1.2.0", Assert.Single(latest.Newer).Version);
    }

    [Fact]
    public async Task AFeedBehindThisBuildNeverHidesIt()
    {
        var (svc, _) = Make(HttpStatusCode.OK, """{ "current": "1.0.0", "releases": [ { "version": "1.0.0", "title": "Older" } ] }""");
        var latest = await svc.CheckAsync(CancellationToken.None);
        Assert.Equal("1.1.0", latest.Version); // deployed before the push: the local release wins
        Assert.Equal("local", latest.Source);
        Assert.False(svc.UpdateAvailable);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "", "answered 404")]
    [InlineData(HttpStatusCode.OK, "<html>not json</html>", "Couldn't read")]
    [InlineData(HttpStatusCode.OK, "{ \"releases\": [] }", "names no version")]
    public async Task AFeedThatFailsFallsBackToThisBuild(HttpStatusCode status, string body, string error)
    {
        var (svc, _) = Make(status, body);
        var latest = await svc.CheckAsync(CancellationToken.None);
        Assert.Equal("1.1.0", latest.Version);
        Assert.Equal("local", latest.Source);
        Assert.Contains(error, latest.Error);
        Assert.False(svc.UpdateAvailable);
    }

    [Fact]
    public async Task NoFeedMeansNoCall()
    {
        var (svc, h) = Make(HttpStatusCode.OK, Newer, feed: "");
        Assert.False(svc.FeedConfigured);
        await svc.CheckAsync(CancellationToken.None);
        Assert.Equal(0, h.Calls);
    }
}

/// <summary>
/// Downloads the real DB-IP country lite file (about 4 MB) into a temp folder and looks addresses up. Off by default:
/// set ARK_YDL_GEOIP_LIVE=1 to run it.
/// </summary>
public class GeoIpLiveTests
{
    [Fact]
    public async Task DownloadsValidatesAndLooksUp()
    {
        if (Environment.GetEnvironmentVariable("ARK_YDL_GEOIP_LIVE") != "1") return;
        var root = Directory.CreateTempSubdirectory("ark-ydl-geoip-").FullName;
        try
        {
            var o = new YdlOptions { DataRoot = root, Audit = new AuditOptions { GeoIp = new GeoIpOptions { Edition = "country" } } };
            var paths = new YdlPaths(o, root);
            using var handler = new SocketsHttpHandler();
            using var geo = new GeoIpService(Microsoft.Extensions.Options.Options.Create(o), paths, new Clients(handler), NullLogger<GeoIpService>.Instance);

            Assert.True(await geo.RefreshAsync(CancellationToken.None), geo.Status.Error);
            Assert.Equal("ready", geo.Status.State);
            Assert.Single(Directory.GetFiles(paths.GeoIp, "dbip-country-lite-*.mmdb"));
            Assert.Empty(Directory.GetFiles(paths.GeoIp, "*.part"));

            Assert.Equal("US", geo.Lookup(IPAddress.Parse("8.8.8.8"))?.Country);
            Assert.Equal("GB", geo.Lookup(IPAddress.Parse("81.2.69.160"))?.Country);
            Assert.NotNull(geo.Lookup(IPAddress.Parse("2001:4860:4860::8888"))?.CountryName);
            Assert.Null(geo.Lookup(IPAddress.Parse("192.168.1.1")));

            // A second refresh in the same month does nothing.
            Assert.True(await geo.RefreshAsync(CancellationToken.None));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    /// <summary>A fresh client per call (the service disposes each one), over one shared handler.</summary>
    private sealed class Clients(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { Timeout = TimeSpan.FromMinutes(5) };
    }
}
