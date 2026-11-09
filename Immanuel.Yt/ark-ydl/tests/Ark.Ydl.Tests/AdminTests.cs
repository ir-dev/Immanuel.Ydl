using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ark.Ydl.Audit;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Ark.Ydl.Tests;

public sealed class AdminFactory : AppFactory
{
    public const string Key = "test-admin-key-0123456789";
    protected override IEnumerable<(string Key, string Value)> Settings => [("Ydl:Admin:Key", Key)];
}

public sealed class ShortKeyFactory : AppFactory
{
    protected override IEnumerable<(string Key, string Value)> Settings => [("Ydl:Admin:Key", "short")];
}

/// <summary>No key, not Development: the console is locked.</summary>
public class AdminLockedTests(AppFactory factory) : IClassFixture<AppFactory>
{
    [Theory]
    [InlineData("/api/admin/overview")]
    [InlineData("/api/admin/breakdowns")]
    [InlineData("/api/admin/downloads")]
    [InlineData("/api/admin/requests")]
    [InlineData("/api/admin/status")]
    [InlineData("/api/admin/export/downloads.csv")]
    public async Task EveryDataEndpointNeedsSignIn(string path)
    {
        var res = await factory.CreateClient().GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.Equal("no-store", res.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task SessionExplainsWhyItIsLocked()
    {
        var client = factory.CreateClient();
        var s = await client.GetFromJsonAsync<JsonElement>("/api/admin/session");
        Assert.False(s.GetProperty("signedIn").GetBoolean());
        Assert.False(s.GetProperty("keyConfigured").GetBoolean());
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", s.GetProperty("today").GetString());
        var post = await client.PostAsJsonAsync("/api/admin/session", new { key = "anything at all, long enough" });
        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);
    }

    [Fact]
    public async Task TheConsolePageIsServedButNeverCachedOrIndexed()
    {
        var res = await factory.CreateClient().GetAsync("/admin");
        res.EnsureSuccessStatusCode();
        Assert.Equal("text/html", res.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-store", res.Headers.CacheControl?.ToString());
        Assert.Contains("noindex", string.Join(",", res.Headers.GetValues("X-Robots-Tag")));
        Assert.Contains("Admin console", await res.Content.ReadAsStringAsync());
        foreach (var asset in new[] { "/js/admin.js", "/js/charts.js", "/css/admin.css" })
            (await factory.CreateClient().GetAsync(asset)).EnsureSuccessStatusCode();
    }
}

public class AdminShortKeyTests(ShortKeyFactory factory) : IClassFixture<ShortKeyFactory>
{
    [Fact]
    public async Task AShortKeyIsRefusedNotTrusted()
    {
        var client = factory.CreateClient();
        var s = await client.GetFromJsonAsync<JsonElement>("/api/admin/session");
        Assert.False(s.GetProperty("keyConfigured").GetBoolean());
        Assert.Contains("shorter than 16", s.GetProperty("keyProblem").GetString());
        var post = await client.PostAsJsonAsync("/api/admin/session", new { key = "short" });
        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);
    }
}

public class AdminTests(AdminFactory factory) : IClassFixture<AdminFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private async Task<HttpClient> SignedIn()
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var res = await client.PostAsJsonAsync("/api/admin/session", new { key = AdminFactory.Key });
        res.EnsureSuccessStatusCode();
        return client;
    }

    [Fact]
    public async Task KeySignInSetsAStrictHttpOnlyCookie()
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var wrong = await client.PostAsJsonAsync("/api/admin/session", new { key = "not-the-key-but-long-enough" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.False(wrong.Headers.Contains("Set-Cookie"));

        var ok = await client.PostAsJsonAsync("/api/admin/session", new { key = AdminFactory.Key });
        ok.EnsureSuccessStatusCode();
        var cookie = ok.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("ark_ydl_admin="));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(AdminFactory.Key, cookie);

        // A forged cookie is ignored.
        var forged = new HttpRequestMessage(HttpMethod.Get, "/api/admin/status");
        forged.Headers.Add("Cookie", "ark_ydl_admin=key|deadbeef");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(forged)).StatusCode);

        var real = new HttpRequestMessage(HttpMethod.Get, "/api/admin/status");
        real.Headers.Add("Cookie", cookie.Split(';')[0]);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(real)).StatusCode);
    }

    [Fact]
    public async Task SignedInSessionAndSignOut()
    {
        var client = await SignedIn();
        var s = await client.GetFromJsonAsync<JsonElement>("/api/admin/session");
        Assert.True(s.GetProperty("signedIn").GetBoolean());
        Assert.Equal("key", s.GetProperty("method").GetString());
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/admin/session")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/admin/status")).StatusCode);
    }

    [Fact]
    public async Task ReportsHaveTheirShape()
    {
        var client = await SignedIn();
        var o = await client.GetFromJsonAsync<JsonElement>("/api/admin/overview?from=2026-01-01&to=2026-01-30");
        Assert.Equal("2026-01-01", o.GetProperty("range").GetProperty("from").GetString());
        Assert.Equal(30, o.GetProperty("daily").GetArrayLength());
        Assert.Equal("2025-12-02", o.GetProperty("previous").GetProperty("from").GetString());
        Assert.True(o.GetProperty("kpis").TryGetProperty("successRate", out _));
        Assert.False(o.TryGetProperty("hourly", out var h) && h.ValueKind != JsonValueKind.Null); // only for ranges of 1–2 days

        var today = (await client.GetFromJsonAsync<JsonElement>("/api/admin/session")).GetProperty("today").GetString();
        var t = await client.GetFromJsonAsync<JsonElement>($"/api/admin/overview?from={today}&to={today}");
        Assert.Equal(24, t.GetProperty("hourly").GetArrayLength());

        var b = await client.GetFromJsonAsync<JsonElement>("/api/admin/breakdowns");
        foreach (var k in new[] { "genres", "formats", "modes", "scopes", "countries", "visitorCountries", "devices", "browsers", "os", "languages", "videos", "heatmap", "errors", "requestKinds", "statusCodes" })
            Assert.True(b.GetProperty("data").TryGetProperty(k, out _), k);

        var st = await client.GetFromJsonAsync<JsonElement>("/api/admin/status");
        Assert.True(st.GetProperty("audit").GetProperty("enabled").GetBoolean());
        Assert.EndsWith("ark-ydl-audit.db", st.GetProperty("audit").GetProperty("file").GetString());
        Assert.Equal("1", st.GetProperty("audit").GetProperty("schema").GetString());
        Assert.Contains("DB-IP", st.GetProperty("geoip").GetProperty("attribution").GetString());
        Assert.True(st.GetProperty("matrix").GetArrayLength() >= 1);
    }

    [Fact]
    public async Task ADownloadShowsUpInTheLogsWithItsRequests()
    {
        var anon = factory.CreateClient();
        var sys = await anon.GetFromJsonAsync<JsonElement>("/api/system");
        for (var i = 0; i < 50 && sys.GetProperty("preparing").GetBoolean(); i++)
        {
            await Task.Delay(100);
            sys = await anon.GetFromJsonAsync<JsonElement>("/api/system");
        }
        if (sys.GetProperty("tools").GetProperty("ytDlp").GetProperty("ok").GetBoolean()) return; // a real yt-dlp would go to YouTube

        var marker = "audit-" + Guid.NewGuid().ToString("N")[..8];
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/jobs")
        {
            Content = JsonContent.Create(new { url = "https://youtu.be/jNQXAC9IVRw", mode = "audio", audio = new { format = "mp3", bitrate = 192 }, title = marker, label = "MP3 · 192 kbps" }),
        };
        req.Headers.Add("X-Ark-Client", "tz=Asia%2FKolkata&sw=390&sh=844&dpr=3&pwa=1&v=1.1.0");
        req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (iPhone; CPU iPhone OS 18_1 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.1 Mobile/15E148 Safari/604.1");
        var res = await anon.SendAsync(req);
        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        var id = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        // Wait for it to fail (no engine), then for the writer to catch up.
        for (var i = 0; i < 100; i++)
        {
            var snap = await anon.GetFromJsonAsync<JsonElement>($"/api/jobs/{id}");
            if (snap.GetProperty("status").GetString() is "failed" or "completed" or "canceled") break;
            await Task.Delay(100);
        }
        // Rejected attempts are recorded too.
        await anon.PostAsJsonAsync("/api/jobs", new { url = "https://example.com/x", mode = "video" });
        await factory.Services.GetRequiredService<AuditWriter>().FlushAsync(TimeSpan.FromSeconds(10));

        var admin = await SignedIn();
        var list = await admin.GetFromJsonAsync<JsonElement>($"/api/admin/downloads?q={marker}");
        Assert.Equal(1, list.GetProperty("total").GetInt32());
        var row = list.GetProperty("rows")[0];
        Assert.Equal(id, row.GetProperty("job_id").GetString());
        Assert.Equal("failed", row.GetProperty("status").GetString());
        Assert.Equal("audio", row.GetProperty("mode").GetString());
        Assert.Equal("MP3 192k", row.GetProperty("format").GetString());
        Assert.Equal("Safari", row.GetProperty("browser").GetString());
        Assert.Equal("iOS", row.GetProperty("os").GetString());
        Assert.Equal("mobile", row.GetProperty("device").GetString());

        var detail = await admin.GetFromJsonAsync<JsonElement>($"/api/admin/downloads/{id}");
        Assert.Equal("Asia/Kolkata", detail.GetProperty("row").GetProperty("client_tz").GetString());
        Assert.Equal(1, detail.GetProperty("row").GetProperty("client_pwa").GetInt32());
        Assert.Contains("isn't installed", detail.GetProperty("row").GetProperty("error").GetString());
        Assert.Contains(detail.GetProperty("requests").EnumerateArray(), r => r.GetProperty("kind").GetString() == "job");

        var rejected = await admin.GetFromJsonAsync<JsonElement>("/api/admin/requests?kind=job&status=4xx&q=Only%20YouTube");
        Assert.True(rejected.GetProperty("total").GetInt32() >= 1);

        var csv = await admin.GetAsync("/api/admin/export/downloads.csv");
        csv.EnsureSuccessStatusCode();
        Assert.Equal("text/csv", csv.Content.Headers.ContentType?.MediaType);
        Assert.Contains("attachment", csv.Content.Headers.ContentDisposition?.ToString());
        Assert.Contains(id, await csv.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/admin/export/secrets.csv")).StatusCode);
    }

    [Fact]
    public async Task KeyGuessingIsRateLimited()
    {
        using var isolated = new AdminFactory(); // its own limiter, so the other tests keep signing in
        var client = isolated.CreateClient();
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 12; i++)
            codes.Add((await client.PostAsJsonAsync("/api/admin/session", new { key = $"wrong-guess-number-{i:00}" })).StatusCode);
        Assert.Contains(HttpStatusCode.TooManyRequests, codes);
        Assert.Equal(HttpStatusCode.Unauthorized, codes[0]);
    }
}
