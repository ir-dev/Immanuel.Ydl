using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Ark.Ydl.Tests;

/// <summary>
/// Boots the real app in-process. Tool auto-download/update is switched off, so nothing is
/// fetched or executed beyond what is already installed on the machine.
/// </summary>
public sealed class AppFactory : WebApplicationFactory<Program>
{
    public string DataRoot { get; } = Directory.CreateTempSubdirectory("ark-ydl-tests-").FullName;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Ydl:DataRoot", DataRoot);
        builder.UseSetting("Ydl:Tools:AutoDownloadYtDlp", "false");
        builder.UseSetting("Ydl:Tools:AutoUpdateYtDlp", "false");
        builder.UseSetting("Ydl:Tools:YtDlpPath", Path.Combine(DataRoot, "no-such-yt-dlp"));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { Directory.Delete(DataRoot, true); } catch (IOException) { }
    }
}

public class ApiTests(AppFactory factory) : IClassFixture<AppFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private HttpClient Client(bool redirects = true) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = redirects });

    private async Task<bool> EngineInstalled()
    {
        var sys = await Client().GetFromJsonAsync<JsonElement>("/api/system");
        for (var i = 0; i < 50 && sys.GetProperty("preparing").GetBoolean(); i++)
        {
            await Task.Delay(100);
            sys = await Client().GetFromJsonAsync<JsonElement>("/api/system");
        }
        return sys.GetProperty("tools").GetProperty("ytDlp").GetProperty("ok").GetBoolean();
    }

    [Fact]
    public async Task ServesThePageWithSecurityHeaders()
    {
        var res = await Client().GetAsync("/");
        res.EnsureSuccessStatusCode();
        Assert.Equal("text/html", res.Content.Headers.ContentType?.MediaType);
        var csp = res.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("frame-src https://www.youtube-nocookie.com", csp);
        Assert.Contains("frame-ancestors 'none'", csp);
        Assert.Equal("strict-origin-when-cross-origin", res.Headers.GetValues("Referrer-Policy").Single());
        Assert.Contains("ark-ydl", await res.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/js/app.js", "text/javascript")]
    [InlineData("/css/app.css", "text/css")]
    [InlineData("/manifest.webmanifest", "application/manifest+json")]
    [InlineData("/sw.js", "text/javascript")]
    [InlineData("/img/icon.svg", "image/svg+xml")]
    public async Task ServesAssets(string path, string type)
    {
        var res = await Client().GetAsync(path);
        res.EnsureSuccessStatusCode();
        Assert.Equal(type, res.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task ScriptsRevalidate()
    {
        var res = await Client().GetAsync("/js/app.js");
        Assert.Equal("no-cache", res.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task SystemReportsTools()
    {
        var sys = await Client().GetFromJsonAsync<JsonElement>("/api/system");
        Assert.Equal("ark-ydl", sys.GetProperty("app").GetString());
        Assert.True(sys.GetProperty("tools").TryGetProperty("ffmpeg", out _));
        Assert.Equal(100, sys.GetProperty("limits").GetProperty("maxPlaylistItems").GetInt32());
        Assert.False(sys.GetProperty("auth").GetProperty("enabled").GetBoolean());
        Assert.Contains("sponsor", sys.GetProperty("sponsorCategories").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task Health()
    {
        var res = await Client().GetFromJsonAsync<JsonElement>("/healthz");
        Assert.True(res.TryGetProperty("status", out _));
    }

    [Theory]
    [InlineData("https://vimeo.com/123", "Only YouTube links")]
    [InlineData("not a link", "doesn't look like a link")]
    [InlineData("", "Paste a link")]
    public async Task ProbeRejectsBadLinks(string url, string message)
    {
        var res = await Client().PostAsJsonAsync("/api/probe", new { url });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(message, body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task ProbeWithoutEngineSaysSo()
    {
        if (await EngineInstalled()) return; // a real yt-dlp on PATH would go to YouTube; not in a unit run
        var res = await Client().PostAsJsonAsync("/api/probe", new { url = "https://youtu.be/jNQXAC9IVRw" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("yt-dlp", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task JobValidation()
    {
        var res = await Client().PostAsJsonAsync("/api/jobs", new
        {
            url = "https://youtu.be/jNQXAC9IVRw",
            mode = "video",
            subtitles = new { mode = "file", langs = new[] { "en;rm -rf" } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);

        res = await Client().PostAsJsonAsync("/api/jobs", new { url = "https://youtu.be/jNQXAC9IVRw", mode = "karaoke" });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);

        res = await Client().PostAsJsonAsync("/api/jobs", new { url = "https://example.com/x", mode = "video" });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task JobLifecycleWithoutEngine()
    {
        if (await EngineInstalled()) return;
        var client = Client();
        var res = await client.PostAsJsonAsync("/api/jobs", new
        {
            url = "https://youtu.be/jNQXAC9IVRw?t=3",
            mode = "audio",
            audio = new { format = "mp3", bitrate = 192 },
            clip = new { sections = new[] { new { start = 3.0, end = (double?)9.5 } }, precise = true },
            title = "Me at the zoo",
            label = "Clip · MP3",
        });
        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        var job = await res.Content.ReadFromJsonAsync<JsonElement>();
        var id = job.GetProperty("id").GetString()!;
        Assert.Equal(22, id.Length);
        Assert.Equal($"/api/jobs/{id}", res.Headers.Location?.ToString());

        // The SSE stream delivers snapshots and closes after the final one.
        using var sse = await client.GetAsync($"/api/jobs/{id}/events", HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal("text/event-stream", sse.Content.Headers.ContentType?.MediaType);
        await using var stream = await sse.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);
        string? last = null;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (await reader.ReadLineAsync(cts.Token) is { } line)
            if (line.StartsWith("data: ")) last = line[6..];
        Assert.NotNull(last);
        var final = JsonSerializer.Deserialize<JsonElement>(last!, Json);
        Assert.Equal("failed", final.GetProperty("status").GetString());
        Assert.Contains("isn't installed", final.GetProperty("error").GetString());
        Assert.Equal("Me at the zoo", final.GetProperty("title").GetString());
        Assert.Equal("audio", final.GetProperty("mode").GetString());

        var again = await client.GetFromJsonAsync<JsonElement>($"/api/jobs/{id}");
        Assert.Equal("failed", again.GetProperty("status").GetString());

        var del = await client.DeleteAsync($"/api/jobs/{id}");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);
        Assert.Equal(HttpStatusCode.Gone, (await client.GetAsync($"/api/jobs/{id}")).StatusCode);
    }

    [Theory]
    [InlineData("/api/jobs/AAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("/api/jobs/../../etc/passwd")]
    [InlineData("/api/jobs/AAAAAAAAAAAAAAAAAAAAAA/files/0")]
    [InlineData("/api/jobs/AAAAAAAAAAAAAAAAAAAAAA/zip")]
    public async Task UnknownJobsAreGone(string path)
    {
        var res = await Client().GetAsync(path);
        Assert.True(res.StatusCode is HttpStatusCode.Gone or HttpStatusCode.NotFound, res.StatusCode.ToString());
    }

    [Theory]
    [InlineData("/watch?v=jNQXAC9IVRw&t=42", "https://www.youtube.com/watch?v=jNQXAC9IVRw&t=42")]
    [InlineData("/shorts/jNQXAC9IVRw", "https://www.youtube.com/shorts/jNQXAC9IVRw")]
    [InlineData("/playlist?list=PLabcdefghijklmnop12", "https://www.youtube.com/playlist?list=PLabcdefghijklmnop12")]
    [InlineData("/jNQXAC9IVRw", "https://youtu.be/jNQXAC9IVRw")]
    [InlineData("/@SomeChannel", "https://www.youtube.com/@SomeChannel")]
    public async Task SwapTheDomainShortcuts(string path, string youtubeUrl)
    {
        var res = await Client(redirects: false).GetAsync(path);
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal("/?url=" + Uri.EscapeDataString(youtubeUrl), res.Headers.Location?.ToString());
    }
}
