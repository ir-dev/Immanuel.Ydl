using System.Globalization;
using System.Text;
using Panlingo.LanguageIdentification.FastText;

namespace Ark.Feedback.Nlp;

public sealed record LanguageGuess(string Code, double Confidence, string Method)
{
    public static readonly LanguageGuess Unknown = new("und", 0, "none");
}

/// <summary>
/// Which language a comment is written in: fastText lid.176 (176 languages, microseconds per text) when its native
/// library loads on this platform, else a Unicode-script heuristic that is exact for scripts owned by one language
/// (Tamil, Thai, Korean…) and says "und" for Latin text. Very short texts are judged by script first, because a
/// two-word comment is a coin toss for any statistical model.
/// </summary>
public sealed class LanguageDetector : IDisposable
{
    private readonly FastTextDetector? _ft;
    private readonly object _lock = new();

    public LanguageDetector(ILogger<LanguageDetector> log)
    {
        try
        {
            if (FastTextDetector.IsSupported())
            {
                _ft = new FastTextDetector();
                _ft.LoadDefaultModel();
                Engine = "fastText lid.176";
            }
            else Engine = "script heuristic (fastText has no native build for this platform)";
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "fastText could not be loaded; language detection falls back to Unicode scripts");
            Engine = "script heuristic (fastText failed to load: " + ex.Message + ")";
        }
    }

    public string Engine { get; }

    public bool UsesModel => _ft is not null;

    public LanguageGuess Detect(string text)
    {
        var t = text.Trim();
        if (t.Length == 0) return LanguageGuess.Unknown;
        var (script, scriptLang, letters) = Script(t);
        if (letters == 0) return new LanguageGuess("und", 0, "no letters"); // emoji-only, numbers, links
        if (scriptLang is not null && script != "Latn" && script != "Cyrl" && script != "Arab" && script != "Deva")
            return new LanguageGuess(scriptLang, 0.99, "script");
        if (_ft is not null)
        {
            try
            {
                IReadOnlyList<(string Label, float P)> preds;
                lock (_lock) preds = _ft.Predict(t.Replace('\n', ' '), 3).Select(p => (p.Label, p.Probability)).ToList();
                if (preds.Count > 0)
                {
                    var (label, p) = preds[0];
                    var code = label.Replace("__label__", "");
                    // Short comments ("same", "first!", "lol") give any model a weak guess; fastText's top answer is still right far
                    // more often than not for Latin text, so only a really weak or really short one becomes "und".
                    if (p >= 0.5 || (p >= 0.25 && letters >= 3)) return new LanguageGuess(code, p, "fasttext");
                    return scriptLang is not null ? new LanguageGuess(scriptLang, Math.Max(0.4, p), "script") : new LanguageGuess("und", p, "fasttext-low");
                }
            }
            catch (Exception)
            {
                // fall through to the script answer
            }
        }
        return scriptLang is not null ? new LanguageGuess(scriptLang, 0.6, "script") : new LanguageGuess("und", 0, "script");
    }

    /// <summary>Dominant script of the letters in a text, the language that script implies (when it implies one), and the letter count.</summary>
    internal static (string Script, string? Language, int Letters) Script(string text)
    {
        var counts = new Dictionary<string, int>();
        var letters = 0;
        foreach (var r in text.EnumerateRunes())
        {
            var cat = Rune.GetUnicodeCategory(r);
            if (cat is not (UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.OtherLetter or UnicodeCategory.ModifierLetter or UnicodeCategory.TitlecaseLetter)) continue;
            letters++;
            var s = ScriptOf(r.Value);
            counts[s] = counts.GetValueOrDefault(s) + 1;
        }
        if (letters == 0) return ("Zyyy", null, 0);
        var top = counts.MaxBy(kv => kv.Value).Key;
        return (top, ScriptLanguage(top), letters);
    }

    private static string? ScriptLanguage(string script) => script switch
    {
        "Taml" => "ta", "Telu" => "te", "Knda" => "kn", "Mlym" => "ml", "Beng" => "bn", "Gujr" => "gu", "Guru" => "pa",
        "Orya" => "or", "Sinh" => "si", "Thai" => "th", "Laoo" => "lo", "Khmr" => "km", "Mymr" => "my", "Tibt" => "bo",
        "Hang" => "ko", "Jpan" => "ja", "Hani" => "zh", "Grek" => "el", "Hebr" => "he", "Armn" => "hy", "Geor" => "ka", "Ethi" => "am",
        "Deva" => "hi", "Arab" => "ar", "Cyrl" => "ru",
        _ => null,
    };

    internal static string ScriptOf(int cp) => cp switch
    {
        >= 0x0041 and <= 0x024F => "Latn", >= 0x1E00 and <= 0x1EFF => "Latn",
        >= 0x0370 and <= 0x03FF => "Grek", >= 0x0400 and <= 0x052F => "Cyrl", >= 0x0530 and <= 0x058F => "Armn",
        >= 0x0590 and <= 0x05FF => "Hebr", >= 0x0600 and <= 0x06FF or >= 0x0750 and <= 0x077F or >= 0x08A0 and <= 0x08FF or >= 0xFB50 and <= 0xFDFF or >= 0xFE70 and <= 0xFEFF => "Arab",
        >= 0x0900 and <= 0x097F => "Deva", >= 0x0980 and <= 0x09FF => "Beng", >= 0x0A00 and <= 0x0A7F => "Guru", >= 0x0A80 and <= 0x0AFF => "Gujr",
        >= 0x0B00 and <= 0x0B7F => "Orya", >= 0x0B80 and <= 0x0BFF => "Taml", >= 0x0C00 and <= 0x0C7F => "Telu", >= 0x0C80 and <= 0x0CFF => "Knda",
        >= 0x0D00 and <= 0x0D7F => "Mlym", >= 0x0D80 and <= 0x0DFF => "Sinh", >= 0x0E00 and <= 0x0E7F => "Thai", >= 0x0E80 and <= 0x0EFF => "Laoo",
        >= 0x0F00 and <= 0x0FFF => "Tibt", >= 0x1000 and <= 0x109F => "Mymr", >= 0x10A0 and <= 0x10FF => "Geor", >= 0x1200 and <= 0x137F => "Ethi",
        >= 0x1780 and <= 0x17FF => "Khmr", >= 0x3040 and <= 0x30FF or >= 0x31F0 and <= 0x31FF => "Jpan",
        >= 0x1100 and <= 0x11FF or >= 0x3130 and <= 0x318F or >= 0xAC00 and <= 0xD7AF => "Hang",
        _ when WordPieceTokenizer.IsCjk(cp) => "Hani",
        _ => "Zyyy",
    };

    private static readonly Dictionary<string, string> ExtraNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["und"] = "Unknown", ["ceb"] = "Cebuano", ["war"] = "Waray", ["sh"] = "Serbo-Croatian", ["tl"] = "Tagalog", ["jv"] = "Javanese",
        ["su"] = "Sundanese", ["bh"] = "Bihari", ["new"] = "Newari", ["pnb"] = "Western Punjabi", ["arz"] = "Egyptian Arabic", ["azb"] = "South Azerbaijani",
        ["ckb"] = "Central Kurdish", ["lmo"] = "Lombard", ["pms"] = "Piedmontese", ["scn"] = "Sicilian", ["vec"] = "Venetian", ["nds"] = "Low German",
        ["als"] = "Alemannic", ["bar"] = "Bavarian", ["min"] = "Minangkabau", ["ilo"] = "Ilocano", ["mzn"] = "Mazanderani", ["gom"] = "Goan Konkani",
        ["bpy"] = "Bishnupriya", ["mai"] = "Maithili", ["sa"] = "Sanskrit", ["bo"] = "Tibetan", ["dv"] = "Dhivehi", ["tt"] = "Tatar", ["ba"] = "Bashkir",
    };

    /// <summary>"ta" → "Tamil". Falls back to the code when .NET doesn't know it.</summary>
    public static string Name(string code)
    {
        if (ExtraNames.TryGetValue(code, out var n)) return n;
        try
        {
            var c = CultureInfo.GetCultureInfo(code);
            if (!string.IsNullOrEmpty(c.EnglishName) && !c.EnglishName.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase)) return c.EnglishName.Split(" (")[0];
        }
        catch (CultureNotFoundException) { }
        return code.ToUpperInvariant();
    }

    public void Dispose() => _ft?.Dispose();
}
