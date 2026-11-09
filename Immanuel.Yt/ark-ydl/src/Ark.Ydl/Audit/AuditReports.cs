using System.Globalization;
using System.Text;
using Ark.Rapid;

namespace Ark.Ydl.Audit;

/// <summary>One day's totals: a row of audit_daily, or the same figures computed live.</summary>
public sealed class DayTotals
{
    public string Day { get; set; } = "";
    public long Requests { get; set; }
    public long PageViews { get; set; }
    public long ApiCalls { get; set; }
    public long Probes { get; set; }
    public long ClientErrors { get; set; }
    public long ServerErrors { get; set; }
    public long Visitors { get; set; }
    public long Users { get; set; }
    public long Countries { get; set; }
    public long Downloads { get; set; }
    public long Completed { get; set; }
    public long Failed { get; set; }
    public long Canceled { get; set; }
    public long Bytes { get; set; }
    public long ServedCount { get; set; }
    public long ServedBytes { get; set; }
    public long Videos { get; set; }
    public long Downloaders { get; set; }
    public double AvgQueueMs { get; set; }
    public double AvgRunMs { get; set; }
    public double MediaSeconds { get; set; }
}

public sealed class KeyCount
{
    public string? Key { get; set; }
    public string? Label { get; set; }
    public long Count { get; set; }
    public long Bytes { get; set; }
    public double Avg { get; set; }
}

public sealed class TopVideo
{
    public string? VideoId { get; set; }
    public string? Title { get; set; }
    public string? Channel { get; set; }
    public string? Category { get; set; }
    public long Count { get; set; }
    public long Bytes { get; set; }
    public long Downloaders { get; set; }
}

public sealed class HeatCell
{
    public int Weekday { get; set; }
    public int Hour { get; set; }
    public long Count { get; set; }
}

public sealed record DateRange(string From, string To)
{
    public int Days => AuditClock.TryParseDay(From, out var f) && AuditClock.TryParseDay(To, out var t) ? t.DayNumber - f.DayNumber + 1 : 0;

    /// <summary>The same number of days just before this range — for "vs previous period".</summary>
    public DateRange Previous()
    {
        AuditClock.TryParseDay(From, out var f);
        var n = Math.Max(1, Days);
        return new DateRange(AuditClock.DayText(f.AddDays(-n)), AuditClock.DayText(f.AddDays(-1)));
    }

    /// <summary>Validated from/to (yyyy-MM-dd), defaulting to the last 30 days, at most 3 years long.</summary>
    public static DateRange Parse(string? from, string? to, string today, int defaultDays = 30)
    {
        AuditClock.TryParseDay(today, out var t0);
        var t = AuditClock.TryParseDay(to, out var tt) ? tt : t0;
        var f = AuditClock.TryParseDay(from, out var ff) ? ff : t.AddDays(-(defaultDays - 1));
        if (f > t) (f, t) = (t, f);
        if (t.DayNumber - f.DayNumber > 3 * 366) f = t.AddDays(-3 * 366);
        return new DateRange(AuditClock.DayText(f), AuditClock.DayText(t));
    }
}

public sealed class LogFilter
{
    public string? Q { get; init; }
    public string? Status { get; init; }
    public string? Mode { get; init; }
    public string? Kind { get; init; }
    public string? Category { get; init; }
    public string? Country { get; init; }
    public string? Ip { get; init; }
    public int Page { get; init; } = 1;
    public int Size { get; init; } = 50;
}

/// <summary>
/// Read-side of the audit log: every predefined report the admin console draws. Values reach SQL only through
/// GetSqlValueAsync; identifiers and expressions are constants in this file (Ark.Rapid.Database AI-SPEC.md §7).
/// Portable pieces (IFNULL, LIMIT) come from the manager's DatabaseUtil.
/// </summary>
public sealed class AuditReports(AuditWriter writer, AuditRecorder recorder)
{
    private ArkDbManager Db => writer.Db;

    public string Today => recorder.Clock.Today;

    // ── per-day totals ───────────────────────────────────────────────────────

    /// <summary>One day's totals from the raw rows (the rollup the maintenance job stores, and "today" live).</summary>
    public static async Task<DayTotals> ComputeDayAsync(ArkDbManager db, string day)
    {
        var u = db.Util;
        var d = await db.GetSqlValueAsync(day);
        var r = (await db.ExecuteSelectAsync<DayTotals>($@"
            SELECT COUNT(*) AS Requests,
                   {u.Qry_IfNull("SUM(CASE WHEN kind = 'page' THEN 1 ELSE 0 END)", "0")} AS PageViews,
                   {u.Qry_IfNull("SUM(CASE WHEN kind IN ('probe','job','job-status','events','file','zip','system','release','api') THEN 1 ELSE 0 END)", "0")} AS ApiCalls,
                   {u.Qry_IfNull("SUM(CASE WHEN kind = 'probe' THEN 1 ELSE 0 END)", "0")} AS Probes,
                   {u.Qry_IfNull("SUM(CASE WHEN status >= 400 AND status < 500 THEN 1 ELSE 0 END)", "0")} AS ClientErrors,
                   {u.Qry_IfNull("SUM(CASE WHEN status >= 500 THEN 1 ELSE 0 END)", "0")} AS ServerErrors,
                   COUNT(DISTINCT ip) AS Visitors,
                   COUNT(DISTINCT user_id) AS Users,
                   COUNT(DISTINCT ip_country) AS Countries
              FROM {AuditSchema.Requests} WHERE day = {d};")).First();
        var dl = (await db.ExecuteSelectAsync<DayTotals>($@"
            SELECT COUNT(*) AS Downloads,
                   {u.Qry_IfNull("SUM(CASE WHEN status = 'completed' THEN 1 ELSE 0 END)", "0")} AS Completed,
                   {u.Qry_IfNull("SUM(CASE WHEN status = 'failed' THEN 1 ELSE 0 END)", "0")} AS Failed,
                   {u.Qry_IfNull("SUM(CASE WHEN status = 'canceled' THEN 1 ELSE 0 END)", "0")} AS Canceled,
                   {u.Qry_IfNull(u.Qry_Sum("total_bytes"), "0")} AS Bytes,
                   {u.Qry_IfNull(u.Qry_Sum("served_count"), "0")} AS ServedCount,
                   {u.Qry_IfNull(u.Qry_Sum("served_bytes"), "0")} AS ServedBytes,
                   COUNT(DISTINCT video_id) AS Videos,
                   COUNT(DISTINCT client_key) AS Downloaders,
                   {u.Qry_IfNull("AVG(queue_ms)", "0.0")} AS AvgQueueMs,
                   {u.Qry_IfNull("AVG(run_ms)", "0.0")} AS AvgRunMs,
                   {u.Qry_IfNull($"SUM(CASE WHEN status = 'completed' THEN {u.Qry_IfNull("clip_seconds", "duration_sec")} ELSE 0 END)", "0.0")} AS MediaSeconds
              FROM {AuditSchema.Downloads} WHERE day = {d};")).First();
        r.Day = day;
        r.Downloads = dl.Downloads; r.Completed = dl.Completed; r.Failed = dl.Failed; r.Canceled = dl.Canceled;
        r.Bytes = dl.Bytes; r.ServedCount = dl.ServedCount; r.ServedBytes = dl.ServedBytes; r.Videos = dl.Videos;
        r.Downloaders = dl.Downloaders; r.AvgQueueMs = dl.AvgQueueMs; r.AvgRunMs = dl.AvgRunMs; r.MediaSeconds = dl.MediaSeconds;
        return r;
    }

    /// <summary>Recomputes and stores one day in audit_daily.</summary>
    public static async Task RollupAsync(ArkDbManager db, string day)
    {
        var t = await ComputeDayAsync(db, day);
        var row = new Dictionary<string, object?>
        {
            ["day"] = day, ["requests"] = t.Requests, ["page_views"] = t.PageViews, ["api_calls"] = t.ApiCalls, ["probes"] = t.Probes,
            ["client_errors"] = t.ClientErrors, ["server_errors"] = t.ServerErrors, ["visitors"] = t.Visitors, ["users"] = t.Users,
            ["countries"] = t.Countries, ["downloads"] = t.Downloads, ["completed"] = t.Completed, ["failed"] = t.Failed,
            ["canceled"] = t.Canceled, ["bytes"] = t.Bytes, ["served_count"] = t.ServedCount, ["served_bytes"] = t.ServedBytes,
            ["videos"] = t.Videos, ["downloaders"] = t.Downloaders, ["avg_queue_ms"] = t.AvgQueueMs, ["avg_run_ms"] = t.AvgRunMs,
            ["media_seconds"] = t.MediaSeconds, ["updated_utc"] = DateTime.UtcNow,
        };
        var insert = await AuditWriter.ToSqlAsync(db, new InsertOp(AuditSchema.Daily, row));
        await db.ExecuteAsync($"BEGIN; DELETE FROM {AuditSchema.Daily} WHERE day = {await db.GetSqlValueAsync(day)}; {insert} COMMIT;");
    }

    /// <summary>A row per day of the range (zeros where nothing happened); today is always live.</summary>
    public async Task<List<DayTotals>> DailyAsync(DateRange range)
    {
        var db = Db;
        var stored = (await db.ExecuteSelectAsync<DayTotals>($@"
            SELECT day AS Day, requests AS Requests, page_views AS PageViews, api_calls AS ApiCalls, probes AS Probes,
                   client_errors AS ClientErrors, server_errors AS ServerErrors, visitors AS Visitors, users AS Users,
                   countries AS Countries, downloads AS Downloads, completed AS Completed, failed AS Failed, canceled AS Canceled,
                   bytes AS Bytes, served_count AS ServedCount, served_bytes AS ServedBytes, videos AS Videos,
                   downloaders AS Downloaders, avg_queue_ms AS AvgQueueMs, avg_run_ms AS AvgRunMs, media_seconds AS MediaSeconds
              FROM {AuditSchema.Daily}
             WHERE day >= {await db.GetSqlValueAsync(range.From)} AND day <= {await db.GetSqlValueAsync(range.To)}
             ORDER BY day;")).ToDictionary(d => d.Day);
        var today = Today;
        if (string.CompareOrdinal(today, range.From) >= 0 && string.CompareOrdinal(today, range.To) <= 0)
            stored[today] = await ComputeDayAsync(db, today);

        var list = new List<DayTotals>();
        AuditClock.TryParseDay(range.From, out var f);
        AuditClock.TryParseDay(range.To, out var t);
        for (var d = f; d <= t; d = d.AddDays(1))
        {
            var key = AuditClock.DayText(d);
            list.Add(stored.TryGetValue(key, out var row) ? row : new DayTotals { Day = key });
        }
        return list;
    }

    public sealed class HourTotals
    {
        public string Day { get; set; } = "";
        public int Hour { get; set; }
        public long Requests { get; set; }
        public long Visitors { get; set; }
        public long Downloads { get; set; }
        public long Completed { get; set; }
        public long Failed { get; set; }
        public long Canceled { get; set; }
        public long Bytes { get; set; }
    }

    /// <summary>Every hour of a short range (the daily chart would be one or two bars), zeros included.</summary>
    public async Task<List<HourTotals>> HourlyAsync(DateRange range)
    {
        var db = Db;
        var u = db.Util;
        var where = await DayWhere(db, range);
        var req = (await db.ExecuteSelectAsync<HourTotals>($@"
            SELECT day AS Day, hour AS Hour, COUNT(*) AS Requests, COUNT(DISTINCT ip) AS Visitors
              FROM {AuditSchema.Requests} WHERE {where} GROUP BY day, hour;")).ToDictionary(h => (h.Day, h.Hour));
        var dl = (await db.ExecuteSelectAsync<HourTotals>($@"
            SELECT day AS Day, hour AS Hour, COUNT(*) AS Downloads,
                   {u.Qry_IfNull("SUM(CASE WHEN status = 'completed' THEN 1 ELSE 0 END)", "0")} AS Completed,
                   {u.Qry_IfNull("SUM(CASE WHEN status = 'failed' THEN 1 ELSE 0 END)", "0")} AS Failed,
                   {u.Qry_IfNull("SUM(CASE WHEN status = 'canceled' THEN 1 ELSE 0 END)", "0")} AS Canceled,
                   {u.Qry_IfNull(u.Qry_Sum("total_bytes"), "0")} AS Bytes
              FROM {AuditSchema.Downloads} WHERE {where} GROUP BY day, hour;")).ToDictionary(h => (h.Day, h.Hour));
        var list = new List<HourTotals>();
        AuditClock.TryParseDay(range.From, out var f);
        AuditClock.TryParseDay(range.To, out var t);
        for (var d = f; d <= t; d = d.AddDays(1))
        {
            var day = AuditClock.DayText(d);
            for (var h = 0; h < 24; h++)
            {
                req.TryGetValue((day, h), out var r);
                dl.TryGetValue((day, h), out var x);
                list.Add(new HourTotals
                {
                    Day = day, Hour = h, Requests = r?.Requests ?? 0, Visitors = r?.Visitors ?? 0,
                    Downloads = x?.Downloads ?? 0, Completed = x?.Completed ?? 0, Failed = x?.Failed ?? 0,
                    Canceled = x?.Canceled ?? 0, Bytes = x?.Bytes ?? 0,
                });
            }
        }
        return list;
    }

    // ── headline figures ─────────────────────────────────────────────────────

    public sealed class Kpis
    {
        public long Requests { get; set; }
        public long PageViews { get; set; }
        public long Probes { get; set; }
        public long Visitors { get; set; }
        public long Users { get; set; }
        public long Countries { get; set; }
        public long Downloads { get; set; }
        public long Completed { get; set; }
        public long Failed { get; set; }
        public long Canceled { get; set; }
        public long Bytes { get; set; }
        public long ServedCount { get; set; }
        public long ServedBytes { get; set; }
        public long Videos { get; set; }
        public long Downloaders { get; set; }
        public double AvgBytes { get; set; }
        public double AvgRunMs { get; set; }
        public double AvgQueueMs { get; set; }
        public double MediaSeconds { get; set; }
        public double SuccessRate => Completed + Failed == 0 ? 0 : (double)Completed / (Completed + Failed);
    }

    /// <summary>Range totals from the raw rows: distinct counts can't be summed from daily rows.</summary>
    public async Task<Kpis> KpisAsync(DateRange range)
    {
        var db = Db;
        var u = db.Util;
        var where = await DayWhere(db, range);
        var k = (await db.ExecuteSelectAsync<Kpis>($@"
            SELECT COUNT(*) AS Requests,
                   {u.Qry_IfNull("SUM(CASE WHEN kind = 'page' THEN 1 ELSE 0 END)", "0")} AS PageViews,
                   {u.Qry_IfNull("SUM(CASE WHEN kind = 'probe' THEN 1 ELSE 0 END)", "0")} AS Probes,
                   COUNT(DISTINCT ip) AS Visitors,
                   COUNT(DISTINCT user_id) AS Users,
                   COUNT(DISTINCT ip_country) AS Countries
              FROM {AuditSchema.Requests} WHERE {where};")).First();
        var d = (await db.ExecuteSelectAsync<Kpis>($@"
            SELECT COUNT(*) AS Downloads,
                   {u.Qry_IfNull("SUM(CASE WHEN status = 'completed' THEN 1 ELSE 0 END)", "0")} AS Completed,
                   {u.Qry_IfNull("SUM(CASE WHEN status = 'failed' THEN 1 ELSE 0 END)", "0")} AS Failed,
                   {u.Qry_IfNull("SUM(CASE WHEN status = 'canceled' THEN 1 ELSE 0 END)", "0")} AS Canceled,
                   {u.Qry_IfNull(u.Qry_Sum("total_bytes"), "0")} AS Bytes,
                   {u.Qry_IfNull(u.Qry_Sum("served_count"), "0")} AS ServedCount,
                   {u.Qry_IfNull(u.Qry_Sum("served_bytes"), "0")} AS ServedBytes,
                   COUNT(DISTINCT video_id) AS Videos,
                   COUNT(DISTINCT client_key) AS Downloaders,
                   {u.Qry_IfNull("AVG(CASE WHEN status = 'completed' THEN total_bytes END)", "0.0")} AS AvgBytes,
                   {u.Qry_IfNull("AVG(run_ms)", "0.0")} AS AvgRunMs,
                   {u.Qry_IfNull("AVG(queue_ms)", "0.0")} AS AvgQueueMs,
                   {u.Qry_IfNull($"SUM(CASE WHEN status = 'completed' THEN {u.Qry_IfNull("clip_seconds", "duration_sec")} ELSE 0 END)", "0.0")} AS MediaSeconds
              FROM {AuditSchema.Downloads} WHERE {where};")).First();
        d.Requests = k.Requests; d.PageViews = k.PageViews; d.Probes = k.Probes; d.Visitors = k.Visitors; d.Users = k.Users; d.Countries = k.Countries;
        return d;
    }

    // ── breakdowns ───────────────────────────────────────────────────────────

    private static async Task<string> DayWhere(ArkDbManager db, DateRange r) =>
        $"day >= {await db.GetSqlValueAsync(r.From)} AND day <= {await db.GetSqlValueAsync(r.To)}";

    /// <summary>COUNT(*) (or a distinct count) grouped by a constant expression, biggest first.</summary>
    private async Task<List<KeyCount>> TopAsync(string table, string keyExpr, DateRange range, int limit = 12,
        string? extraWhere = null, string countExpr = "COUNT(*)", string? labelExpr = null, string bytesExpr = "0", string avgExpr = "0.0")
    {
        var db = Db;
        var u = db.Util;
        var sql = $@"
            SELECT {keyExpr} AS Key, {labelExpr ?? "NULL"} AS Label, {countExpr} AS Count,
                   {u.Qry_IfNull(bytesExpr == "0" ? "0" : $"SUM({bytesExpr})", "0")} AS Bytes,
                   {u.Qry_IfNull(avgExpr == "0.0" ? "0.0" : $"AVG({avgExpr})", "0.0")} AS Avg
              FROM {table}
             WHERE {await DayWhere(db, range)}{(extraWhere is null ? "" : " AND " + extraWhere)}
             GROUP BY {keyExpr}
             ORDER BY Count DESC, Key
             {u.Qry_Limit(limit)};";
        return (await db.ExecuteSelectAsync<KeyCount>(sql)).ToList();
    }

    public async Task<Dictionary<string, object>> BreakdownsAsync(DateRange range)
    {
        var u = Db.Util;
        string Or(string col, string fallback) => u.Qry_IfNull(col, $"'{fallback}'");
        const string D = AuditSchema.Downloads, R = AuditSchema.Requests;
        const string visitors = "COUNT(DISTINCT ip)";
        const string humans = "is_bot = 0";

        var result = new Dictionary<string, object>
        {
            ["genres"] = await TopAsync(D, Or("category", "Unknown"), range, 15, bytesExpr: "total_bytes"),
            ["formats"] = await TopAsync(D, Or("format", "Unknown"), range, 12, bytesExpr: "total_bytes"),
            ["modes"] = await TopAsync(D, Or("mode", "unknown"), range, 6),
            ["scopes"] = await TopAsync(D, Or("scope", "whole"), range, 6),
            ["qualities"] = await TopAsync(D, "CAST(" + u.Qry_IfNull("quality", "0") + " AS TEXT)", range, 12, "mode = 'video'"),
            ["statuses"] = await TopAsync(D, Or("status", "unknown"), range, 6),
            ["channels"] = await TopAsync(D, "channel", range, 12, "channel IS NOT NULL", bytesExpr: "total_bytes"),
            ["countries"] = await TopAsync(D, Or("ip_country_name", "Unknown"), range, 15, labelExpr: "MAX(ip_country)"),
            ["cities"] = await TopAsync(D, Or("ip_city", "Unknown"), range, 15, "ip_city IS NOT NULL", labelExpr: "MAX(ip_country_name)"),
            ["visitorCountries"] = await TopAsync(R, Or("ip_country_name", "Unknown"), range, 15, humans, visitors, "MAX(ip_country)"),
            ["devices"] = await TopAsync(R, Or("device", "other"), range, 6, null, visitors),
            ["os"] = await TopAsync(R, Or("os", "Unknown"), range, 10, humans, visitors),
            ["browsers"] = await TopAsync(R, Or("browser", "Unknown"), range, 10, humans, visitors),
            ["languages"] = await TopAsync(R, Or("language", "Unknown"), range, 12, humans, visitors),
            ["timezones"] = await TopAsync(R, "client_tz", range, 12, "client_tz IS NOT NULL", visitors),
            ["referrers"] = await TopAsync(R, "referrer", range, 10, "referrer IS NOT NULL AND kind IN ('page','shortcut')", visitors),
            ["installed"] = await TopAsync(R, "CAST(client_pwa AS TEXT)", range, 3, "client_pwa IS NOT NULL", visitors),
            ["errors"] = await TopAsync(D, Or("error", "Unknown"), range, 10, "status = 'failed'"),
            ["requestKinds"] = await TopAsync(R, Or("kind", "other"), range, 16, avgExpr: "duration_ms"),
            ["statusCodes"] = await TopAsync(R, "CAST(status AS TEXT)", range, 16),
            ["sites"] = await TopAsync(D, Or("site", "Unknown"), range, 8),
            ["users"] = await TopAsync(D, "user_name", range, 10, "user_name IS NOT NULL", bytesExpr: "total_bytes"),
        };

        var db = Db;
        var where = await DayWhere(db, range);
        result["videos"] = (await db.ExecuteSelectAsync<TopVideo>($@"
            SELECT video_id AS VideoId, MAX(title) AS Title, MAX(channel) AS Channel, MAX(category) AS Category,
                   COUNT(*) AS Count, {u.Qry_IfNull(u.Qry_Sum("total_bytes"), "0")} AS Bytes, COUNT(DISTINCT client_key) AS Downloaders
              FROM {D} WHERE {where} AND video_id IS NOT NULL
             GROUP BY video_id ORDER BY Count DESC, Bytes DESC {u.Qry_Limit(15)};")).ToList();
        result["heatmap"] = (await db.ExecuteSelectAsync<HeatCell>($@"
            SELECT weekday AS Weekday, hour AS Hour, COUNT(*) AS Count
              FROM {D} WHERE {where} GROUP BY weekday, hour;")).ToList();
        result["requestHeatmap"] = (await db.ExecuteSelectAsync<HeatCell>($@"
            SELECT weekday AS Weekday, hour AS Hour, COUNT(*) AS Count
              FROM {R} WHERE {where} AND kind = 'page' GROUP BY weekday, hour;")).ToList();
        return result;
    }

    // ── logs ─────────────────────────────────────────────────────────────────

    private const string DownloadListColumns =
        "id, job_id, created_utc, finished_utc, status, title, channel, category, mode, format, scope, video_id, url, duration_sec, " +
        "total_bytes, file_count, item_count, served_count, ip, ip_country, ip_country_name, ip_city, device, browser, os, user_name, error, run_ms";

    private const string RequestListColumns =
        "id, ts_utc, method, path, query, kind, status, duration_ms, bytes, ip, ip_country, ip_country_name, ip_city, device, browser, os, " +
        "user_name, job_id, video_id, detail, user_agent, language, client_tz";

    public async Task<(long Total, IEnumerable<dynamic> Rows)> DownloadsAsync(DateRange range, LogFilter f)
    {
        var db = Db;
        var where = new List<string> { await DayWhere(db, range) };
        if (Pick(f.Status, "queued", "running", "completed", "failed", "canceled") is { } st) where.Add($"status = {await db.GetSqlValueAsync(st)}");
        if (Pick(f.Mode, "video", "audio", "subtitles", "thumbnail") is { } md) where.Add($"mode = {await db.GetSqlValueAsync(md)}");
        if (Clean(f.Category, 100) is { } cat) where.Add(cat == "Unknown" ? "category IS NULL" : $"category = {await db.GetSqlValueAsync(cat)}");
        if (Clean(f.Country, 100) is { } co) where.Add(co == "Unknown" ? "ip_country_name IS NULL" : $"ip_country_name = {await db.GetSqlValueAsync(co)}");
        if (Clean(f.Ip, 80) is { } ip) where.Add($"ip = {await db.GetSqlValueAsync(ip)}");
        if (Clean(f.Q, 200) is { } q)
        {
            var like = await db.GetSqlValueAsync($"%{q}%");
            where.Add($"(title LIKE {like} OR channel LIKE {like} OR video_id LIKE {like} OR job_id LIKE {like} OR url LIKE {like} " +
                      $"OR ip LIKE {like} OR user_name LIKE {like} OR error LIKE {like} OR category LIKE {like})");
        }
        return await PageAsync(db, AuditSchema.Downloads, DownloadListColumns, string.Join(" AND ", where), f);
    }

    public async Task<(long Total, IEnumerable<dynamic> Rows)> RequestsAsync(DateRange range, LogFilter f)
    {
        var db = Db;
        var where = new List<string> { await DayWhere(db, range) };
        if (Clean(f.Kind, 30) is { } kind) where.Add($"kind = {await db.GetSqlValueAsync(kind)}");
        if (Pick(f.Status, "2xx", "3xx", "4xx", "5xx") is { } cls)
        {
            var lo = (cls[0] - '0') * 100;
            where.Add($"status >= {lo} AND status < {lo + 100}");
        }
        if (Clean(f.Ip, 80) is { } ip) where.Add($"ip = {await db.GetSqlValueAsync(ip)}");
        if (Clean(f.Q, 200) is { } q)
        {
            var like = await db.GetSqlValueAsync($"%{q}%");
            where.Add($"(path LIKE {like} OR query LIKE {like} OR ip LIKE {like} OR user_agent LIKE {like} OR detail LIKE {like} " +
                      $"OR user_name LIKE {like} OR video_id LIKE {like} OR job_id LIKE {like})");
        }
        return await PageAsync(db, AuditSchema.Requests, RequestListColumns, string.Join(" AND ", where), f);
    }

    private static async Task<(long, IEnumerable<dynamic>)> PageAsync(ArkDbManager db, string table, string columns, string where, LogFilter f)
    {
        var size = Math.Clamp(f.Size, 10, 200);
        var page = Math.Max(1, f.Page);
        var total = await db.ExecuteCountAsync($"SELECT COUNT(*) FROM {table} WHERE {where};");
        IEnumerable<dynamic> rows = await db.SelectAsync(
            $"SELECT {columns} FROM {table} WHERE {where} ORDER BY id DESC {db.Util.Qry_Limit(size, (page - 1) * size)};");
        return (total, rows);
    }

    public async Task<IDictionary<string, object>?> DownloadAsync(string jobId)
    {
        var db = Db;
        IEnumerable<dynamic> rows = await db.SelectAsync(
            $"SELECT * FROM {AuditSchema.Downloads} WHERE job_id = {await db.GetSqlValueAsync(jobId)} {db.Util.Qry_Limit(1)};");
        var row = rows.FirstOrDefault() as IDictionary<string, object>;
        return row;
    }

    public async Task<List<IDictionary<string, object>>> RequestsForJobAsync(string jobId)
    {
        var db = Db;
        IEnumerable<dynamic> rows = await db.SelectAsync(
            $"SELECT {RequestListColumns} FROM {AuditSchema.Requests} WHERE job_id = {await db.GetSqlValueAsync(jobId)} ORDER BY id {db.Util.Qry_Limit(200)};");
        return rows.Cast<IDictionary<string, object>>().ToList();
    }

    /// <summary>Every column of every row in the range, as CSV (capped).</summary>
    public async Task<string> CsvAsync(string table, DateRange range, int max = 50_000)
    {
        var db = Db;
        IEnumerable<dynamic> rows = await db.SelectAsync(
            $"SELECT * FROM {table} WHERE {await DayWhere(db, range)} ORDER BY id {db.Util.Qry_Limit(max)};");
        var sb = new StringBuilder();
        var first = true;
        foreach (IDictionary<string, object> row in rows)
        {
            if (first)
            {
                sb.AppendJoin(',', row.Keys.Select(Csv)).Append("\r\n");
                first = false;
            }
            sb.AppendJoin(',', row.Values.Select(v => Csv(v switch
            {
                null => "",
                double x => x.ToString(CultureInfo.InvariantCulture),
                IFormattable fm => fm.ToString(null, CultureInfo.InvariantCulture),
                _ => v.ToString() ?? "",
            }))).Append("\r\n");
        }
        if (first) sb.Append("no rows\r\n");
        return sb.ToString();
    }

    /// <summary>RFC 4180 quoting, and a leading ' on anything a spreadsheet would run as a formula.</summary>
    internal static string Csv(string s)
    {
        if (s.Length > 0 && s[0] is '=' or '+' or '-' or '@' or '\t' or '\r' && !double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            s = "'" + s;
        return s.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    // ── store status ─────────────────────────────────────────────────────────

    public sealed class TableStats
    {
        public long Rows { get; set; }
        public string? First { get; set; }
        public string? Last { get; set; }
    }

    public async Task<Dictionary<string, TableStats>> TableStatsAsync()
    {
        var db = Db;
        var result = new Dictionary<string, TableStats>();
        foreach (var t in new[] { AuditSchema.Requests, AuditSchema.Downloads, AuditSchema.Daily })
            result[t] = (await db.ExecuteSelectAsync<TableStats>($"SELECT COUNT(*) AS Rows, MIN(day) AS First, MAX(day) AS Last FROM {t};")).First();
        return result;
    }

    private static string? Clean(string? s, int max)
    {
        s = s?.Trim();
        return string.IsNullOrEmpty(s) ? null : s.Length > max ? s[..max] : s;
    }

    private static string? Pick(string? s, params string[] allowed) =>
        allowed.FirstOrDefault(a => a.Equals(s?.Trim(), StringComparison.OrdinalIgnoreCase));
}
