using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Ark.Ydl.Options;
using Ark.Ydl.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ark.Ydl.Tests;

public class FfmpegArchiveTests
{
    private static readonly ToolOptions O = new();

    [Theory]
    [InlineData("OSX", Architecture.Arm64, "macos/arm64")]
    [InlineData("OSX", Architecture.X64, "macos/amd64")]
    [InlineData("Linux", Architecture.X64, "linux/amd64")]
    [InlineData("Linux", Architecture.Arm64, "linux/arm64")]
    public void MacAndLinuxGetOneZipPerToolWithASha256BesideIt(string os, Architecture arch, string folder)
    {
        var s = Assert.Single(ToolLocator.FfmpegSources(O, OSPlatform.Create(os.ToUpperInvariant()), arch));
        Assert.Equal("ffmpeg.martin-riedl.de", s.Label);
        var a = s.Archives;
        Assert.Equal(2, a.Count);
        Assert.Equal($"https://ffmpeg.martin-riedl.de/redirect/latest/{folder}/release/ffmpeg.zip", a[0].Url);
        Assert.Equal($"https://ffmpeg.martin-riedl.de/redirect/latest/{folder}/release/ffprobe.zip", a[1].Url);
        Assert.All(a, x => Assert.Null(x.SumsUrl));
        Assert.Equal(["ffmpeg.zip", "ffprobe.zip"], a.Select(x => x.Name));
    }

    [Theory]
    [InlineData(Architecture.X64, "win64")]
    [InlineData(Architecture.Arm64, "winarm64")]
    [InlineData(Architecture.X86, "win32")]
    public void WindowsGetsYtDlpsBuildListedInChecksumsFirst(Architecture arch, string flavor)
    {
        var s = ToolLocator.FfmpegSources(O, OSPlatform.Windows, arch)[0];
        Assert.Equal("github.com/yt-dlp/FFmpeg-Builds", s.Label);
        var a = Assert.Single(s.Archives);
        const string b = "https://github.com/yt-dlp/FFmpeg-Builds/releases/download/latest/";
        Assert.Equal($"{b}ffmpeg-master-latest-{flavor}-gpl.zip", a.Url);
        Assert.Equal(b + "checksums.sha256", a.SumsUrl);
        Assert.Equal($"ffmpeg-master-latest-{flavor}-gpl.zip", a.Name);
    }

    [Fact]
    public void WindowsX64FallsBackToGyanDevWithTheSha256BesideIt()
    {
        var all = ToolLocator.FfmpegSources(O, OSPlatform.Windows, Architecture.X64);
        Assert.Equal(["github.com/yt-dlp/FFmpeg-Builds", "www.gyan.dev"], all.Select(s => s.Label));
        var a = Assert.Single(all[1].Archives);
        Assert.Equal("https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip", a.Url);
        Assert.Null(a.SumsUrl);

        // gyan.dev builds only x64; either source can be switched off.
        Assert.Single(ToolLocator.FfmpegSources(O, OSPlatform.Windows, Architecture.Arm64));
        Assert.Equal(["www.gyan.dev"], ToolLocator.FfmpegSources(new ToolOptions { FfmpegWindowsReleaseBase = "" }, OSPlatform.Windows, Architecture.X64).Select(s => s.Label));
        Assert.Equal(["github.com/yt-dlp/FFmpeg-Builds"], ToolLocator.FfmpegSources(new ToolOptions { FfmpegWindowsFallbackUrl = null }, OSPlatform.Windows, Architecture.X64).Select(s => s.Label));
    }

    [Fact]
    public void UnsupportedPlatformsGetNothing()
    {
        Assert.Empty(ToolLocator.FfmpegSources(O, OSPlatform.Linux, Architecture.Arm));
        Assert.Empty(ToolLocator.FfmpegSources(O, OSPlatform.FreeBSD, Architecture.X64));
    }

    [Fact]
    public void ConfiguredMirrorIsUsed()
    {
        var s = Assert.Single(ToolLocator.FfmpegSources(new ToolOptions { FfmpegReleaseBase = "https://mirror.test/ff" }, OSPlatform.Linux, Architecture.X64));
        Assert.Equal("mirror.test", s.Label);
        Assert.Equal("https://mirror.test/ff/linux/amd64/release/ffmpeg.zip", s.Archives[0].Url);
    }

    [Fact]
    public void GitHubReleaseFilesAreRecognised()
    {
        var f = ToolLocator.GitHubReleaseFile("https://github.com/yt-dlp/FFmpeg-Builds/releases/download/latest/checksums.sha256")!;
        Assert.Equal(("yt-dlp", "FFmpeg-Builds", "latest", "checksums.sha256"), (f.Owner, f.Repo, f.Tag, f.Name));
        Assert.Equal("https://api.github.com/repos/yt-dlp/FFmpeg-Builds/releases/tags/latest", f.ReleaseApiUrl);

        // ".../releases/latest/download/" is GitHub's alias for the newest release, not a tag named "latest".
        var y = ToolLocator.GitHubReleaseFile("https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe")!;
        Assert.Equal(("yt-dlp", "yt-dlp", null, "yt-dlp.exe"), (y.Owner, y.Repo, y.Tag, y.Name));
        Assert.Equal("https://api.github.com/repos/yt-dlp/yt-dlp/releases/latest", y.ReleaseApiUrl);
        Assert.Null(ToolLocator.GitHubReleaseFile("https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip"));
        Assert.Null(ToolLocator.GitHubReleaseFile(null));
    }

    [Fact]
    public void ABareHashCountsOnlyForTheFileItSitsBeside()
    {
        var h = new string('A', 64);
        Assert.Equal(new string('a', 64), ToolLocator.ExpectedHash(h, "x.zip", bareHash: true));
        Assert.Null(ToolLocator.ExpectedHash(h, "x.zip"));
        Assert.Null(ToolLocator.ExpectedHash("not-a-hash", "x.zip", bareHash: true));
    }

    [Theory]
    [InlineData("ffmpeg version 9.0.2-https://www.martin-riedl.de Copyright (c) 2000-2026 the FFmpeg developers", "9.0.2")]
    [InlineData("ffmpeg version N-128220-gbea9d176f7-https://www.martin-riedl.de Copyright (c) 2000-2026", "N-128220-gbea9d176f7")]
    [InlineData("ffmpeg version N-121583-g4348bde2d2-20251108 Copyright (c) 2000-2025 the FFmpeg developers", "N-121583-g4348bde2d2-20251108")]
    [InlineData("ffmpeg version 6.1.1-3ubuntu5 Copyright (c) 2000-2023 the FFmpeg developers", "6.1.1-3ubuntu5")]
    [InlineData("ffmpeg version 8.0.1 Copyright (c) 2000-2025 the FFmpeg developers", "8.0.1")]
    [InlineData("ffmpeg version 9.0.2", "9.0.2")]
    [InlineData("something else", "something else")]
    public void FfmpegVersionDropsTheBuilderTag(string line, string version) => Assert.Equal(version, ToolLocator.FfmpegVersion(line));

    [Fact]
    public void PerFileSha256IsParsed() =>
        Assert.Equal(new string('c', 64), ToolLocator.ExpectedHash(new string('c', 64) + "  ffmpeg.zip\n", "ffmpeg.zip"));

    [Fact]
    public void ExtractsOnlyTheNamedToolsFlatAndNeverOutsideTheFolder()
    {
        var root = Directory.CreateTempSubdirectory("ark-ydl-zip-").FullName;
        try
        {
            var zip = Path.Combine(root, "a.zip");
            File.WriteAllBytes(zip, FfmpegInstallTests.Zip(
                ("ffmpeg-master-latest-win64-gpl/bin/ffmpeg.exe", "ff"),
                ("ffmpeg-master-latest-win64-gpl/bin/ffplay.exe", "play"),
                ("ffmpeg-master-latest-win64-gpl/doc/ffprobe.exe/", ""),
                ("../../evil/ffprobe.exe", "probe")));
            var dir = Directory.CreateDirectory(Path.Combine(root, "out")).FullName;

            Assert.Equal(2, ToolLocator.ExtractNamed(zip, dir, ["ffmpeg.exe", "ffprobe.exe"]));
            Assert.Equal(["ffmpeg.exe", "ffprobe.exe"], Directory.GetFiles(dir).Select(Path.GetFileName).Order());
            Assert.Equal("ff", File.ReadAllText(Path.Combine(dir, "ffmpeg.exe")));
            Assert.Equal("probe", File.ReadAllText(Path.Combine(dir, "ffprobe.exe")));
            Assert.False(Directory.Exists(Path.Combine(root, "..", "evil")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}

/// <summary>
/// The whole ffmpeg bootstrap (download, follow the "latest" redirect, verify, extract and install)
/// against an in-memory server. The server hands out a shell-script "ffmpeg", so this test runs only on
/// macOS and Linux.
/// </summary>
public sealed class FfmpegInstallTests : IDisposable
{
    private const string Mirror = "https://mirror.test/redirect/latest/";
    private const string Script = "#!/bin/sh\necho 'ffmpeg version 9.9-test-https://www.martin-riedl.de Copyright (c) the FFmpeg developers'\n";
    private readonly string _root = Directory.CreateTempSubdirectory("ark-ydl-ff-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    internal static byte[] Zip(params (string Name, string Content)[] entries)
    {
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var e = z.CreateEntry(name);
                if (name.EndsWith('/')) continue;
                using var w = new StreamWriter(e.Open());
                w.Write(content);
            }
        }
        return ms.ToArray();
    }

    private static string Sha(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));

    /// <summary>
    /// Answers like ffmpeg.martin-riedl.de: ".../redirect/latest/{os}/{cpu}/release/{tool}.zip" ends up at a
    /// versioned URL, and the .sha256 exists only next to that versioned URL.
    /// </summary>
    private sealed class FakeMirror(bool corrupt) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var url = req.RequestUri!.AbsoluteUri;
            Requests.Add(url);
            foreach (var tool in new[] { "ffmpeg", "ffprobe" })
            {
                var zip = Zip((tool, Script));
                var versioned = $"https://mirror.test/download/x/1789931890_9.9/{tool}.zip";
                if (url.StartsWith(Mirror) && url.EndsWith($"/release/{tool}.zip"))
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(zip),
                        RequestMessage = new HttpRequestMessage(HttpMethod.Get, versioned), // as after a redirect
                    });
                if (url == versioned + ".sha256")
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent($"{(corrupt ? new string('0', 64) : Sha(zip))}  {tool}.zip\n", Encoding.ASCII),
                    });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = req });
        }
    }

    /// <summary>A server made of one function; it records each request as "{Accept} {URL}".</summary>
    internal sealed class Fake(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            Requests.Add($"{req.Headers.Accept} {req.RequestUri!.AbsoluteUri}".Trim());
            var resp = answer(req);
            resp.RequestMessage ??= req;
            return Task.FromResult(resp);
        }
    }

    private static HttpResponseMessage Answer(HttpStatusCode code, byte[]? body = null, string? redirectedTo = null) => new(code)
    {
        Content = new ByteArrayContent(body ?? []),
        RequestMessage = redirectedTo is null ? null : new HttpRequestMessage(HttpMethod.Get, redirectedTo),
    };

    private static HttpResponseMessage Answer(string text) => Answer(HttpStatusCode.OK, Encoding.UTF8.GetBytes(text));

    internal sealed class Factory(HttpMessageHandler h) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(h, disposeHandler: false);
    }

    private (ToolLocator Tools, YdlPaths Paths) Create(HttpMessageHandler server)
    {
        var o = new YdlOptions
        {
            DataRoot = _root,
            Tools =
            {
                FfmpegReleaseBase = Mirror,
                YtDlpPath = Path.Combine(_root, "no-such-yt-dlp"),
                AutoDownloadYtDlp = false,
                AutoUpdateYtDlp = false,
                JsRuntime = "none",
            },
        };
        var paths = new YdlPaths(o, _root);
        var tools = new ToolLocator(Microsoft.Extensions.Options.Options.Create(o), paths, new Factory(server), NullLogger<ToolLocator>.Instance)
        {
            RetryDelay = TimeSpan.Zero,
        };
        return (tools, paths);
    }

    // Windows sources as configured by default, except on test hosts. The archives hold "ffmpeg" and
    // "ffprobe" without .exe because the test runs them on macOS / Linux.
    private const string Gh = "https://github.com/acme/ff-builds/releases/download/latest/";
    private const string GhApi = "https://api.github.com/repos/acme/ff-builds/releases";
    private const string GhZip = "ffmpeg-master-latest-win64-gpl.zip";
    private const string Gyan = "https://dl.test/builds/ffmpeg-release-essentials.zip";
    private const string GyanVersioned = "https://dl.test/builds/packages/ffmpeg-9.9-essentials_build.zip";

    private static IReadOnlyList<ToolLocator.ToolSource> WindowsSources() =>
        ToolLocator.FfmpegSources(new ToolOptions { FfmpegWindowsReleaseBase = Gh, FfmpegWindowsFallbackUrl = Gyan }, OSPlatform.Windows, Architecture.X64);

    private static byte[] BuildZip(string folder) =>
        Zip(($"{folder}/bin/ffmpeg", Script), ($"{folder}/bin/ffprobe", Script), ($"{folder}/bin/ffplay", "x"), ($"{folder}/LICENSE.txt", "GPL"));

    /// <summary>
    /// Answers like GitHub on 2026-11-09: github.com's /releases/download/latest/… times out, while the REST API
    /// lists the release and serves each file as raw bytes (anything else gets the asset's JSON).
    /// </summary>
    private static Func<HttpRequestMessage, HttpResponseMessage> GitHub(byte[] zip, int gatewayTimeoutsFirst = 0, bool rateLimited = false)
    {
        var timeouts = gatewayTimeoutsFirst;
        return req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            var raw = req.Headers.Accept.ToString() == "application/octet-stream";
            if (url.StartsWith(Gh)) return Answer(HttpStatusCode.GatewayTimeout);
            if (url == GhApi + "/tags/latest")
                return rateLimited
                    ? Answer(HttpStatusCode.Forbidden)
                    : Answer($$"""{"tag_name":"latest","assets":[{"name":"{{GhZip}}","url":"{{GhApi}}/assets/1"},{"name":"checksums.sha256","url":"{{GhApi}}/assets/2"}]}""");
            if (url == GhApi + "/assets/1")
            {
                if (timeouts-- > 0) return Answer(HttpStatusCode.GatewayTimeout);
                return raw ? Answer(HttpStatusCode.OK, zip, "https://cdn.test/asset-1") : Answer("""{"id":1}""");
            }
            if (url == GhApi + "/assets/2" && raw)
                return Answer($"{Sha(zip)}  {GhZip}\n{new string('e', 64)}  ffmpeg-master-latest-win32-gpl.zip\n");
            return Answer(HttpStatusCode.NotFound);
        };
    }

    [Fact]
    public async Task GitHubReleaseFilesComeThroughTheApiAndA504IsRetried()
    {
        if (OperatingSystem.IsWindows()) return;
        var server = new Fake(GitHub(BuildZip("ffmpeg-master-latest-win64-gpl"), gatewayTimeoutsFirst: 2));
        var (tools, paths) = Create(server);

        var info = await tools.InstallFfmpegAsync(CancellationToken.None, WindowsSources());

        Assert.True(info.Ok, info.Note);
        Assert.Equal("9.9-test", info.Version);
        Assert.Contains("github.com/acme/ff-builds", info.Note);
        Assert.Equal(["ffmpeg", "ffprobe"], Directory.GetFileSystemEntries(Path.Combine(paths.Tools, "ffmpeg")).Select(Path.GetFileName).Order());
        Assert.DoesNotContain(server.Requests, r => r.Contains(Gh));          // the route that times out is never used
        Assert.DoesNotContain(server.Requests, r => r.Contains("dl.test"));  // nor the fallback
        Assert.Equal(3, server.Requests.Count(r => r == $"application/octet-stream {GhApi}/assets/1"));
        Assert.Contains($"application/vnd.github+json {GhApi}/tags/latest", server.Requests);
    }

    [Fact]
    public async Task WhenGitHubFailsTheFallbackIsUsed()
    {
        if (OperatingSystem.IsWindows()) return;
        var gyanZip = BuildZip("ffmpeg-9.9-essentials_build");
        var github = GitHub(BuildZip("x"), rateLimited: true);
        var server = new Fake(req => req.RequestUri!.AbsoluteUri switch
        {
            Gyan => Answer(HttpStatusCode.OK, gyanZip, GyanVersioned),
            GyanVersioned + ".sha256" => Answer(Sha(gyanZip)), // a bare hash, no newline, like gyan.dev
            _ => github(req),
        });
        var (tools, _) = Create(server);

        var info = await tools.InstallFfmpegAsync(CancellationToken.None, WindowsSources());

        Assert.True(info.Ok, info.Note);
        Assert.Contains("dl.test", info.Note);
        Assert.Single(server.Requests, r => r.EndsWith(GhApi + "/tags/latest")); // a 403 is not worth retrying
    }

    [Fact]
    public async Task WhenEverySourceFailsTheNoteNamesEachAndNothingIsInstalled()
    {
        if (OperatingSystem.IsWindows()) return;
        var server = new Fake(req => req.RequestUri!.Host == "dl.test" ? Answer(HttpStatusCode.BadGateway) : GitHub(BuildZip("x"), gatewayTimeoutsFirst: 99)(req));
        var (tools, paths) = Create(server);

        var info = await tools.InstallFfmpegAsync(CancellationToken.None, WindowsSources());

        Assert.False(info.Ok);
        Assert.Contains("github.com/acme/ff-builds: api.github.com answered 504 (Gateway Timeout), 3 times.", info.Note);
        Assert.Contains("dl.test: dl.test answered 502 (Bad Gateway), 3 times.", info.Note);
        Assert.Contains("retrying in the background", info.Note);
        Assert.False(Directory.Exists(Path.Combine(paths.Tools, "ffmpeg")));
        Assert.False(Directory.Exists(Path.Combine(paths.Tools, "ffmpeg.download")));
    }

    [Fact]
    public async Task ATamperedFallbackIsRejected()
    {
        if (OperatingSystem.IsWindows()) return;
        var server = new Fake(req => req.RequestUri!.AbsoluteUri switch
        {
            Gyan => Answer(HttpStatusCode.OK, BuildZip("evil"), GyanVersioned),
            GyanVersioned + ".sha256" => Answer(new string('0', 64)),
            _ => Answer(HttpStatusCode.NotFound),
        });
        var (tools, paths) = Create(server);

        var info = await tools.InstallFfmpegAsync(CancellationToken.None, WindowsSources());

        Assert.False(info.Ok);
        Assert.Contains("Checksum mismatch for ffmpeg-release-essentials.zip", info.Note);
        Assert.False(Directory.Exists(Path.Combine(paths.Tools, "ffmpeg")));
    }

    [Fact]
    public async Task DownloadsVerifiesAndInstallsIntoTheToolsFolder()
    {
        if (OperatingSystem.IsWindows()) return;
        var mirror = new FakeMirror(corrupt: false);
        var (tools, paths) = Create(mirror);

        var info = await tools.InstallFfmpegAsync(CancellationToken.None);

        var dir = Path.Combine(paths.Tools, "ffmpeg");
        Assert.True(info.Ok, info.Note);
        Assert.Equal(Path.Combine(dir, "ffmpeg"), info.Path);
        Assert.Equal("9.9-test", info.Version);
        Assert.Contains("mirror.test", info.Note);
        Assert.True(File.Exists(Path.Combine(dir, "ffprobe")));
        Assert.Equal(["ffmpeg", "ffprobe"], Directory.GetFileSystemEntries(dir).Select(Path.GetFileName).Order());
        Assert.False(Directory.Exists(dir + ".download"));
        Assert.Contains("https://mirror.test/download/x/1789931890_9.9/ffprobe.zip.sha256", mirror.Requests);

        // Next start: the app's own copy is found (it comes before PATH) and nothing is downloaded.
        mirror.Requests.Clear();
        await tools.InitializeAsync(CancellationToken.None);
        Assert.Equal(Path.Combine(dir, "ffmpeg"), tools.Status.Ffmpeg.Path);
        Assert.Empty(mirror.Requests);
        Assert.Equal(["--ffmpeg-location", Path.Combine(dir, "ffmpeg")], tools.ToolArgs());
    }

    [Fact]
    public async Task AChecksumMismatchInstallsNothing()
    {
        if (OperatingSystem.IsWindows()) return;
        var (tools, paths) = Create(new FakeMirror(corrupt: true));

        var info = await tools.InstallFfmpegAsync(CancellationToken.None);

        Assert.False(info.Ok);
        Assert.Contains("Checksum mismatch for ffmpeg.zip", info.Note);
        Assert.False(Directory.Exists(Path.Combine(paths.Tools, "ffmpeg")));
        Assert.False(Directory.Exists(Path.Combine(paths.Tools, "ffmpeg.download")));
    }
}
