using Ark.Rapid;

namespace Ark.Feedback.Audit;

public enum AuditTable { Requests, Analyses, Comments }

/// <summary>
/// The per-day tables, declared the Ark.Rapid.Database way (IMPLEMENTATION.md §3: one ColumnProp dictionary per table,
/// CREATE TABLE IF NOT EXISTS on first use). Every day gets its own requests_yyyyMMdd, analyses_yyyyMMdd and
/// comments_yyyyMMdd inside that month's file, plus two per-file tables: audit_meta (schema version) and audit_days (which
/// days exist, with row counts the maintenance job refreshes). Flags are INTEGER 0/1 (VALIDATION.md V9); timestamps are
/// TEXT "yyyy-MM-dd HH:mm:ss.fff" UTC; day / hour / weekday are pre-computed in the configured zone.
/// </summary>
public static class AuditSchema
{
    public const int Version = 1;
    public const string Meta = "audit_meta";
    public const string Days = "audit_days";

    public static string Name(AuditTable t, string day8) => t switch
    {
        AuditTable.Requests => "requests_" + day8,
        AuditTable.Analyses => "analyses_" + day8,
        _ => "comments_" + day8,
    };

    public static string Prefix(AuditTable t) => t switch { AuditTable.Requests => "requests_", AuditTable.Analyses => "analyses_", _ => "comments_" };

    private static ColumnProp Id(int seq) => new() { Seq = seq, DataType = typeof(long), Constraints = [Constraint.Primary_AutoIncrement] };
    private static ColumnProp Text(int seq, bool notNull = false) => new() { Seq = seq, DataType = typeof(string), Constraints = notNull ? [Constraint.NotNull] : [] };
    private static ColumnProp Int(int seq) => new() { Seq = seq, DataType = typeof(int), Constraints = [] };
    private static ColumnProp Long(int seq) => new() { Seq = seq, DataType = typeof(long), Constraints = [] };
    private static ColumnProp Real(int seq) => new() { Seq = seq, DataType = typeof(double), Constraints = [] };
    private static ColumnProp Zero(int seq, Type t) => new() { Seq = seq, DataType = t, Constraints = [Constraint.NotNull, Constraint.Default], Default = "0" };

    /// <summary>Who made a request / asked for an analysis, in the same order in every table.</summary>
    private static void AddClient(Dictionary<string, ColumnProp> c, int start)
    {
        var s = start;
        foreach (var name in new[]
                 {
                     "ip", "ip_country", "ip_country_name", "ip_region", "ip_city", "user_id", "user_name", "client_key", "user_agent", "browser", "browser_version",
                     "os", "os_version", "device",
                 })
            c[name] = Text(s += 10);
        c["is_bot"] = Int(s += 10);
        foreach (var name in new[] { "language", "accept_language", "referrer", "client_tz", "client_screen" }) c[name] = Text(s += 10);
        c["client_pwa"] = Int(s += 10);
        c["client_version"] = Text(s += 10);
    }

    /// <summary>One row per HTTP request.</summary>
    public static Dictionary<string, ColumnProp> RequestColumns
    {
        get
        {
            var c = new Dictionary<string, ColumnProp>
            {
                ["id"] = Id(0),
                ["ts_utc"] = Text(10, notNull: true),
                ["day"] = Text(20, notNull: true),
                ["hour"] = Int(30),
                ["weekday"] = Int(40),
                ["method"] = Text(50),
                ["path"] = Text(60),
                ["query"] = Text(70),
                ["kind"] = Text(80),
                ["status"] = Int(90),
                ["duration_ms"] = Long(100),
                ["bytes"] = Long(110),
                ["analysis_id"] = Text(120),
                ["video_id"] = Text(130),
                ["detail"] = Text(140),
                ["app_version"] = Text(150),
            };
            AddClient(c, 200);
            return c;
        }
    }

    /// <summary>One row per analysis: who asked, for which video, what came out, and what the asker thought of it.</summary>
    public static Dictionary<string, ColumnProp> AnalysisColumns
    {
        get
        {
            var c = new Dictionary<string, ColumnProp>
            {
                ["id"] = Id(0),
                ["analysis_id"] = new() { Seq = 10, DataType = typeof(string), Constraints = [Constraint.NotNull, Constraint.Unique] },
                ["day"] = Text(20, notNull: true),
                ["hour"] = Int(30),
                ["weekday"] = Int(40),
                ["created_utc"] = Text(50, notNull: true),
                ["started_utc"] = Text(60),
                ["finished_utc"] = Text(70),
                ["queue_ms"] = Long(80),
                ["run_ms"] = Long(90),
                ["fetch_ms"] = Long(100),
                ["infer_ms"] = Long(110),
                ["status"] = Text(120),
                ["error"] = Text(130),
                ["error_detail"] = Text(140),
                ["source"] = Text(150),
                ["tool_version"] = Text(160),
                ["profile"] = Text(170),
                ["fresh"] = Int(180),
                ["cached"] = Int(190),
                // the video
                ["url"] = Text(300),
                ["video_id"] = Text(310),
                ["title"] = Text(320),
                ["channel"] = Text(330),
                ["channel_id"] = Text(340),
                ["channel_followers"] = Long(350),
                ["duration_sec"] = Real(360),
                ["view_count"] = Long(370),
                ["like_count"] = Long(380),
                ["comment_count_total"] = Long(390),
                ["upload_date"] = Text(400),
                ["category"] = Text(410),
                ["tags"] = Text(420),
                ["video_language"] = Text(430),
                ["is_short"] = Int(440),
                // what was read
                ["fetched"] = Int(500),
                ["analysed"] = Int(510),
                ["unique_count"] = Int(520),
                ["repeats"] = Int(530),
                ["top_level"] = Int(540),
                ["replies"] = Int(550),
                ["sampled"] = Int(560),
                ["sample_note"] = Text(570),
                // what the sentiment is
                ["positive"] = Int(600),
                ["neutral"] = Int(610),
                ["negative"] = Int(620),
                ["positive_pct"] = Real(630),
                ["neutral_pct"] = Real(640),
                ["negative_pct"] = Real(650),
                ["score"] = Real(660),
                ["weighted_score"] = Real(670),
                ["mean_score"] = Real(680),
                ["polarization"] = Real(690),
                ["confidence"] = Real(700),
                ["verdict"] = Text(710),
                ["language_count"] = Int(720),
                ["primary_language"] = Text(730),
                ["primary_language_pct"] = Real(740),
                ["languages"] = Text(750),
                ["themes"] = Text(760),
                ["keywords_positive"] = Text(770),
                ["keywords_negative"] = Text(780),
                ["emoji"] = Text(790),
                ["toxic_count"] = Int(800),
                ["toxic_pct"] = Real(810),
                ["top_emotion"] = Text(820),
                ["emotions"] = Text(830),
                ["uploader_comments"] = Int(840),
                ["hearted"] = Int(850),
                ["pinned_text"] = Text(860),
                ["top_comment"] = Text(870),
                ["top_comment_likes"] = Long(880),
                ["insights"] = Text(890),
                ["models"] = Text(900),
                ["threads"] = Int(910),
                ["ms_per_comment"] = Real(920),
                ["app_version"] = Text(930),
                // did the asker like the report?
                ["liked"] = Int(1000),
                ["feedback_note"] = Text(1010),
                ["feedback_utc"] = Text(1020),
                ["feedback_count"] = Zero(1030, typeof(int)),
                ["report_views"] = Zero(1040, typeof(int)),
                ["last_viewed_utc"] = Text(1050),
                ["updated_utc"] = Text(1060),
            };
            AddClient(c, 1100);
            return c;
        }
    }

    /// <summary>One row per analysed comment (when Feedback:Analysis:StoreComments is on).</summary>
    public static Dictionary<string, ColumnProp> CommentColumns => new()
    {
        ["id"] = Id(0),
        ["analysis_id"] = Text(10, notNull: true),
        ["video_id"] = Text(20),
        ["comment_id"] = Text(30, notNull: true),
        ["parent_id"] = Text(40),
        ["is_reply"] = Int(50),
        ["author"] = Text(60),
        ["author_id"] = Text(70),
        ["author_is_uploader"] = Int(80),
        ["author_is_verified"] = Int(90),
        ["is_pinned"] = Int(100),
        ["is_hearted"] = Int(110),
        ["like_count"] = Long(120),
        ["published_utc"] = Text(130),
        ["text"] = Text(140),
        ["text_len"] = Int(150),
        ["lang"] = Text(160),
        ["lang_conf"] = Real(170),
        ["sentiment"] = Text(180),
        ["score"] = Real(190),
        ["p_pos"] = Real(200),
        ["p_neu"] = Real(210),
        ["p_neg"] = Real(220),
        ["toxic"] = Real(230),
        ["emotion"] = Text(240),
        ["emotion_p"] = Real(250),
        ["theme"] = Int(260),
        ["duplicate_of"] = Text(270),
        ["repeats"] = Int(280),
        ["created_utc"] = Text(290, notNull: true),
    };

    public static Dictionary<string, ColumnProp> DaysColumns => new()
    {
        ["day"] = new() { Seq = 0, DataType = typeof(string), Constraints = [Constraint.Primary] },
        ["created_utc"] = Text(10),
        ["requests"] = Zero(20, typeof(long)),
        ["analyses"] = Zero(30, typeof(long)),
        ["completed"] = Zero(40, typeof(long)),
        ["comments"] = Zero(50, typeof(long)),
        ["feedback"] = Zero(60, typeof(long)),
        ["liked"] = Zero(70, typeof(long)),
        ["updated_utc"] = Text(80),
    };

    public static Dictionary<string, ColumnProp> MetaColumns => new()
    {
        ["meta_key"] = new() { Seq = 0, DataType = typeof(string), Constraints = [Constraint.Primary] },
        ["meta_value"] = Text(10),
        ["updated_utc"] = Text(20),
    };

    /// <summary>Per-file tables. Idempotent (no IsTableExistAsync precheck — VALIDATION.md V4).</summary>
    public static async Task EnsureFileAsync(ArkDbManager db)
    {
        try { await db.ExecuteScalarAsync<string>("PRAGMA journal_mode=WAL;"); }
        catch (Microsoft.Data.Sqlite.SqliteException) { /* a file system without shared memory keeps the rollback journal */ }
        await db.CreateTableAsync(Meta, MetaColumns);
        await db.CreateTableAsync(Days, DaysColumns);
        if (await GetMetaAsync(db, "schema_version") is null)
        {
            await SetMetaAsync(db, "schema_version", Version.ToString());
            await SetMetaAsync(db, "created_utc", AuditClock.Format(DateTime.UtcNow));
        }
    }

    /// <summary>One day's three tables and their indexes, plus its row in audit_days.</summary>
    public static async Task EnsureDayAsync(ArkDbManager db, string day8)
    {
        if (!AuditClock.IsDay8(day8)) throw new ArgumentException($"'{day8}' is not a yyyyMMdd day.", nameof(day8));
        var r = Name(AuditTable.Requests, day8);
        var a = Name(AuditTable.Analyses, day8);
        var c = Name(AuditTable.Comments, day8);
        await db.CreateTableAsync(r, RequestColumns);
        await db.CreateTableAsync(a, AnalysisColumns);
        await db.CreateTableAsync(c, CommentColumns);
        foreach (var ix in new[]
                 {
                     $"CREATE INDEX IF NOT EXISTS ix_{r}_kind ON {r} (kind)",
                     $"CREATE INDEX IF NOT EXISTS ix_{r}_ip ON {r} (ip)",
                     $"CREATE INDEX IF NOT EXISTS ix_{r}_analysis ON {r} (analysis_id)",
                     $"CREATE INDEX IF NOT EXISTS ix_{a}_video ON {a} (video_id)",
                     $"CREATE INDEX IF NOT EXISTS ix_{a}_status ON {a} (status)",
                     $"CREATE INDEX IF NOT EXISTS ix_{a}_client ON {a} (client_key)",
                     $"CREATE INDEX IF NOT EXISTS ix_{c}_analysis ON {c} (analysis_id)",
                     $"CREATE INDEX IF NOT EXISTS ix_{c}_sentiment ON {c} (sentiment)",
                 })
            await db.ExecuteAsync(ix + ";");
        var day = await db.GetSqlValueAsync(AuditClock.DayOfDay8(day8));
        var n = await db.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM {Days} WHERE day = {day};");
        if (n == 0)
            await db.ExecuteAsync($"INSERT INTO {Days} (day, created_utc, updated_utc) VALUES ({day}, {await db.GetSqlValueAsync(AuditClock.Format(DateTime.UtcNow))}, {await db.GetSqlValueAsync(AuditClock.Format(DateTime.UtcNow))});");
    }

    /// <summary>Days ("yyyy-MM-dd") that have tables in this file, from sqlite_master, oldest first.</summary>
    public static async Task<List<string>> DaysInFileAsync(ArkDbManager db)
    {
        var names = await db.ExecuteSelectAsync<string>($"SELECT name FROM sqlite_master WHERE type = 'table' AND name LIKE 'analyses\\_%' ESCAPE '\\' ORDER BY name;");
        return names.Select(n => n["analyses_".Length..]).Where(AuditClock.IsDay8).Select(AuditClock.DayOfDay8).ToList();
    }

    public static async Task<string?> GetMetaAsync(ArkDbManager db, string key) =>
        await db.ExecuteScalarAsync<string?>($"SELECT meta_value FROM {Meta} WHERE meta_key = {await db.GetSqlValueAsync(key)};");

    public static async Task SetMetaAsync(ArkDbManager db, string key, string value)
    {
        var k = await db.GetSqlValueAsync(key);
        await db.ExecuteAsync(
            $"BEGIN; DELETE FROM {Meta} WHERE meta_key = {k}; " +
            $"INSERT INTO {Meta} (meta_key, meta_value, updated_utc) VALUES ({k}, {await db.GetSqlValueAsync(value)}, {await db.GetSqlValueAsync(AuditClock.Format(DateTime.UtcNow))}); COMMIT;");
    }
}
