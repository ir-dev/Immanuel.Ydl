using System.Globalization;

namespace Ark.Ydl.Audit;

/// <summary>
/// How audit timestamps are written, and which day / hour / weekday they count towards. Timestamps are always UTC
/// text that sorts as text; the day buckets follow Ydl:Audit:TimeZone (UTC unless configured).
/// </summary>
public sealed class AuditClock
{
    public const string Pattern = "yyyy-MM-dd HH:mm:ss.fff";
    public const string DayPattern = "yyyy-MM-dd";

    private readonly TimeZoneInfo _zone;

    public AuditClock(string? timeZone)
    {
        _zone = Resolve(timeZone, out var note);
        Note = note;
    }

    public TimeZoneInfo Zone => _zone;

    /// <summary>Set when the configured zone was not found and UTC is used instead.</summary>
    public string? Note { get; }

    public static string Format(DateTime utc) => utc.ToString(Pattern, CultureInfo.InvariantCulture);

    public static string Format(DateTimeOffset t) => Format(t.UtcDateTime);

    public DateTime Local(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), _zone);

    /// <summary>The day a moment counts towards, in the configured zone.</summary>
    public string Day(DateTime utc) => Local(utc).ToString(DayPattern, CultureInfo.InvariantCulture);

    public (string Day, int Hour, int Weekday) Bucket(DateTime utc)
    {
        var l = Local(utc);
        return (l.ToString(DayPattern, CultureInfo.InvariantCulture), l.Hour, (int)l.DayOfWeek);
    }

    public string Today => Day(DateTime.UtcNow);

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
