using System.Globalization;
using Ark.Feedback.Nlp;
using Ark.Feedback.YouTube;

namespace Ark.Feedback.Analysis;

/// <summary>Everything the pipeline measured, handed to the builder.</summary>
public sealed class AnalysisFacts
{
    public required string Id { get; init; }
    public required AnalysisProfile Profile { get; init; }
    public required string AppVersion { get; init; }
    public required VideoInfo Video { get; init; }
    public required FetchSummary Fetch { get; init; }
    /// <summary>Scored comments (exact repeats included, carrying the score of the comment they repeat).</summary>
    public required List<ScoredComment> Comments { get; init; }
    public float[][]? Embeddings { get; init; }
    public KMeans.Result? Clusters { get; init; }
    public List<ModelUse> Models { get; init; } = [];
    public required ComputeSummary Compute { get; init; }
    public string? ToxicityLabelNote { get; init; }
}

/// <summary>Turns scored comments into the report: aggregates, themes, keywords, highlights and plain-language insights.</summary>
public static class ReportBuilder
{
    public static Report Build(AnalysisFacts f)
    {
        var comments = f.Comments;
        var n = comments.Count;
        var sentiment = Summarise(comments);
        var languages = Languages(comments);
        var timeline = Timeline(comments);
        var themes = Themes(f);
        var (posKw, negKw) = Keywords(comments);
        var emoji = Emoji(comments);
        var emotions = f.Profile.Emotion ? Emotions(comments) : null;
        var toxicity = f.Profile.Toxicity ? Toxicity(comments) : null;
        var threads = Threads(comments);
        var engagement = Engagement(f.Video, comments);
        var highlights = Highlights(comments, toxicity);
        var report = new Report
        {
            Id = f.Id,
            CreatedAt = DateTimeOffset.UtcNow,
            Profile = f.Profile.Id,
            ProfileName = f.Profile.Name,
            AppVersion = f.AppVersion,
            Video = f.Video,
            Fetch = f.Fetch,
            Sentiment = sentiment,
            Languages = languages,
            Timeline = timeline,
            Themes = themes,
            PositiveKeywords = posKw,
            NegativeKeywords = negKw,
            Emoji = emoji,
            Emotions = emotions,
            Toxicity = toxicity,
            Threads = threads,
            Engagement = engagement,
            Highlights = highlights,
            Models = f.Models,
            Compute = f.Compute,
            Comments = comments,
        };
        report.Insights.AddRange(Insights(report));
        return report;
    }

    // ── sentiment ──────────────────────────────────────────────────────────

    internal static SentimentSummary Summarise(IReadOnlyList<ScoredComment> c)
    {
        var n = c.Count;
        int pos = 0, neu = 0, neg = 0;
        double wPos = 0, wNeg = 0, wAll = 0, mean = 0, conf = 0;
        foreach (var x in c)
        {
            switch (x.Sentiment) { case "positive": pos++; break; case "negative": neg++; break; default: neu++; break; }
            var w = 1 + Math.Log10(1 + Math.Max(0, x.Likes));
            wAll += w;
            if (x.Sentiment == "positive") wPos += w;
            else if (x.Sentiment == "negative") wNeg += w;
            mean += x.Score;
            conf += Math.Max(x.PPos, Math.Max(x.PNeu, x.PNeg));
        }
        var s = new SentimentSummary
        {
            Analysed = n, Positive = pos, Neutral = neu, Negative = neg,
            PositivePct = Pct(pos, n), NeutralPct = Pct(neu, n), NegativePct = Pct(neg, n),
            Score = n == 0 ? 0 : Math.Round((pos - neg) * 100.0 / n, 1),
            WeightedScore = wAll == 0 ? 0 : Math.Round((wPos - wNeg) * 100.0 / wAll, 1),
            MeanScore = n == 0 ? 0 : Math.Round(mean * 100.0 / n, 1),
            Polarization = n == 0 ? 0 : Math.Round(2 * Math.Min(pos, neg) / (double)n, 3),
            MeanConfidence = n == 0 ? 0 : Math.Round(conf / n, 3),
            Verdict = "none", VerdictText = "",
        };
        (s.Verdict, s.VerdictText) = Verdict(s);
        return s;
    }

    internal static (string, string) Verdict(SentimentSummary s)
    {
        if (s.Analysed == 0) return ("none", "No comments to read.");
        if (s.Analysed < 8) return ("thin", "Too few comments for a verdict; the numbers below are indicative at best.");
        if (s.Polarization >= 0.45) return ("divided", "The audience is split: large positive and negative camps.");
        return s.Score switch
        {
            >= 60 => ("loved", "Viewers love it: positive comments dominate."),
            >= 30 => ("liked", "Clearly well received."),
            >= 10 => ("leaning-positive", "More praise than complaint, with a sizeable neutral middle."),
            > -10 => ("mixed", "Praise and complaint roughly balance; most comments are neutral or conversational."),
            > -30 => ("leaning-negative", "More complaint than praise."),
            > -60 => ("disliked", "The audience is largely unhappy."),
            _ => ("rejected", "Overwhelmingly negative reception."),
        };
    }

    private static double Pct(int part, int whole) => whole == 0 ? 0 : Math.Round(part * 100.0 / whole, 1);

    private static double? MeanScore100(IEnumerable<ScoredComment> xs)
    {
        var list = xs.ToList();
        return list.Count == 0 ? null : Math.Round(list.Average(x => x.Score) * 100, 1);
    }

    private static LabelCount Bucket(string key, string? label, IReadOnlyList<ScoredComment> all, IEnumerable<ScoredComment> xs)
    {
        var list = xs.ToList();
        return new LabelCount
        {
            Key = key, Label = label, Count = list.Count, Pct = Pct(list.Count, all.Count), Score = MeanScore100(list),
            Positive = list.Count(x => x.Sentiment == "positive"), Neutral = list.Count(x => x.Sentiment == "neutral"), Negative = list.Count(x => x.Sentiment == "negative"),
        };
    }

    // ── breakdowns ─────────────────────────────────────────────────────────

    internal static List<LabelCount> Languages(IReadOnlyList<ScoredComment> c) =>
        c.GroupBy(x => x.Lang).Select(g => Bucket(g.Key, LanguageDetector.Name(g.Key), c, g))
            .OrderByDescending(l => l.Count).ThenBy(l => l.Key, StringComparer.Ordinal).ToList();

    internal static List<TimeBucket> Timeline(IReadOnlyList<ScoredComment> c)
    {
        var dated = c.Where(x => x.PublishedAt is not null).ToList();
        if (dated.Count < 5) return [];
        var min = dated.Min(x => x.PublishedAt!.Value);
        var max = dated.Max(x => x.PublishedAt!.Value);
        var span = max - min;
        string Unit = span.TotalDays <= 21 ? "day" : span.TotalDays <= 200 ? "week" : span.TotalDays <= 1100 ? "month" : "quarter";
        static DateTime Floor(DateTimeOffset t, string unit)
        {
            var d = t.UtcDateTime.Date;
            return unit switch
            {
                "day" => d,
                "week" => d.AddDays(-(((int)d.DayOfWeek + 6) % 7)),
                "month" => new DateTime(d.Year, d.Month, 1),
                _ => new DateTime(d.Year, (d.Month - 1) / 3 * 3 + 1, 1),
            };
        }
        var groups = dated.GroupBy(x => Floor(x.PublishedAt!.Value, Unit)).OrderBy(g => g.Key).ToList();
        var list = new List<TimeBucket>();
        foreach (var g in groups)
        {
            var label = Unit switch
            {
                "day" => g.Key.ToString("d MMM", CultureInfo.InvariantCulture),
                "week" => "wk " + g.Key.ToString("d MMM", CultureInfo.InvariantCulture),
                "month" => g.Key.ToString("MMM yyyy", CultureInfo.InvariantCulture),
                _ => $"Q{(g.Key.Month - 1) / 3 + 1} {g.Key.Year}",
            };
            list.Add(new TimeBucket
            {
                Start = g.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), Label = label, Count = g.Count(),
                Positive = g.Count(x => x.Sentiment == "positive"), Neutral = g.Count(x => x.Sentiment == "neutral"), Negative = g.Count(x => x.Sentiment == "negative"),
                Score = MeanScore100(g),
            });
        }
        return list;
    }

    internal static List<Theme> Themes(AnalysisFacts f)
    {
        if (f.Clusters is null || f.Embeddings is null) return [];
        var c = f.Comments;
        var assign = f.Clusters.Assignment;
        var k = f.Clusters.Centroids.Length;
        // c-TF-IDF: a word's weight in a cluster is its frequency there, discounted by how many clusters use it.
        var clusterWords = new Dictionary<string, int>[k];
        var df = new Dictionary<string, int>();
        for (var i = 0; i < k; i++) clusterWords[i] = new Dictionary<string, int>(StringComparer.Ordinal);
        var themeIndex = new Dictionary<int, List<int>>();
        for (var i = 0; i < c.Count; i++)
        {
            if (c[i].Theme is not { } t) continue;
            themeIndex.TryAdd(t, []);
            themeIndex[t].Add(i);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var w in TextTools.Words(c[i].Text, c[i].Lang))
            {
                clusterWords[t][w] = clusterWords[t].GetValueOrDefault(w) + 1;
                if (seen.Add(w)) { }
            }
        }
        foreach (var words in clusterWords) foreach (var w in words.Keys) df[w] = df.GetValueOrDefault(w) + 1;

        var themes = new List<Theme>();
        for (var t = 0; t < k; t++)
        {
            if (!themeIndex.TryGetValue(t, out var members) || members.Count == 0) continue;
            var total = Math.Max(1, clusterWords[t].Values.Sum());
            var keywords = clusterWords[t]
                .Where(kv => kv.Value >= 2 || members.Count < 6)
                .Select(kv => (Word: kv.Key, Weight: kv.Value / (double)total * Math.Log(1 + (double)k / df[kv.Key]) * (kv.Key.Length >= 4 ? 1 : 0.6)))
                .OrderByDescending(x => x.Weight).ThenBy(x => x.Word, StringComparer.Ordinal).Take(6).Select(x => x.Word).ToList();
            var xs = members.Select(i => c[i]).ToList();
            var quotes = members
                .OrderByDescending(i => f.Clusters.Similarity[i] + Math.Min(0.15, Math.Log10(1 + c[i].Likes) * 0.03) - (c[i].DuplicateOf is null ? 0 : 1))
                .Where(i => c[i].Text.Length >= 12).Take(3).Select(i => c[i].Id).ToList();
            var theme = new Theme
            {
                Id = t, Label = keywords.Count == 0 ? $"Theme {t + 1}" : string.Join(" · ", keywords.Take(3)), Keywords = keywords,
                Count = xs.Count, Pct = Pct(xs.Count, c.Count), Score = MeanScore100(xs) ?? 0,
                Positive = xs.Count(x => x.Sentiment == "positive"), Neutral = xs.Count(x => x.Sentiment == "neutral"), Negative = xs.Count(x => x.Sentiment == "negative"),
                QuoteIds = quotes,
                Languages = xs.GroupBy(x => x.Lang).OrderByDescending(g => g.Count()).Take(3).Select(g => g.Key).ToList(),
            };
            themes.Add(theme);
        }
        return themes.OrderByDescending(t => t.Count).ToList();
    }

    internal static (List<LabelCount> Positive, List<LabelCount> Negative) Keywords(IReadOnlyList<ScoredComment> c)
    {
        var pos = new Dictionary<string, int>(StringComparer.Ordinal);
        var neg = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var x in c)
        {
            if (x.DuplicateOf is not null) continue;
            var target = x.Sentiment == "positive" ? pos : x.Sentiment == "negative" ? neg : null;
            if (target is null) continue;
            foreach (var w in TextTools.Words(x.Text, x.Lang).Distinct()) target[w] = target.GetValueOrDefault(w) + 1;
        }
        List<LabelCount> Top(Dictionary<string, int> d, Dictionary<string, int> other, int total) =>
            d.Where(kv => kv.Value >= 2)
             .Select(kv => (kv.Key, kv.Value, Distinct: kv.Value / (1.0 + other.GetValueOrDefault(kv.Key))))
             .OrderByDescending(x => x.Distinct).ThenByDescending(x => x.Value).Take(18)
             .Select(x => new LabelCount { Key = x.Key, Count = x.Value, Pct = Pct(x.Value, total) }).ToList();
        var nPos = c.Count(x => x.Sentiment == "positive");
        var nNeg = c.Count(x => x.Sentiment == "negative");
        return (Top(pos, neg, nPos), Top(neg, pos, nNeg));
    }

    internal static List<LabelCount> Emoji(IReadOnlyList<ScoredComment> c)
    {
        var counts = new Dictionary<string, (int Count, double Score, int Pos, int Neu, int Neg)>(StringComparer.Ordinal);
        foreach (var x in c)
            foreach (var e in TextTools.Emoji(x.Text).Distinct())
            {
                var cur = counts.GetValueOrDefault(e);
                counts[e] = (cur.Count + 1, cur.Score + x.Score, cur.Pos + (x.Sentiment == "positive" ? 1 : 0), cur.Neu + (x.Sentiment == "neutral" ? 1 : 0), cur.Neg + (x.Sentiment == "negative" ? 1 : 0));
            }
        return counts.OrderByDescending(kv => kv.Value.Count).Take(12)
            .Select(kv => new LabelCount { Key = kv.Key, Count = kv.Value.Count, Pct = Pct(kv.Value.Count, c.Count), Score = Math.Round(kv.Value.Score * 100 / kv.Value.Count, 1), Positive = kv.Value.Pos, Neutral = kv.Value.Neu, Negative = kv.Value.Neg })
            .ToList();
    }

    internal static List<LabelCount> Emotions(IReadOnlyList<ScoredComment> c)
    {
        var with = c.Where(x => x.Emotion is not null).ToList();
        return with.GroupBy(x => x.Emotion!).Select(g => Bucket(g.Key, Capital(g.Key), c, g)).OrderByDescending(l => l.Count).Take(12).ToList();
    }

    /// <summary>A comment counts as toxic above this probability; 0.5 flags too much mild grumbling on the multilingual model.</summary>
    public const double ToxicThreshold = 0.7;

    internal static ToxicitySummary Toxicity(IReadOnlyList<ScoredComment> c)
    {
        var flagged = c.Where(x => x.Toxic is >= ToxicThreshold).OrderByDescending(x => x.Toxic).ThenByDescending(x => x.Likes).ToList();
        return new ToxicitySummary { Flagged = flagged.Count, Pct = Pct(flagged.Count, c.Count), Threshold = ToxicThreshold, ExampleIds = flagged.Take(5).Select(x => x.Id).ToList() };
    }

    internal static ThreadSummary Threads(IReadOnlyList<ScoredComment> c) => new()
    {
        TopLevelScore = MeanScore100(c.Where(x => !x.IsReply)),
        ReplyScore = MeanScore100(c.Where(x => x.IsReply)),
        UploaderComments = c.Count(x => x.AuthorIsUploader),
        Hearted = c.Count(x => x.IsHearted),
        Verified = c.Count(x => x.AuthorIsVerified),
        PinnedId = c.FirstOrDefault(x => x.IsPinned)?.Id,
    };

    internal static Engagement Engagement(VideoInfo v, IReadOnlyList<ScoredComment> c)
    {
        var days = v.UploadedAt is { } up ? Math.Max(1, (int)(DateTimeOffset.UtcNow - up).TotalDays) : 0;
        var total = v.CommentCount ?? c.Count;
        return new Engagement
        {
            CommentsPerThousandViews = v.ViewCount is > 0 ? Math.Round(total * 1000.0 / v.ViewCount.Value, 2) : null,
            LikesPerComment = c.Count == 0 ? null : Math.Round(c.Average(x => (double)x.Likes), 1),
            AvgLength = c.Count == 0 ? 0 : Math.Round(c.Average(x => (double)x.Text.Length), 0),
            CommentsPerDay = days > 0 ? Math.Round(total / (double)days, 2) : null,
            DaysSinceUpload = days,
            EmojiShare = c.Count == 0 ? 0 : Math.Round(c.Count(x => TextTools.Emoji(x.Text).Any()) * 100.0 / c.Count, 1),
            QuestionShare = c.Count == 0 ? 0 : Math.Round(c.Count(x => x.Text.Contains('?')) * 100.0 / c.Count, 1),
        };
    }

    internal static Dictionary<string, List<string>> Highlights(IReadOnlyList<ScoredComment> c, ToxicitySummary? tox)
    {
        var unique = c.Where(x => x.DuplicateOf is null && x.Text.Length >= 8).ToList();
        var h = new Dictionary<string, List<string>>
        {
            ["mostLiked"] = unique.OrderByDescending(x => x.Likes).Take(5).Select(x => x.Id).ToList(),
            ["mostPositive"] = unique.Where(x => x.Sentiment == "positive").OrderByDescending(x => x.PPos + Math.Min(0.1, Math.Log10(1 + x.Likes) * 0.02)).Take(5).Select(x => x.Id).ToList(),
            ["mostNegative"] = unique.Where(x => x.Sentiment == "negative").OrderByDescending(x => x.PNeg + Math.Min(0.1, Math.Log10(1 + x.Likes) * 0.02)).Take(5).Select(x => x.Id).ToList(),
            ["mostRepeated"] = c.Where(x => x.Repeats > 1).OrderByDescending(x => x.Repeats).Take(5).Select(x => x.Id).ToList(),
            ["creator"] = c.Where(x => x.AuthorIsUploader).OrderByDescending(x => x.Likes).Take(5).Select(x => x.Id).ToList(),
            ["questions"] = unique.Where(x => x.Text.Contains('?')).OrderByDescending(x => x.Likes).Take(5).Select(x => x.Id).ToList(),
        };
        if (tox is not null) h["toxic"] = tox.ExampleIds;
        return h;
    }

    // ── insights ───────────────────────────────────────────────────────────

    internal static IEnumerable<Insight> Insights(Report r)
    {
        var s = r.Sentiment;
        var n = s.Analysed;
        if (n == 0) yield break;

        yield return new Insight
        {
            Kind = s.Score >= 10 ? "good" : s.Score <= -10 ? "bad" : "mixed",
            Title = $"{s.PositivePct:0}% positive, {s.NegativePct:0}% negative",
            Text = $"{s.VerdictText} Across {n:N0} analysed comments the net score is {Signed(s.Score)} (positive minus negative share).",
        };

        var gap = s.WeightedScore - s.Score;
        if (Math.Abs(gap) >= 12)
            yield return new Insight
            {
                Kind = gap > 0 ? "good" : "bad",
                Title = gap > 0 ? "The most-liked comments are more positive than the crowd" : "The most-liked comments are more critical than the crowd",
                Text = $"Weighting each comment by its likes moves the score from {Signed(s.Score)} to {Signed(s.WeightedScore)}: the comments other viewers endorse {(gap > 0 ? "praise" : "criticise")} more than the average comment does.",
            };

        if (s.Polarization >= 0.35)
            yield return new Insight
            {
                Kind = "mixed",
                Title = "A divided audience",
                Text = $"Both camps are large ({s.PositivePct:0}% positive vs {s.NegativePct:0}% negative). Expect strong reactions either way; the themes below show what each side is talking about.",
            };

        var langs = r.Languages.Where(l => l.Key != "und").ToList();
        if (langs.Count >= 2)
        {
            var top = langs[0];
            var others = langs.Skip(1).Where(l => l.Count >= 10 && l.Score is not null).ToList();
            var outlier = others.OrderByDescending(l => Math.Abs((l.Score ?? 0) - (top.Score ?? 0))).FirstOrDefault();
            yield return new Insight
            {
                Kind = "info",
                Title = $"{langs.Count} languages, led by {top.Label} ({top.Pct:0}%)",
                Text = outlier is not null && Math.Abs((outlier.Score ?? 0) - (top.Score ?? 0)) >= 20
                    ? $"{outlier.Label} speakers ({outlier.Pct:0}% of comments) are {((outlier.Score ?? 0) > (top.Score ?? 0) ? "noticeably warmer" : "noticeably more critical")} than {top.Label} speakers (score {Signed(outlier.Score ?? 0)} vs {Signed(top.Score ?? 0)})."
                    : $"Sentiment is consistent across languages; the top three are {string.Join(", ", langs.Take(3).Select(l => $"{l.Label} {l.Pct:0}%"))}.",
            };
        }

        if (r.Themes.Count > 0)
        {
            var worst = r.Themes.Where(t => t.Count >= 8).OrderBy(t => t.Score).FirstOrDefault();
            var best = r.Themes.Where(t => t.Count >= 8).OrderByDescending(t => t.Score).FirstOrDefault();
            if (worst is not null && worst.Score <= -10 && (best is null || best.Score - worst.Score >= 25))
                yield return new Insight
                {
                    Kind = "bad",
                    Title = $"Complaints cluster around “{worst.Label}”",
                    Text = $"{worst.Count:N0} comments ({worst.Pct:0}%) share this theme and score {Signed(worst.Score)}. The quotes under the theme show what viewers want changed.",
                };
            if (best is not null && best.Score >= 30)
                yield return new Insight
                {
                    Kind = "good",
                    Title = $"What lands best: “{best.Label}”",
                    Text = $"{best.Count:N0} comments ({best.Pct:0}%) praise this, scoring {Signed(best.Score)}. Worth more of in the next video.",
                };
        }

        if (r.Timeline.Count >= 4)
        {
            var dated = r.Timeline.Where(t => t.Count >= 5 && t.Score is not null).ToList();
            if (dated.Count >= 3)
            {
                var early = dated.Take(Math.Max(1, dated.Count / 3)).Average(t => t.Score!.Value);
                var late = dated.Skip(dated.Count - Math.Max(1, dated.Count / 3)).Average(t => t.Score!.Value);
                if (Math.Abs(late - early) >= 15)
                    yield return new Insight
                    {
                        Kind = late > early ? "good" : "bad",
                        Title = late > early ? "Reception has warmed over time" : "Reception has cooled over time",
                        Text = $"Early comments scored {Signed(early)}; the most recent ones {Signed(late)}. Newer viewers {(late > early ? "like it more" : "are more critical")} than the first wave.",
                    };
            }
        }

        if (r.Threads.TopLevelScore is { } tl && r.Threads.ReplyScore is { } rp && Math.Abs(tl - rp) >= 20 && r.Fetch.Replies >= 15)
            yield return new Insight
            {
                Kind = rp < tl ? "mixed" : "info",
                Title = rp < tl ? "Arguments in the replies" : "Replies are warmer than the top-level comments",
                Text = $"Top-level comments score {Signed(tl)}, replies {Signed(rp)}. {(rp < tl ? "Debates are happening under the comments rather than in them." : "Viewers reply to agree and encourage.")}",
            };

        if (r.Toxicity is { Flagged: > 0 } t)
            yield return new Insight
            {
                Kind = t.Pct >= 5 ? "bad" : "info",
                Title = $"{t.Pct:0.#}% of comments read as toxic",
                Text = t.Pct >= 5
                    ? $"{t.Flagged:N0} comments were flagged as insulting or hateful — more than the usual 1–3%. Moderation or holding comments for review may be worth it."
                    : $"{t.Flagged:N0} comments were flagged, which is within the normal range for a public comment section.",
            };

        if (r.Emotions is { Count: > 0 } em)
        {
            var top = em.Where(e => e.Key != "neutral").Take(3).ToList();
            if (top.Count > 0)
                yield return new Insight
                {
                    Kind = "info",
                    Title = $"Dominant emotions: {string.Join(", ", top.Select(e => e.Label))}",
                    Text = $"{top[0].Label} shows in {top[0].Pct:0}% of comments" + (top.Count > 1 ? $", {top[1].Label!.ToLowerInvariant()} in {top[1].Pct:0}%" : "") + ".",
                };
        }

        if (r.Fetch.ExactRepeats >= Math.Max(5, n / 20))
            yield return new Insight
            {
                Kind = "info",
                Title = $"{r.Fetch.ExactRepeats:N0} comments are word-for-word repeats",
                Text = "Copy-pasted comments (catchphrases, spam, “who’s watching in 2026”) were counted once for the themes and keywords but kept in the totals.",
            };

        if (r.Threads.UploaderComments > 0)
            yield return new Insight
            {
                Kind = "good",
                Title = $"The creator replied {r.Threads.UploaderComments:N0} time{(r.Threads.UploaderComments == 1 ? "" : "s")}",
                Text = r.Threads.Hearted > 0 ? $"And hearted {r.Threads.Hearted:N0} comments. An active creator in the comments lifts the mood of the section." : "An active creator in the comments lifts the mood of the section.",
            };

        if (r.Engagement.CommentsPerThousandViews is { } cpv)
            yield return new Insight
            {
                Kind = "info",
                Title = cpv >= 2 ? "An unusually talkative audience" : cpv >= 0.5 ? "A normal amount of conversation" : "A quiet audience",
                Text = $"{cpv:0.##} comments per 1,000 views" + (r.Engagement.CommentsPerDay is { } cpd ? $", about {cpd:0.#} a day since upload" : "") + $". Average comment length {r.Engagement.AvgLength:0} characters; {r.Engagement.QuestionShare:0}% ask a question.",
            };

        if (r.Fetch.Sampled && r.Fetch.SampleNote is { } note)
            yield return new Insight { Kind = "info", Title = "A sample was scored", Text = note };
    }

    private static string Signed(double v) => (v > 0 ? "+" : "") + v.ToString("0", CultureInfo.InvariantCulture);

    private static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
