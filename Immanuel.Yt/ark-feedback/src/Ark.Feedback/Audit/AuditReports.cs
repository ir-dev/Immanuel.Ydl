using System.Globalization;
using System.Text;
using Ark.Rapid;

namespace Ark.Feedback.Audit;

public sealed record DateRange(string From, string To)
{
    public int Days => AuditClock.TryParseDay(From, out var f) && AuditClock.TryParseDay(To, out var t) ? t.DayNumber - f.DayNumber + 1 : 0;

    public DateRange Previous()
    {
        AuditClock.TryParseDay(From, out var f);
        var n = Math.Max(1, Days);
        return new DateRange(AuditClock.DayText(f.AddDays(-n)), AuditClock.DayText(f.AddDays(-1)));
    }

    /// <summary>Validated from/to (yyyy-MM-dd), defaulting to the last 30 days, at most 400 days long (one query per day table).</summary>
    public static DateRange Parse(string? from, string? to, string today, int defaultDays = 30)
    {
        AuditClock.TryParseDay(today, out var t0);
        var t = AuditClock.TryParseDay(to, out var tt) ? tt : t0;
        var f = AuditClock.TryParseDay(from, out var ff) ? ff : t.AddDays(-(defaultDays - 1));
        if (f > t) (f, t) = (t, f);
        if (t.DayNumber - f.DayNumber > 400) f = t.AddDays(-400);
        return new DateRange(AuditClock.DayText(f), AuditClock.DayText(t));
    }

    public IEnumerable<string> EachDay()
    {
        AuditClock.TryParseDay(From, out var f);
        AuditClock.TryParseDay(To, out var t);
        for (var d = f; d <= t; d = d.AddDays(1)) yield return AuditClock.DayText(d);
    }
}

public sealed class DayTotals
{
    public string Day { get; set; } = "";
    public long Requests { get; set; }
    public long PageViews { get; set; }
    public long Visitors { get; set; }
    public long Analyses { get; set; }
    public long Completed { get; set; }
    public long Failed { get; set; }
    public long Canceled { get; set; }
    public long Comments { get; set; }
    public long Videos { get; set; }
    public long Askers { get; set; }
    public long Countries { get; set; }
    public long Liked { get; set; }
    public long Disliked { get; set; }
    public long Views { get; set; }
    public double AvgRunMs { get; set; }
    public double AvgScore { get; set; }
    public long Positive { get; set; }
    public long Negative { get; set; }
    public long Neutral { get; set; }
}

public sealed class KeyCount
{
    public string? Key { get; set; }
    public string? Label { get; set; }
    public long Count { get; set; }
    public double Avg { get; set; }
}

public sealed class LogFilter
{
    public string? Q { get; init; }
    public string? Status { get; init; }
    public string? Verdict { get; init; }
    public string? Profile { get; init; }
    public string? Kind { get; init; }
    public string? Country { get; init; }
    public string? Ip { get; init; }
    public string? Liked { get; init; }
    public int Page { get; init; } = 1;
    public int Size { get; init; } = 50;
}

/// <summary>
/// Read side of the audit log for the admin console. Every figure comes from the per-day tables of the month files
/// in the range; ranges are walked day by day (one small query per table) and summed here, which keeps every query
/// trivial and lets a month be missing without anything breaking. Values reach SQL only through GetSqlValueAsync;
/// identifiers are constants of this file (Ark.Rapid.Database AI-SPEC.md §7).
/// </summary>
public sealed class AuditReports(AuditDatabases dbs, AuditClock clock)
{
    public string Today => clock.Today;

    /// <summary>Days in the range that have tables, with the month manager to query them.</summary>
    private async Task<List<(string Day, string Day8, ArkDbManager Db)>> DaysAsync(DateRange range)
    {
        var result = new List<(string, string, ArkDbManager)>();
        var wanted = new HashSet<string>(range.EachDay(), StringComparer.Ordinal);
        foreach (var month in AuditDatabases.MonthsBetween(range.From, range.To))
        {
            if (!dbs.Exists(month)) continue;
            var db = dbs.For(month);
            foreach (var day in await AuditSchema.DaysInFileAsync(db))
                if (wanted.Contains(day)) result.Add((day, AuditClock.Day8OfDay(day), db));
        }
        return result.OrderBy(d => d.Item1, StringComparer.Ordinal).ToList();
    }

    // ── per-day totals ───────────────────────────────────────────────────────

    public static async Task<DayTotals> ComputeDayAsync(ArkDbManager db, string day, string day8)
    {
        var u = db.Util;
        var r = AuditSchema.Name(AuditTable.Requests, day8);
        var a = AuditSchema.Name(AuditTable.Analyses, day8);
        var c = AuditSchema.Name(AuditTable.Comments, day8);
        var req = (await db.ExecuteSelectAsync<DayTotals>($@"
            SELECT COUNT(*) AS Requests,
                   {u.Qry_IfNull("SUM(CASE WHEN kind IN ('page','report-page') THEN 1 ELSE 0 END)", "0")} AS PageViews,
                   COUNT(DISTINCT ip) AS Visitors
              FROM {r};")).First();
        var an = (await db.ExecuteSelectAsync<DayTotals>($@"
            SELECT COUNT(*) AS Analyses,
                   {u.Qry_IfNull("SUM(CASE WHEN status = 'completed' THEN 1 ELSE 0 END)", "0")} AS Completed,
                   {u.Qry_IfNull("SUM(CASE WHEN status = 'failed' THEN 1 ELSE 0 END)", "0")} AS Failed,
                   {u.Qry_IfNull("SUM(CASE WHEN status = 'canceled' THEN 1 ELSE 0 END)", "0")} AS Canceled,
                   COUNT(DISTINCT video_id) AS Videos,
                   COUNT(DISTINCT client_key) AS Askers,
                   COUNT(DISTINCT ip_country) AS Countries,
                   {u.Qry_IfNull("SUM(CASE WHEN liked = 1 THEN 1 ELSE 0 END)", "0")} AS Liked,
                   {u.Qry_IfNull("SUM(CASE WHEN liked = 0 THEN 1 ELSE 0 END)", "0")} AS Disliked,
                   {u.Qry_IfNull(u.Qry_Sum("report_views"), "0")} AS Views,
                   {u.Qry_IfNull("AVG(CASE WHEN status = 'completed' THEN run_ms END)", "0.0")} AS AvgRunMs,
                   {u.Qry_IfNull("AVG(CASE WHEN status = 'completed' THEN score END)", "0.0")} AS AvgScore,
                   {u.Qry_IfNull(u.Qry_Sum("positive"), "0")} AS Positive,
                   {u.Qry_IfNull(u.Qry_Sum("negative"), "0")} AS Negative,
                   {u.Qry_IfNull(u.Qry_Sum("neutral"), "0")} AS Neutral
              FROM {a};")).First();
        an.Comments = await db.ExecuteCountAsync($"SELECT COUNT(*) FROM {c};");
        an.Day = day;
        an.Requests = req.Requests; an.PageViews = req.PageViews; an.Visitors = req.Visitors;
        return an;
    }

    /// <summary>A row per day of the range, zeros where nothing happened.</summary>
    public async Task<List<DayTotals>> DailyAsync(DateRange range)
    {
        var have = (await DaysAsync(range)).ToDictionary(d => d.Day, StringComparer.Ordinal);
        var list = new List<DayTotals>();
        foreach (var day in range.EachDay())
            list.Add(have.TryGetValue(day, out var d) ? await ComputeDayAsync(d.Db, d.Day, d.Day8) : new DayTotals { Day = day });
        return list;
    }

    public sealed class Kpis
    {
        public long Requests { get; set; }
        public long PageViews { get; set; }
        public long Visitors { get; set; }
        public long Analyses { get; set; }
        public long Completed { get; set; }
        public long Failed { get; set; }
        public long Canceled { get; set; }
        public long Comments { get; set; }
        public long Videos { get; set; }
        public long Askers { get; set; }
        public long Countries { get; set; }
        public long Liked { get; set; }
        public long Disliked { get; set; }
        public long Views { get; set; }
        public double AvgRunMs { get; set; }
        public double AvgScore { get; set; }
        public long Positive { get; set; }
        public long Neutral { get; set; }
        public long Negative { get; set; }
        public double SuccessRate => Completed + Failed == 0 ? 0 : (double)Completed / (Completed + Failed);
        public double LikeRate => Liked + Disliked == 0 ? 0 : (double)Liked / (Liked + Disliked);
    }

    /// <summary>Range totals. Distinct counts (visitors, videos, askers) are unions across the days' tables.</summary>
    public async Task<Kpis> KpisAsync(DateRange range)
    {
        var k = new Kpis();
        var visitors = new HashSet<string>(StringComparer.Ordinal);
        var videos = new HashSet<string>(StringComparer.Ordinal);
        var askers = new HashSet<string>(StringComparer.Ordinal);
        var countries = new HashSet<string>(StringComparer.Ordinal);
        double runSum = 0, scoreSum = 0;
        long runN = 0, scoreN = 0;
        foreach (var (day, day8, db) in await DaysAsync(range))
        {
            var t = await ComputeDayAsync(db, day, day8);
            k.Requests += t.Requests; k.PageViews += t.PageViews; k.Analyses += t.Analyses; k.Completed += t.Completed; k.Failed += t.Failed; k.Canceled += t.Canceled;
            k.Comments += t.Comments; k.Liked += t.Liked; k.Disliked += t.Disliked; k.Views += t.Views; k.Positive += t.Positive; k.Neutral += t.Neutral; k.Negative += t.Negative;
            runSum += t.AvgRunMs * t.Completed; runN += t.Completed; scoreSum += t.AvgScore * t.Completed; scoreN += t.Completed;
            var a = AuditSchema.Name(AuditTable.Analyses, day8);
            var r = AuditSchema.Name(AuditTable.Requests, day8);
            foreach (var v in await db.ExecuteSelectAsync<string>($"SELECT DISTINCT ip FROM {r} WHERE ip IS NOT NULL;")) visitors.Add(v);
            foreach (var v in await db.ExecuteSelectAsync<string>($"SELECT DISTINCT video_id FROM {a} WHERE video_id IS NOT NULL;")) videos.Add(v);
            foreach (var v in await db.ExecuteSelectAsync<string>($"SELECT DISTINCT client_key FROM {a} WHERE client_key IS NOT NULL;")) askers.Add(v);
            foreach (var v in await db.ExecuteSelectAsync<string>($"SELECT DISTINCT ip_country FROM {a} WHERE ip_country IS NOT NULL;")) countries.Add(v);
        }
        k.Visitors = visitors.Count; k.Videos = videos.Count; k.Askers = askers.Count; k.Countries = countries.Count;
        k.AvgRunMs = runN == 0 ? 0 : runSum / runN;
        k.AvgScore = scoreN == 0 ? 0 : scoreSum / scoreN;
        return k;
    }

    // ── breakdowns ───────────────────────────────────────────────────────────

    /// <summary>COUNT(*) grouped by a constant expression, summed over the range's day tables, biggest first.</summary>
    private async Task<List<KeyCount>> TopAsync(AuditTable table, string keyExpr, DateRange range, int limit = 12, string? where = null, string countExpr = "COUNT(*)", string? avgExpr = null)
    {
        var acc = new Dictionary<string, (long Count, double AvgSum)>(StringComparer.Ordinal);
        foreach (var (_, day8, db) in await DaysAsync(range))
        {
            var u = db.Util;
            var sql = $"SELECT {keyExpr} AS Key, {countExpr} AS Count, {(avgExpr is null ? "0.0" : u.Qry_IfNull($"AVG({avgExpr})", "0.0"))} AS Avg FROM {AuditSchema.Name(table, day8)}" +
                      (where is null ? "" : $" WHERE {where}") + $" GROUP BY {keyExpr};";
            foreach (var row in await db.ExecuteSelectAsync<KeyCount>(sql))
            {
                var key = row.Key ?? "Unknown";
                var cur = acc.GetValueOrDefault(key);
                acc[key] = (cur.Count + row.Count, cur.AvgSum + row.Avg * row.Count);
            }
        }
        return acc.Select(kv => new KeyCount { Key = kv.Key, Count = kv.Value.Count, Avg = kv.Value.Count == 0 ? 0 : kv.Value.AvgSum / kv.Value.Count })
            .OrderByDescending(k => k.Count).ThenBy(k => k.Key, StringComparer.Ordinal).Take(limit).ToList();
    }

    public sealed class TopVideo
    {
        public string? VideoId { get; set; }
        public string? Title { get; set; }
        public string? Channel { get; set; }
        public long Count { get; set; }
        public double Score { get; set; }
        public string? Verdict { get; set; }
        public long Askers { get; set; }
    }

    public sealed class HeatCell
    {
        public int Weekday { get; set; }
        public int Hour { get; set; }
        public long Count { get; set; }
    }

    public async Task<Dictionary<string, object>> BreakdownsAsync(DateRange range)
    {
        const AuditTable A = AuditTable.Analyses, R = AuditTable.Requests, C = AuditTable.Comments;
        string Or(string col, string fallback) => $"COALESCE({col}, '{fallback}')";
        var result = new Dictionary<string, object>
        {
            ["verdicts"] = await TopAsync(A, Or("verdict", "none"), range, 12, "status = 'completed'"),
            ["profiles"] = await TopAsync(A, Or("profile", "unknown"), range, 6, null, "COUNT(*)", "run_ms"),
            ["statuses"] = await TopAsync(A, Or("status", "unknown"), range, 8),
            ["sources"] = await TopAsync(A, Or("source", "unknown"), range, 6, "status = 'completed'", "COUNT(*)", "fetch_ms"),
            ["videoLanguages"] = await TopAsync(A, Or("primary_language", "und"), range, 15, "status = 'completed'", "COUNT(*)", "score"),
            ["commentLanguages"] = await TopAsync(C, Or("lang", "und"), range, 15, null, "COUNT(*)", "score"),
            ["commentSentiment"] = await TopAsync(C, Or("sentiment", "unknown"), range, 4),
            ["emotions"] = await TopAsync(C, "emotion", range, 12, "emotion IS NOT NULL"),
            ["categories"] = await TopAsync(A, Or("category", "Unknown"), range, 12, "status = 'completed'", "COUNT(*)", "score"),
            ["channels"] = await TopAsync(A, "channel", range, 12, "channel IS NOT NULL", "COUNT(*)", "score"),
            ["countries"] = await TopAsync(A, Or("ip_country_name", "Unknown"), range, 15),
            ["visitorCountries"] = await TopAsync(R, Or("ip_country_name", "Unknown"), range, 15, "is_bot = 0", "COUNT(DISTINCT ip)"),
            ["devices"] = await TopAsync(R, Or("device", "other"), range, 6, null, "COUNT(DISTINCT ip)"),
            ["browsers"] = await TopAsync(R, Or("browser", "Unknown"), range, 10, "is_bot = 0", "COUNT(DISTINCT ip)"),
            ["os"] = await TopAsync(R, Or("os", "Unknown"), range, 10, "is_bot = 0", "COUNT(DISTINCT ip)"),
            ["referrers"] = await TopAsync(R, "referrer", range, 10, "referrer IS NOT NULL AND kind IN ('page','report-page')", "COUNT(DISTINCT ip)"),
            ["requestKinds"] = await TopAsync(R, Or("kind", "other"), range, 16, null, "COUNT(*)", "duration_ms"),
            ["statusCodes"] = await TopAsync(R, "CAST(status AS TEXT)", range, 16),
            ["errors"] = await TopAsync(A, Or("error", "Unknown"), range, 10, "status = 'failed'"),
            ["feedback"] = await TopAsync(A, "CASE liked WHEN 1 THEN 'liked' WHEN 0 THEN 'disliked' ELSE 'no answer' END", range, 3, "status = 'completed'"),
            ["users"] = await TopAsync(A, "user_name", range, 10, "user_name IS NOT NULL"),
        };

        var videos = new Dictionary<string, TopVideo>(StringComparer.Ordinal);
        var heat = new Dictionary<(int, int), long>();
        var reqHeat = new Dictionary<(int, int), long>();
        foreach (var (_, day8, db) in await DaysAsync(range))
        {
            var a = AuditSchema.Name(A, day8);
            foreach (var v in await db.ExecuteSelectAsync<TopVideo>($@"
                SELECT video_id AS VideoId, MAX(title) AS Title, MAX(channel) AS Channel, COUNT(*) AS Count,
                       {db.Util.Qry_IfNull("AVG(score)", "0.0")} AS Score, MAX(verdict) AS Verdict, COUNT(DISTINCT client_key) AS Askers
                  FROM {a} WHERE status = 'completed' AND video_id IS NOT NULL GROUP BY video_id;"))
            {
                if (v.VideoId is null) continue;
                if (videos.TryGetValue(v.VideoId, out var cur))
                {
                    cur.Score = (cur.Score * cur.Count + v.Score * v.Count) / (cur.Count + v.Count);
                    cur.Count += v.Count; cur.Askers += v.Askers; cur.Title ??= v.Title; cur.Channel ??= v.Channel; cur.Verdict = v.Verdict ?? cur.Verdict;
                }
                else videos[v.VideoId] = v;
            }
            foreach (var h in await db.ExecuteSelectAsync<HeatCell>($"SELECT weekday AS Weekday, hour AS Hour, COUNT(*) AS Count FROM {a} GROUP BY weekday, hour;"))
                heat[(h.Weekday, h.Hour)] = heat.GetValueOrDefault((h.Weekday, h.Hour)) + h.Count;
            foreach (var h in await db.ExecuteSelectAsync<HeatCell>($"SELECT weekday AS Weekday, hour AS Hour, COUNT(*) AS Count FROM {AuditSchema.Name(R, day8)} WHERE kind IN ('page','report-page') GROUP BY weekday, hour;"))
                reqHeat[(h.Weekday, h.Hour)] = reqHeat.GetValueOrDefault((h.Weekday, h.Hour)) + h.Count;
        }
        result["videos"] = videos.Values.OrderByDescending(v => v.Count).ThenByDescending(v => v.Askers).Take(15).ToList();
        result["heatmap"] = heat.Select(kv => new HeatCell { Weekday = kv.Key.Item1, Hour = kv.Key.Item2, Count = kv.Value }).ToList();
        result["requestHeatmap"] = reqHeat.Select(kv => new HeatCell { Weekday = kv.Key.Item1, Hour = kv.Key.Item2, Count = kv.Value }).ToList();
        return result;
    }

    // ── logs ─────────────────────────────────────────────────────────────────

    private const string AnalysisListColumns =
        "id, analysis_id, created_utc, finished_utc, status, profile, source, title, channel, video_id, url, analysed, fetched, score, weighted_score, verdict, " +
        "positive_pct, neutral_pct, negative_pct, primary_language, language_count, toxic_pct, top_emotion, run_ms, error, liked, feedback_note, feedback_utc, report_views, " +
        "ip, ip_country, ip_country_name, ip_city, device, browser, os, user_name, client_key, sampled, cached";

    private const string RequestListColumns =
        "id, ts_utc, method, path, query, kind, status, duration_ms, bytes, ip, ip_country, ip_country_name, ip_city, device, browser, os, user_name, analysis_id, video_id, detail, user_agent, language, client_tz";

    private const string CommentListColumns =
        "id, analysis_id, comment_id, parent_id, is_reply, author, author_is_uploader, is_pinned, is_hearted, like_count, published_utc, text, lang, sentiment, score, p_pos, p_neu, p_neg, toxic, emotion, theme, duplicate_of, repeats";

    /// <summary>Newest first across every day table in the range; each table contributes at most what a page could show, then the pages are merged here.</summary>
    private async Task<(long Total, List<IDictionary<string, object>> Rows)> PageAsync(AuditTable table, string columns, DateRange range, Func<ArkDbManager, Task<string>> where, string orderCol, LogFilter f)
    {
        var size = Math.Clamp(f.Size, 10, 200);
        var page = Math.Max(1, f.Page);
        var want = page * size;
        long total = 0;
        var rows = new List<IDictionary<string, object>>();
        foreach (var (_, day8, db) in (await DaysAsync(range)).OrderByDescending(d => d.Day, StringComparer.Ordinal))
        {
            var t = AuditSchema.Name(table, day8);
            var w = await where(db);
            total += await db.ExecuteCountAsync($"SELECT COUNT(*) FROM {t} WHERE {w};");
            if (rows.Count >= want) continue; // older days can't reach this page, but their count still matters
            IEnumerable<dynamic> part = await db.SelectAsync($"SELECT {columns} FROM {t} WHERE {w} ORDER BY {orderCol} DESC {db.Util.Qry_Limit(want)};");
            rows.AddRange(part.Cast<IDictionary<string, object>>());
        }
        var pageRows = rows.OrderByDescending(r => r.TryGetValue(orderCol, out var v) ? v?.ToString() : null, StringComparer.Ordinal).Skip((page - 1) * size).Take(size).ToList();
        return (total, pageRows);
    }

    public Task<(long Total, List<IDictionary<string, object>> Rows)> AnalysesAsync(DateRange range, LogFilter f) =>
        PageAsync(AuditTable.Analyses, AnalysisListColumns, range, async db =>
        {
            var where = new List<string> { "1 = 1" };
            if (Pick(f.Status, "queued", "fetching", "analysing", "completed", "failed", "canceled") is { } st) where.Add($"status = {await db.GetSqlValueAsync(st)}");
            if (Clean(f.Verdict, 30) is { } vd) where.Add($"verdict = {await db.GetSqlValueAsync(vd)}");
            if (Pick(f.Profile, "fast", "balanced", "thorough") is { } pf) where.Add($"profile = {await db.GetSqlValueAsync(pf)}");
            if (Clean(f.Country, 100) is { } co) where.Add(co == "Unknown" ? "ip_country_name IS NULL" : $"ip_country_name = {await db.GetSqlValueAsync(co)}");
            if (Clean(f.Ip, 80) is { } ip) where.Add($"ip = {await db.GetSqlValueAsync(ip)}");
            if (Pick(f.Liked, "liked", "disliked", "any") is { } lk) where.Add(lk == "liked" ? "liked = 1" : lk == "disliked" ? "liked = 0" : "liked IS NOT NULL");
            if (Clean(f.Q, 200) is { } q)
            {
                var like = await db.GetSqlValueAsync($"%{q}%");
                where.Add($"(title LIKE {like} OR channel LIKE {like} OR video_id LIKE {like} OR analysis_id LIKE {like} OR url LIKE {like} OR ip LIKE {like} OR user_name LIKE {like} OR error LIKE {like} OR feedback_note LIKE {like})");
            }
            return string.Join(" AND ", where);
        }, "created_utc", f);

    public Task<(long Total, List<IDictionary<string, object>> Rows)> RequestsAsync(DateRange range, LogFilter f) =>
        PageAsync(AuditTable.Requests, RequestListColumns, range, async db =>
        {
            var where = new List<string> { "1 = 1" };
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
                where.Add($"(path LIKE {like} OR query LIKE {like} OR ip LIKE {like} OR user_agent LIKE {like} OR detail LIKE {like} OR user_name LIKE {like} OR video_id LIKE {like} OR analysis_id LIKE {like})");
            }
            return string.Join(" AND ", where);
        }, "ts_utc", f);

    private (ArkDbManager Db, string Day8)? Locate(string analysisId)
    {
        if (!Analysis.ReportStore.IsValidId(analysisId)) return null;
        var day8 = analysisId[..8];
        var month = AuditClock.MonthOfDay8(day8);
        return dbs.Exists(month) ? (dbs.For(month), day8) : null;
    }

    public async Task<IDictionary<string, object>?> AnalysisAsync(string analysisId)
    {
        if (Locate(analysisId) is not { } loc) return null;
        if (!(await AuditSchema.DaysInFileAsync(loc.Db)).Contains(AuditClock.DayOfDay8(loc.Day8))) return null;
        IEnumerable<dynamic> rows = await loc.Db.SelectAsync($"SELECT * FROM {AuditSchema.Name(AuditTable.Analyses, loc.Day8)} WHERE analysis_id = {await loc.Db.GetSqlValueAsync(analysisId)} {loc.Db.Util.Qry_Limit(1)};");
        return rows.FirstOrDefault() as IDictionary<string, object>;
    }

    public async Task<(long Total, List<IDictionary<string, object>> Rows)> CommentsAsync(string analysisId, string? sentiment, int page, int size)
    {
        if (Locate(analysisId) is not { } loc) return (0, []);
        if (!(await AuditSchema.DaysInFileAsync(loc.Db)).Contains(AuditClock.DayOfDay8(loc.Day8))) return (0, []);
        var t = AuditSchema.Name(AuditTable.Comments, loc.Day8);
        var where = $"analysis_id = {await loc.Db.GetSqlValueAsync(analysisId)}";
        if (Pick(sentiment, "positive", "neutral", "negative") is { } s) where += $" AND sentiment = {await loc.Db.GetSqlValueAsync(s)}";
        size = Math.Clamp(size, 10, 500);
        page = Math.Max(1, page);
        var total = await loc.Db.ExecuteCountAsync($"SELECT COUNT(*) FROM {t} WHERE {where};");
        IEnumerable<dynamic> rows = await loc.Db.SelectAsync($"SELECT {CommentListColumns} FROM {t} WHERE {where} ORDER BY like_count DESC, id {loc.Db.Util.Qry_Limit(size, (page - 1) * size)};");
        return (total, rows.Cast<IDictionary<string, object>>().ToList());
    }

    public async Task<List<IDictionary<string, object>>> RequestsForAnalysisAsync(string analysisId)
    {
        if (Locate(analysisId) is not { } loc) return [];
        var list = new List<IDictionary<string, object>>();
        // requests about a report can land on later days (the page is reopened); look at the whole month
        foreach (var day in await AuditSchema.DaysInFileAsync(loc.Db))
        {
            var t = AuditSchema.Name(AuditTable.Requests, AuditClock.Day8OfDay(day));
            IEnumerable<dynamic> rows = await loc.Db.SelectAsync($"SELECT {RequestListColumns} FROM {t} WHERE analysis_id = {await loc.Db.GetSqlValueAsync(analysisId)} ORDER BY id {loc.Db.Util.Qry_Limit(200)};");
            list.AddRange(rows.Cast<IDictionary<string, object>>());
        }
        return list;
    }

    /// <summary>Every column of every row of one day table, as CSV (capped).</summary>
    public async Task<string?> CsvAsync(AuditTable table, string day, int max = 50_000)
    {
        if (!AuditClock.TryParseDay(day, out _)) return null;
        var month = day[..7];
        if (!dbs.Exists(month)) return null;
        var db = dbs.For(month);
        if (!(await AuditSchema.DaysInFileAsync(db)).Contains(day)) return null;
        IEnumerable<dynamic> rows = await db.SelectAsync($"SELECT * FROM {AuditSchema.Name(table, AuditClock.Day8OfDay(day))} ORDER BY id {db.Util.Qry_Limit(max)};");
        var sb = new StringBuilder();
        var first = true;
        foreach (IDictionary<string, object> row in rows)
        {
            if (first) { sb.AppendJoin(',', row.Keys.Select(Csv)).Append("\r\n"); first = false; }
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

    internal static string Csv(string s)
    {
        if (s.Length > 0 && s[0] is '=' or '+' or '-' or '@' or '\t' or '\r' && !double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            s = "'" + s;
        return s.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    // ── store status ─────────────────────────────────────────────────────────

    public sealed class DayRegistry
    {
        public string Day { get; set; } = "";
        public long Requests { get; set; }
        public long Analyses { get; set; }
        public long Completed { get; set; }
        public long Comments { get; set; }
        public long Feedback { get; set; }
        public long Liked { get; set; }
        public string? UpdatedUtc { get; set; }
    }

    public async Task<List<object>> StoreAsync()
    {
        var list = new List<object>();
        foreach (var m in dbs.Months())
        {
            var db = dbs.For(m.Month);
            List<DayRegistry> days;
            string? schema = null;
            try
            {
                days = (await db.ExecuteSelectAsync<DayRegistry>($"SELECT day AS Day, requests AS Requests, analyses AS Analyses, completed AS Completed, comments AS Comments, feedback AS Feedback, liked AS Liked, updated_utc AS UpdatedUtc FROM {AuditSchema.Days} ORDER BY day;")).ToList();
                schema = await AuditSchema.GetMetaAsync(db, "schema_version");
            }
            catch (Exception ex)
            {
                list.Add(new { m.Month, m.Path, m.Bytes, error = ex.Message });
                continue;
            }
            list.Add(new { m.Month, m.Path, m.Bytes, modifiedUtc = m.ModifiedUtc, schema, days });
        }
        return list;
    }

    private static string? Clean(string? s, int max)
    {
        s = s?.Trim();
        return string.IsNullOrEmpty(s) ? null : s.Length > max ? s[..max] : s;
    }

    private static string? Pick(string? s, params string[] allowed) => allowed.FirstOrDefault(a => a.Equals(s?.Trim(), StringComparison.OrdinalIgnoreCase));
}
