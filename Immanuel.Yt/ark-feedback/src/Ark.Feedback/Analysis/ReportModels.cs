using Ark.Feedback.YouTube;

namespace Ark.Feedback.Analysis;

/// <summary>One analysed comment: what was fetched plus what the models said about it.</summary>
public sealed class ScoredComment
{
    public required string Id { get; init; }
    public string? ParentId { get; init; }
    public bool IsReply { get; init; }
    public required string Text { get; init; }
    public string? Author { get; init; }
    public string? AuthorId { get; init; }
    public bool AuthorIsUploader { get; init; }
    public bool AuthorIsVerified { get; init; }
    public bool IsPinned { get; init; }
    /// <summary>Hearted by the creator.</summary>
    public bool IsHearted { get; init; }
    public long Likes { get; init; }
    public DateTimeOffset? PublishedAt { get; init; }
    public string Lang { get; set; } = "und";
    public string LangName { get; set; } = "Unknown";
    public double LangConfidence { get; set; }
    /// <summary>positive | neutral | negative.</summary>
    public string Sentiment { get; set; } = "neutral";
    /// <summary>P(positive) − P(negative), −1..1.</summary>
    public double Score { get; set; }
    public double PPos { get; set; }
    public double PNeu { get; set; }
    public double PNeg { get; set; }
    public double? Toxic { get; set; }
    public string? Emotion { get; set; }
    public double? EmotionP { get; set; }
    public int? Theme { get; set; }
    /// <summary>Set when this comment is a word-for-word repeat of another analysed comment (it inherits that one's scores).</summary>
    public string? DuplicateOf { get; set; }
    /// <summary>How many fetched comments this one stands for (itself + exact repeats).</summary>
    public int Repeats { get; set; } = 1;
}

public sealed class LabelCount
{
    public required string Key { get; init; }
    public string? Label { get; init; }
    public int Count { get; set; }
    public double Pct { get; set; }
    /// <summary>Mean score of the comments behind this key, −100..100, when it makes sense.</summary>
    public double? Score { get; set; }
    public int Positive { get; set; }
    public int Neutral { get; set; }
    public int Negative { get; set; }
}

public sealed class TimeBucket
{
    public required string Start { get; init; }
    public required string Label { get; init; }
    public int Count { get; set; }
    public int Positive { get; set; }
    public int Neutral { get; set; }
    public int Negative { get; set; }
    public double? Score { get; set; }
}

public sealed class Theme
{
    public int Id { get; init; }
    public required string Label { get; set; }
    public List<string> Keywords { get; set; } = [];
    public int Count { get; set; }
    public double Pct { get; set; }
    public double Score { get; set; }
    public int Positive { get; set; }
    public int Neutral { get; set; }
    public int Negative { get; set; }
    public List<string> QuoteIds { get; set; } = [];
    public List<string> Languages { get; set; } = [];
}

public sealed class SentimentSummary
{
    public int Analysed { get; set; }
    public int Positive { get; set; }
    public int Neutral { get; set; }
    public int Negative { get; set; }
    public double PositivePct { get; set; }
    public double NeutralPct { get; set; }
    public double NegativePct { get; set; }
    /// <summary>(positive − negative) / analysed × 100: −100..100.</summary>
    public double Score { get; set; }
    /// <summary>The same, with every comment weighted by 1 + log10(1 + likes): what the audience endorsed.</summary>
    public double WeightedScore { get; set; }
    /// <summary>Mean of each comment's P(pos) − P(neg) × 100.</summary>
    public double MeanScore { get; set; }
    /// <summary>2 × min(positive share, negative share): 0 = one-sided, 1 = split down the middle.</summary>
    public double Polarization { get; set; }
    public double MeanConfidence { get; set; }
    public required string Verdict { get; set; }
    public required string VerdictText { get; set; }
}

public sealed class FetchSummary
{
    public required string Source { get; set; }
    public string? ToolVersion { get; set; }
    public int Fetched { get; set; }
    public int Analysed { get; set; }
    public int Unique { get; set; }
    public int ExactRepeats { get; set; }
    public int TopLevel { get; set; }
    public int Replies { get; set; }
    public long? TotalOnYouTube { get; set; }
    public bool Sampled { get; set; }
    public string? SampleNote { get; set; }
    public List<string> Warnings { get; set; } = [];
}

public sealed class Insight
{
    /// <summary>good | bad | mixed | info.</summary>
    public required string Kind { get; init; }
    public required string Title { get; init; }
    public required string Text { get; init; }
}

public sealed class ModelUse
{
    public required string Role { get; init; }
    public required string Slug { get; init; }
    public required string Name { get; init; }
    public int Items { get; set; }
    public long Ms { get; set; }
}

public sealed class ThreadSummary
{
    public double? TopLevelScore { get; set; }
    public double? ReplyScore { get; set; }
    public int UploaderComments { get; set; }
    public int Hearted { get; set; }
    public int Verified { get; set; }
    public string? PinnedId { get; set; }
}

public sealed class Engagement
{
    public double? CommentsPerThousandViews { get; set; }
    public double? LikesPerComment { get; set; }
    public double AvgLength { get; set; }
    public double? CommentsPerDay { get; set; }
    public int DaysSinceUpload { get; set; }
    public double EmojiShare { get; set; }
    public double QuestionShare { get; set; }
}

public sealed class ToxicitySummary
{
    public int Flagged { get; set; }
    public double Pct { get; set; }
    public double Threshold { get; set; } = 0.5;
    public List<string> ExampleIds { get; set; } = [];
}

public sealed class ComputeSummary
{
    public long TotalMs { get; set; }
    public long FetchMs { get; set; }
    public long InferMs { get; set; }
    public int Threads { get; set; }
    public double MsPerComment { get; set; }
    public bool FromCache { get; set; }
}

/// <summary>The report: everything the page draws and everything the audit log summarises.</summary>
public sealed class Report
{
    public const int SchemaVersion = 2;

    public required string Id { get; init; }
    public int Schema { get; init; } = SchemaVersion;
    public DateTimeOffset CreatedAt { get; init; }
    public required string Profile { get; init; }
    public required string ProfileName { get; init; }
    public required string AppVersion { get; init; }
    public required VideoInfo Video { get; init; }
    public required FetchSummary Fetch { get; init; }
    public required SentimentSummary Sentiment { get; init; }
    public List<LabelCount> Languages { get; init; } = [];
    public List<TimeBucket> Timeline { get; init; } = [];
    public List<Theme> Themes { get; init; } = [];
    public List<LabelCount> PositiveKeywords { get; init; } = [];
    public List<LabelCount> NegativeKeywords { get; init; } = [];
    public List<LabelCount> Emoji { get; init; } = [];
    public List<LabelCount>? Emotions { get; init; }
    public ToxicitySummary? Toxicity { get; init; }
    public ThreadSummary Threads { get; init; } = new();
    public Engagement Engagement { get; init; } = new();
    public Dictionary<string, List<string>> Highlights { get; init; } = new();
    public List<Insight> Insights { get; init; } = [];
    /// <summary>Genre, geographic reach, age group and the parents' and teachers' guide (schema 2; filled in on load for older reports).</summary>
    public Audience? Audience { get; set; }
    /// <summary>A few plain sentences that sum the whole report up.</summary>
    public string? Summary { get; set; }
    public List<ModelUse> Models { get; init; } = [];
    public ComputeSummary Compute { get; init; } = new();
    public List<ScoredComment> Comments { get; init; } = [];
}

/// <summary>What the home page lists and what the cache lookup reads: a few fields per report, kept beside the report.</summary>
public sealed class ReportMeta
{
    public required string Id { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public required string VideoId { get; init; }
    public string? Title { get; init; }
    public string? Channel { get; init; }
    public string? Thumbnail { get; init; }
    public required string Profile { get; init; }
    public int Analysed { get; init; }
    public double Score { get; init; }
    public required string Verdict { get; init; }
    public string? PrimaryLanguage { get; init; }
    public string? Genre { get; init; }
    public string? GenreIcon { get; init; }
    public string? AgeGroup { get; init; }
    public string? AgeRange { get; init; }
    public string? Reach { get; init; }
    /// <summary>green | amber | red | unknown.</summary>
    public string? Family { get; init; }
}
