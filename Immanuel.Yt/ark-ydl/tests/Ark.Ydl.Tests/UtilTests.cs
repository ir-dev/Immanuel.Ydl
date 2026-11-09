using Ark.Ydl.Options;
using Ark.Ydl.Tools;
using Ark.Ydl.Util;

namespace Ark.Ydl.Tests;

public class TimeCodeTests
{
    [Theory]
    [InlineData("90", 90)]
    [InlineData("90.5", 90.5)]
    [InlineData("1:30", 90)]
    [InlineData("01:02:03", 3723)]
    [InlineData("1:02:03.25", 3723.25)]
    [InlineData("1h2m3s", 3723)]
    [InlineData("1m30s", 90)]
    [InlineData("2m", 120)]
    [InlineData("45s", 45)]
    [InlineData("1h", 3600)]
    [InlineData(" 0:07,5 ", 7.5)]
    public void Parses(string text, double expected)
    {
        Assert.True(TimeCode.TryParse(text, out var s));
        Assert.Equal(expected, s, 3);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1:75")]
    [InlineData("1:2:3:4")]
    [InlineData("-5")]
    [InlineData("h")]
    public void Rejects(string text) => Assert.False(TimeCode.TryParse(text, out _));

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(7.5, "0:07.5")]
    [InlineData(90, "1:30")]
    [InlineData(3723.25, "1:02:03.25")]
    public void Formats(double seconds, string expected) => Assert.Equal(expected, TimeCode.Format(seconds));

    [Fact]
    public void ArgIsInvariant() => Assert.Equal("75.5", TimeCode.ToArg(75.5));
}

public class UrlGuardTests
{
    private static readonly string[] Sites = YdlOptions.DefaultSites;

    private static UrlAnalysis A(string input, bool? playlist = null, bool any = false) => UrlGuard.Analyze(input, Sites, any, playlist);

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=jNQXAC9IVRw")]
    [InlineData("https://youtu.be/jNQXAC9IVRw?si=tracking")]
    [InlineData("youtube.com/watch?v=jNQXAC9IVRw&feature=share")]
    [InlineData("https://m.youtube.com/watch?v=jNQXAC9IVRw")]
    [InlineData("https://www.youtube.com/shorts/jNQXAC9IVRw")]
    [InlineData("https://www.youtube.com/live/jNQXAC9IVRw")]
    [InlineData("https://www.youtube-nocookie.com/embed/jNQXAC9IVRw")]
    [InlineData("jNQXAC9IVRw")]
    [InlineData("Watch this!! https://youtu.be/jNQXAC9IVRw — so good")]
    public void NormalisesVideos(string input)
    {
        var a = A(input);
        Assert.True(a.Ok, a.Error);
        Assert.Equal(UrlKind.Video, a.Kind);
        Assert.Equal("jNQXAC9IVRw", a.VideoId);
        Assert.Equal("https://www.youtube.com/watch?v=jNQXAC9IVRw", a.Url);
    }

    [Fact]
    public void VideoInPlaylistDefaultsToTheVideoButRemembersTheList()
    {
        var a = A("https://www.youtube.com/watch?v=jNQXAC9IVRw&list=PLabcdefghijklmnop12");
        Assert.Equal(UrlKind.Video, a.Kind);
        Assert.Equal("PLabcdefghijklmnop12", a.ListId);

        var p = A("https://www.youtube.com/watch?v=jNQXAC9IVRw&list=PLabcdefghijklmnop12", playlist: true);
        Assert.Equal(UrlKind.Playlist, p.Kind);
        Assert.Equal("https://www.youtube.com/playlist?list=PLabcdefghijklmnop12", p.Url);
    }

    [Fact]
    public void MixesKeepTheirSeedVideo()
    {
        var p = A("https://www.youtube.com/watch?v=jNQXAC9IVRw&list=RDjNQXAC9IVRw", playlist: true);
        Assert.Equal(UrlKind.Playlist, p.Kind);
        Assert.Equal("https://www.youtube.com/watch?v=jNQXAC9IVRw&list=RDjNQXAC9IVRw", p.Url);
    }

    [Fact]
    public void PlaylistLinks()
    {
        var p = A("https://www.youtube.com/playlist?list=PLabcdefghijklmnop12");
        Assert.Equal(UrlKind.Playlist, p.Kind);
        Assert.Equal("https://www.youtube.com/playlist?list=PLabcdefghijklmnop12", p.Url);
        Assert.Equal(UrlKind.Playlist, A("PLabcdefghijklmnop12").Kind);
    }

    [Theory]
    [InlineData("https://www.youtube.com/@SomeChannel", "https://www.youtube.com/@SomeChannel/videos")]
    [InlineData("https://www.youtube.com/@SomeChannel/shorts", "https://www.youtube.com/@SomeChannel/shorts")]
    [InlineData("https://www.youtube.com/channel/UC1234567890", "https://www.youtube.com/channel/UC1234567890/videos")]
    public void Channels(string input, string expected)
    {
        var a = A(input);
        Assert.Equal(UrlKind.Channel, a.Kind);
        Assert.Equal(expected, a.Url);
    }

    [Theory]
    [InlineData("https://youtu.be/jNQXAC9IVRw?t=90", 90.0, null)]
    [InlineData("https://www.youtube.com/watch?v=jNQXAC9IVRw&t=1m30s", 90.0, null)]
    [InlineData("https://www.youtube.com/watch?v=jNQXAC9IVRw&start=10&end=25", 10.0, 25.0)]
    [InlineData("https://www.youtube.com/watch?v=jNQXAC9IVRw#t=1:05", 65.0, null)]
    public void TimeHints(string input, double start, double? end)
    {
        var a = A(input);
        Assert.Equal(start, a.Start);
        Assert.Equal(end, a.End);
    }

    [Fact]
    public void MusicKeepsItsHost()
    {
        var a = A("https://music.youtube.com/watch?v=jNQXAC9IVRw&feature=share");
        Assert.True(a.IsMusic);
        Assert.Equal("https://music.youtube.com/watch?v=jNQXAC9IVRw", a.Url);
    }

    [Theory]
    [InlineData("")]
    [InlineData("hello world")]
    [InlineData("ftp://youtube.com/watch?v=jNQXAC9IVRw")]
    [InlineData("https://vimeo.com/123456")]
    [InlineData("https://evil-youtube.com/watch?v=jNQXAC9IVRw")]
    [InlineData("https://www.youtube.com/feed/trending")]
    public void Rejects(string input) => Assert.False(A(input).Ok);

    [Theory]
    [InlineData("http://127.0.0.1/video")]
    [InlineData("http://localhost:8080/")]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    [InlineData("http://10.1.2.3/a.mp4")]
    [InlineData("http://192.168.1.10/a.mp4")]
    [InlineData("http://[::1]/a.mp4")]
    [InlineData("http://metadata.google.internal/")]
    public void AnySiteStillRefusesInternalAddresses(string input) => Assert.False(A(input, any: true).Ok);

    [Fact]
    public void AnySiteAllowsPublicHosts()
    {
        var a = A("https://vimeo.com/123456", any: true);
        Assert.True(a.Ok);
        Assert.Equal(UrlKind.Other, a.Kind);
    }
}

public class ToolLocatorTests
{
    [Fact]
    public void ParsesGnuChecksumFiles()
    {
        const string sums = """
            1111111111111111111111111111111111111111111111111111111111111111  yt-dlp
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA  yt-dlp_linux
            bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb *yt-dlp_macos
            """;
        Assert.Equal(new string('a', 64), ToolLocator.ExpectedHash(sums, "yt-dlp_linux"));
        Assert.Equal(new string('b', 64), ToolLocator.ExpectedHash(sums, "yt-dlp_macos"));
        Assert.Null(ToolLocator.ExpectedHash(sums, "yt-dlp.exe"));
    }

    [Fact]
    public void KnowsTheReleaseAssetForThisMachine() => Assert.NotNull(ToolLocator.ReleaseAssetName());
}
