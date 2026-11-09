using Ark.Rapid;

namespace Ark.Ydl.Audit;

/// <summary>
/// The audit tables, declared the Ark.Rapid.Database way (IMPLEMENTATION.md §3): one ColumnProp dictionary per table,
/// created with CREATE TABLE IF NOT EXISTS on every start. Flags are INTEGER 0/1 (not typeof(bool), which SQLite
/// would store as 'TRUE'/'FALSE' text — VALIDATION.md V9). Timestamps are TEXT "yyyy-MM-dd HH:mm:ss.fff" in UTC; the
/// day / hour / weekday columns are pre-computed in the configured time zone so reports group without date functions.
/// </summary>
public static class AuditSchema
{
    public const int Version = 1;

    public const string Requests = "audit_request";
    public const string Downloads = "audit_download";
    public const string Daily = "audit_daily";
    public const string Meta = "audit_meta";

    private static ColumnProp Id(int seq) => new() { Seq = seq, DataType = typeof(long), Constraints = [Constraint.Primary_AutoIncrement] };
    private static ColumnProp Text(int seq, bool notNull = false) => new() { Seq = seq, DataType = typeof(string), Constraints = notNull ? [Constraint.NotNull] : [] };
    private static ColumnProp Int(int seq) => new() { Seq = seq, DataType = typeof(int), Constraints = [] };
    private static ColumnProp Long(int seq) => new() { Seq = seq, DataType = typeof(long), Constraints = [] };
    private static ColumnProp Real(int seq) => new() { Seq = seq, DataType = typeof(double), Constraints = [] };
    private static ColumnProp Zero(int seq, Type t) => new() { Seq = seq, DataType = t, Constraints = [Constraint.NotNull, Constraint.Default], Default = "0" };

    /// <summary>Who made a request: shared by the request and download tables, in the same order.</summary>
    private static void AddClient(Dictionary<string, ColumnProp> c, int start)
    {
        var s = start;
        c["ip"] = Text(s += 10);
        c["ip_country"] = Text(s += 10);
        c["ip_country_name"] = Text(s += 10);
        c["ip_region"] = Text(s += 10);
        c["ip_city"] = Text(s += 10);
        c["user_id"] = Text(s += 10);
        c["user_name"] = Text(s += 10);
        c["client_key"] = Text(s += 10);
        c["user_agent"] = Text(s += 10);
        c["browser"] = Text(s += 10);
        c["browser_version"] = Text(s += 10);
        c["os"] = Text(s += 10);
        c["os_version"] = Text(s += 10);
        c["device"] = Text(s += 10);
        c["is_bot"] = Int(s += 10);
        c["language"] = Text(s += 10);
        c["accept_language"] = Text(s += 10);
        c["referrer"] = Text(s += 10);
        c["client_tz"] = Text(s += 10);
        c["client_screen"] = Text(s += 10);
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
                ["job_id"] = Text(120),
                ["video_id"] = Text(130),
                ["detail"] = Text(140),
                ["app_version"] = Text(150),
            };
            AddClient(c, 200);
            return c;
        }
    }

    /// <summary>One row per download job, inserted when it is queued and updated as it runs and is saved.</summary>
    public static Dictionary<string, ColumnProp> DownloadColumns
    {
        get
        {
            var c = new Dictionary<string, ColumnProp>
            {
                ["id"] = Id(0),
                ["job_id"] = new() { Seq = 10, DataType = typeof(string), Constraints = [Constraint.NotNull, Constraint.Unique] },
                ["day"] = Text(20, notNull: true),
                ["hour"] = Int(30),
                ["weekday"] = Int(40),
                ["created_utc"] = Text(50, notNull: true),
                ["started_utc"] = Text(60),
                ["finished_utc"] = Text(70),
                ["queue_ms"] = Long(80),
                ["run_ms"] = Long(90),
                ["status"] = Text(100),
                ["error"] = Text(110),
                ["error_detail"] = Text(120),
                // the link
                ["url"] = Text(200),
                ["site"] = Text(210),
                ["url_kind"] = Text(220),
                ["video_id"] = Text(230),
                ["list_id"] = Text(240),
                ["is_youtube"] = Int(250),
                ["is_music"] = Int(260),
                ["is_short"] = Int(270),
                ["live_status"] = Text(280),
                // what the video is
                ["title"] = Text(300),
                ["channel"] = Text(310),
                ["channel_id"] = Text(320),
                ["channel_url"] = Text(330),
                ["channel_followers"] = Long(340),
                ["duration_sec"] = Real(350),
                ["view_count"] = Long(360),
                ["like_count"] = Long(370),
                ["upload_date"] = Text(380),
                ["age_limit"] = Int(390),
                ["category"] = Text(400),
                ["categories"] = Text(410),
                ["genre"] = Text(420),
                ["tags"] = Text(430),
                ["video_language"] = Text(440),
                ["artist"] = Text(450),
                ["track"] = Text(460),
                ["album"] = Text(470),
                // what was asked for
                ["mode"] = Text(500),
                ["format"] = Text(510),
                ["scope"] = Text(520),
                ["quality"] = Int(530),
                ["codec"] = Text(540),
                ["container"] = Text(550),
                ["hdr"] = Int(560),
                ["audio_format"] = Text(570),
                ["audio_bitrate"] = Int(580),
                ["clip_sections"] = Text(590),
                ["clip_seconds"] = Real(600),
                ["playlist"] = Int(610),
                ["playlist_items"] = Int(620),
                ["playlist_count"] = Int(630),
                ["subtitles"] = Text(640),
                ["subtitle_langs"] = Text(650),
                ["sponsorblock"] = Text(660),
                ["embed"] = Text(670),
                ["file_name_style"] = Text(680),
                ["label"] = Text(690),
                ["estimated_bytes"] = Long(700),
                // what came out
                ["item_count"] = Int(800),
                ["items"] = Text(810),
                ["file_count"] = Int(820),
                ["total_bytes"] = Long(830),
                ["files"] = Text(840),
                ["served_count"] = Zero(850, typeof(int)),
                ["served_bytes"] = Zero(860, typeof(long)),
                ["first_served_utc"] = Text(870),
                ["last_served_utc"] = Text(880),
                ["app_version"] = Text(890),
                ["ytdlp_version"] = Text(900),
                ["updated_utc"] = Text(910),
            };
            AddClient(c, 1000);
            return c;
        }
    }

    /// <summary>One row per day, recomputed from the raw rows and kept after they are pruned.</summary>
    public static Dictionary<string, ColumnProp> DailyColumns => new()
    {
        ["day"] = new() { Seq = 0, DataType = typeof(string), Constraints = [Constraint.Primary] },
        ["requests"] = Long(10),
        ["page_views"] = Long(20),
        ["api_calls"] = Long(30),
        ["probes"] = Long(40),
        ["client_errors"] = Long(50),
        ["server_errors"] = Long(60),
        ["visitors"] = Long(70),
        ["users"] = Long(80),
        ["countries"] = Long(90),
        ["downloads"] = Long(100),
        ["completed"] = Long(110),
        ["failed"] = Long(120),
        ["canceled"] = Long(130),
        ["bytes"] = Long(140),
        ["served_count"] = Long(150),
        ["served_bytes"] = Long(160),
        ["videos"] = Long(170),
        ["downloaders"] = Long(180),
        ["avg_queue_ms"] = Real(190),
        ["avg_run_ms"] = Real(200),
        ["media_seconds"] = Real(210),
        ["updated_utc"] = Text(220),
    };

    public static Dictionary<string, ColumnProp> MetaColumns => new()
    {
        ["meta_key"] = new() { Seq = 0, DataType = typeof(string), Constraints = [Constraint.Primary] },
        ["meta_value"] = Text(10),
        ["updated_utc"] = Text(20),
    };

    private static readonly string[] Indexes =
    [
        $"CREATE INDEX IF NOT EXISTS ix_{Requests}_day ON {Requests} (day)",
        $"CREATE INDEX IF NOT EXISTS ix_{Requests}_day_kind ON {Requests} (day, kind)",
        $"CREATE INDEX IF NOT EXISTS ix_{Requests}_ip ON {Requests} (ip)",
        $"CREATE INDEX IF NOT EXISTS ix_{Requests}_job ON {Requests} (job_id)",
        $"CREATE INDEX IF NOT EXISTS ix_{Downloads}_day ON {Downloads} (day)",
        $"CREATE INDEX IF NOT EXISTS ix_{Downloads}_day_status ON {Downloads} (day, status)",
        $"CREATE INDEX IF NOT EXISTS ix_{Downloads}_video ON {Downloads} (video_id)",
        $"CREATE INDEX IF NOT EXISTS ix_{Downloads}_ip ON {Downloads} (ip)",
    ];

    /// <summary>Idempotent: safe on every start (IMPLEMENTATION.md §4 — no IsTableExistAsync precheck, V4).</summary>
    public static async Task EnsureCreatedAsync(ArkDbManager db)
    {
        if (db is SqliteManager)
        {
            // WAL lets the admin console read while the writer writes. Persistent per file; a file system without
            // shared memory (some network shares) keeps the rollback journal, which still works, just with more waiting.
            try { await db.ExecuteScalarAsync<string>("PRAGMA journal_mode=WAL;"); }
            catch (Microsoft.Data.Sqlite.SqliteException) { }
        }
        await db.CreateTableAsync(Requests, RequestColumns);
        await db.CreateTableAsync(Downloads, DownloadColumns);
        await db.CreateTableAsync(Daily, DailyColumns);
        await db.CreateTableAsync(Meta, MetaColumns);
        foreach (var ix in Indexes) await db.ExecuteAsync(ix + ";");
        await MigrateAsync(db);
    }

    /// <summary>
    /// Ordered, one-way schema steps (IMPLEMENTATION.md §5.1). Append new steps; never edit a shipped one.
    /// Column existence is checked exactly (pragma_table_info) rather than with IsColumnExistAsync, whose SQLite
    /// implementation is a substring match on the CREATE TABLE text (AI-SPEC.md §5) and would see "ip" inside "ip_city".
    /// </summary>
    private static async Task MigrateAsync(ArkDbManager db)
    {
        var current = await GetMetaAsync(db, "schema_version");
        if (current is null)
        {
            await SetMetaAsync(db, "schema_version", Version.ToString());
            await SetMetaAsync(db, "created_utc", AuditClock.Format(DateTime.UtcNow));
            return;
        }
        // v1 is the first schema; later steps go here, e.g.
        // if (int.Parse(current) < 2) { await AddColumnIfMissingAsync(db, Downloads, "x", new ColumnProp {...}); await SetMetaAsync(db, "schema_version", "2"); }
    }

    internal static async Task AddColumnIfMissingAsync(ArkDbManager db, string table, string column, ColumnProp prop)
    {
        if (db is SqliteManager)
        {
            var n = await db.ExecuteScalarAsync<long>(
                $"SELECT COUNT(*) FROM pragma_table_info({await db.GetSqlValueAsync(table)}) WHERE name = {await db.GetSqlValueAsync(column)};");
            if (n > 0) return;
        }
        await db.AddColumnAsync(table, column, prop);
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
