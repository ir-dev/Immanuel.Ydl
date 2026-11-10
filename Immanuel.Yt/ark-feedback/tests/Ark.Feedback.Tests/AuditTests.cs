using Ark.Feedback.Analysis;
using Ark.Feedback.Audit;
using Ark.Feedback.Options;
using Ark.Feedback.YouTube;
using Ark.Rapid;
using Microsoft.Extensions.Options;

namespace Ark.Feedback.Tests;

public class AuditClockTests
{
    [Fact]
    public void BucketsFollowTheConfiguredZone()
    {
        var utc = new AuditClock("UTC");
        var kolkata = new AuditClock("Asia/Kolkata");
        var t = new DateTime(2026, 11, 10, 22, 30, 0, DateTimeKind.Utc);
        Assert.Equal("2026-11-10", utc.Day(t));
        Assert.Equal("2026-11-11", kolkata.Day(t)); // 04:00 the next day in India
        Assert.Equal("20261111", kolkata.Day8(t));
        Assert.Equal((("2026-11-10"), 22, 2), utc.Bucket(t));
        Assert.Null(utc.Note);
        Assert.NotNull(new AuditClock("Mars/Olympus").Note);
    }

    [Fact]
    public void DayAndMonthHelpers()
    {
        Assert.Equal("2026-11", AuditClock.MonthOfDay8("20261110"));
        Assert.Equal("2026-11-10", AuditClock.DayOfDay8("20261110"));
        Assert.Equal("20261110", AuditClock.Day8OfDay("2026-11-10"));
        Assert.True(AuditClock.IsDay8("20261110"));
        Assert.False(AuditClock.IsDay8("20261310"));
        Assert.True(AuditClock.IsMonth("2026-11"));
        Assert.False(AuditClock.IsMonth("2026-13"));
        Assert.Equal(["2026-11", "2026-12", "2027-01"], AuditDatabases.MonthsBetween("2026-11-20", "2027-01-03").ToList());
        Assert.Equal("requests_20261110", AuditSchema.Name(AuditTable.Requests, "20261110"));
        Assert.Equal("comments_20261110", AuditSchema.Name(AuditTable.Comments, "20261110"));
    }
}

/// <summary>Writes real rows into a real monthly SQLite file under a temp folder and reads them back through the reports.</summary>
public class AuditWriterTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ark-feedback-audit-").FullName;
    private readonly AuditDatabases _dbs;
    private readonly AuditWriter _writer;
    private readonly AuditReports _reports;
    private readonly AuditClock _clock = new("UTC");

    public AuditWriterTests()
    {
        var opt = Microsoft.Extensions.Options.Options.Create(new FeedbackOptions { DataRoot = _root });
        var paths = new FeedbackPaths(opt.Value, _root);
        _dbs = new AuditDatabases(opt, paths);
        _writer = new AuditWriter(opt, _dbs, Logs.Of<AuditWriter>());
        _reports = new AuditReports(_dbs, _clock);
    }

    private static Dictionary<string, object?> RequestRow(string day, int hour, string kind, int status) => new()
    {
        ["ts_utc"] = AuditClock.Format(DateTime.UtcNow), ["day"] = day, ["hour"] = hour, ["weekday"] = 1, ["method"] = "GET", ["path"] = "/", ["kind"] = kind,
        ["status"] = status, ["duration_ms"] = 12L, ["ip"] = "203.0.113.9", ["ip_country"] = "IN", ["ip_country_name"] = "India", ["device"] = "desktop", ["browser"] = "Chrome", ["is_bot"] = 0,
    };

    [Fact]
    public async Task WritesIntoTheMonthFileAndTheDayTables()
    {
        var day8 = "20261110";
        var batch = new List<AuditOp>
        {
            new InsertOp(day8, AuditTable.Requests, RequestRow("2026-11-10", 9, "page", 200)),
            new InsertOp(day8, AuditTable.Requests, RequestRow("2026-11-10", 9, "analyse", 202)),
            new InsertOp(day8, AuditTable.Analyses, new Dictionary<string, object?>
            {
                ["analysis_id"] = "20261110-00c0ffee", ["day"] = "2026-11-10", ["hour"] = 9, ["weekday"] = 2, ["created_utc"] = DateTime.UtcNow, ["status"] = "queued", ["profile"] = "balanced",
                ["video_id"] = "jNQXAC9IVRw", ["url"] = "https://www.youtube.com/watch?v=jNQXAC9IVRw", ["client_key"] = "ip:203.0.113.9", ["ip"] = "203.0.113.9", ["ip_country"] = "IN", ["ip_country_name"] = "India",
            }),
            // a second day, same month
            new InsertOp("20261111", AuditTable.Requests, RequestRow("2026-11-11", 10, "page", 200)),
        };
        await _writer.WriteBatchAsync(batch);
        await _writer.WriteBatchAsync([
            new UpdateOp(day8, AuditTable.Analyses, "analysis_id", "20261110-00c0ffee", () => new Dictionary<string, object?> { ["status"] = "completed", ["analysed"] = 120, ["score"] = 42.5, ["verdict"] = "liked", ["positive"] = 80, ["neutral"] = 25, ["negative"] = 15, ["run_ms"] = 9000L, ["title"] = "Me at the zoo", ["channel"] = "jawed", ["primary_language"] = "en" }),
            new InsertOp(day8, AuditTable.Comments, new Dictionary<string, object?> { ["analysis_id"] = "20261110-00c0ffee", ["comment_id"] = "c1", ["text"] = "lovely", ["sentiment"] = "positive", ["score"] = 0.9, ["lang"] = "en", ["like_count"] = 5L, ["created_utc"] = DateTime.UtcNow, ["is_reply"] = 0, ["repeats"] = 1 }),
            new FeedbackOp(day8, "20261110-00c0ffee", true, "spot on", DateTime.UtcNow),
            new ViewedOp(day8, "20261110-00c0ffee", DateTime.UtcNow),
        ]);

        Assert.True(File.Exists(Path.Combine(_root, "audit", "ark-feedback-audit-2026-11.db")));
        Assert.Equal(0, _writer.Status.Failed);
        var db = _dbs.For("2026-11");
        Assert.Equal(["2026-11-10", "2026-11-11"], await AuditSchema.DaysInFileAsync(db));
        var tables = await db.ExecuteSelectAsync<string>("SELECT name FROM sqlite_master WHERE type='table' ORDER BY name;");
        Assert.Contains("requests_20261110", tables);
        Assert.Contains("analyses_20261110", tables);
        Assert.Contains("comments_20261110", tables);
        Assert.Contains("requests_20261111", tables);
        Assert.Contains("audit_days", tables);
        Assert.Contains("audit_meta", tables);
        Assert.Equal("1", await AuditSchema.GetMetaAsync(db, "schema_version"));

        var row = await _reports.AnalysisAsync("20261110-00c0ffee");
        Assert.NotNull(row);
        Assert.Equal("completed", row!["status"]);
        Assert.Equal(1L, Convert.ToInt64(row["liked"]));
        Assert.Equal("spot on", row["feedback_note"]);
        Assert.Equal(1L, Convert.ToInt64(row["feedback_count"]));
        Assert.Equal(1L, Convert.ToInt64(row["report_views"]));

        var range = new DateRange("2026-11-10", "2026-11-11");
        var k = await _reports.KpisAsync(range);
        Assert.Equal(3, k.Requests);
        Assert.Equal(2, k.PageViews);
        Assert.Equal(1, k.Analyses);
        Assert.Equal(1, k.Completed);
        Assert.Equal(1, k.Liked);
        Assert.Equal(1, k.Comments);
        Assert.Equal(1, k.Videos);
        Assert.Equal(42.5, k.AvgScore);
        var daily = await _reports.DailyAsync(range);
        Assert.Equal(2, daily.Count);
        Assert.Equal(2, daily[0].Requests);
        Assert.Equal(1, daily[1].Requests);

        var (total, rows) = await _reports.AnalysesAsync(range, new LogFilter { Liked = "liked" });
        Assert.Equal(1, total);
        Assert.Equal("Me at the zoo", rows[0]["title"]);
        var (ctotal, crows) = await _reports.CommentsAsync("20261110-00c0ffee", null, 1, 50);
        Assert.Equal(1, ctotal);
        Assert.Equal("lovely", crows[0]["text"]);
        var b = await _reports.BreakdownsAsync(range);
        var verdicts = (List<KeyCount>)b["verdicts"];
        Assert.Equal("liked", verdicts[0].Key);
        var csv = await _reports.CsvAsync(AuditTable.Analyses, "2026-11-10");
        Assert.Contains("Me at the zoo", csv);
        Assert.Null(await _reports.CsvAsync(AuditTable.Analyses, "2026-11-12"));
        var store = await _reports.StoreAsync();
        Assert.Single(store);
    }

    [Fact]
    public async Task ABadRowDoesNotSinkTheBatch()
    {
        await _writer.WriteBatchAsync([
            new InsertOp("20261201", AuditTable.Requests, RequestRow("2026-12-01", 1, "page", 200)),
            new InsertOp("20261201", AuditTable.Requests, new Dictionary<string, object?> { ["no_such_column"] = 1, ["ts_utc"] = "x", ["day"] = "2026-12-01" }),
        ]);
        Assert.Equal(1, _writer.Status.Written);
        Assert.Equal(1, _writer.Status.Failed);
        Assert.True(File.Exists(_dbs.FileFor("2026-12")));
        Assert.Equal(1, await _dbs.For("2026-12").ExecuteCountAsync("SELECT COUNT(*) FROM requests_20261201;"));
    }

    [Fact]
    public async Task ReportRowCarriesTheWholeSummary()
    {
        VideoUrl.TryParse("jNQXAC9IVRw", out var url, out _);
        var job = new AnalysisJob("20261110-0badf00d", url!, Nlp.ModelCatalog.Profile("balanced")!, ClientInfo.System, fresh: false) { StartedAt = DateTimeOffset.UtcNow };
        var comments = new List<ScoredComment>
        {
            new() { Id = "a", Text = "great stuff", Sentiment = "positive", Score = 0.9, PPos = 0.9, Likes = 10, Lang = "en", LangName = "English", IsPinned = true },
            new() { Id = "b", Text = "awful audio", Sentiment = "negative", Score = -0.8, PNeg = 0.85, Likes = 2, Lang = "en", LangName = "English" },
        };
        var report = ReportBuilder.Build(new AnalysisFacts
        {
            Id = job.Id, Profile = job.Profile, AppVersion = "1.0.0", Video = new VideoInfo { Id = "jNQXAC9IVRw", Title = "Me at the zoo", Channel = "jawed", ViewCount = 10 },
            Fetch = new FetchSummary { Source = "yt-dlp", ToolVersion = "2026.10.26", Fetched = 2, Analysed = 2, Unique = 2 }, Comments = comments, Compute = new ComputeSummary { Threads = 2 },
        });
        job.Complete(report);
        var row = AuditWriter.Clean(AuditRecorder.ReportRow(job, report));
        Assert.Equal("completed", row["status"]);
        Assert.Equal("Me at the zoo", row["title"]);
        Assert.Equal(2, row["analysed"]);
        Assert.Equal("great stuff", row["pinned_text"]);
        Assert.Equal("great stuff", row["top_comment"]);
        Assert.Equal("en", row["primary_language"]);
        Assert.Equal("yt-dlp", row["source"]);
        Assert.Contains("positive", (string)row["insights"]);
        await _writer.WriteBatchAsync([
            new InsertOp("20261110", AuditTable.Analyses, new Dictionary<string, object?> { ["analysis_id"] = job.Id, ["day"] = "2026-11-10", ["created_utc"] = DateTime.UtcNow, ["status"] = "queued" }),
            new UpdateOp("20261110", AuditTable.Analyses, "analysis_id", job.Id, () => AuditRecorder.ReportRow(job, report)),
            new InsertOp("20261110", AuditTable.Comments, AuditRecorder.CommentRow(job.Id, "jNQXAC9IVRw", comments[0], DateTime.UtcNow)),
        ]);
        Assert.Equal(0, _writer.Status.Failed);
        var stored = await _reports.AnalysisAsync(job.Id);
        Assert.Equal("Me at the zoo", stored!["title"]);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }
}
