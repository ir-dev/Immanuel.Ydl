using System.Text.Json;
using Ark.Ydl.Jobs;
using Ark.Ydl.Options;
using Ark.Ydl.Util;
using Ark.Ydl.YtDlp;

namespace Ark.Ydl.Tests;

public class YtDlpArgsTests
{
    private static readonly YdlOptions Opt = new() { MaxPlaylistItems = 50 };
    private static readonly UrlAnalysis Video = UrlGuard.Analyze("https://youtu.be/jNQXAC9IVRw", YdlOptions.DefaultSites, false);
    private static readonly UrlAnalysis Playlist = UrlGuard.Analyze("https://www.youtube.com/playlist?list=PLabcdefghijklmnop12", YdlOptions.DefaultSites, false);

    private static List<string> Build(DownloadRequest r, UrlAnalysis? url = null, YdlOptions? o = null) =>
        YtDlpArgs.Download(r, url ?? Video, YtDlpArgs.Common(o ?? Opt, "/cache", ["--ffmpeg-location", "/ff"], null), "/jobs/x", o ?? Opt);

    private static string? After(List<string> a, string flag) => a.IndexOf(flag) is var i and >= 0 && i + 1 < a.Count ? a[i + 1] : null;

    private static List<string> AllAfter(List<string> a, string flag) =>
        a.Select((v, i) => (v, i)).Where(x => x.v == flag && x.i + 1 < a.Count).Select(x => a[x.i + 1]).ToList();

    [Fact]
    public void DefaultVideoIsSmartMp4WithTheUrlLast()
    {
        var a = Build(new DownloadRequest { Url = Video.Url, Video = { MaxHeight = 1080 } });
        Assert.Equal("res:1080,fps,+hdr,vcodec:h264,acodec:m4a", After(a, "-S"));
        Assert.Equal("bv*+ba/b", After(a, "-f"));
        Assert.Equal("mp4", After(a, "--merge-output-format"));
        Assert.Contains("--no-playlist", a);
        Assert.Contains("--embed-metadata", a);
        Assert.Contains("--embed-thumbnail", a);
        Assert.Contains("--embed-chapters", a);
        Assert.Contains("--ignore-config", a);
        Assert.Equal("never", After(a, "--color"));
        Assert.Equal("/jobs/x", After(a, "-P"));
        Assert.Equal("!is_live", After(a, "--match-filter"));
        Assert.Equal(["--", Video.Url], a[^2..]);
        Assert.Contains(YtDlpArgs.ProgressTemplate, a);
    }

    [Theory]
    [InlineData(VideoCodec.Best, null, "res,fps,+hdr")]
    [InlineData(VideoCodec.H264, 2160, "vcodec:h264,res:2160,fps,acodec:m4a")]
    [InlineData(VideoCodec.Vp9, 1440, "vcodec:vp9,res:1440,fps,+hdr")]
    [InlineData(VideoCodec.Av1, null, "vcodec:av01,res,fps,+hdr")]
    public void CodecPreferences(VideoCodec codec, int? height, string sort)
    {
        var a = Build(new DownloadRequest { Url = Video.Url, Video = { Codec = codec, MaxHeight = height } });
        Assert.Equal(sort, After(a, "-S"));
    }

    [Fact]
    public void HdrAllowedDropsTheSdrPreference()
    {
        var a = Build(new DownloadRequest { Url = Video.Url, Video = { Codec = VideoCodec.Best, Hdr = true } });
        Assert.Equal("res,fps", After(a, "-S"));
    }

    [Fact]
    public void WebmNeverAsksForH264OrCoverArt()
    {
        var a = Build(new DownloadRequest { Url = Video.Url, Video = { Codec = VideoCodec.H264, Container = VideoContainer.Webm } });
        Assert.StartsWith("vcodec:vp9", After(a, "-S"));
        Assert.EndsWith("acodec:opus", After(a, "-S"));
        Assert.Equal("webm", After(a, "--merge-output-format"));
        Assert.Equal("webm>webm/mkv", After(a, "--remux-video"));
        Assert.DoesNotContain("--embed-thumbnail", a);
    }

    [Fact]
    public void Mp3AtAFixedBitrate()
    {
        var a = Build(new DownloadRequest { Url = Video.Url, Mode = DownloadMode.Audio, Audio = { Format = AudioFormat.Mp3, Bitrate = 320, SquareCover = true } });
        Assert.Contains("-x", a);
        Assert.Equal("mp3", After(a, "--audio-format"));
        Assert.Equal("320K", After(a, "--audio-quality"));
        Assert.Equal("ba/b", After(a, "-f"));
        Assert.Equal(YtDlpArgs.SquareCoverPpa, After(a, "--ppa"));
        Assert.Equal("title:%(artist)s - %(title)s", After(a, "--parse-metadata"));
        Assert.DoesNotContain("-S", a);
    }

    [Theory]
    [InlineData(AudioFormat.M4a, "m4a", "ba[ext=m4a]/ba/b", true)]
    [InlineData(AudioFormat.Opus, "opus", "ba[acodec^=opus]/ba/b", true)]
    [InlineData(AudioFormat.Flac, "flac", "ba/b", false)]
    [InlineData(AudioFormat.Ogg, "vorbis", "ba/b", true)]
    [InlineData(AudioFormat.Best, "best", "ba/b", false)]
    public void AudioFormats(AudioFormat f, string ytFormat, string selector, bool hasQuality)
    {
        var a = Build(new DownloadRequest { Url = Video.Url, Mode = DownloadMode.Audio, Audio = { Format = f } });
        Assert.Equal(ytFormat, After(a, "--audio-format"));
        Assert.Equal(selector, After(a, "-f"));
        Assert.Equal(hasQuality, a.Contains("--audio-quality"));
        if (hasQuality) Assert.Equal("0", After(a, "--audio-quality"));
    }

    [Fact]
    public void WavGetsNoCoverArt()
    {
        var a = Build(new DownloadRequest { Url = Video.Url, Mode = DownloadMode.Audio, Audio = { Format = AudioFormat.Wav } });
        Assert.DoesNotContain("--embed-thumbnail", a);
    }

    [Fact]
    public void ClipTimeRanges()
    {
        var r = new DownloadRequest
        {
            Url = Video.Url,
            Clip = new ClipOptions { Precise = true, Sections = [new() { Start = 10, End = 25.5 }, new() { Start = 60 }] },
            SponsorBlock = { Action = SponsorAction.Remove, Categories = ["sponsor"] },
        };
        var a = Build(r);
        Assert.Equal(["*10-25.5", "*60-inf"], AllAfter(a, "--download-sections"));
        Assert.Contains("--force-keyframes-at-cuts", a);
        Assert.Contains("[%(section_start>%H-%M-%S)s-%(section_end>%H-%M-%S)s]", After(a, "-o"));
        Assert.DoesNotContain("--sponsorblock-remove", a); // SponsorBlock only applies to whole videos
    }

    [Fact]
    public void ChapterSectionsAreEscapedRegexes()
    {
        var r = new DownloadRequest
        {
            Url = Video.Url,
            Clip = new ClipOptions { Precise = false, Sections = [new() { Start = 0, End = 60, Chapter = "Intro (part 1) [HD]?" }] },
        };
        var a = Build(r);
        Assert.Equal(@"^Intro \(part 1\) \[HD\]\?$", After(a, "--download-sections"));
        Assert.DoesNotContain("--force-keyframes-at-cuts", a);
        Assert.EndsWith(" - %(section_title).80B.%(ext)s", After(a, "-o"));
    }

    [Fact]
    public void SplitChaptersKeepsTheFullFileAside()
    {
        var a = Build(new DownloadRequest { Url = Video.Url, Clip = new ClipOptions { SplitChapters = true } });
        Assert.Contains("--split-chapters", a);
        var outputs = AllAfter(a, "-o");
        Assert.StartsWith(".full/", outputs[0]);
        Assert.StartsWith("chapter:", outputs[1]);
    }

    [Fact]
    public void PlaylistSelectionIsCompacted()
    {
        var r = new DownloadRequest { Url = Playlist.Url, Playlist = true, PlaylistItems = [1, 2, 3, 7, 9, 10, 11], Mode = DownloadMode.Audio };
        var a = Build(r, Playlist);
        Assert.Equal("1:3,7,9:11", After(a, "-I"));
        Assert.Contains("--yes-playlist", a);
        Assert.Contains("--ignore-errors", a);
        Assert.StartsWith("%(playlist_index)03d - ", After(a, "-o"));
        Assert.Contains("playlist_title:%(album)s", a);
    }

    [Fact]
    public void WholePlaylistIsCappedByTheServerLimit()
    {
        var a = Build(new DownloadRequest { Url = Playlist.Url, Playlist = true }, Playlist);
        Assert.Equal("1:50", After(a, "-I"));
    }

    [Fact]
    public void SubtitlesEmbedAndFile()
    {
        var r = new DownloadRequest { Url = Video.Url, Subtitles = { Mode = SubtitleMode.Both, Langs = ["en", "ta"], AutoLangs = ["en-orig"] } };
        var a = Build(r);
        Assert.Contains("--write-subs", a);
        Assert.Contains("--write-auto-subs", a);
        Assert.Contains("--embed-subs", a);
        Assert.Equal("en,ta,en-orig", After(a, "--sub-langs"));
        Assert.Equal("srt", After(a, "--convert-subs"));
    }

    [Fact]
    public void SubtitlesOnly()
    {
        var a = Build(new DownloadRequest { Url = Video.Url, Mode = DownloadMode.Subtitles, Subtitles = { Langs = ["en"] } });
        Assert.Contains("--skip-download", a);
        Assert.Equal("en", After(a, "--sub-langs"));
        Assert.DoesNotContain("--embed-metadata", a);
    }

    [Fact]
    public void ThumbnailOnly()
    {
        var a = Build(new DownloadRequest { Url = Video.Url, Mode = DownloadMode.Thumbnail });
        Assert.Contains("--skip-download", a);
        Assert.Contains("--write-thumbnail", a);
        Assert.Equal("jpg", After(a, "--convert-thumbnails"));
    }

    [Fact]
    public void SponsorBlockMark()
    {
        var a = Build(new DownloadRequest { Url = Video.Url, SponsorBlock = { Action = SponsorAction.Mark, Categories = ["sponsor", "intro", "sponsor"] } });
        Assert.Equal("sponsor,intro", After(a, "--sponsorblock-mark"));
    }

    [Fact]
    public void ServerLimitsBecomeArguments()
    {
        var o = new YdlOptions { MaxDurationMinutes = 90, MaxFileSize = "2G", CookiesFile = null, Proxy = "socks5://proxy:1080", ExtraArgs = ["--extractor-args", "youtube:player_client=default"] };
        var a = Build(new DownloadRequest { Url = Video.Url }, o: o);
        Assert.Equal("!is_live & duration <=? 5400", After(a, "--match-filter"));
        Assert.Equal("2G", After(a, "--max-filesize"));
        Assert.Equal("socks5://proxy:1080", After(a, "--proxy"));
        Assert.Equal("youtube:player_client=default", After(a, "--extractor-args"));
    }

    [Fact]
    public void ProbeArgs()
    {
        var common = YtDlpArgs.Common(Opt, "/cache", [], null);
        var v = YtDlpArgs.Probe(common, Video, false, 100);
        Assert.Contains("-J", v);
        Assert.Contains("--no-playlist", v);
        var p = YtDlpArgs.Probe(common, Playlist, true, 100);
        Assert.Contains("--flat-playlist", p);
        Assert.Equal("1:100", p[p.IndexOf("-I") + 1]);
        Assert.Equal(Playlist.Url, p[^1]);
    }
}

public class ValidatorTests
{
    private static readonly YdlOptions Opt = new() { MaxClipSections = 3, MaxPlaylistItems = 5 };

    [Fact]
    public void AcceptsADefaultRequest() => Assert.Null(DownloadRequestValidator.Validate(new DownloadRequest { Url = "x" }, Opt));

    [Fact]
    public void RejectsInjectionShapedLanguages()
    {
        var r = new DownloadRequest { Url = "x", Subtitles = { Mode = SubtitleMode.File, Langs = ["en,--exec,rm"] } };
        Assert.NotNull(DownloadRequestValidator.Validate(r, Opt));
    }

    [Fact]
    public void RejectsUnknownSponsorCategories()
    {
        var r = new DownloadRequest { Url = "x", SponsorBlock = { Action = SponsorAction.Remove, Categories = ["all,--exec"] } };
        Assert.NotNull(DownloadRequestValidator.Validate(r, Opt));
    }

    [Fact]
    public void ClipRules()
    {
        Assert.NotNull(DownloadRequestValidator.Validate(new DownloadRequest { Url = "x", Clip = new ClipOptions() }, Opt));
        Assert.NotNull(DownloadRequestValidator.Validate(new DownloadRequest { Url = "x", Clip = new ClipOptions { Sections = [new() { Start = 10, End = 5 }] } }, Opt));
        Assert.NotNull(DownloadRequestValidator.Validate(new DownloadRequest { Url = "x", Playlist = true, Clip = new ClipOptions { Sections = [new() { Start = 1, End = 5 }] } }, Opt));
        Assert.NotNull(DownloadRequestValidator.Validate(new DownloadRequest
        {
            Url = "x",
            Clip = new ClipOptions { Sections = [new() { Start = 1 }, new() { Start = 2 }, new() { Start = 3 }, new() { Start = 4 }] },
        }, Opt));
        Assert.Null(DownloadRequestValidator.Validate(new DownloadRequest { Url = "x", Clip = new ClipOptions { Sections = [new() { Start = 1, End = 5 }] } }, Opt));
    }

    [Fact]
    public void PlaylistLimit()
    {
        Assert.NotNull(DownloadRequestValidator.Validate(new DownloadRequest { Url = "x", Playlist = true, PlaylistItems = [1, 2, 3, 4, 5, 6] }, Opt));
        Assert.NotNull(DownloadRequestValidator.Validate(new DownloadRequest { Url = "x", Playlist = true, PlaylistItems = [0] }, Opt));
    }

    [Fact]
    public void Bitrates()
    {
        Assert.NotNull(DownloadRequestValidator.Validate(new DownloadRequest { Url = "x", Audio = { Bitrate = 333 } }, Opt));
        Assert.Null(DownloadRequestValidator.Validate(new DownloadRequest { Url = "x", Audio = { Bitrate = 320 } }, Opt));
    }

    [Fact]
    public void CompactItems() => Assert.Equal("1:3,5,8:9", DownloadRequestValidator.CompactItems([9, 1, 2, 3, 5, 8, 2]));
}

public class ProgressTrackerTests
{
    [Fact]
    public void VideoThenAudioThenMerge()
    {
        var t = new ProgressTracker(audioOnly: false);
        t.Feed("[youtube] jNQXAC9IVRw: Downloading webpage");
        Assert.Equal("Fetching video info", t.Stage);
        t.Feed("[info] jNQXAC9IVRw: Downloading 1 format(s): 137+140");
        Assert.Equal(2, t.Streams);
        t.Feed("[download] Destination: /jobs/x/Me at the zoo.f137.mp4");
        Assert.Equal("Downloading video", t.Stage);
        t.Feed("@@P|downloading|500|1000|NA|250000.5|2|NA|jNQXAC9IVRw");
        Assert.Equal(0.25, t.Overall!.Value, 3);
        Assert.Equal(250000.5, t.Speed);
        Assert.Equal(2, t.Eta);
        t.Feed("@@P|finished|1000|1000|NA|NA|NA|NA|jNQXAC9IVRw");
        t.Feed("[download] Destination: /jobs/x/Me at the zoo.f140.m4a");
        Assert.Equal("Downloading audio", t.Stage);
        t.Feed("@@P|downloading|NA|NA|400|NA|NA|NA|jNQXAC9IVRw");
        Assert.Equal(0.5, t.Overall!.Value, 3);
        t.Feed("@@P|downloading|200|NA|400|NA|NA|NA|jNQXAC9IVRw");
        Assert.Equal(0.75, t.Overall!.Value, 3);
        t.Feed("@@PP|started|Merger|jNQXAC9IVRw");
        Assert.True(t.PostProcessing);
        Assert.Equal("Merging video and audio", t.Stage);
        Assert.Equal(1, t.Overall);
    }

    [Fact]
    public void UnknownSizeIsIndeterminate()
    {
        var t = new ProgressTracker(audioOnly: false);
        t.Feed("[download] Destination: clip.mp4");
        t.Feed("@@P|downloading|123456|NA|NA|NA|NA|NA|id");
        Assert.Null(t.Overall);
        Assert.Equal(123456, t.Downloaded);
    }

    [Fact]
    public void PlaylistItemsDriveTheOverallBar()
    {
        var t = new ProgressTracker(audioOnly: true);
        t.Feed("[download] Downloading playlist: My Mix");
        Assert.Equal("My Mix", t.PlaylistTitle);
        t.Feed("[download] Downloading item 2 of 4");
        Assert.Equal(0.25, t.Overall!.Value, 3);
        t.Feed("[download] Destination: a.webm");
        t.Feed("@@P|downloading|50|100|NA|NA|NA|2|id");
        Assert.Equal("Downloading audio", t.Stage);
        Assert.Equal(0.375, t.Overall!.Value, 3);
        t.Feed("[ExtractAudio] Destination: a.mp3");
        Assert.Equal("Converting audio", t.Stage);
        Assert.Equal(0.5, t.Overall!.Value, 3);
    }

    [Fact]
    public void CollectsErrorsAndFilters()
    {
        var t = new ProgressTracker(audioOnly: false);
        Assert.True(t.Feed("ERROR: [youtube] abcdefghijk: Private video. Sign in if you've been granted access"));
        t.Feed("WARNING: something minor");
        t.Feed("[download] Some live show does not pass filter (!is_live), skipping ..");
        Assert.Single(t.Errors);
        Assert.Single(t.Warnings);
        Assert.Equal(1, t.FilteredOut);
    }
}

public class YtDlpErrorsTests
{
    [Theory]
    [InlineData("[youtube] abcdefghijk: Sign in to confirm you’re not a bot. Use --cookies-from-browser", "bot")]
    [InlineData("[youtube] abcdefghijk: Private video. Sign in if you've been granted access to this video", "private")]
    [InlineData("[youtube] abcdefghijk: Video unavailable. This video has been removed by the uploader", "unavailable")]
    [InlineData("[youtube] abcdefghijk: Sign in to confirm your age. This video may be inappropriate for some users.", "age-restricted")]
    [InlineData("[youtube] abcdefghijk: Requested format is not available. Use --list-formats for a list of available formats", "quality")]
    [InlineData("unable to download video data: HTTP Error 429: Too Many Requests", "rate-limiting")]
    public void FriendlyMessages(string error, string expectedWord)
    {
        var (message, detail) = YtDlpErrors.Translate([error]);
        Assert.Contains(expectedWord, message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(error, detail);
    }

    [Fact]
    public void FallsBackToTheCleanedErrorText()
    {
        var (message, _) = YtDlpErrors.Translate(["[youtube] abcdefghijk: Something brand new happened"]);
        Assert.Equal("Something brand new happened", message);
    }
}

public class JobFilesTests
{
    [Theory]
    [InlineData("Video.f137.mp4", true)]
    [InlineData("Video.f251-drc.webm", true)]
    [InlineData("Video.mp4.part", true)]
    [InlineData("Video.mp4.ytdl", true)]
    [InlineData("Video.temp.mp4", true)]
    [InlineData("Video.mp4", false)]
    [InlineData("Song - Artist.mp3", false)]
    public void Partials(string name, bool partial) => Assert.Equal(partial, JobFiles.IsPartial(name));

    [Fact]
    public void CollectSkipsDotFoldersAndOrdersMediaFirst()
    {
        var dir = Directory.CreateTempSubdirectory("ark-ydl-files-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "b.srt"), "1");
            File.WriteAllText(Path.Combine(dir, "a.mp4"), "12");
            File.WriteAllText(Path.Combine(dir, "a.f137.mp4"), "x");
            Directory.CreateDirectory(Path.Combine(dir, ".tmp"));
            File.WriteAllText(Path.Combine(dir, ".tmp", "c.mp4"), "x");
            var files = JobFiles.Collect(dir);
            Assert.Equal(["a.mp4", "b.srt"], files.Select(f => f.Name));
            Assert.Equal([0, 1], files.Select(f => f.Index));
            Assert.Equal("video", files[0].Kind);
            Assert.Equal(2, files[0].Size);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void SafeFileName() => Assert.Equal("a_b_c", JobManager.SafeFileName("a/b:c"));
}

public class ProbeMapperTests
{
    private const string VideoJson = """
    {
      "id": "jNQXAC9IVRw", "title": "Me at the zoo", "channel": "jawed", "channel_url": "https://www.youtube.com/channel/UC4QobU6STFB0P71PMvOGN5A",
      "duration": 19, "view_count": 300000000, "upload_date": "20050424", "thumbnail": "https://i.ytimg.com/vi/jNQXAC9IVRw/maxresdefault.jpg",
      "webpage_url": "https://www.youtube.com/watch?v=jNQXAC9IVRw", "live_status": "not_live", "extractor_key": "Youtube",
      "chapters": [ { "start_time": 0, "end_time": 10, "title": "Elephants" }, { "start_time": 10, "end_time": 19, "title": "Trunks" } ],
      "heatmap": [ { "start_time": 0, "end_time": 9.5, "value": 1 }, { "start_time": 9.5, "end_time": 19, "value": 0.25 } ],
      "subtitles": { "en": [ { "ext": "vtt", "name": "English" } ], "live_chat": [ { "ext": "json" } ] },
      "automatic_captions": { "en-orig": [ { "ext": "vtt", "name": "English (Original)" } ], "ta": [ { "ext": "vtt", "name": "Tamil" } ] },
      "formats": [
        { "format_id": "sb0", "protocol": "mhtml", "vcodec": "none", "acodec": "none", "format_note": "storyboard" },
        { "format_id": "140", "vcodec": "none", "acodec": "mp4a.40.2", "abr": 129.5, "filesize": 300000 },
        { "format_id": "251", "vcodec": "none", "acodec": "opus", "abr": 135.2, "filesize": 280000 },
        { "format_id": "137", "vcodec": "avc1.640028", "acodec": "none", "width": 1920, "height": 1080, "fps": 30, "tbr": 2500, "filesize": 5000000 },
        { "format_id": "248", "vcodec": "vp9", "acodec": "none", "width": 1920, "height": 1076, "fps": 30, "tbr": 2000, "filesize": 4000000 },
        { "format_id": "399", "vcodec": "av01.0.08M.08", "acodec": "none", "width": 1920, "height": 1080, "fps": 30, "tbr": 1500, "filesize_approx": 3000000 },
        { "format_id": "18", "vcodec": "avc1.42001E", "acodec": "mp4a.40.2", "width": 640, "height": 360, "fps": 30, "tbr": 500 },
        { "format_id": "616", "vcodec": "vp09.00.40.08", "acodec": "none", "width": 1080, "height": 1920, "fps": 60, "dynamic_range": "HDR10", "tbr": 3000 }
      ]
    }
    """;

    [Fact]
    public void MapsAVideo()
    {
        var url = UrlGuard.Analyze("https://youtu.be/jNQXAC9IVRw?t=5", YdlOptions.DefaultSites, false);
        using var doc = JsonDocument.Parse(VideoJson);
        var r = ProbeMapper.Map(doc.RootElement, url);

        Assert.Equal("video", r.Kind);
        Assert.Equal("Me at the zoo", r.Title);
        Assert.Equal("jawed", r.Uploader);
        Assert.Equal("2005-04-24", r.UploadDate);
        Assert.Equal(5, r.HintStart);
        Assert.Equal(2, r.Chapters.Count);
        Assert.Equal(2, r.Heatmap.Count);
        Assert.Single(r.Subtitles);
        Assert.Equal(["en-orig", "ta"], r.AutoCaptions.Select(s => s.Lang));

        // 1080 (avc/vp9/av1 with the 1076 letterbox merged in) and 360; the vertical HDR 1080x1920 also counts as 1080
        Assert.Equal([1080, 360], r.VideoQualities.Select(q => q.Res));
        var hd = r.VideoQualities[0];
        Assert.Equal(["h264", "vp9", "av1"], hd.Codecs);
        Assert.True(hd.Hdr);
        Assert.Equal(60, hd.Fps);
        Assert.Equal(5000000, hd.Sizes["h264"]);
        Assert.Equal(3000000, hd.Sizes["av1"]);
        Assert.Equal(["opus", "aac"], r.AudioTracks.Select(a => a.Codec));
    }

    [Fact]
    public void MapsAFlatPlaylist()
    {
        const string json = """
        { "_type": "playlist", "id": "PLx", "title": "Mix", "channel": "Someone", "playlist_count": 3,
          "entries": [
            { "id": "aaaaaaaaaaa", "title": "One", "duration": 61, "url": "https://www.youtube.com/watch?v=aaaaaaaaaaa",
              "thumbnails": [ { "url": "https://i.ytimg.com/a/small.jpg", "width": 168 }, { "url": "https://i.ytimg.com/a/big.jpg", "width": 480 } ] },
            { "id": "bbbbbbbbbbb", "title": "[Private video]", "duration": null }
          ] }
        """;
        var url = UrlGuard.Analyze("https://www.youtube.com/playlist?list=PLabcdefghijklmnop12", YdlOptions.DefaultSites, false);
        using var doc = JsonDocument.Parse(json);
        var r = ProbeMapper.Map(doc.RootElement, url);
        Assert.Equal("playlist", r.Kind);
        Assert.Equal(3, r.PlaylistCount);
        Assert.True(r.Truncated);
        Assert.Equal(2, r.Entries.Count);
        Assert.Equal("https://i.ytimg.com/a/big.jpg", r.Entries[0].Thumbnail);
        Assert.True(r.Entries[1].Unavailable);
        Assert.Equal("https://i.ytimg.com/vi/bbbbbbbbbbb/mqdefault.jpg", r.Entries[1].Thumbnail);
        Assert.Equal(61, r.Duration);
    }

    [Theory]
    [InlineData("avc1.640028", "h264")]
    [InlineData("vp09.00.51.08", "vp9")]
    [InlineData("av01.0.12M.10", "av1")]
    [InlineData("mp4a.40.5", "aac")]
    [InlineData("opus", "opus")]
    public void CodecFamilies(string codec, string family) => Assert.Equal(family, ProbeMapper.Family(codec));
}
