using Ark.Feedback.Analysis;
using Ark.Feedback.YouTube;

namespace Ark.Feedback.Tests;

public class AnalysisTests
{
    private static ScoredComment C(string id, string sentiment, double score, long likes = 0, string lang = "en", string? text = null, DateTimeOffset? at = null, bool reply = false,
        bool pinned = false, bool uploader = false) => new()
    {
        Id = id, Text = text ?? $"comment {id}", Sentiment = sentiment, Score = score, PPos = sentiment == "positive" ? 0.8 : 0.1, PNeg = sentiment == "negative" ? 0.8 : 0.1, PNeu = 0.1,
        Likes = likes, Lang = lang, LangName = lang == "en" ? "English" : lang, PublishedAt = at, IsReply = reply, IsPinned = pinned, AuthorIsUploader = uploader,
    };

    [Fact]
    public void SamplingKeepsEverythingWhenAffordable()
    {
        var plan = Sampling.Decide(500, msPerComment: 40, workFactor: 1.45, budgetSeconds: 90);
        Assert.False(plan.Sampled);
        Assert.Equal(500, plan.Keep);
    }

    [Fact]
    public void SamplingCutsToTheBudgetAndKeepsPinnedCreatorAndTopLiked()
    {
        var plan = Sampling.Decide(800, msPerComment: 120, workFactor: 2.65, budgetSeconds: 90);
        Assert.True(plan.Sampled);
        Assert.InRange(plan.Keep, 60, 400);
        Assert.Contains("were scored", plan.Note);
        var comments = Enumerable.Range(0, 800).Select(i => C($"c{i}", "neutral", 0, likes: i == 5 ? 9999 : i % 7, at: DateTimeOffset.UtcNow.AddDays(-i), pinned: i == 10, uploader: i == 20)).ToList();
        var chosen = Sampling.Select(comments, plan.Keep);
        Assert.Equal(plan.Keep, chosen.Length);
        Assert.Contains(10, chosen);
        Assert.Contains(20, chosen);
        Assert.Contains(5, chosen);
        Assert.True(chosen.SequenceEqual(chosen.OrderBy(i => i)));
    }

    [Fact]
    public void SummaryAndVerdictFollowTheShares()
    {
        var c = new List<ScoredComment>();
        for (var i = 0; i < 70; i++) c.Add(C($"p{i}", "positive", 0.8, likes: 10));
        for (var i = 0; i < 20; i++) c.Add(C($"n{i}", "neutral", 0));
        for (var i = 0; i < 10; i++) c.Add(C($"g{i}", "negative", -0.8));
        var s = ReportBuilder.Summarise(c);
        Assert.Equal(100, s.Analysed);
        Assert.Equal(70, s.PositivePct);
        Assert.Equal(60, s.Score);
        Assert.Equal("loved", s.Verdict);
        Assert.True(s.WeightedScore > s.Score); // liked comments are positive
        Assert.Equal(0.2, s.Polarization, 3);

        var split = new List<ScoredComment>();
        for (var i = 0; i < 45; i++) split.Add(C($"p{i}", "positive", 0.8));
        for (var i = 0; i < 45; i++) split.Add(C($"g{i}", "negative", -0.8));
        for (var i = 0; i < 10; i++) split.Add(C($"n{i}", "neutral", 0));
        Assert.Equal("divided", ReportBuilder.Summarise(split).Verdict);
        Assert.Equal("thin", ReportBuilder.Summarise(c.Take(3).ToList()).Verdict);
        Assert.Equal("none", ReportBuilder.Summarise([]).Verdict);
    }

    [Fact]
    public void TimelinePicksAUnitFromTheSpan()
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var daily = Enumerable.Range(0, 10).Select(i => C($"d{i}", "positive", 0.5, at: start.AddDays(i))).ToList();
        Assert.Equal(10, ReportBuilder.Timeline(daily).Count);
        Assert.Equal("1 Jan", ReportBuilder.Timeline(daily)[0].Label);
        var yearly = Enumerable.Range(0, 24).Select(i => C($"m{i}", "positive", 0.5, at: start.AddMonths(i))).ToList();
        var tl = ReportBuilder.Timeline(yearly);
        Assert.Equal(24, tl.Count);
        Assert.Equal("Jan 2026", tl[0].Label);
        Assert.Empty(ReportBuilder.Timeline(daily.Take(3).ToList()));
    }

    [Fact]
    public void KeywordsAreDistinctivePerSide()
    {
        var c = new List<ScoredComment>();
        for (var i = 0; i < 5; i++) c.Add(C($"p{i}", "positive", 0.8, text: "the editing is superb and the pacing is great"));
        for (var i = 0; i < 5; i++) c.Add(C($"g{i}", "negative", -0.8, text: "the audio is terrible and the music is too loud"));
        var (pos, neg) = ReportBuilder.Keywords(c);
        Assert.Contains(pos, k => k.Key == "editing");
        Assert.Contains(neg, k => k.Key == "audio");
        Assert.DoesNotContain(pos, k => k.Key == "audio");
    }

    [Fact]
    public void BuildProducesInsightsAndHighlights()
    {
        var c = new List<ScoredComment>();
        for (var i = 0; i < 40; i++) c.Add(C($"p{i}", "positive", 0.7, likes: i, text: $"Great video number {i}, loved the editing", lang: i % 4 == 0 ? "hi" : "en", uploader: i == 3));
        for (var i = 0; i < 10; i++) c.Add(C($"g{i}", "negative", -0.7, text: $"The audio is bad {i}", reply: i % 2 == 0));
        var r = ReportBuilder.Build(new AnalysisFacts
        {
            Id = "20261110-deadbeef", Profile = ModelCatalog_Profile("balanced"), AppVersion = "1.0.0",
            Video = new VideoInfo { Id = "jNQXAC9IVRw", Title = "T", ViewCount = 100_000, CommentCount = 500, UploadedAt = DateTimeOffset.UtcNow.AddDays(-30) },
            Fetch = new FetchSummary { Source = "test", Fetched = 50, Analysed = 50, Unique = 50, TopLevel = 45, Replies = 5 },
            Comments = c, Compute = new ComputeSummary(),
        });
        Assert.Equal("loved", r.Sentiment.Verdict);
        Assert.Contains(r.Insights, i => i.Title.Contains("positive"));
        Assert.Contains(r.Insights, i => i.Title.Contains("creator replied"));
        Assert.Equal(2, r.Languages.Count);
        Assert.Equal("en", r.Languages[0].Key);
        Assert.Equal(5, r.Highlights["mostLiked"].Count);
        Assert.Equal("p39", r.Highlights["mostLiked"][0]);
        Assert.Contains("p3", r.Highlights["creator"]);
        Assert.Equal(1, r.Threads.UploaderComments);
        Assert.Equal(5.0, r.Engagement.CommentsPerThousandViews);
        Assert.Contains(r.PositiveKeywords, k => k.Key == "editing");
    }

    private static Nlp.AnalysisProfile ModelCatalog_Profile(string id) => Nlp.ModelCatalog.Profile(id)!;

    [Fact]
    public void ReportIdsEncodeTheDay()
    {
        var id = ReportStore.NewId("20261110");
        Assert.True(ReportStore.IsValidId(id));
        Assert.StartsWith("20261110-", id);
        Assert.False(ReportStore.IsValidId("../etc/passwd"));
        Assert.False(ReportStore.IsValidId("20261110_abcd1234"));
    }
}
