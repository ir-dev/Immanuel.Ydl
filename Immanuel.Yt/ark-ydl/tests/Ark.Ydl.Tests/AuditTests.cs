using System.Net;
using Ark.Rapid;
using Ark.Ydl.Audit;
using Ark.Ydl.Jobs;
using Ark.Ydl.Options;
using Ark.Ydl.Util;
using Ark.Ydl.YtDlp;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Ark.Ydl.Tests;

/// <summary>A fresh app (and so a fresh audit database) for the audit tests, with a short retention to test pruning.</summary>
public sealed class AuditFactory : AppFactory
{
    protected override IEnumerable<(string Key, string Value)> Settings => [("Ydl:Audit:RetentionDays", "30")];
}

public class AuditUnitTests
{
    [Theory]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36", "Chrome", "131", "Windows", "desktop", false)]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36 Edg/131.0.2903.70", "Edge", "131", "Windows", "desktop", false)]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 18_1 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.1 Mobile/15E148 Safari/604.1", "Safari", "18", "iOS", "mobile", false)]
    [InlineData("Mozilla/5.0 (iPad; CPU OS 17_6 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.6 Mobile/15E148 Safari/604.1", "Safari", "17", "iPadOS", "tablet", false)]
    [InlineData("Mozilla/5.0 (X11; Ubuntu; Linux x86_64; rv:133.0) Gecko/20100101 Firefox/133.0", "Firefox", "133", "Linux", "desktop", false)]
    [InlineData("Mozilla/5.0 (Linux; Android 14; SM-X710) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36", "Chrome", "130", "Android", "tablet", false)]
    [InlineData("Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Mobile Safari/537.36", "Chrome", "130", "Android", "mobile", false)]
    [InlineData("Mozilla/5.0 (Linux; Android 13; SAMSUNG SM-S911B) AppleWebKit/537.36 (KHTML, like Gecko) SamsungBrowser/26.0 Chrome/122.0.0.0 Mobile Safari/537.36", "Samsung Internet", "26", "Android", "mobile", false)]
    [InlineData("Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)", "Googlebot", null, null, "bot", true)]
    [InlineData("curl/8.7.1", "curl", null, null, "bot", true)]
    [InlineData("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.1 Safari/605.1.15", "Safari", "18", "macOS", "desktop", false)]
    public void ReadsUserAgents(string ua, string? browser, string? version, string? os, string device, bool bot)
    {
        var a = UserAgentParser.Parse(ua);
        if (browser is not null) Assert.Equal(browser, a.Browser);
        if (version is not null) Assert.Equal(version, a.BrowserVersion);
        Assert.Equal(os, a.Os);
        Assert.Equal(device, a.Device);
        Assert.Equal(bot, a.IsBot);
    }

    [Fact]
    public void ClientHintsBeatTheFrozenUserAgent()
    {
        // Chromium reports "Windows NT 10.0" and "Mac OS X 10_15_7" forever; the hints say what it really is.
        var a = UserAgentParser.Parse(
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36",
            "\"Brave\";v=\"131\", \"Chromium\";v=\"131\", \"Not_A Brand\";v=\"24\"", "?0", "\"macOS\"");
        Assert.Equal("Brave", a.Browser);
        Assert.Equal("131", a.BrowserVersion);
        Assert.Equal("macOS", a.Os);
        Assert.Equal("desktop", a.Device);
        Assert.Equal("Unknown", UserAgentParser.Parse(null).Browser ?? "Unknown");
    }

    [Fact]
    public void MasksHashesAndRecognisesPrivateAddresses()
    {
        Assert.Equal("81.2.69.0", IpTools.Mask(IPAddress.Parse("81.2.69.160")));
        Assert.Equal("81.2.69.0", IpTools.Mask(IPAddress.Parse("::ffff:81.2.69.160")));
        Assert.Equal("2001:db8:85a3::", IpTools.Mask(IPAddress.Parse("2001:db8:85a3:8d3:1319:8a2e:370:7348")));
        var h1 = IpTools.Hash(IPAddress.Parse("81.2.69.160"), "salt");
        Assert.Equal(h1, IpTools.Hash(IPAddress.Parse("::ffff:81.2.69.160"), "salt"));
        Assert.NotEqual(h1, IpTools.Hash(IPAddress.Parse("81.2.69.160"), "other"));
        Assert.Matches("^h:[0-9a-f]{16}$", h1);
        foreach (var p in new[] { "127.0.0.1", "10.1.2.3", "172.20.0.1", "192.168.1.9", "100.64.3.3", "::1", "fd00::1", "fe80::1" })
            Assert.True(IpTools.IsPrivate(IPAddress.Parse(p)), p);
        foreach (var p in new[] { "81.2.69.160", "8.8.8.8", "172.32.0.1", "2001:4860:4860::8888" })
            Assert.False(IpTools.IsPrivate(IPAddress.Parse(p)), p);
    }

    [Fact]
    public void ReadsTheClientHeaderAndDropsJunk()
    {
        var (tz, screen, pwa, v) = ClientHeader.Parse("tz=Asia%2FKolkata&sw=1440&sh=900&dpr=2&pwa=1&v=1.1.0");
        Assert.Equal("Asia/Kolkata", tz);
        Assert.Equal("1440x900@2", screen);
        Assert.True(pwa);
        Assert.Equal("1.1.0", v);

        (tz, screen, pwa, v) = ClientHeader.Parse("tz=%3Cscript%3E&sw=-1&sh=abc&pwa=yes&v=1.1.0%27--");
        Assert.Null(tz);
        Assert.Null(screen);
        Assert.Null(pwa);
        Assert.Null(v);
        Assert.Equal((null, null, null, null), ClientHeader.Parse(new string('a', 500)));
    }

    [Theory]
    [InlineData("GET", "/", "page")]
    [InlineData("GET", "/index.html", "page")]
    [InlineData("POST", "/api/probe", "probe")]
    [InlineData("POST", "/api/jobs", "job")]
    [InlineData("GET", "/api/jobs/AAAAAAAAAAAAAAAAAAAAAA", "job-status")]
    [InlineData("GET", "/api/jobs/AAAAAAAAAAAAAAAAAAAAAA/events", "events")]
    [InlineData("GET", "/api/jobs/AAAAAAAAAAAAAAAAAAAAAA/files/0", "file")]
    [InlineData("GET", "/api/jobs/AAAAAAAAAAAAAAAAAAAAAA/zip", "zip")]
    [InlineData("GET", "/api/admin/overview", "admin")]
    [InlineData("GET", "/admin", "admin-page")]
    [InlineData("GET", "/api/version", "release")]
    [InlineData("GET", "/api/system", "system")]
    [InlineData("GET", "/js/app.js", "static")]
    [InlineData("GET", "/manifest.webmanifest", "static")]
    [InlineData("GET", "/watch", "shortcut")]
    [InlineData("GET", "/jNQXAC9IVRw", "shortcut")]
    [InlineData("GET", "/@SomeChannel", "shortcut")]
    [InlineData("GET", "/signin-oidc", "auth")]
    [InlineData("GET", "/wp-login.php", "other")]
    public void ClassifiesRequests(string method, string path, string kind) => Assert.Equal(kind, AuditRecorder.Kind(method, path));

    [Fact]
    public void DescribesWhatWasAskedFor()
    {
        var video = new DownloadRequest { Mode = DownloadMode.Video, Video = new VideoOptions { MaxHeight = 1080, Container = VideoContainer.Mp4 } };
        Assert.Equal("MP4 1080p", AuditRecorder.Format(video));
        Assert.Equal("MKV best", AuditRecorder.Format(new DownloadRequest { Video = new VideoOptions { Container = VideoContainer.Mkv } }));
        Assert.Equal("MP3 192k", AuditRecorder.Format(new DownloadRequest { Mode = DownloadMode.Audio, Audio = new AudioOptions { Format = AudioFormat.Mp3, Bitrate = 192 } }));
        Assert.Equal("Original audio", AuditRecorder.Format(new DownloadRequest { Mode = DownloadMode.Audio, Audio = new AudioOptions { Format = AudioFormat.Best } }));
        Assert.Equal("Subtitles", AuditRecorder.Format(new DownloadRequest { Mode = DownloadMode.Subtitles }));

        var clip = new ClipOptions { Sections = [new ClipSection { Start = 10, End = 40 }, new ClipSection { Start = 100 }] };
        Assert.Equal("clip", AuditRecorder.Scope(video, false, clip));
        Assert.Equal("chapters", AuditRecorder.Scope(video, false, new ClipOptions { SplitChapters = true }));
        Assert.Equal("playlist", AuditRecorder.Scope(video, true, null));
        Assert.Equal("whole", AuditRecorder.Scope(video, false, null));
        Assert.Equal(30 + 20, AuditRecorder.ClipSeconds(clip, 120));
        Assert.Null(AuditRecorder.ClipSeconds(clip, null)); // an open-ended section needs the duration
    }

    [Fact]
    public void CsvNeverLetsASpreadsheetRunAFormula()
    {
        Assert.Equal("plain", AuditReports.Csv("plain"));
        Assert.Equal("\"a,b\"", AuditReports.Csv("a,b"));
        Assert.Equal("\"say \"\"hi\"\"\"", AuditReports.Csv("say \"hi\""));
        Assert.Equal("'=HYPERLINK(\"x\")", AuditReports.Csv("=HYPERLINK(\"x\")").Trim('"').Replace("\"\"", "\""));
        Assert.Equal("'@SUM(A1)", AuditReports.Csv("@SUM(A1)"));
        Assert.Equal("-12.5", AuditReports.Csv("-12.5")); // a negative number is just a number
    }

    [Fact]
    public void RangesAreValidatedAndBounded()
    {
        var r = DateRange.Parse(null, null, "2026-11-10");
        Assert.Equal(new DateRange("2026-10-12", "2026-11-10"), r);
        Assert.Equal(30, r.Days);
        Assert.Equal(new DateRange("2026-09-12", "2026-10-11"), r.Previous());
        Assert.Equal(new DateRange("2026-11-01", "2026-11-05"), DateRange.Parse("2026-11-05", "2026-11-01", "2026-11-10")); // swapped
        Assert.Equal(new DateRange("2026-11-04", "2026-11-10"), DateRange.Parse("'; DROP TABLE x; --", null, "2026-11-10", 7));
        Assert.True(DateRange.Parse("1990-01-01", "2026-11-10", "2026-11-10").Days <= 3 * 366 + 1);
    }

    [Fact]
    public void ReadsYtDlpItemMetadataAndSkipsJunk()
    {
        var dir = Directory.CreateTempSubdirectory("ark-ydl-items-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, ItemMetadata.Folder));
            File.WriteAllLines(ItemMetadata.PathFor(dir),
            [
                """{"id":"jNQXAC9IVRw","title":"Me at the zoo","channel":"jawed","channel_id":"UC4QobU6STFB0P71PMvOGN5A","duration":19,"view_count":350000000,"categories":["Film & Animation"],"tags":["zoo","elephants"],"upload_date":"20050424","playlist_index":1}""",
                "NA",
                "{not json",
                """{"id":"jNQXAC9IVRw","title":"again (retry)"}""",
                """{"id":"abcdefghijk","title":"Second","uploader":"someone","categories":["Music"],"genre":"Jazz","duration":200.5}""",
            ]);
            var items = ItemMetadata.Read(dir);
            Assert.Equal(2, items.Count);
            Assert.Equal("Film & Animation", items[0].Categories.Single());
            Assert.Equal("2005-04-24", items[0].UploadDate);
            Assert.Equal(19, items[0].Duration);
            Assert.Equal("someone", items[1].Channel);
            Assert.Equal("Jazz", items[1].Genre);
            Assert.Empty(ItemMetadata.Read(Path.Combine(dir, "missing")));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void AsksYtDlpForItemMetadataOnlyWhenAuditing()
    {
        var url = UrlGuard.Analyze("https://youtu.be/jNQXAC9IVRw", YdlOptions.DefaultSites, false);
        var on = new YdlOptions();
        var args = YtDlpArgs.Download(new DownloadRequest(), url, YtDlpArgs.Common(on, "/cache", [], null), "/jobs/x%y", on);
        var i = args.IndexOf("--print-to-file");
        Assert.True(i > 0);
        Assert.StartsWith("video:%(.{id,title,", args[i + 1]);
        Assert.EndsWith("})j", args[i + 1]);
        Assert.Equal(Path.Combine("/jobs/x%%y", ".meta", "items.jsonl"), args[i + 2]); // % doubled: it is an output template too
        Assert.True(i < args.IndexOf("--")); // never after the URL separator

        var off = new YdlOptions { Audit = new AuditOptions { CaptureItemMetadata = false } };
        Assert.DoesNotContain("--print-to-file", YtDlpArgs.Download(new DownloadRequest(), url, YtDlpArgs.Common(off, "/c", [], null), "/j", off));
        var disabled = new YdlOptions { Audit = new AuditOptions { Enabled = false } };
        Assert.DoesNotContain("--print-to-file", YtDlpArgs.Download(new DownloadRequest(), url, YtDlpArgs.Common(disabled, "/c", [], null), "/j", disabled));
    }

    [Fact]
    public void MapsDbIpRecords()
    {
        var city = new Dictionary<string, object>
        {
            ["city"] = new Dictionary<string, object> { ["names"] = new Dictionary<string, object> { ["en"] = "Chennai" } },
            ["country"] = new Dictionary<string, object> { ["iso_code"] = "IN", ["names"] = new Dictionary<string, object> { ["en"] = "India", ["de"] = "Indien" } },
            ["subdivisions"] = new List<object> { new Dictionary<string, object> { ["names"] = new Dictionary<string, object> { ["en"] = "Tamil Nadu" } } },
        };
        Assert.Equal(new GeoResult("IN", "India", "Tamil Nadu", "Chennai"), GeoIpService.Map(city));
        var country = new Dictionary<string, object>
        {
            ["country"] = new Dictionary<string, object> { ["iso_code"] = "GB", ["names"] = new Dictionary<string, object> { ["en"] = "United Kingdom" } },
        };
        Assert.Equal(new GeoResult("GB", "United Kingdom", null, null), GeoIpService.Map(country));
    }
}

public class AuditStoreTests(AuditFactory factory) : IClassFixture<AuditFactory>
{
    private async Task<(AuditWriter Writer, AuditRecorder Recorder, AuditReports Reports)> Services()
    {
        _ = factory.Server; // boots the app
        var writer = factory.Services.GetRequiredService<AuditWriter>();
        await writer.Ready.WaitAsync(TimeSpan.FromSeconds(20));
        return (writer, factory.Services.GetRequiredService<AuditRecorder>(), factory.Services.GetRequiredService<AuditReports>());
    }

    private static DefaultHttpContext Request(string method, string path, int status, string ip = "81.2.69.160", string? ua = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.Path = path;
        ctx.Request.Headers.UserAgent = ua ?? "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";
        ctx.Request.Headers.AcceptLanguage = "en-GB,en;q=0.9";
        ctx.Request.Headers["X-Ark-Client"] = "tz=Europe%2FLondon&sw=1920&sh=1080&dpr=1&pwa=0&v=1.1.0";
        ctx.Connection.RemoteIpAddress = IPAddress.Parse(ip);
        ctx.Response.StatusCode = status;
        return ctx;
    }

    private static string Range(AuditRecorder r) => r.Clock.Today;

    /// <summary>List defaults live in appsettings.json: binding appends to a non-empty list, so a class default would repeat (and could never be removed).</summary>
    [Fact]
    public void ShippedListDefaultsBindExactlyOnce()
    {
        var o = factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<YdlOptions>>().Value;
        Assert.Equal(new[] { "/healthz" }, o.Audit.ExcludePaths);
        Assert.Equal(new[] { "ark-ydl.admin" }, o.Admin.Roles);
        Assert.Equal("city", o.Audit.GeoIp.Edition);
        Assert.Equal(30, o.Audit.RetentionDays); // this factory's override
    }

    [Fact]
    public async Task SchemaIsCreatedOnceAndStaysIdempotent()
    {
        var (writer, _, _) = await Services();
        var db = writer.Db;
        await AuditSchema.EnsureCreatedAsync(db); // a second start
        var tables = (await db.GetTables()).ToList();
        foreach (var t in new[] { AuditSchema.Requests, AuditSchema.Downloads, AuditSchema.Daily, AuditSchema.Meta }) Assert.Contains(t, tables);
        Assert.Equal("1", await AuditSchema.GetMetaAsync(db, "schema_version"));
        Assert.Equal("wal", await db.ExecuteScalarAsync<string>("PRAGMA journal_mode;"));
        Assert.True(db is SqliteManager);
    }

    [Fact]
    public async Task RecordsADownloadAndEveryReportSeesIt()
    {
        var (writer, recorder, reports) = await Services();
        var marker = "zoo-" + Guid.NewGuid().ToString("N")[..8];

        // The page probed the video (request row), then queued it.
        var probe = Request("POST", "/api/probe", 200);
        AuditRecorder.Annotate(probe, videoId: "jNQXAC9IVRw", detail: $"Me at the {marker} [Film & Animation]");
        recorder.Request(probe, 1234);

        var url = UrlGuard.Analyze("https://youtu.be/jNQXAC9IVRw", YdlOptions.DefaultSites, false);
        var req = new DownloadRequest
        {
            Url = url.Url, Mode = DownloadMode.Video, Title = $"Me at the {marker}",
            Video = new VideoOptions { MaxHeight = 720 }, Label = "MP4 · 720p",
        };
        var dir = Directory.CreateTempSubdirectory("ark-ydl-job-").FullName;
        var job = new DownloadJob(JobManager.NewId(), 1, req, url, dir, "ip:81.2.69.160");
        var probed = new ProbeResult
        {
            Kind = "video", Id = "jNQXAC9IVRw", Title = $"Me at the {marker}", Uploader = "jawed", Duration = 19,
            Categories = ["Film & Animation"], Tags = ["zoo"], ViewCount = 350_000_000, UploadDate = "2005-04-24",
            VideoQualities = [new VideoQuality { Res = 720, Sizes = new() { ["h264"] = 900_000 } }],
            AudioTracks = [new AudioTrack { Codec = "aac", Size = 100_000 }],
        };
        var jobCtx = Request("POST", "/api/jobs", 202);
        recorder.DownloadQueued(job, recorder.Client(jobCtx), probed);
        recorder.Request(jobCtx, 15);

        // It ran and finished; one file was saved twice (the second time resumed, which doesn't count).
        job.Mutate(j =>
        {
            j.Status = JobStatus.Completed;
            j.StartedAt = j.CreatedAt.AddSeconds(2);
            j.FinishedAt = j.CreatedAt.AddSeconds(9);
            j.Files = [new JobFile(0, "Me at the zoo.mp4", "Me at the zoo.mp4", 1_000_000, "video")];
        });
        writer.Enqueue(new UpdateOp(AuditSchema.Downloads, "job_id", job.Id, () => AuditRecorder.DownloadUpdate(job)));
        recorder.Served(job, 1_000_000);
        await writer.FlushAsync(TimeSpan.FromSeconds(10));

        var row = await reports.DownloadAsync(job.Id);
        Assert.NotNull(row);
        Assert.Equal("completed", row!["status"]);
        Assert.Equal("Film & Animation", row["category"]);
        Assert.Equal("jawed", row["channel"]);
        Assert.Equal("MP4 720p", row["format"]);
        Assert.Equal("whole", row["scope"]);
        Assert.Equal(1_000_000L, row["total_bytes"]);
        Assert.Equal(1_000_000L, row["estimated_bytes"]);
        Assert.Equal(1L, row["served_count"]);
        Assert.Equal(7000L, row["run_ms"]);
        Assert.Equal(2000L, row["queue_ms"]);
        Assert.Equal("81.2.69.160", row["ip"]);
        Assert.Equal("ip:81.2.69.160", row["client_key"]);
        Assert.Equal("Chrome", row["browser"]);
        Assert.Equal("Windows", row["os"]);
        Assert.Equal("en-GB", row["language"]);
        Assert.Equal("Europe/London", row["client_tz"]);
        Assert.Equal("1920x1080@1", row["client_screen"]);
        Assert.Equal(recorder.Clock.Today, row["day"]);

        var range = DateRange.Parse(Range(recorder), Range(recorder), recorder.Clock.Today);
        var kpis = await reports.KpisAsync(range);
        Assert.True(kpis.Downloads >= 1 && kpis.Completed >= 1 && kpis.Bytes >= 1_000_000 && kpis.ServedCount >= 1);
        Assert.True(kpis.Visitors >= 1 && kpis.Probes >= 1);

        var b = await reports.BreakdownsAsync(range);
        Assert.Contains(((List<KeyCount>)b["genres"]), k => k.Key == "Film & Animation");
        Assert.Contains(((List<KeyCount>)b["formats"]), k => k.Key == "MP4 720p");
        Assert.Contains(((List<KeyCount>)b["browsers"]), k => k.Key == "Chrome");
        Assert.Contains(((List<KeyCount>)b["qualities"]), k => k.Key == "720");
        Assert.Contains(((List<TopVideo>)b["videos"]), v => v.VideoId == "jNQXAC9IVRw");
        Assert.Contains(((List<HeatCell>)b["heatmap"]), c => c.Count >= 1);

        var (total, rows) = await reports.DownloadsAsync(range, new LogFilter { Q = marker });
        Assert.Equal(1, total);
        Assert.Equal(job.Id, ((IDictionary<string, object>)rows.Single())["job_id"]);
        var (rtotal, _) = await reports.RequestsAsync(range, new LogFilter { Kind = "probe", Q = marker });
        Assert.Equal(1, rtotal);

        var daily = await reports.DailyAsync(range);
        Assert.True(daily.Single().Downloads >= 1);
        var hourly = await reports.HourlyAsync(range);
        Assert.Equal(24, hourly.Count);
        Assert.True(hourly.Sum(h => h.Downloads) >= 1);

        var csv = await reports.CsvAsync(AuditSchema.Downloads, range);
        Assert.StartsWith("id,job_id,", csv);
        Assert.Contains(job.Id, csv);
        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task HostileTextIsStoredVerbatimAndNeverRunAsSql()
    {
        var (writer, recorder, reports) = await Services();
        var marker = Guid.NewGuid().ToString("N")[..10];
        var evil = $"x'); DROP TABLE {AuditSchema.Requests}; -- {marker} \0 ünïcødé 🎵";
        var ctx = Request("GET", "/api/system", 200, ua: evil);
        AuditRecorder.Annotate(ctx, detail: evil);
        recorder.Request(ctx, 1);
        await writer.FlushAsync(TimeSpan.FromSeconds(10));

        Assert.Contains(AuditSchema.Requests, await writer.Db.GetTables());
        var range = DateRange.Parse(null, null, recorder.Clock.Today, 1);
        var (total, rows) = await reports.RequestsAsync(range, new LogFilter { Q = marker });
        Assert.Equal(1, total);
        var row = (IDictionary<string, object>)rows.Single();
        Assert.Equal(evil.Replace("\0", ""), row["detail"]);
        // A search term is a value, not SQL: this matches nothing rather than everything.
        var (none, _) = await reports.RequestsAsync(range, new LogFilter { Q = "%' OR 1=1 --" });
        Assert.Equal(0, none);
    }

    [Fact]
    public async Task OneBadRowDoesNotSinkItsBatch()
    {
        var (writer, recorder, reports) = await Services();
        var marker = Guid.NewGuid().ToString("N")[..10];
        var failedBefore = writer.Status.Failed;
        var good = new Dictionary<string, object?>
        {
            ["ts_utc"] = AuditClock.Format(DateTime.UtcNow), ["day"] = recorder.Clock.Today, ["kind"] = "api", ["path"] = "/x", ["detail"] = marker,
        };
        await writer.WriteBatchAsync([
            new InsertOp("no_such_table", new Dictionary<string, object?> { ["a"] = 1 }),
            new InsertOp(AuditSchema.Requests, good),
        ]);
        Assert.Equal(failedBefore + 1, writer.Status.Failed);
        var (total, _) = await reports.RequestsAsync(DateRange.Parse(null, null, recorder.Clock.Today, 1), new LogFilter { Q = marker });
        Assert.Equal(1, total);
    }

    [Fact]
    public async Task RollsUpDaysAndPrunesRawRowsPastRetention()
    {
        var (writer, recorder, _) = await Services();
        var db = writer.Db;
        var old = recorder.Clock.Day(DateTime.UtcNow.AddDays(-400));
        await writer.WriteBatchAsync([
            new InsertOp(AuditSchema.Requests, new Dictionary<string, object?> { ["ts_utc"] = "2025-01-01 00:00:00.000", ["day"] = old, ["kind"] = "page", ["ip"] = "1.1.1.1" }),
            new InsertOp(AuditSchema.Requests, new Dictionary<string, object?> { ["ts_utc"] = "2025-01-01 00:00:01.000", ["day"] = old, ["kind"] = "page", ["ip"] = "1.1.1.2" }),
        ]);
        var maintenance = factory.Services.GetRequiredService<AuditMaintenance>();
        await maintenance.RunOnceAsync(backfill: true, forcePrune: true);
        Assert.Null(maintenance.Status.LastError);

        var d = await db.GetSqlValueAsync(old);
        Assert.Equal(0, await db.ExecuteCountAsync($"SELECT COUNT(*) FROM {AuditSchema.Requests} WHERE day = {d};"));
        var kept = (await db.ExecuteSelectAsync<DayTotals>(
            $"SELECT day AS Day, page_views AS PageViews, visitors AS Visitors FROM {AuditSchema.Daily} WHERE day = {d};")).Single();
        Assert.Equal(2, kept.PageViews);
        Assert.Equal(2, kept.Visitors);
    }

    [Fact]
    public async Task MaskedModeNeverStoresTheFullAddress()
    {
        var (_, recorder, _) = await Services();
        var masked = new AuditRecorder(
            Microsoft.Extensions.Options.Options.Create(new YdlOptions { Audit = new AuditOptions { IpMode = AuditIpMode.Masked } }),
            factory.Services.GetRequiredService<AuditWriter>(), factory.Services.GetRequiredService<GeoIpService>(),
            factory.Services.GetRequiredService<Ark.Ydl.Releases.ReleaseCatalog>(), factory.Services.GetRequiredService<Ark.Ydl.Tools.ToolLocator>(),
            factory.Services.GetRequiredService<YdlPaths>(), Microsoft.Extensions.Logging.Abstractions.NullLogger<AuditRecorder>.Instance);
        var c = masked.Client(Request("GET", "/", 200, ip: "81.2.69.160"));
        Assert.Equal("81.2.69.0", c.Ip);
        Assert.Equal("ip:81.2.69.0", c.ClientKey);
        Assert.Equal("Local network", recorder.Client(Request("GET", "/", 200, ip: "192.168.1.4")).CountryName);
    }
}
