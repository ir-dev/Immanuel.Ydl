using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Ark.Feedback.Nlp;
using Ark.Feedback.YouTube;

namespace Ark.Feedback.Analysis;

/// <summary>
/// Reads the video's metadata and the scored comments for the questions a parent, a teacher or a sponsor asks first:
/// what kind of video is it, where is the audience, how old are they, and is the comment section fit to share?
/// Everything here is word lists and arithmetic over public comments — fast, explainable, and honest about its limits.
/// </summary>
public static partial class AudienceProfiler
{
    public static Audience Profile(VideoInfo video, IReadOnlyList<ScoredComment> comments, SentimentSummary sentiment, ToxicitySummary? toxicity, IReadOnlyList<LabelCount> languages,
        Dictionary<string, List<string>> highlights, ThreadSummary threads)
    {
        var texts = comments.Select(c => new CommentText(c.Text)).ToList();
        var genre = Genre(video, comments, texts);
        var reach = Reach(video, comments, texts, languages);
        var profanity = Profanity(comments, texts);
        var flags = Flags(comments, texts);
        var age = Age(video, comments, texts, genre, profanity, toxicity, sentiment);
        var family = Family(video, comments, texts, genre, age, profanity, flags, toxicity, sentiment, languages, highlights, threads);
        return new Audience { Genre = genre, Reach = reach, Age = age, Family = family };
    }

    private static double Pct(int part, int whole) => whole == 0 ? 0 : Math.Round(part * 100.0 / whole, 1);

    private static string N(int n, string noun) => $"{n:N0} {noun}{(n == 1 ? "" : "s")}";

    private static string Lower(string? s) => (s ?? "").ToLowerInvariant();

    // ── genre ──────────────────────────────────────────────────────────────

    internal static GenreGuess Genre(VideoInfo v, IReadOnlyList<ScoredComment> comments, IReadOnlyList<CommentText> texts)
    {
        var title = new CommentText(v.Title ?? "");
        var channel = new CommentText(v.Channel ?? "");
        var description = new CommentText(TextTools.Clip(v.Description ?? "", 600));
        var tags = v.Tags.Select(t => new CommentText(t)).ToList();
        var scores = new List<(GenreDef Def, double Score, List<string> Why)>();
        var unique = texts.Where((_, i) => comments[i].DuplicateOf is null).ToList();
        var n = Math.Max(1, unique.Count);
        foreach (var g in Lexicons.Genres)
        {
            double score = 0;
            var why = new List<string>();
            var inTitle = g.Title.Match(title);
            if (inTitle.Count > 0) { score += Math.Min(3, inTitle.Count) * 3; why.Add($"title mentions {Quote(inTitle)}"); }
            var inTags = tags.Sum(t => g.Title.Any(t) ? 1 : 0);
            if (inTags > 0) { score += Math.Min(5, inTags) * 1.4; why.Add($"{inTags} tag{(inTags == 1 ? "" : "s")} match"); }
            var inDesc = g.Title.Match(description);
            if (inDesc.Count > 0) { score += Math.Min(4, inDesc.Count) * 0.6; why.Add($"description mentions {Quote(inDesc)}"); }
            var inChannel = g.Title.Match(channel);
            if (inChannel.Count > 0) { score += 1.2; why.Add($"channel name mentions {Quote(inChannel)}"); }
            if (v.Category is { } cat && g.Categories.Contains(cat, StringComparer.OrdinalIgnoreCase)) { score += Lexicons.CategoryPrior; why.Add($"YouTube category “{cat}”"); }
            var inComments = unique.Count(t => g.Comments.Any(t));
            var share = inComments / (double)n;
            if (inComments >= 3 && share >= 0.03) { score += Math.Min(4, share * 12); why.Add($"{inComments:N0} comments ({share * 100:0}%) talk about it"); }
            if (score > 0) scores.Add((g, Math.Round(score, 2), why));
        }
        // duration tells podcasts from clips and kids' rhymes from lectures
        if (v.DurationSeconds is { } d)
        {
            if (d >= 2400) Bump(scores, "podcast", 1.5, "runs over 40 minutes");
            if (d >= 2400) Bump(scores, "docu", 0.6, null);
            if (d <= 75) Bump(scores, "viral", 1.2, "under 75 seconds");
        }
        if (v.IsShort) Bump(scores, "viral", 2, "published as a Short");
        if (v.LiveStatus is "is_live" or "was_live" or "post_live") Bump(scores, "podcast", 0.5, null);

        scores.Sort((a, b) => b.Score.CompareTo(a.Score));
        var (format, formatLabel) = Format(v);
        if (scores.Count == 0 || scores[0].Score < 2)
        {
            return new GenreGuess
            {
                Key = "general", Label = v.Category ?? "General", Icon = "▶️", Confidence = 0, Category = v.Category, Format = format, FormatLabel = formatLabel,
                Evidence = scores.Count == 0 ? ["Nothing in the title, tags or comments points to a particular genre."] : scores[0].Why,
            };
        }
        var total = scores.Sum(s => s.Score);
        var top = scores[0];
        var secondary = scores.Skip(1).Where(s => s.Score >= Math.Max(2, top.Score * 0.35)).Take(2)
            .Select(s => new GenreScore { Key = s.Def.Key, Label = s.Def.Label, Icon = s.Def.Icon, Score = s.Score }).ToList();
        return new GenreGuess
        {
            Key = top.Def.Key, Label = top.Def.Label, Icon = top.Def.Icon, Confidence = Math.Round(top.Score / total, 2), Secondary = secondary,
            Category = v.Category, Format = format, FormatLabel = formatLabel, Evidence = top.Why,
        };
    }

    private static void Bump(List<(GenreDef Def, double Score, List<string> Why)> scores, string key, double by, string? why)
    {
        var i = scores.FindIndex(s => s.Def.Key == key);
        if (i < 0)
        {
            var def = Lexicons.Genres.First(g => g.Key == key);
            scores.Add((def, by, why is null ? [] : [why]));
            return;
        }
        var cur = scores[i];
        if (why is not null) cur.Why.Add(why);
        scores[i] = (cur.Def, Math.Round(cur.Score + by, 2), cur.Why);
    }

    private static string Quote(IEnumerable<string> words) => string.Join(", ", words.Take(3).Select(w => $"“{w}”"));

    internal static (string Key, string Label) Format(VideoInfo v)
    {
        if (v.LiveStatus is "is_live") return ("live", "Live stream");
        if (v.LiveStatus is "was_live" or "post_live") return ("live", "Live stream replay");
        if (v.IsShort) return ("short", "Short");
        return v.DurationSeconds switch
        {
            null => ("standard", "Video"),
            <= 60 => ("short", "Short-form clip"),
            <= 300 => ("clip", "Short video"),
            <= 1200 => ("standard", "Standard-length video"),
            <= 3600 => ("long", "Long-form video"),
            _ => ("feature", "Feature-length video"),
        };
    }

    // ── reach ──────────────────────────────────────────────────────────────

    internal sealed class PlaceHit
    {
        public required string Code { get; init; }
        public double Weight { get; set; }
        public bool Flag { get; set; }
        public string? Term { get; set; }
        /// <summary>A flag or a "from …" phrase: the commenter is there, rather than talking about it.</summary>
        public bool Presence => Flag || Weight >= 2;
    }

    /// <summary>A language guess is trusted for placement only when the comment is long enough to be guessed at all.</summary>
    private static bool PlaceableLanguage(ScoredComment c) => c.Lang != "und" && Base(c.Lang) != "en" && c.LangConfidence >= 0.5 && c.Text.Length >= 12 && Lexicons.LanguageRegion.ContainsKey(Base(c.Lang));

    internal static ReachSummary Reach(VideoInfo v, IReadOnlyList<ScoredComment> comments, IReadOnlyList<CommentText> texts, IReadOnlyList<LabelCount> languages)
    {
        var n = comments.Count;
        var regionCount = new Dictionary<string, int>(StringComparer.Ordinal);
        var regionLangs = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        var regionCountries = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        var countries = new Dictionary<string, CountryMention>(StringComparer.Ordinal);
        var placed = 0;
        var weakPlaced = 0;
        var evidence = new List<string>();
        var flagged = 0;
        var fromPhrases = 0;
        var named = 0;

        // what the video itself names
        var videoText = new CommentText(string.Join(" ", new[] { v.Title ?? "", string.Join(" ", v.Tags), TextTools.Clip(v.Description ?? "", 600) }));
        foreach (var hit in PlacesIn(videoText, strict: true)) CountryOf(countries, hit.Code).NamedByVideo = true;

        for (var i = 0; i < n; i++)
        {
            var c = comments[i];
            var t = texts[i];
            var hits = PlacesIn(t, strict: false);
            string? region = null;
            var weak = false;
            if (hits.Count > 0)
            {
                foreach (var h in hits)
                {
                    var cm = CountryOf(countries, h.Code);
                    cm.Comments++;
                    if (h.Flag) cm.Flags++;
                    if (h.Presence) cm.Presence++;
                    if (cm.ExampleIds.Count < 3 && c.DuplicateOf is null) cm.ExampleIds.Add(c.Id);
                }
                if (hits.Any(h => h.Flag)) flagged++;
                if (hits.Any(h => !h.Flag && h.Weight >= 2)) fromPhrases++;
                if (hits.All(h => !h.Presence)) named++;
                var best = hits.OrderByDescending(h => h.Weight).First();
                var bestRegion = Lexicons.RegionOfCountry(best.Code);
                var langRegion = PlaceableLanguage(c) ? Lexicons.LanguageRegion[Base(c.Lang)] : null;
                if (best.Presence) region = bestRegion;                                   // "love from Kenya 🇰🇪": the commenter is there
                else if (langRegion is not null)
                {
                    // a Spanish or Portuguese comment naming a Spanish- or Portuguese-speaking country is probably from there; otherwise the language wins
                    var iberian = Base(c.Lang) is "es" or "pt";
                    region = iberian && bestRegion is "latin-america" or "western-europe" && best.Code is not ("FR" or "DE" or "IT" or "GB") ? bestRegion : langRegion;
                }
                else if (bestRegion is not null && best.Weight >= 1) { region = bestRegion; weak = true; } // an English comment naming a place: topic or home, we can't tell
                if (region is not null && region == bestRegion)
                {
                    var cc = regionCountries.GetValueOrDefault(region) ?? (regionCountries[region] = new Dictionary<string, int>(StringComparer.Ordinal));
                    cc[best.Code] = cc.GetValueOrDefault(best.Code) + 1;
                }
            }
            if (region is null && PlaceableLanguage(c))
            {
                region = Lexicons.LanguageRegion[Base(c.Lang)];
                var ll = regionLangs.GetValueOrDefault(region) ?? (regionLangs[region] = new Dictionary<string, int>(StringComparer.Ordinal));
                ll[c.Lang] = ll.GetValueOrDefault(c.Lang) + 1;
            }
            if (region is null) continue;
            placed++;
            if (weak) weakPlaced++;
            regionCount[region] = regionCount.GetValueOrDefault(region) + 1;
        }

        var regions = regionCount.OrderByDescending(kv => kv.Value).Select(kv => new RegionShare
        {
            Key = kv.Key, Label = Lexicons.RegionLabels[kv.Key], Count = kv.Value, Pct = Pct(kv.Value, placed),
            Languages = regionLangs.GetValueOrDefault(kv.Key)?.OrderByDescending(x => x.Value).Take(3).Select(x => $"{LanguageDetector.Name(x.Key)} {x.Value}").ToList() ?? [],
            Countries = regionCountries.GetValueOrDefault(kv.Key)?.OrderByDescending(x => x.Value).Take(4).Select(x => Lexicons.PlaceByCode(x.Key)?.Name ?? x.Key).ToList() ?? [],
        }).ToList();

        var countryList = countries.Values.Where(cm => cm.Comments > 0 || cm.NamedByVideo)
            .OrderByDescending(cm => cm.Presence).ThenByDescending(cm => cm.Comments).ThenBy(cm => cm.Name, StringComparer.Ordinal).Take(10).ToList();

        var langs = languages.Where(l => l.Key != "und").ToList();
        var identified = Math.Max(1, langs.Sum(l => l.Count));
        var primary = langs.FirstOrDefault();
        var english = langs.FirstOrDefault(l => Base(l.Key) == "en");
        var englishPct = english is null ? 0 : Math.Round(english.Count * 100.0 / identified, 1);
        var spread = langs.Count(l => l.Pct >= 3);
        var strongRegions = regions.Count(r => r.Pct >= 8 && r.Count >= 3);
        var topRegionShare = regions.Count == 0 ? 1 : regions[0].Pct / 100.0;
        var placedPct = Pct(placed, n);
        var presenceCountries = countryList.Count(c => c.Presence >= 2);

        string key, label, note;
        var score = 0;
        if (n == 0 || placed < Math.Max(5, n * 0.05))
        {
            key = "unplaced";
            label = "Hard to place";
            note = "Almost no comment carries a flag, names a place or is written in a language that points to one region.";
        }
        else
        {
            var raw = strongRegions * 14 + Math.Min(5, spread) * 7 + (1 - topRegionShare) * 45 + Math.Min(12, presenceCountries * 2.0 + countryList.Count(c => c.Comments >= 2) * 0.8);
            score = (int)Math.Round(Math.Min(100, raw * (0.6 + 0.4 * Math.Min(1, placedPct / 35))));
            if (englishPct >= 70 && placedPct < 25)
            {
                key = "english-wide";
                var lead = regions.Count > 0 && regions[0].Pct >= 45 && regions[0].Count >= 8 ? regions[0] : null;
                label = lead is null ? "Wide English-speaking audience" : $"English-speaking, {lead.Label} leads";
                note = $"{englishPct:0}% of the comments with a recognisable language are English, which can't be placed on a map; {placedPct:0}% of comments name a place, carry a flag or use a language tied to one region.";
                score = Math.Max(score, 40);
            }
            else
            {
                (key, label) = score switch { >= 68 => ("global", "Global"), >= 45 => ("multi-regional", "Multi-regional"), >= 25 => ("regional", "Regional"), _ => ("local", "Mostly one country") };
                if (key is "regional" or "local" && regions.Count > 0) label += $": {regions[0].Label}";
                note = $"Placed {placedPct:0}% of comments: by the language they are written in" + (flagged + fromPhrases > 0 ? $", by flag emoji ({flagged:N0}) and “from …” phrases ({fromPhrases:N0})" : "") +
                       (weakPlaced > 0 ? $"; {weakPlaced:N0} only name a country, which may be the topic rather than home" : "") +
                       ". Spanish and Portuguese are counted under Latin America; English comments are placed only when they name a place.";
            }
        }
        if (regions.Count > 0) evidence.Add($"{regions[0].Label} leads with {regions[0].Pct:0}% of placeable comments" + (regions.Count > 1 ? $", then {regions[1].Label} ({regions[1].Pct:0}%)" : "") + ".");
        if (fromPhrases > 0) evidence.Add($"{N(fromPhrases, "comment")} say{(fromPhrases == 1 ? "s" : "")} where they watch from (“love from…”, “greetings from…”).");
        if (flagged > 0) evidence.Add($"{N(flagged, "comment")} carr{(flagged == 1 ? "ies" : "y")} a country flag.");
        if (named > 0) evidence.Add($"{N(named, "comment")} mention{(named == 1 ? "s" : "")} a country or city without saying they are there.");
        var namedByVideo = countryList.Where(c => c.NamedByVideo).Select(c => c.Name).Take(3).ToList();
        if (namedByVideo.Count > 0) evidence.Add($"The video itself names {string.Join(", ", namedByVideo)}.");
        if (langs.Count > 0) evidence.Add($"{langs.Count} languages; {primary!.Label} {primary.Pct:0}%" + (langs.Count > 1 ? $", {langs[1].Label} {langs[1].Pct:0}%" : "") + (langs.Count > 2 ? $", {langs[2].Label} {langs[2].Pct:0}%" : "") + ".");

        return new ReachSummary
        {
            Key = key, Label = label, Score = score, Regions = regions, Countries = countryList, Placed = placed, PlacedPct = placedPct,
            LanguageCount = langs.Count, PrimaryLanguage = primary?.Label, PrimaryLanguagePct = primary?.Pct ?? 0, EnglishPct = englishPct, Note = note, Evidence = evidence,
        };
    }

    private static CountryMention CountryOf(Dictionary<string, CountryMention> d, string code)
    {
        if (d.TryGetValue(code, out var cm)) return cm;
        var place = Lexicons.PlaceByCode(code);
        cm = new CountryMention { Code = code, Name = place?.Name ?? CountryName(code), Region = place?.Region ?? "unknown" };
        d[code] = cm;
        return cm;
    }

    private static string CountryName(string code)
    {
        try { return new RegionInfo(code).EnglishName; } catch (ArgumentException) { return code; }
    }

    private static string Base(string lang) => lang.Length > 2 && lang[2] is '-' or '_' ? lang[..2] : lang;

    [GeneratedRegex(@"(?:^|\s)(?:from|here in|greetings from|love from|hello from|hi from|watching from|watching in|listening from|support from|respect from|salute from|desde|saludos desde|aquí en|aqui en|aqui no|aqui na|aqui do|aqui da|depuis|ici en|ici au|hier in|hier aus|dall'|dalla|qui in|dari|di sini|dari sini|buradan|привет из|из|से देख|से हूँ|से हूं)\s+(?:(?:the|la|el|los|las|o|os|a|as|le|les|der|die|das|dem|den|il|lo|gli|de|du|des|l')\s+)?(?=\S)", RegexOptions.IgnoreCase)]
    private static partial Regex FromRx();

    /// <summary>Places a comment names: flags (regional-indicator pairs), country and city names, demonyms. "from X" more than doubles the weight.</summary>
    internal static List<PlaceHit> PlacesIn(CommentText t, bool strict)
    {
        var hits = new Dictionary<string, PlaceHit>(StringComparer.Ordinal);
        // flags
        var runes = t.Raw.EnumerateRunes().ToArray();
        for (var i = 0; i + 1 < runes.Length; i++)
        {
            var a = runes[i].Value;
            var b = runes[i + 1].Value;
            if (a is >= 0x1F1E6 and <= 0x1F1FF && b is >= 0x1F1E6 and <= 0x1F1FF)
            {
                var code = new string([(char)('A' + (a - 0x1F1E6)), (char)('A' + (b - 0x1F1E6))]);
                i++;
                if (code is "UN" or "EU") continue;
                var h = hits.GetValueOrDefault(code) ?? (hits[code] = new PlaceHit { Code = code, Term = "flag" });
                h.Weight = Math.Max(h.Weight, 1.5);
                h.Flag = true;
            }
        }
        var talksLanguage = Lexicons.LanguageTalk.Any(t);
        var fromSpots = strict ? [] : FromRx().Matches(t.Norm).Select(m => m.Index + m.Length).ToList();
        foreach (var p in Lexicons.Places)
        {
            double w = 0;
            string? term = null;
            foreach (var alias in p.Aliases)
            {
                if (alias.Length < 2) continue;
                var ambiguous = Lexicons.AmbiguousDemonyms.Contains(alias);
                if (ambiguous && (talksLanguage || strict)) continue;
                var spaceless = IsSpacelessScript(alias);
                var found = alias.Contains(' ') ? t.HasPhrase(alias) : spaceless ? t.Lower.Contains(alias, StringComparison.Ordinal) : t.Tokens.Contains(alias);
                if (!found) continue;
                var weight = ambiguous ? 0.5 : 1.0;
                if (fromSpots.Count > 0)
                {
                    var idx = spaceless ? t.Lower.IndexOf(alias, StringComparison.Ordinal) : t.Norm.IndexOf(" " + alias + " ", StringComparison.Ordinal) + 1;
                    if (idx >= 1 && fromSpots.Any(sp => idx >= sp - 1 && idx <= sp + 1)) weight = 2.2;
                }
                if (weight > w) { w = weight; term = alias; }
            }
            if (!strict)
                foreach (var dem in p.Demonyms)
                {
                    if (dem.Length < 3) continue;
                    var ambiguous = Lexicons.AmbiguousDemonyms.Contains(dem);
                    if (ambiguous && talksLanguage) continue;
                    var found = dem.Contains(' ') ? t.HasPhrase(dem) : t.Tokens.Contains(dem);
                    if (!found) continue;
                    var weight = ambiguous ? 0.3 : 0.6;
                    if (weight > w) { w = weight; term = dem; }
                }
            if (w <= 0) continue;
            var h = hits.GetValueOrDefault(p.Code) ?? (hits[p.Code] = new PlaceHit { Code = p.Code });
            if (w >= h.Weight) { h.Weight = w; h.Term = term; }
        }
        return hits.Values.ToList();
    }

    private static bool IsSpacelessScript(string s)
    {
        foreach (var r in s.EnumerateRunes())
        {
            var cp = r.Value;
            if (cp is >= 0x0E00 and <= 0x0EFF or >= 0x1000 and <= 0x109F or >= 0x1780 and <= 0x17FF or >= 0x3040 and <= 0x30FF or >= 0x3400 and <= 0x9FFF or >= 0xAC00 and <= 0xD7AF) return true;
        }
        return false;
    }

    // ── age ────────────────────────────────────────────────────────────────

    private static readonly (string Key, string Label, string Range, int Lo, int Hi)[] Bands =
    [
        ("preschool", "Preschool & early years", "2–6", 2, 6),
        ("kids", "Children", "7–12", 7, 12),
        ("teens", "Teens", "13–17", 13, 17),
        ("young", "Young adults", "18–24", 18, 24),
        ("adults", "Adults", "25+", 25, 99),
    ];

    private static readonly Dictionary<string, double[]> GenrePrior = new(StringComparer.Ordinal)
    {
        ["kids"] = [0.55, 0.30, 0.05, 0.03, 0.07],
        ["gaming"] = [0.02, 0.25, 0.40, 0.23, 0.10],
        ["music"] = [0.03, 0.12, 0.30, 0.30, 0.25],
        ["comedy"] = [0.02, 0.15, 0.40, 0.28, 0.15],
        ["viral"] = [0.03, 0.20, 0.40, 0.25, 0.12],
        ["education"] = [0.02, 0.15, 0.30, 0.28, 0.25],
        ["howto"] = [0.01, 0.08, 0.22, 0.32, 0.37],
        ["science"] = [0.03, 0.17, 0.25, 0.25, 0.30],
        ["news"] = [0.00, 0.02, 0.10, 0.28, 0.60],
        ["finance"] = [0.00, 0.01, 0.08, 0.36, 0.55],
        ["podcast"] = [0.00, 0.02, 0.10, 0.35, 0.53],
        ["vlog"] = [0.01, 0.10, 0.35, 0.34, 0.20],
        ["film"] = [0.03, 0.15, 0.30, 0.30, 0.22],
        ["sports"] = [0.02, 0.15, 0.30, 0.28, 0.25],
        ["food"] = [0.02, 0.08, 0.15, 0.30, 0.45],
        ["tech"] = [0.00, 0.08, 0.27, 0.35, 0.30],
        ["travel"] = [0.01, 0.06, 0.15, 0.33, 0.45],
        ["faith"] = [0.01, 0.05, 0.12, 0.27, 0.55],
        ["health"] = [0.00, 0.03, 0.12, 0.30, 0.55],
        ["motivation"] = [0.00, 0.03, 0.22, 0.40, 0.35],
        ["art"] = [0.04, 0.20, 0.30, 0.26, 0.20],
        ["beauty"] = [0.00, 0.08, 0.37, 0.37, 0.18],
        ["auto"] = [0.01, 0.08, 0.22, 0.34, 0.35],
        ["relax"] = [0.02, 0.10, 0.30, 0.33, 0.25],
        ["horror"] = [0.00, 0.05, 0.40, 0.35, 0.20],
        ["reaction"] = [0.01, 0.12, 0.42, 0.30, 0.15],
        ["docu"] = [0.01, 0.07, 0.17, 0.30, 0.45],
        ["animals"] = [0.08, 0.22, 0.20, 0.22, 0.28],
        ["general"] = [0.03, 0.12, 0.25, 0.30, 0.30],
    };

    // "i'm 12", "im 14 years old", "i am 9", "turning 16"; not "i'm 100% sure", "i'm 20 minutes in", "i'm 2 years late"
    [GeneratedRegex(@"\b(?:i'?m|i’m|i am|im|ich bin|je suis|tengo|tenho|ho|ik ben|mam|мне|मैं|umurku|umur saya|aku|saya)\s+(?:only |just |now |already |turning |almost |nearly |a |um |une |un |солько |уже )?(\d{1,2})(?:\s*(?:years? old|yrs? old|yo|y\.o\.|y/o|años|anos|ans|jahre|jahre alt|anni|jaar|lat|yaşında|yaşındayım|лет|года|год|साल|साल का|साल की|tahun|th))?\b(?!\s*(?:%|k\b|th\b|st\b|nd\b|rd\b|million|billion|thousand|minutes?|mins?|hours?|hrs?|days?|weeks?|months?|times|seconds?|secs?|years? (?:ago|late|later|into|behind|in)|yrs? (?:ago|late)|in\b|out\b|away\b|more\b|of\b|percent|views|likes|subs|subscribers|comments|episodes?|seasons?|hrs|km|kg|lbs|cm|ft|inch|dollars|bucks|rs|rupees|euros?|pounds|years? (?:younger|older)))", RegexOptions.IgnoreCase)]
    private static partial Regex AgeRx();

    [GeneratedRegex(@"\b(?:born in|nacido en|nací en|nasci em|nascido em|né en|née en|geboren|nato nel|nata nel|lahir tahun|doğumluyum|родился в|родилась в|में जन्म)\s*(?:the year )?((?:19[3-9]|20[0-2])\d)\b", RegexOptions.IgnoreCase)]
    private static partial Regex BornRx();

    [GeneratedRegex(@"\b(?:when i was|cuando tenía|cuando tenia|quando eu tinha|quand j'avais|als ich|quando avevo|waktu umur|yaşındayken|когда мне было|जब मैं)\s+(\d{1,2})\b", RegexOptions.IgnoreCase)]
    private static partial Regex WasRx();

    internal static AgeGuess Age(VideoInfo v, IReadOnlyList<ScoredComment> comments, IReadOnlyList<CommentText> texts, GenreGuess genre, ProfanitySummary profanity, ToxicitySummary? toxicity, SentimentSummary sentiment)
    {
        var n = comments.Count;
        var prior = GenrePrior.GetValueOrDefault(genre.Key) ?? GenrePrior["general"];
        var w = prior.ToArray();
        var evidence = new List<string>();
        var stated = new List<(int Age, string Id)>();
        int coViewing = 0, teachers = 0, school = 0, college = 0, adults = 0, slang = 0, nostalgia = 0, kidWords = 0;
        var year = DateTime.UtcNow.Year;
        var avgLen = 0.0;
        for (var i = 0; i < n; i++)
        {
            var c = comments[i];
            var t = texts[i];
            avgLen += c.Text.Length;
            if (c.DuplicateOf is not null) continue;
            foreach (Match m in AgeRx().Matches(t.Lower))
                if (int.TryParse(m.Groups[1].Value, out var a) && a is >= 4 and <= 85) { stated.Add((a, c.Id)); break; }
            foreach (Match m in BornRx().Matches(t.Lower))
                if (int.TryParse(m.Groups[1].Value, out var y) && year - y is >= 4 and <= 85) { stated.Add((year - y, c.Id)); break; }
            if (WasRx().IsMatch(t.Lower) || Lexicons.Nostalgia.Any(t)) nostalgia++;
            if (Lexicons.CoViewing.Any(t)) coViewing++;
            if (Lexicons.Teachers.Any(t)) teachers++;
            else if (Lexicons.School.Any(t)) school++;
            if (Lexicons.College.Any(t)) college++;
            if (Lexicons.Adults.Any(t)) adults++;
            if (Lexicons.TeenSlang.Match(t).Count >= 1) slang++;
            if (Lexicons.KidTitle.Any(t)) kidWords++;
        }
        avgLen = n == 0 ? 0 : avgLen / n;
        var unique = Math.Max(1, comments.Count(c => c.DuplicateOf is null));
        double R(int count) => count / (double)unique;

        // the video's own labels
        var titleTags = new CommentText((v.Title ?? "") + " " + string.Join(" ", v.Tags));
        var kidTitle = Lexicons.KidTitle.Match(titleTags);
        if (kidTitle.Count > 0) { w[0] += 0.6; w[1] += 0.35; evidence.Add($"The title or tags say it is for children ({Quote(kidTitle)})."); }
        else if (Lexicons.KidTitleBare.Any(new CommentText(v.Title ?? ""))) { w[0] += 0.2; w[1] += 0.2; evidence.Add("The title mentions children."); }
        if (v.AgeLimit is >= 18) { w = [0, 0, 0.02, 0.28, 0.7]; evidence.Add("YouTube marks the video 18+."); }

        // what commenters say about themselves
        if (stated.Count >= 3)
        {
            var hist = new double[5];
            foreach (var (a, _) in stated) hist[BandIndex(a)]++;
            var weight = Math.Min(1.3, 0.35 + stated.Count / 20.0);
            for (var b = 0; b < 5; b++) w[b] += weight * hist[b] / stated.Count;
            var ages = stated.Select(s => s.Age).OrderBy(a => a).ToList();
            evidence.Add($"{stated.Count:N0} commenters state their age (median {Median(ages):0}, from {ages[0]} to {ages[^1]}).");
        }
        if (coViewing > 0) { w[0] += 2.2 * R(coViewing); w[1] += 2.2 * R(coViewing); w[4] += 1.0 * R(coViewing); evidence.Add($"{N(coViewing, "comment")} from parents or relatives watching with a child."); }
        if (teachers > 0) { w[1] += 1.2 * R(teachers); w[2] += 1.2 * R(teachers); w[4] += 0.6 * R(teachers); evidence.Add($"{N(teachers, "comment")} from teachers using it with a class."); }
        if (school > 0) { w[1] += 1.6 * R(school); w[2] += 2.6 * R(school); evidence.Add($"{N(school, "comment")} mention{(school == 1 ? "s" : "")} school, homework or exams."); }
        if (college > 0) { w[3] += 3.0 * R(college); w[4] += 0.6 * R(college); evidence.Add($"{N(college, "comment")} mention{(college == 1 ? "s" : "")} college or university."); }
        if (adults > 0) { w[4] += 3.2 * R(adults); w[3] += 0.4 * R(adults); evidence.Add($"{N(adults, "comment")} mention{(adults == 1 ? "s" : "")} work, marriage, children or growing up decades ago."); }
        if (nostalgia > 0) { w[4] += 1.5 * R(nostalgia); w[3] += 0.8 * R(nostalgia); if (R(nostalgia) >= 0.03) evidence.Add($"{N(nostalgia, "comment")} look{(nostalgia == 1 ? "s" : "")} back on childhood or “the old days”."); }
        if (slang > 0 && R(slang) >= 0.04) { w[2] += 2.4 * R(slang); w[3] += 1.0 * R(slang); evidence.Add($"{R(slang) * 100:0}% of comments use teen internet slang."); }
        if (kidWords > 0 && R(kidWords) >= 0.03) { w[0] += 1.5 * R(kidWords); w[1] += 1.2 * R(kidWords); }
        if (avgLen >= 120) { w[4] += 0.12; w[3] += 0.06; evidence.Add($"Comments are long (about {avgLen:0} characters on average), which skews older."); }
        else if (avgLen > 0 && avgLen < 40) { w[2] += 0.08; w[1] += 0.05; }
        var rough = profanity.Pct / 100.0 + (toxicity?.Pct ?? 0) / 100.0;
        if (rough > 0.01)
        {
            var penalty = 1 - Math.Min(0.85, rough * 6);
            w[0] *= penalty;
            w[1] *= penalty;
            w[2] += 0.5 * rough;
            w[3] += 0.8 * rough;
            w[4] += 0.5 * rough;
            if (profanity.Pct >= 3) evidence.Add($"{profanity.Pct:0.#}% of comments contain swearing, which points away from a young audience.");
        }

        var sum = w.Sum();
        if (sum <= 0) { w = prior.ToArray(); sum = w.Sum(); }
        var bands = Bands.Select((b, i) => new AgeBand { Key = b.Key, Label = b.Label, Range = b.Range, Pct = Math.Round(w[i] * 100 / sum, 1) }).ToList();
        var top = bands.OrderByDescending(b => b.Pct).First();
        var ti = bands.IndexOf(top);
        var label = top.Label;
        var range = top.Range;
        // two adjacent bands close together read as one span
        AgeBand? neighbour = null;
        if (ti > 0 && bands[ti - 1].Pct >= top.Pct - 9) neighbour = bands[ti - 1];
        if (ti < 4 && bands[ti + 1].Pct >= top.Pct - 9 && (neighbour is null || bands[ti + 1].Pct > neighbour.Pct)) neighbour = bands[ti + 1];
        if (neighbour is not null)
        {
            var lo = Math.Min(ti, bands.IndexOf(neighbour));
            var hi = Math.Max(ti, bands.IndexOf(neighbour));
            label = $"{Bands[lo].Label} & {Bands[hi].Label.ToLowerInvariant()}";
            range = Bands[hi].Hi >= 99 ? $"{Bands[lo].Lo}+" : $"{Bands[lo].Lo}–{Bands[hi].Hi}";
        }
        var signals = coViewing + teachers + school + college + adults + slang + stated.Count;
        var confidence = v.AgeLimit is >= 18 || stated.Count >= 15 || R(signals) >= 0.12 ? "high" : stated.Count >= 5 || R(signals) >= 0.05 ? "medium" : "low";
        if (evidence.Count == 0) evidence.Add($"Few commenters say anything about themselves; this rests mostly on the video being {genre.Label.ToLowerInvariant()}.");

        var ages2 = stated.Select(s => s.Age).OrderBy(a => a).ToList();
        return new AgeGuess
        {
            Primary = top.Key, Label = label, Range = range, Confidence = confidence, Bands = bands,
            StatedAges = new StatedAges { Count = stated.Count, Median = ages2.Count == 0 ? null : Math.Round(Median(ages2), 0), Min = ages2.Count == 0 ? null : ages2[0], Max = ages2.Count == 0 ? null : ages2[^1], ExampleIds = stated.Take(6).Select(s => s.Id).ToList() },
            CoViewing = coViewing, Teachers = teachers, School = school, College = college, Slang = slang, Nostalgia = nostalgia, Evidence = evidence,
            Note = "Read from what commenters say about themselves and from the kind of video; YouTube does not publish viewer ages. Commenters skew older than viewers for children's videos (parents write the comments).",
        };
    }

    private static int BandIndex(int age) => age <= 6 ? 0 : age <= 12 ? 1 : age <= 17 ? 2 : age <= 24 ? 3 : 4;

    private static double Median(List<int> sorted) => sorted.Count == 0 ? 0 : sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2.0;

    // ── profanity, flags ───────────────────────────────────────────────────

    internal static ProfanitySummary Profanity(IReadOnlyList<ScoredComment> comments, IReadOnlyList<CommentText> texts)
    {
        var terms = new Dictionary<string, int>(StringComparer.Ordinal);
        var ids = new List<string>();
        var count = 0;
        for (var i = 0; i < comments.Count; i++)
        {
            var hits = Lexicons.Profanity.Match(texts[i]);
            if (hits.Count == 0) continue;
            count++;
            foreach (var h in hits.Distinct()) terms[h] = terms.GetValueOrDefault(h) + 1;
            if (ids.Count < 5 && comments[i].DuplicateOf is null) ids.Add(comments[i].Id);
        }
        return new ProfanitySummary
        {
            Comments = count, Pct = Pct(count, comments.Count),
            Terms = terms.OrderByDescending(kv => kv.Value).Take(8).Select(kv => new LabelCount { Key = Mask(kv.Key), Count = kv.Value, Pct = Pct(kv.Value, comments.Count) }).ToList(),
            ExampleIds = ids,
        };
    }

    /// <summary>"fuck" → "f***": the count is useful to a parent, the word itself is not.</summary>
    internal static string Mask(string w)
    {
        var runes = w.EnumerateRunes().ToArray();
        if (runes.Length <= 2) return runes[0].ToString() + "*";
        var sb = new StringBuilder(runes[0].ToString());
        for (var i = 1; i < runes.Length; i++) sb.Append(runes[i].Value == ' ' ? ' ' : '*');
        return sb.ToString();
    }

    internal static List<FlagGroup> Flags(IReadOnlyList<ScoredComment> comments, IReadOnlyList<CommentText> texts)
    {
        var groups = new List<FlagGroup>();
        foreach (var (key, label, lex) in Lexicons.RedFlags)
        {
            var terms = new Dictionary<string, int>(StringComparer.Ordinal);
            var ids = new List<string>();
            var count = 0;
            for (var i = 0; i < comments.Count; i++)
            {
                var hits = lex.Match(texts[i]);
                if (hits.Count == 0) continue;
                count++;
                foreach (var h in hits.Distinct()) terms[h] = terms.GetValueOrDefault(h) + 1;
                if (ids.Count < 4 && comments[i].DuplicateOf is null) ids.Add(comments[i].Id);
            }
            if (count == 0) continue;
            groups.Add(new FlagGroup { Key = key, Label = label, Count = count, Pct = Pct(count, comments.Count), Terms = terms.OrderByDescending(kv => kv.Value).Take(5).Select(kv => kv.Key).ToList(), ExampleIds = ids });
        }
        return groups.OrderByDescending(g => g.Count).ToList();
    }

    // ── family guide ───────────────────────────────────────────────────────

    internal static FamilyGuide Family(VideoInfo v, IReadOnlyList<ScoredComment> comments, IReadOnlyList<CommentText> texts, GenreGuess genre, AgeGuess age, ProfanitySummary profanity,
        List<FlagGroup> flags, ToxicitySummary? toxicity, SentimentSummary sentiment, IReadOnlyList<LabelCount> languages, Dictionary<string, List<string>> highlights, ThreadSummary threads)
    {
        var n = comments.Count;
        double Flag(string key) => flags.FirstOrDefault(f => f.Key == key)?.Pct ?? 0;
        var toxicPct = toxicity?.Pct ?? 0;
        var checks = new List<FamilyCheck>();
        var reasons = new List<string>();
        var bad = 0;
        var warn = 0;

        void Check(string key, string label, string status, string detail)
        {
            checks.Add(new FamilyCheck { Key = key, Label = label, Status = status, Detail = detail });
            if (status == "bad") bad++;
            else if (status == "warn") warn++;
        }

        // learning
        var learnTerms = new Dictionary<string, int>(StringComparer.Ordinal);
        var learnIds = new List<(string Id, long Likes)>();
        var learnCount = 0;
        var wholesome = 0;
        var links = 0;
        for (var i = 0; i < n; i++)
        {
            var hits = Lexicons.Learning.Match(texts[i]);
            if (hits.Count > 0)
            {
                learnCount++;
                foreach (var h in hits.Distinct()) learnTerms[h] = learnTerms.GetValueOrDefault(h) + 1;
                if (comments[i].DuplicateOf is null && comments[i].Text.Length >= 25) learnIds.Add((comments[i].Id, comments[i].Likes));
            }
            if (Lexicons.Wholesome.Any(texts[i])) wholesome++;
            if (texts[i].Tokens.Contains("http")) links++;
        }
        var learnPct = Pct(learnCount, n);
        var learnPrior = genre.Key switch { "education" => 30, "science" => 22, "docu" => 20, "howto" => 18, "news" => 8, "podcast" => 10, "health" => 10, "finance" => 8, "art" => 8, _ => 0 } + (v.Category is "Education" ? 12 : v.Category is "Science & Technology" ? 6 : 0);
        var learnScore = (int)Math.Round(Math.Min(100, learnPct * 3.2 + learnPrior));
        var learning = new LearningSignal
        {
            Score = learnScore, Level = learnScore >= 60 ? "strong" : learnScore >= 30 ? "some" : learnScore >= 10 ? "light" : "little", Comments = learnCount, Pct = learnPct,
            Terms = learnTerms.OrderByDescending(kv => kv.Value).Take(10).Select(kv => new LabelCount { Key = kv.Key, Count = kv.Value, Pct = Pct(kv.Value, n) }).ToList(),
            ExampleIds = learnIds.OrderByDescending(x => x.Likes).Take(3).Select(x => x.Id).ToList(),
        };

        // rating
        string rating;
        if (n < 20) { rating = "unknown"; reasons.Add("Fewer than 20 comments were read, which is too little to judge the comment section by."); }
        else
        {
            if (v.AgeLimit is >= 18) { rating = "red"; reasons.Add("YouTube itself marks this video 18+."); }
            else rating = "green";
            if (profanity.Pct >= 8) { rating = "red"; reasons.Add($"Swearing in {profanity.Pct:0}% of comments."); }
            else if (profanity.Pct >= 2.5) { if (rating == "green") rating = "amber"; reasons.Add($"Swearing in {profanity.Pct:0.#}% of comments."); }
            if (toxicPct >= 10) { rating = "red"; reasons.Add($"{toxicPct:0}% of comments read as toxic."); }
            else if (toxicPct >= 4) { if (rating == "green") rating = "amber"; reasons.Add($"{toxicPct:0.#}% of comments read as toxic."); }
            if (Flag("adult") >= 2.5) { rating = "red"; reasons.Add("Adult or sexual content comes up repeatedly in the comments."); }
            else if (Flag("adult") >= 0.8) { if (rating == "green") rating = "amber"; reasons.Add("A few comments mention adult content."); }
            if (Flag("gambling") >= 4) { rating = "red"; reasons.Add("Gambling or betting comes up in the comments."); }
            else if (Flag("gambling") >= 1.5) { if (rating == "green") rating = "amber"; reasons.Add("Gambling or betting is mentioned."); }
            if (Flag("substances") >= 3) { rating = "red"; reasons.Add("Alcohol or drugs come up often in the comments."); }
            else if (Flag("substances") >= 1) { if (rating == "green") rating = "amber"; reasons.Add("Alcohol or drugs are mentioned in some comments."); }
            if (genre.Key == "horror" && genre.Confidence >= 0.3) { if (rating == "green") rating = "amber"; reasons.Add("Horror content: expect scares."); }
            if (Flag("violence") >= 2.5) { if (rating == "green") rating = "amber"; reasons.Add($"Violence, weapons or war come up in {Flag("violence"):0.#}% of comments."); }
            if (Flag("danger") >= 1.5) { if (rating == "green") rating = "amber"; reasons.Add("Comments warn about dangerous stunts or challenges."); }
            if (Flag("distress") >= 1.5) { if (rating == "green") rating = "amber"; reasons.Add("Mental-health or self-harm topics come up in the comments."); }
            if (Flag("scam") >= 2.5) { if (rating == "green") rating = "amber"; reasons.Add($"{Flag("scam"):0.#}% of comments warn about scams or fraud."); }
            if (sentiment.Polarization >= 0.42 && n >= 50) { if (rating == "green") rating = "amber"; reasons.Add("The audience is sharply divided; expect heated replies."); }
            if (genre.Key == "news" && Flag("belief") >= 4) { if (rating == "green") rating = "amber"; reasons.Add("Religion and politics run through the discussion."); }
            if (rating == "green") reasons.Add(profanity.Comments == 0 ? "No swearing found in the comments read." : $"Only {profanity.Comments:N0} comments contain swearing.");
            if (rating == "green" && toxicity is not null) reasons.Add(toxicity.Flagged == 0 ? "No toxic comments flagged." : $"Toxic comments stay at {toxicPct:0.#}%, within the usual range.");
            if (rating == "green" && wholesome > 0) reasons.Add($"{Pct(wholesome, n):0}% of comments are warm or grateful.");
        }

        // checks (the printable list)
        Check("age-limit", "YouTube age restriction", v.AgeLimit is >= 18 ? "bad" : "ok", v.AgeLimit is >= 18 ? "Marked 18+ by YouTube." : "No age restriction set by YouTube.");
        Check("profanity", "Swearing in comments", profanity.Pct >= 8 ? "bad" : profanity.Pct >= 2.5 ? "warn" : "ok", profanity.Comments == 0 ? "None found." : $"{profanity.Comments:N0} comments ({profanity.Pct:0.#}%) contain swear words.");
        if (toxicity is not null) Check("toxic", "Toxic comments", toxicPct >= 10 ? "bad" : toxicPct >= 4 ? "warn" : "ok", toxicity.Flagged == 0 ? "None flagged by the toxicity model." : $"{toxicity.Flagged:N0} comments ({toxicPct:0.#}%) flagged as insulting or hateful.");
        else Check("toxic", "Toxic comments", "info", "Not measured in this profile; run the Deep profile to check for insults and hate speech.");
        var adultPct = Flag("adult");
        var riskyPct = Flag("gambling") + Flag("substances");
        Check("adult", "Adult themes, gambling, substances", adultPct >= 2.5 || Flag("gambling") >= 4 || Flag("substances") >= 3 ? "bad" : adultPct >= 0.8 || Flag("gambling") >= 1.5 || Flag("substances") >= 1 ? "warn" : "ok",
            adultPct + riskyPct == 0 ? "None mentioned." : $"Mentioned in about {adultPct + riskyPct:0.#}% of comments.");
        var violencePct = Flag("violence");
        Check("violence", "Violence, weapons or war", violencePct >= 6 ? "bad" : violencePct >= 2.5 ? "warn" : "ok", violencePct == 0 ? "None mentioned." : $"Mentioned in {violencePct:0.#}% of comments" + (genre.Key is "news" or "docu" or "gaming" ? " (common for this kind of video)." : "."));
        Check("danger", "Dangerous stunts", Flag("danger") >= 1.5 ? "warn" : "ok", Flag("danger") == 0 ? "No warnings from commenters." : $"{flags.First(f => f.Key == "danger").Count:N0} comments warn about dangerous behaviour.");
        Check("debate", "Debate heat", sentiment.Polarization >= 0.42 ? "warn" : sentiment.Polarization >= 0.25 ? "info" : "ok",
            sentiment.Polarization >= 0.42 ? "Two large camps; replies get heated." : sentiment.Polarization >= 0.25 ? "Some disagreement in the comments." : "Mostly one-sided and calm.");
        var repeats = comments.Count(c => c.Repeats > 1 || c.DuplicateOf is not null);
        Check("spam", "Spam, repeats and links", Pct(links, n) >= 8 || Pct(repeats, n) >= 35 ? "warn" : "ok", $"{Pct(repeats, n):0}% repeated comments, {Pct(links, n):0.#}% carry links.");
        Check("creator", "Creator in the comments", threads.UploaderComments > 0 ? "ok" : "info", threads.UploaderComments > 0 ? $"The creator replied {threads.UploaderComments:N0} time{(threads.UploaderComments == 1 ? "" : "s")}" + (threads.Hearted > 0 ? $" and hearted {threads.Hearted:N0}." : ".") : "No replies from the creator among the comments read.");
        var dur = v.DurationSeconds;
        Check("length", "Length", "info", dur is null ? "Unknown." : dur <= 240 ? "Short enough to show in full and discuss." : dur <= 1200 ? "Fits a single lesson slot." : dur <= 3600 ? "Plan a full session, or pick a segment." : "Over an hour: choose segments.");
        Check("learning", "Learning signals", learning.Level is "strong" or "some" ? "ok" : "info", learning.Comments == 0 ? "Commenters don't mention learning anything." : $"{learning.Pct:0}% of comments mention learning, understanding or school.");
        if (age.CoViewing > 0 || age.Teachers > 0) Check("audience", "Parents and teachers present", "ok", $"{age.CoViewing:N0} comments from parents watching with children, {age.Teachers:N0} from teachers.");
        if (Flag("accuracy") >= 2) Check("accuracy", "Accuracy disputed", "warn", $"{Flag("accuracy"):0.#}% of comments call the content fake, misleading or biased — a media-literacy moment.");
        if (Flag("distress") > 0) Check("distress", "Mental-health topics", Flag("distress") >= 1.5 ? "warn" : "info", $"Mentioned in {Flag("distress"):0.#}% of comments.");

        var (label, headline) = rating switch
        {
            "green" => ("Looks fine to share", "The comment section reads clean: little swearing, little toxicity, no adult themes in what was read."),
            "amber" => ("Preview before sharing", "Mostly fine, but a few things below deserve a look before showing it to younger viewers."),
            "red" => ("Not for younger viewers", "The comments (or YouTube's own rating) point to adult content or a rough comment section."),
            _ => ("Not enough comments to judge", "Too few comments were read to say anything about the comment section."),
        };

        // suggested uses
        var uses = new List<string>();
        var qShare = n == 0 ? 0 : comments.Count(c => c.Text.Contains('?')) * 100.0 / n;
        if (rating == "red") uses.Add("Not for the classroom or family viewing");
        if (rating == "amber") uses.Add("Preview first");
        if (rating != "red" && learning.Level is "strong" or "some") uses.Add("Homework or revision reference");
        if (rating != "red" && (qShare >= 6 || sentiment.Polarization >= 0.3)) uses.Add("Discussion starter");
        if (rating == "green" && (age.CoViewing > 0 || genre.Key is "kids" or "animals" or "science" or "art")) uses.Add("Watch together");
        if (Flag("accuracy") >= 2 || Flag("scam") >= 2) uses.Add("Media-literacy exercise: who disputes what, and why?");
        var topForeign = languages.FirstOrDefault(l => l.Key != "und" && Base(l.Key) != "en" && l.Pct >= 30);
        if (topForeign is not null && rating != "red") uses.Add($"Language practice: {topForeign.Label} ({topForeign.Pct:0}% of comments)");
        if (dur is { } d2)
        {
            if (d2 <= 240) uses.Add("Short enough for a warm-up or a break");
            else if (d2 <= 1200) uses.Add("Fits one lesson");
            else if (d2 <= 3600) uses.Add("Needs a full session, or pick a segment");
            else uses.Add("Split across sessions");
        }
        if (genre.Key == "relax" && rating != "red") uses.Add("Background for quiet work");
        if (genre.Key == "motivation" && rating != "red") uses.Add("Assembly or pep-talk material");

        var heat = sentiment.Polarization >= 0.42 || (threads.TopLevelScore is { } tl && threads.ReplyScore is { } rp && tl - rp >= 25) ? "high" : sentiment.Polarization >= 0.25 ? "medium" : "low";
        var starters = (highlights.GetValueOrDefault("questions") ?? []).Take(4).ToList();

        return new FamilyGuide
        {
            Rating = rating, Label = label, Headline = headline, Reasons = reasons, Checks = checks, Learning = learning, Profanity = profanity, Flags = flags,
            Wholesome = wholesome, WholesomePct = Pct(wholesome, n), ConversationStarterIds = starters, SuggestedUses = uses,
            LanguageExposure = languages.Where(l => l.Key != "und").Take(5).ToList(), DebateHeat = heat,
            Disclaimer = "This page reads public comments and the video's own labels. It does not watch the video. Treat it as a first screen, not a rating: preview anything you plan to show a class or a child.",
        };
    }

    // ── narrative ──────────────────────────────────────────────────────────

    /// <summary>Four plain sentences a parent can read in ten seconds; the sections below carry the numbers.</summary>
    public static string Summary(Report r)
    {
        var a = r.Audience;
        var v = r.Video;
        var s = r.Sentiment;
        var sb = new StringBuilder();
        if (a is not null)
        {
            var adj = a.Genre.Format switch { "short" or "clip" => "short ", "long" => "long-form ", "feature" => "feature-length ", "live" => "live-streamed ", _ => "" };
            var what = adj + GenreNoun(a.Genre.Key);
            sb.Append("AEIOUaeiou".Contains(what[0]) ? "An " : "A ").Append(what);
            if (!string.IsNullOrEmpty(v.Channel)) sb.Append($" from {v.Channel}");
            if (v.ChannelFollowers is > 0) sb.Append($" ({Compact(v.ChannelFollowers.Value)} subscribers)");
            if (v.UploadedAt is { } up) sb.Append($", uploaded {Ago(up)}");
            sb.Append(". ");
        }
        if (s.Analysed > 0)
        {
            sb.Append(s.Verdict switch
            {
                "loved" => "Viewers love it",
                "liked" => "Viewers like it",
                "leaning-positive" => "Viewers lean positive",
                "mixed" => "Reactions are mixed",
                "divided" => "The audience is divided",
                "leaning-negative" => "Viewers lean negative",
                "disliked" => "Viewers dislike it",
                "rejected" => "Viewers reject it",
                _ => "Too few comments to call it",
            });
            sb.Append($": {s.PositivePct:0}% of {s.Analysed:N0} analysed comments are positive and {s.NegativePct:0}% negative (net {(s.Score > 0 ? "+" : "")}{s.Score:0}). ");
        }
        if (a is not null)
        {
            var reach = a.Reach;
            if (reach.Key == "unplaced") sb.Append("The comments give almost no clue where viewers are. ");
            else if (reach.Key == "english-wide") sb.Append($"Comments are {reach.EnglishPct:0}% English and rarely name a place, so the audience is wide but hard to pin down");
            else if (reach.Key is "regional" or "local")
            {
                sb.Append($"The audience is mostly in {reach.Regions[0].Label} ({reach.Regions[0].Pct:0}% of placeable comments)");
                if (reach.Regions.Count > 1 && reach.Regions[1].Pct >= 8) sb.Append($", with some in {reach.Regions[1].Label}");
            }
            else
            {
                sb.Append(reach.Key == "global" ? "The audience is global" : "The audience spans several regions");
                if (reach.Regions.Count > 0) sb.Append($": {reach.Regions[0].Label} leads ({reach.Regions[0].Pct:0}% of placeable comments)");
                if (reach.Regions.Count > 1) sb.Append($", with {reach.Regions[1].Label} next");
            }
            if (reach.Key != "unplaced")
            {
                if (reach.LanguageCount > 1) sb.Append($"; {reach.LanguageCount} languages appear, led by {reach.PrimaryLanguage} ({reach.PrimaryLanguagePct:0}%)");
                sb.Append(". ");
            }
            sb.Append($"The crowd reads as {a.Age.Label.ToLowerInvariant()} ({a.Age.Range}), {a.Age.Confidence} confidence");
            if (a.Age.StatedAges.Count >= 3) sb.Append($"; {a.Age.StatedAges.Count} commenters state their age, median {a.Age.StatedAges.Median:0}");
            sb.Append(". ");
            sb.Append($"For parents and teachers: {a.Family.Label.ToLowerInvariant()}");
            if (a.Family.Reasons.Count > 0) sb.Append($" — {Decapitalise(a.Family.Reasons[0].TrimEnd('.'))}");
            sb.Append('.');
        }
        return sb.ToString().Trim();
    }

    /// <summary>"music" → "music video": the thing a parent would call it.</summary>
    internal static string GenreNoun(string key) => key switch
    {
        "music" => "music video", "gaming" => "gaming video", "education" => "explainer", "howto" => "tutorial", "news" => "news & politics video", "podcast" => "interview or podcast episode",
        "comedy" => "comedy video", "vlog" => "vlog", "kids" => "children's video", "film" => "film or TV clip", "sports" => "sports video", "food" => "cooking video", "tech" => "tech review",
        "science" => "science & nature video", "travel" => "travel video", "faith" => "religious or spiritual video", "finance" => "finance & business video", "health" => "health video",
        "motivation" => "motivational video", "art" => "art & craft video", "beauty" => "beauty & fashion video", "auto" => "car video", "relax" => "relaxation or ASMR video", "horror" => "horror video",
        "reaction" => "reaction or commentary video", "docu" => "documentary", "viral" => "viral clip", "animals" => "animal video", _ => "video",
    };

    private static string Decapitalise(string s) => s.Length == 0 || (s.Length > 1 && char.IsUpper(s[1])) ? s : char.ToLowerInvariant(s[0]) + s[1..];

    internal static string Compact(long n) => n switch
    {
        >= 1_000_000_000 => (n / 1_000_000_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "B",
        >= 1_000_000 => (n / 1_000_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "M",
        >= 10_000 => (n / 1_000.0).ToString("0", CultureInfo.InvariantCulture) + "K",
        >= 1_000 => (n / 1_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "K",
        _ => n.ToString(CultureInfo.InvariantCulture),
    };

    internal static string Ago(DateTimeOffset t)
    {
        var d = (DateTimeOffset.UtcNow - t).TotalDays;
        if (d < 1) return "today";
        if (d < 2) return "yesterday";
        if (d < 30) return $"{(int)d} days ago";
        if (d < 365) return $"{(int)(d / 30.4)} months ago";
        var y = (int)(d / 365.25);
        return y == 1 ? "a year ago" : $"{y} years ago";
    }
}
