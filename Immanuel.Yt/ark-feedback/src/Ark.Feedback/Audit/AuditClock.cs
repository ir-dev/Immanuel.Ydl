using System.Globalization;

namespace Ark.Feedback.Audit;

/// <summary>
/// How audit timestamps are written, and which day (table) and month (database file) an event belongs to. Timestamps
/// are UTC text that sorts as text; the buckets follow Feedback:Audit:TimeZone (UTC unless configured).
/// </summary>
public sealed class AuditClock
{
    public const string Pattern = "yyyy-MM-dd HH:mm:ss.fff";
    public const string DayPattern = "yyyy-MM-dd";

    public AuditClock(string? timeZone)
    {
        Zone = Resolve(timeZone, out var note);
        Note = note;
    }

    public TimeZoneInfo Zone { get; }

    /// <summary>Set when the configured zone was not found and UTC is used instead.</summary>
    public string? Note { get; }

    public static string Format(DateTime utc) => utc.ToString(Pattern, CultureInfo.InvariantCulture);

    public static string Format(DateTimeOffset t) => Format(t.UtcDateTime);

    public DateTime Local(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone);

    /// <summary>The day a moment counts towards, "yyyy-MM-dd".</summary>
    public string Day(DateTime utc) => Local(utc).ToString(DayPattern, CultureInfo.InvariantCulture);

    /// <summary>The same day as the 8-digit table suffix, "yyyyMMdd".</summary>
    public string Day8(DateTime utc) => Local(utc).ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    public (string Day, int Hour, int Weekday) Bucket(DateTime utc)
    {
        var l = Local(utc);
        return (l.ToString(DayPattern, CultureInfo.InvariantCulture), l.Hour, (int)l.DayOfWeek);
    }

    public string Today => Day(DateTime.UtcNow);

    public string ThisMonth => Local(DateTime.UtcNow).ToString("yyyy-MM", CultureInfo.InvariantCulture);

    /// <summary>"20261110" → "2026-11"; the month file a day table lives in.</summary>
    public static string MonthOfDay8(string day8) => $"{day8[..4]}-{day8[4..6]}";

    /// <summary>"20261110" → "2026-11-10".</summary>
    public static string DayOfDay8(string day8) => $"{day8[..4]}-{day8[4..6]}-{day8[6..8]}";

    /// <summary>"2026-11-10" → "20261110".</summary>
    public static string Day8OfDay(string day) => day.Replace("-", "");

    public static bool IsDay8(string? s) => s is { Length: 8 } && s.All(char.IsAsciiDigit) && DateOnly.TryParseExact(s, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    public static bool IsMonth(string? s) => s is { Length: 7 } && DateOnly.TryParseExact(s + "-01", DayPattern, CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    private static TimeZoneInfo Resolve(string? id, out string? note)
    {
        note = null;
        if (string.IsNullOrWhiteSpace(id) || id.Equals("UTC", StringComparison.OrdinalIgnoreCase)) return TimeZoneInfo.Utc;
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id.Trim());
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            note = $"Time zone '{id}' was not found on this server; days are counted in UTC.";
            return TimeZoneInfo.Utc;
        }
    }

    public static bool TryParseDay(string? text, out DateOnly day) =>
        DateOnly.TryParseExact(text, DayPattern, CultureInfo.InvariantCulture, DateTimeStyles.None, out day);

    public static string DayText(DateOnly d) => d.ToString(DayPattern, CultureInfo.InvariantCulture);
}
