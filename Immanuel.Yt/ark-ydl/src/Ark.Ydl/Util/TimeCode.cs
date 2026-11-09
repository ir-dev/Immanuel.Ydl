using System.Globalization;
using System.Text.RegularExpressions;

namespace Ark.Ydl.Util;

/// <summary>Parses and formats the time codes people type or YouTube puts in links.</summary>
public static partial class TimeCode
{
    [GeneratedRegex(@"^(?:(?<h>\d+(?:\.\d+)?)h)?(?:(?<m>\d+(?:\.\d+)?)m)?(?:(?<s>\d+(?:\.\d+)?)s?)?$", RegexOptions.IgnoreCase)]
    private static partial Regex UnitsRx();

    /// <summary>
    /// Accepts "90", "90.5", "1:30", "01:02:03.250", "1h2m3s", "2m", "45s".
    /// </summary>
    public static bool TryParse(string? text, out double seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim().Replace(',', '.');

        if (t.Contains(':'))
        {
            var parts = t.Split(':');
            if (parts.Length is < 2 or > 3) return false;
            double total = 0;
            for (var i = 0; i < parts.Length; i++)
            {
                if (!double.TryParse(parts[i], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var v) || v < 0)
                    return false;
                // every field but the first must be a proper 0-59 sexagesimal value
                if (i > 0 && v >= 60) return false;
                total = total * 60 + v;
            }
            seconds = total;
            return true;
        }

        var m = UnitsRx().Match(t);
        if (!m.Success || t.Length == 0) return false;
        double Get(string g) => m.Groups[g].Success
            ? double.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture) : 0;
        if (!m.Groups["h"].Success && !m.Groups["m"].Success && !m.Groups["s"].Success) return false;
        seconds = Get("h") * 3600 + Get("m") * 60 + Get("s");
        return true;
    }

    /// <summary>1:02:03.25 / 2:03 / 0:07.5</summary>
    public static string Format(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) seconds = 0;
        var whole = (long)Math.Floor(seconds);
        var frac = seconds - whole;
        var h = whole / 3600;
        var m = whole % 3600 / 60;
        var s = whole % 60;
        var core = h > 0 ? $"{h}:{m:00}:{s:00}" : $"{m}:{s:00}";
        if (frac >= 0.005)
            core += frac.ToString(".##", CultureInfo.InvariantCulture);
        return core;
    }

    /// <summary>Invariant seconds for a yt-dlp argument, e.g. "75.5".</summary>
    public static string ToArg(double seconds) =>
        Math.Max(0, seconds).ToString("0.###", CultureInfo.InvariantCulture);
}
