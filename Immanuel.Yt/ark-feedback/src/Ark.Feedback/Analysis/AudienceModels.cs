namespace Ark.Feedback.Analysis;

/// <summary>What kind of video this is, read from the title, tags, description, YouTube category, duration and what the comments talk about.</summary>
public sealed class GenreGuess
{
    /// <summary>Stable key (music, gaming, education, …) or "general" when nothing stands out.</summary>
    public required string Key { get; set; }
    public required string Label { get; set; }
    /// <summary>A pictograph for the badge (emoji).</summary>
    public required string Icon { get; set; }
    /// <summary>Share of the evidence behind the top genre: 0..1.</summary>
    public double Confidence { get; set; }
    public List<GenreScore> Secondary { get; set; } = [];
    /// <summary>YouTube's own category, when the source gave one.</summary>
    public string? Category { get; set; }
    /// <summary>short | clip | standard | long | feature | live.</summary>
    public required string Format { get; set; }
    public required string FormatLabel { get; set; }
    /// <summary>Plain-language reasons: "title mentions 'podcast', 'episode'", "63 comments talk about the song".</summary>
    public List<string> Evidence { get; set; } = [];
}

public sealed class GenreScore
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public required string Icon { get; init; }
    public double Score { get; init; }
}

public sealed class RegionShare
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public int Count { get; set; }
    /// <summary>Share of the comments that could be placed, 0..100.</summary>
    public double Pct { get; set; }
    public List<string> Languages { get; set; } = [];
    public List<string> Countries { get; set; } = [];
}

public sealed class CountryMention
{
    /// <summary>ISO 3166-1 alpha-2.</summary>
    public required string Code { get; init; }
    public required string Name { get; init; }
    public required string Region { get; init; }
    /// <summary>Comments that mention the place, carry its flag or say "from …".</summary>
    public int Comments { get; set; }
    public int Flags { get; set; }
    /// <summary>Comments that carry the flag or say "from …": people who are there, not just talking about it.</summary>
    public int Presence { get; set; }
    /// <summary>The video's own title, tags or description name this place.</summary>
    public bool NamedByVideo { get; set; }
    public List<string> ExampleIds { get; set; } = [];
}

/// <summary>Where the audience is, as far as public comments reveal it: languages, flag emoji, places people name.</summary>
public sealed class ReachSummary
{
    /// <summary>global | multi-regional | regional | local | english-wide | unplaced.</summary>
    public required string Key { get; set; }
    public required string Label { get; set; }
    /// <summary>0..100, higher = more spread out.</summary>
    public int Score { get; set; }
    public List<RegionShare> Regions { get; set; } = [];
    public List<CountryMention> Countries { get; set; } = [];
    public int Placed { get; set; }
    /// <summary>Share of analysed comments that could be placed at all, 0..100.</summary>
    public double PlacedPct { get; set; }
    public int LanguageCount { get; set; }
    public string? PrimaryLanguage { get; set; }
    public double PrimaryLanguagePct { get; set; }
    public double EnglishPct { get; set; }
    /// <summary>What the labels rest on and what they can't tell.</summary>
    public required string Note { get; set; }
    public List<string> Evidence { get; set; } = [];
}

public sealed class AgeBand
{
    /// <summary>preschool | kids | teens | young | adults.</summary>
    public required string Key { get; init; }
    public required string Label { get; init; }
    public required string Range { get; init; }
    public double Pct { get; set; }
}

public sealed class StatedAges
{
    public int Count { get; set; }
    public double? Median { get; set; }
    public int? Min { get; set; }
    public int? Max { get; set; }
    public List<string> ExampleIds { get; set; } = [];
}

/// <summary>Who seems to be watching, as a spread over age bands with the evidence that moved it.</summary>
public sealed class AgeGuess
{
    public required string Primary { get; set; }
    public required string Label { get; set; }
    public required string Range { get; set; }
    /// <summary>low | medium | high.</summary>
    public required string Confidence { get; set; }
    public List<AgeBand> Bands { get; set; } = [];
    public StatedAges StatedAges { get; set; } = new();
    /// <summary>Comments by parents or grandparents saying a child watches ("my 5-year-old loves this").</summary>
    public int CoViewing { get; set; }
    /// <summary>Comments by teachers ("showing this to my class").</summary>
    public int Teachers { get; set; }
    public int School { get; set; }
    public int College { get; set; }
    public int Slang { get; set; }
    public int Nostalgia { get; set; }
    public List<string> Evidence { get; set; } = [];
    public required string Note { get; set; }
}

public sealed class FamilyCheck
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    /// <summary>ok | warn | bad | info.</summary>
    public required string Status { get; init; }
    public required string Detail { get; init; }
}

public sealed class FlagGroup
{
    /// <summary>gambling | adult | substances | violence | danger | scam | distress | accuracy | belief.</summary>
    public required string Key { get; init; }
    public required string Label { get; init; }
    public int Count { get; set; }
    public double Pct { get; set; }
    public List<string> Terms { get; set; } = [];
    public List<string> ExampleIds { get; set; } = [];
}

public sealed class LearningSignal
{
    /// <summary>0..100.</summary>
    public int Score { get; set; }
    /// <summary>strong | some | light | little.</summary>
    public required string Level { get; set; }
    public int Comments { get; set; }
    public double Pct { get; set; }
    public List<LabelCount> Terms { get; set; } = [];
    public List<string> ExampleIds { get; set; } = [];
}

public sealed class ProfanitySummary
{
    public int Comments { get; set; }
    public double Pct { get; set; }
    /// <summary>Masked words ("f***"), most frequent first.</summary>
    public List<LabelCount> Terms { get; set; } = [];
    public List<string> ExampleIds { get; set; } = [];
}

/// <summary>The parents' and teachers' page: suitability, checks, learning value, things to look at before sharing.</summary>
public sealed class FamilyGuide
{
    /// <summary>green | amber | red | unknown.</summary>
    public required string Rating { get; set; }
    public required string Label { get; set; }
    public required string Headline { get; set; }
    public List<string> Reasons { get; set; } = [];
    public List<FamilyCheck> Checks { get; set; } = [];
    public LearningSignal Learning { get; set; } = new() { Level = "little" };
    public ProfanitySummary Profanity { get; set; } = new();
    public List<FlagGroup> Flags { get; set; } = [];
    public int Wholesome { get; set; }
    public double WholesomePct { get; set; }
    /// <summary>Highest-liked questions from the comments, to open a discussion with.</summary>
    public List<string> ConversationStarterIds { get; set; } = [];
    public List<string> SuggestedUses { get; set; } = [];
    public List<LabelCount> LanguageExposure { get; set; } = [];
    /// <summary>low | medium | high: how heated the replies and the split are.</summary>
    public required string DebateHeat { get; set; }
    public required string Disclaimer { get; set; }
}

/// <summary>Genre, reach, age group and the family guide together.</summary>
public sealed class Audience
{
    public required GenreGuess Genre { get; set; }
    public required ReachSummary Reach { get; set; }
    public required AgeGuess Age { get; set; }
    public required FamilyGuide Family { get; set; }
}
