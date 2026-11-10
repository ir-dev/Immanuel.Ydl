using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Ark.Feedback.Tests;

/// <summary>Boots the real app in-process with every download switched off, so nothing is fetched beyond what is on the machine.</summary>
public class AppFactory : WebApplicationFactory<Program>
{
    public string DataRoot { get; } = Directory.CreateTempSubdirectory("ark-feedback-tests-").FullName;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Feedback:DataRoot", DataRoot);
        builder.UseSetting("Feedback:Tools:AutoDownloadYtDlp", "false");
        builder.UseSetting("Feedback:Tools:AutoUpdateYtDlp", "false");
        builder.UseSetting("Feedback:Tools:YtDlpPath", Path.Combine(DataRoot, "no-such-yt-dlp"));
        builder.UseSetting("Feedback:Analysis:AutoDownloadModels", "false");
        builder.UseSetting("Feedback:Audit:GeoIp:AutoDownload", "false");
        builder.UseSetting("Feedback:Admin:Key", "test-admin-key-0123456789");
        foreach (var (k, v) in Settings) builder.UseSetting(k, v);
    }

    protected virtual IEnumerable<(string Key, string Value)> Settings => [];

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { Directory.Delete(DataRoot, true); } catch (IOException) { }
    }
}

public class ApiTests(AppFactory factory) : IClassFixture<AppFactory>
{
    private HttpClient Client() => factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    [Fact]
    public async Task ServesThePageWithSecurityHeaders()
    {
        var res = await Client().GetAsync("/");
        res.EnsureSuccessStatusCode();
        Assert.Equal("text/html", res.Content.Headers.ContentType?.MediaType);
        var csp = res.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("frame-ancestors 'none'", csp);
        Assert.Contains("i.ytimg.com", csp);
        var body = await res.Content.ReadAsStringAsync();
        Assert.Contains("ARK-RAPID", body);
        Assert.Contains("/js/app.js", body);
    }

    [Fact]
    public async Task ReportPageRendersTheShellForAnUnknownId()
    {
        var res = await Client().GetAsync("/r/20261110-00c0ffee");
        res.EnsureSuccessStatusCode();
        Assert.Contains("data-report=\"20261110-00c0ffee\"", await res.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await Client().GetAsync("/r/not-a-report-id!")).StatusCode);
    }

    [Theory]
    [InlineData("/js/app.js", "text/javascript")]
    [InlineData("/js/charts.js", "text/javascript")]
    [InlineData("/js/admin.js", "text/javascript")]
    [InlineData("/css/app.css", "text/css")]
    [InlineData("/css/admin.css", "text/css")]
    [InlineData("/manifest.webmanifest", "application/manifest+json")]
    [InlineData("/img/mark.svg", "image/svg+xml")]
    [InlineData("/favicon.ico", "image/x-icon")]
    public async Task ServesAssets(string path, string type)
    {
        var res = await Client().GetAsync(path);
        res.EnsureSuccessStatusCode();
        Assert.Equal(type, res.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task SystemReportsModelsAndProfiles()
    {
        var sys = await Client().GetFromJsonAsync<JsonElement>("/api/system");
        Assert.Equal("ARK-RAPID", sys.GetProperty("app").GetString());
        Assert.Equal(5, sys.GetProperty("models").GetArrayLength());
        Assert.Equal("balanced", sys.GetProperty("defaultProfile").GetString());
        Assert.False(sys.GetProperty("ready").GetBoolean()); // no yt-dlp, no models in the test data root
        var profiles = await Client().GetFromJsonAsync<JsonElement>("/api/profiles");
        Assert.Equal(3, profiles.GetProperty("profiles").GetArrayLength());
        var health = await Client().GetAsync("/healthz");
        health.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task RefusesBadLinksBeforeQueueing()
    {
        var res = await Client().PostAsJsonAsync("/api/analyses", new { url = "https://www.youtube.com/playlist?list=PL1" });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("playlist", body.GetProperty("error").GetString());
        res = await Client().PostAsJsonAsync("/api/analyses", new { url = "" });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task QueuesAnAnalysisAndFailsItHonestlyWithoutTools()
    {
        var res = await Client().PostAsJsonAsync("/api/analyses", new { url = "https://youtu.be/jNQXAC9IVRw", profile = "fast" });
        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        var job = await res.Content.ReadFromJsonAsync<JsonElement>();
        var id = job.GetProperty("id").GetString()!;
        Assert.Equal("fast", job.GetProperty("profile").GetString());
        JsonElement snap = default;
        for (var i = 0; i < 100; i++)
        {
            snap = await Client().GetFromJsonAsync<JsonElement>($"/api/analyses/{id}");
            var state = snap.GetProperty("state").GetString();
            if (state is "failed" or "completed") break;
            await Task.Delay(100);
        }
        Assert.Equal("failed", snap.GetProperty("state").GetString());
        Assert.Contains("yt-dlp", snap.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.True(snap.GetProperty("retryable").GetBoolean());
        // the event stream ends with the same final snapshot
        var events = await Client().GetStringAsync($"/api/analyses/{id}/events");
        Assert.Contains("\"state\":\"failed\"", events);
    }

    [Fact]
    public async Task UnknownReportsAnswer404()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await Client().GetAsync("/api/reports/20261110-00c0ffee")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client().PostAsJsonAsync("/api/reports/20261110-00c0ffee/feedback", new { liked = true })).StatusCode);
        var recent = await Client().GetFromJsonAsync<JsonElement>("/api/reports");
        Assert.Equal(0, recent.GetProperty("reports").GetArrayLength());
    }

    [Fact]
    public async Task AdminNeedsAKeyAndThenWorks()
    {
        var client = Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/admin/status")).StatusCode);
        var session = await client.GetFromJsonAsync<JsonElement>("/api/admin/session");
        Assert.False(session.GetProperty("signedIn").GetBoolean());
        Assert.True(session.GetProperty("keyConfigured").GetBoolean());

        var wrong = await client.PostAsJsonAsync("/api/admin/session", new { key = "nope" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        var ok = await client.PostAsJsonAsync("/api/admin/session", new { key = "test-admin-key-0123456789" });
        ok.EnsureSuccessStatusCode();
        var cookie = ok.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("ark_feedback_admin=", StringComparison.Ordinal)).Split(';')[0];
        client.DefaultRequestHeaders.Add("Cookie", cookie);

        var status = await client.GetFromJsonAsync<JsonElement>("/api/admin/status");
        Assert.Equal(5, status.GetProperty("models").GetArrayLength());
        Assert.True(status.GetProperty("audit").GetProperty("enabled").GetBoolean());
        // the audit writer opens the month file at start-up; give it a moment
        JsonElement overview = default;
        HttpResponseMessage? ov = null;
        for (var i = 0; i < 150; i++)
        {
            ov = await client.GetAsync("/api/admin/overview");
            if (ov.IsSuccessStatusCode) { overview = await ov.Content.ReadFromJsonAsync<JsonElement>(); break; }
            await Task.Delay(100);
        }
        Assert.True(ov!.IsSuccessStatusCode, $"{(int)ov.StatusCode}: {await ov.Content.ReadAsStringAsync()}");
        Assert.Equal(30, overview.GetProperty("daily").GetArrayLength());
        var page = await client.GetAsync("/admin");
        page.EnsureSuccessStatusCode();
        Assert.Contains("Admin console", await page.Content.ReadAsStringAsync());
        var store = await client.GetFromJsonAsync<JsonElement>("/api/admin/store");
        Assert.True(store.GetProperty("months").GetArrayLength() >= 1);
    }
}
