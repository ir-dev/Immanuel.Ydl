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
        var a = ToolLocator.FfmpegArchives(O, OSPlatform.Create(os.ToUpperInvariant()), arch)!;
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
    public void WindowsGetsYtDlpsBuildListedInChecksums(Architecture arch, string flavor)
    {
        var a = Assert.Single(ToolLocator.FfmpegArchives(O, OSPlatform.Windows, arch)!);
        const string b = "https://github.com/yt-dlp/FFmpeg-Builds/releases/download/latest/";
        Assert.Equal($"{b}ffmpeg-master-latest-{flavor}-gpl.zip", a.Url);
        Assert.Equal(b + "checksums.sha256", a.SumsUrl);
        Assert.Equal($"ffmpeg-master-latest-{flavor}-gpl.zip", a.Name);
    }

    [Fact]
    public void UnsupportedPlatformsGetNothing()
    {
        Assert.Null(ToolLocator.FfmpegArchives(O, OSPlatform.Linux, Architecture.Arm));
        Assert.Null(ToolLocator.FfmpegArchives(O, OSPlatform.FreeBSD, Architecture.X64));
    }

    [Fact]
    public void ConfiguredMirrorIsUsed()
    {
        var a = ToolLocator.FfmpegArchives(new ToolOptions { FfmpegReleaseBase = "https://mirror.test/ff" }, OSPlatform.Linux, Architecture.X64)!;
        Assert.Equal("https://mirror.test/ff/linux/amd64/release/ffmpeg.zip", a[0].Url);
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

    private sealed class Factory(HttpMessageHandler h) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(h, disposeHandler: false);
    }

    private (ToolLocator Tools, YdlPaths Paths) Create(FakeMirror mirror)
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
        return (new ToolLocator(Microsoft.Extensions.Options.Options.Create(o), paths, new Factory(mirror), NullLogger<ToolLocator>.Instance), paths);
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
