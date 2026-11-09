using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Ark.Ydl.Options;
using Ark.Ydl.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using static Ark.Ydl.Tests.FfmpegInstallTests;

namespace Ark.Ydl.Tests;

public class JsRuntimeNameTests
{
    [Theory]
    [InlineData("WINDOWS", Architecture.X64, "x86_64-pc-windows-msvc")]
    [InlineData("WINDOWS", Architecture.Arm64, "aarch64-pc-windows-msvc")]
    [InlineData("OSX", Architecture.Arm64, "aarch64-apple-darwin")]
    [InlineData("OSX", Architecture.X64, "x86_64-apple-darwin")]
    [InlineData("LINUX", Architecture.X64, "x86_64-unknown-linux-gnu")]
    [InlineData("LINUX", Architecture.Arm64, "aarch64-unknown-linux-gnu")]
    [InlineData("WINDOWS", Architecture.X86, null)] // a 32-bit OS; a 32-bit app pool on 64-bit Windows reports X64
    [InlineData("LINUX", Architecture.Arm, null)]
    [InlineData("FREEBSD", Architecture.X64, null)]
    public void DenoTargetMatchesItsReleaseFiles(string os, Architecture arch, string? target) =>
        Assert.Equal(target, ToolLocator.DenoTarget(OSPlatform.Create(os), arch));

    [Theory]
    [InlineData(@"C:\home\site\tools\deno.exe", "deno")]
    [InlineData("/usr/local/bin/deno", "deno")]
    [InlineData("/usr/bin/nodejs", "node")]
    [InlineData(@"D:\Program Files\nodejs\node.exe", "node")]
    [InlineData("/opt/bun/bin/bun", "bun")]
    [InlineData("qjs", "quickjs")]
    [InlineData("/x/js-runner", null)]
    public void JsRuntimePathNamesTheRuntime(string path, string? name) => Assert.Equal(name, ToolLocator.JsRuntimeFromFileName(path));
}

/// <summary>
/// The deno bootstrap (find, download from dl.deno.land, verify, install, retry) against an in-memory server.
/// Nothing on the test machine's PATH is used. The "deno" handed out is a shell script, so the tests that run
/// it need macOS or Linux.
/// </summary>
public sealed partial class JsRuntimeInstallTests : IDisposable
{
    private const string Base = "https://deno.test/";
    private readonly string _root = Directory.CreateTempSubdirectory("ark-ydl-js-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private static string DenoScript(string version) => $"#!/bin/sh\necho 'deno {version} (stable, release, test)'\necho 'v8 14.0.0'\n";

    private static string Sha(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));

    private static HttpResponseMessage Ok(byte[] body) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
    private static HttpResponseMessage Ok(string text) => Ok(Encoding.UTF8.GetBytes(text));
    private static HttpResponseMessage Status(HttpStatusCode code) => new(code) { Content = new ByteArrayContent([]) };

    [GeneratedRegex(@"^https://deno\.test/release/(v[^/]+)/(deno-[a-z0-9_]+-[a-z0-9_-]+\.zip)(\.sha256sum)?$")]
    private static partial Regex ReleaseFileRx();

    /// <summary>Answers like dl.deno.land: release-latest.txt, then release/{v}/deno-{target}.zip with a GNU-style .sha256sum beside it.</summary>
    private static Func<HttpRequestMessage, HttpResponseMessage> DenoServer(string latest = "v2.9.9\n", bool corrupt = false)
    {
        return req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url == Base + "release-latest.txt") return Ok(latest);
            var m = ReleaseFileRx().Match(url);
            if (!m.Success) return Status(HttpStatusCode.NotFound);
            var zip = Zip((Exe("deno"), DenoScript(m.Groups[1].Value[1..])));
            return m.Groups[3].Success
                ? Ok($"{(corrupt ? new string('0', 64) : Sha(zip))}  {m.Groups[2].Value}\n")
                : Ok(zip);
        };
    }

    private static string Exe(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;

    private (ToolLocator Tools, YdlPaths Paths) Create(HttpMessageHandler server, Action<ToolOptions>? configure = null)
    {
        var o = new YdlOptions
        {
            DataRoot = _root,
            Tools =
            {
                YtDlpPath = Path.Combine(_root, "no-such-yt-dlp"),
                AutoDownloadYtDlp = false,
                AutoUpdateYtDlp = false,
                FfmpegPath = Path.Combine(_root, "no-such-ffmpeg"),
                AutoDownloadFfmpeg = false,
                DenoReleaseBase = Base,
            },
        };
        configure?.Invoke(o.Tools);
        var paths = new YdlPaths(o, _root);
        var tools = new ToolLocator(Microsoft.Extensions.Options.Options.Create(o), paths, new Factory(server), NullLogger<ToolLocator>.Instance)
        {
            RetryDelay = TimeSpan.Zero,
            Which = _ => null, // as on a host where nothing is installed
        };
        return (tools, paths);
    }

    [Fact]
    public async Task WithNothingInstalledDenoIsDownloadedVerifiedAndFoundAgainOnTheNextStart()
    {
        if (OperatingSystem.IsWindows()) return;
        var server = new Fake(DenoServer());
        var (tools, paths) = Create(server);

        await tools.InitializeAsync(CancellationToken.None);

        var dir = Path.Combine(paths.Tools, "deno");
        var js = tools.Status.JsRuntime;
        Assert.True(js.Ok, js.Note);
        Assert.Equal(("deno", "2.9.9", Path.Combine(dir, "deno")), (js.Name, js.Version, js.Path));
        Assert.Equal("Downloaded deno v2.9.9 from deno.test (SHA-256 verified).", js.Note);
        Assert.Equal(["deno"], Directory.GetFileSystemEntries(dir).Select(Path.GetFileName));
        Assert.False(Directory.Exists(dir + ".download"));
        Assert.Equal(Base + "release-latest.txt", server.Requests[0]);
        Assert.Matches(@"/release/v2\.9\.9/deno-[a-z0-9_]+-[a-z0-9_-]+\.zip\.sha256sum$", server.Requests[^1]);
        Assert.Equal(["--js-runtimes", "deno:" + js.Path], tools.ToolArgs());
        Assert.Empty(tools.MissingDownloads());
        Assert.False(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DENO_DIR"))); // deno gets a writable cache

        // Next start: the app's own copy is found and nothing is downloaded.
        server.Requests.Clear();
        await tools.InitializeAsync(CancellationToken.None);
        Assert.Equal(js.Path, tools.Status.JsRuntime.Path);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task APinnedVersionSkipsTheLatestLookup()
    {
        if (OperatingSystem.IsWindows()) return;
        var server = new Fake(DenoServer());
        var (tools, _) = Create(server, t => t.DenoVersion = "2.9.8");

        var info = await tools.InstallDenoAsync(CancellationToken.None);

        Assert.True(info.Ok, info.Note);
        Assert.Equal("2.9.8", info.Version);
        Assert.DoesNotContain(server.Requests, r => r.EndsWith("release-latest.txt"));
        Assert.All(server.Requests, r => Assert.Contains("/release/v2.9.8/", r));
    }

    [Fact]
    public async Task AChecksumMismatchInstallsNothingAndStaysMissing()
    {
        var (tools, paths) = Create(new Fake(DenoServer(corrupt: true)));

        await tools.InitializeAsync(CancellationToken.None);

        var js = tools.Status.JsRuntime;
        Assert.False(js.Ok);
        Assert.Contains("Checksum mismatch for deno-", js.Note);
        Assert.Contains("keeps retrying in the background", js.Note);
        Assert.False(Directory.Exists(Path.Combine(paths.Tools, "deno")));
        Assert.False(Directory.Exists(Path.Combine(paths.Tools, "deno.download")));
        Assert.Equal(["deno"], tools.MissingDownloads());
        Assert.Empty(tools.ToolArgs());
    }

    [Fact]
    public async Task SomethingThatIsNotAVersionIsRefused()
    {
        var server = new Fake(DenoServer(latest: "<html>captive portal</html>"));
        var (tools, _) = Create(server);

        var info = await tools.InstallDenoAsync(CancellationToken.None);

        Assert.False(info.Ok);
        Assert.Contains("\"<html>captive portal</html>\" is not a deno version", info.Note);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task AFailedDownloadIsRetriedLater()
    {
        if (OperatingSystem.IsWindows()) return;
        var up = false;
        var deno = DenoServer();
        var server = new Fake(req => up ? deno(req) : Status(HttpStatusCode.ServiceUnavailable));
        var (tools, _) = Create(server);

        await tools.InitializeAsync(CancellationToken.None);
        Assert.False(tools.Status.JsRuntime.Ok);
        Assert.Contains("deno.test answered 503 (Service Unavailable), 3 times.", tools.Status.JsRuntime.Note);
        Assert.Equal(["deno"], tools.MissingDownloads());

        up = true;
        Assert.Empty(await tools.RetryMissingAsync(CancellationToken.None));
        Assert.True(tools.Status.JsRuntime.Ok, tools.Status.JsRuntime.Note);
        Assert.Equal("deno", tools.Status.JsRuntime.Name);
    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private string Script(string name, string body)
    {
        var path = Path.Combine(Directory.CreateDirectory(Path.Combine(_root, "uploaded")).FullName, name);
        File.WriteAllText(path, body);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    [Fact]
    public async Task AnUploadedRuntimeIsUsedWithAutoAndTheFileNameSaysWhichOneItIs()
    {
        if (OperatingSystem.IsWindows()) return;
        var path = Script("deno", DenoScript("2.5.0"));
        var server = new Fake(DenoServer());
        var (tools, _) = Create(server, t => t.JsRuntimePath = path);

        await tools.InitializeAsync(CancellationToken.None);

        Assert.Equal(("deno", path, "2.5.0"), (tools.Status.JsRuntime.Name, tools.Status.JsRuntime.Path, tools.Status.JsRuntime.Version));
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task ARuntimeTooOldForYtDlpIsSkippedAndSaidSo()
    {
        if (OperatingSystem.IsWindows()) return;
        var path = Script("node", "#!/bin/sh\necho v18.20.4\n"); // e.g. the iisnode a shared host ships
        var (tools, _) = Create(new Fake(DenoServer()), t => t.JsRuntimePath = path);

        await tools.InitializeAsync(CancellationToken.None);

        var js = tools.Status.JsRuntime;
        Assert.True(js.Ok, js.Note);
        Assert.Equal(("deno", "Downloaded deno v2.9.9 from deno.test (SHA-256 verified)."), (js.Name, js.Note)); // downloaded instead
    }

    [Fact]
    public async Task WhenTheDownloadFailsTooTheNoteSaysWhyTheInstalledRuntimeWasSkipped()
    {
        if (OperatingSystem.IsWindows()) return;
        var path = Script("node", "#!/bin/sh\necho v18.20.4\n");
        var (tools, paths) = Create(new Fake(_ => Status(HttpStatusCode.Forbidden)), t => t.JsRuntimePath = path);

        await tools.InitializeAsync(CancellationToken.None);

        var note = tools.Status.JsRuntime.Note!;
        Assert.False(tools.Status.JsRuntime.Ok);
        Assert.StartsWith($"node 18.20.4 at {path} is too old (yt-dlp needs node 22 or later). The automatic deno download from deno.test failed", note);
        Assert.Contains(Path.Combine(paths.Tools, "deno"), note); // where to upload it by hand
    }

    /// <summary>
    /// A fresh deploy on 2026-11-09: github.com's release download route answers 504 for yt-dlp as well, so the
    /// bootstrap goes through the REST API's latest release.
    /// </summary>
    [Fact]
    public async Task YtDlpIsBootstrappedThroughTheGitHubApiWhenTheDownloadRouteTimesOut()
    {
        if (OperatingSystem.IsWindows()) return;
        const string api = "https://api.github.com/repos/acme/ytdl/releases";
        var asset = ToolLocator.ReleaseAssetName()!;
        var bin = Encoding.UTF8.GetBytes("#!/bin/sh\necho 2026.11.01\n");
        var server = new Fake(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            var raw = req.Headers.Accept.ToString() == "application/octet-stream";
            if (url.StartsWith("https://github.com/")) return Status(HttpStatusCode.GatewayTimeout);
            if (url == api + "/latest")
                return Ok($$"""{"tag_name":"2026.11.01","assets":[{"name":"{{asset}}","url":"{{api}}/assets/1"},{"name":"SHA2-256SUMS","url":"{{api}}/assets/2"}]}""");
            if (url == api + "/assets/1" && raw) return Ok(bin);
            if (url == api + "/assets/2" && raw) return Ok($"{new string('e', 64)}  yt-dlp_other\n{Sha(bin)}  {asset}\n");
            return Status(HttpStatusCode.NotFound);
        });
        var (tools, paths) = Create(server, t =>
        {
            t.YtDlpPath = null;
            t.AutoDownloadYtDlp = true;
            t.YtDlpReleaseBase = "https://github.com/acme/ytdl/releases/latest/download/";
            t.JsRuntime = "none";
        });

        await tools.InitializeAsync(CancellationToken.None);

        Assert.True(tools.Status.YtDlp.Ok, tools.Status.YtDlp.Note);
        Assert.Equal(("2026.11.01", Path.Combine(paths.Tools, "yt-dlp")), (tools.Status.YtDlp.Version, tools.Status.YtDlp.Path));
        Assert.True(tools.OwnsYtDlp);
        Assert.DoesNotContain(server.Requests, r => r.Contains("https://github.com/"));
    }

    [Fact]
    public async Task NothingIsDownloadedWhenTheConfigurationWouldNotUseDeno()
    {
        var server = new Fake(DenoServer());
        var (node, _) = Create(server, t => t.JsRuntime = "node");
        await node.InitializeAsync(CancellationToken.None);
        Assert.False(node.Status.JsRuntime.Ok);
        Assert.Contains("Install node", node.Status.JsRuntime.Note);
        Assert.Empty(node.MissingDownloads());

        var (off, _) = Create(server, t => t.AutoDownloadJsRuntime = false);
        await off.InitializeAsync(CancellationToken.None);
        Assert.Contains("enable Ydl:Tools:AutoDownloadJsRuntime", off.Status.JsRuntime.Note);
        Assert.Empty(off.MissingDownloads());

        var (none, _) = Create(server, t => t.JsRuntime = "none");
        await none.InitializeAsync(CancellationToken.None);
        Assert.Contains("Disabled by configuration", none.Status.JsRuntime.Note);

        Assert.Empty(server.Requests);
    }
}
