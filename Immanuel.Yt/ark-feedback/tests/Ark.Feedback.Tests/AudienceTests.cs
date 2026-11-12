using System.Text.Json;
using Ark.Feedback.Analysis;
using Ark.Feedback.YouTube;
using Xunit.Abstractions;

namespace Ark.Feedback.Tests;

public class AudienceTests(ITestOutputHelper output)
{
    private static ScoredComment C(string id, string text, string lang = "en", string sentiment = "positive", long likes = 0, double? toxic = null) => new()
    {
        Id = id, Text = text, Lang = lang, LangName = lang, LangConfidence = 0.9, Sentiment = sentiment, Score = sentiment == "positive" ? 0.7 : sentiment == "negative" ? -0.7 : 0,
        PPos = sentiment == "positive" ? 0.8 : 0.1, PNeg = sentiment == "negative" ? 0.8 : 0.1, PNeu = 0.1, Likes = likes, Toxic = toxic,
    };

    private static Audience Profile(VideoInfo v, List<ScoredComment> c)
    {
        var s = ReportBuilder.Summarise(c);
        var langs = ReportBuilder.Languages(c);
        var h = ReportBuilder.Highlights(c, null);
        return AudienceProfiler.Profile(v, c, s, null, langs, h, ReportBuilder.Threads(c));
    }

    [Fact]
    public void GenreReadsTitleTagsCategoryAndComments()
    {
        var v = new VideoInfo { Id = "x", Title = "Luis Fonsi - Despacito ft. Daddy Yankee (Official Music Video)", Category = "Music", Tags = ["Latin", "Pop", "Official Music Video"], DurationSeconds = 282 };
        var c = Enumerable.Range(0, 40).Select(i => C($"c{i}", i % 3 == 0 ? "this song is a banger, on repeat" : "quien escucha en 2026", i % 3 == 0 ? "en" : "es")).ToList();
        var g = AudienceProfiler.Genre(v, c, c.Select(x => new CommentText(x.Text)).ToList());
        Assert.Equal("music", g.Key);
        Assert.Equal("clip", g.Format);
        Assert.True(g.Confidence > 0.4, $"confidence {g.Confidence}");
        Assert.Contains(g.Evidence, e => e.Contains("title"));
        Assert.Contains(g.Evidence, e => e.Contains("category"));

        var podcast = new VideoInfo { Id = "y", Title = "How Money Is Created | Gita Gopinath | FO585 Raj Shamani", Category = "People & Blogs", Tags = ["podcast", "hindi podcast", "imf", "wealth"], DurationSeconds = 6067 };
        var pc = Enumerable.Range(0, 30).Select(i => C($"p{i}", i % 2 == 0 ? "great episode, the guest was insightful" : "inflation and the economy explained so well, learned a lot")).ToList();
        var pg = AudienceProfiler.Genre(podcast, pc, pc.Select(x => new CommentText(x.Text)).ToList());
        Assert.Equal("podcast", pg.Key);
        Assert.Equal("feature", pg.Format);
        Assert.Contains(pg.Secondary, s => s.Key is "finance" or "education");

        var nothing = AudienceProfiler.Genre(new VideoInfo { Id = "z", Title = "zzz" }, [], []);
        Assert.Equal("general", nothing.Key);
    }

    [Fact]
    public void ReachPlacesFlagsPlacesAndLanguages()
    {
        var c = new List<ScoredComment>();
        for (var i = 0; i < 30; i++) c.Add(C($"es{i}", "me encanta esta canción", "es"));
        for (var i = 0; i < 10; i++) c.Add(C($"in{i}", "love from India 🇮🇳", "en"));
        for (var i = 0; i < 6; i++) c.Add(C($"br{i}", "saudades, aqui no Brasil todo mundo ama", "pt"));
        for (var i = 0; i < 5; i++) c.Add(C($"ph{i}", "greetings from the Philippines!", "en"));
        for (var i = 0; i < 4; i++) c.Add(C($"de{i}", "Grüße aus Deutschland", "de"));
        for (var i = 0; i < 20; i++) c.Add(C($"en{i}", "who's watching in 2026", "en"));
        var v = new VideoInfo { Id = "x", Title = "Despacito", Tags = ["Puerto Rico"] };
        var langs = ReportBuilder.Languages(c);
        var r = AudienceProfiler.Reach(v, c, c.Select(x => new CommentText(x.Text)).ToList(), langs);
        Assert.Contains(r.Regions, x => x.Key == "latin-america");
        Assert.Contains(r.Regions, x => x.Key == "south-asia");
        Assert.Contains(r.Regions, x => x.Key == "southeast-asia");
        Assert.Contains(r.Regions, x => x.Key == "western-europe");
        Assert.Equal("latin-america", r.Regions[0].Key);
        var india = r.Countries.First(x => x.Code == "IN");
        Assert.Equal(10, india.Comments);
        Assert.Equal(10, india.Flags);
        Assert.Equal(10, india.Presence);
        Assert.Equal(5, r.Countries.First(x => x.Code == "PH").Presence);
        Assert.Contains(r.Countries, x => x.Code == "PR" && x.NamedByVideo);
        Assert.Contains(r.Countries, x => x.Code == "PH" && x.Comments == 5);
        Assert.True(r.Key is "global" or "multi-regional", r.Key);
        Assert.True(r.PlacedPct > 60);

        // English only, nothing named: wide but unplaceable
        var en = Enumerable.Range(0, 60).Select(i => C($"e{i}", "great video, thanks")).ToList();
        var r2 = AudienceProfiler.Reach(new VideoInfo { Id = "y" }, en, en.Select(x => new CommentText(x.Text)).ToList(), ReportBuilder.Languages(en));
        Assert.Equal("unplaced", r2.Key);

        // "la" and "up" must not place a comment (they were removed as aliases)
        var sp = Enumerable.Range(0, 30).Select(i => C($"s{i}", "la mejor canción, la amo", "es")).ToList();
        var r3 = AudienceProfiler.Reach(new VideoInfo { Id = "z" }, sp, sp.Select(x => new CommentText(x.Text)).ToList(), ReportBuilder.Languages(sp));
        Assert.DoesNotContain(r3.Countries, x => x.Code == "US");
    }

    [Fact]
    public void AgeReadsStatedAgesSchoolAndParents()
    {
        var kidsVideo = new VideoInfo { Id = "k", Title = "ABC Song | Nursery Rhymes for Kids", Category = "Education", Tags = ["nursery rhymes", "kids songs"] };
        var kc = new List<ScoredComment>();
        for (var i = 0; i < 20; i++) kc.Add(C($"p{i}", "my 3 year old loves this song and sings it every day"));
        for (var i = 0; i < 10; i++) kc.Add(C($"t{i}", "showing this to my class tomorrow, my students will love it"));
        for (var i = 0; i < 10; i++) kc.Add(C($"n{i}", "so cute 😊"));
        var ka = Profile(kidsVideo, kc).Age;
        Assert.Equal("preschool", ka.Primary);
        Assert.Equal(20, ka.CoViewing);
        Assert.Equal(10, ka.Teachers);
        Assert.Equal("high", ka.Confidence);

        var teen = new VideoInfo { Id = "g", Title = "Minecraft but every block is random", Category = "Gaming" };
        var tc = new List<ScoredComment>();
        for (var i = 0; i < 12; i++) tc.Add(C($"a{i}", $"i'm {13 + i % 5} and this is fire ngl"));
        for (var i = 0; i < 10; i++) tc.Add(C($"s{i}", "watching this in class while my teacher explains homework lol"));
        for (var i = 0; i < 15; i++) tc.Add(C($"r{i}", "bruh that was sus fr fr 💀"));
        for (var i = 0; i < 10; i++) tc.Add(C($"x{i}", "nice video"));
        var ta = Profile(teen, tc).Age;
        Assert.Equal("teens", ta.Primary);
        Assert.Equal(12, ta.StatedAges.Count);
        Assert.InRange(ta.StatedAges.Median!.Value, 13, 17);
        Assert.True(ta.Slang >= 15);

        var adult = new VideoInfo { Id = "f", Title = "How Money Is Created: Central Banks and Inflation", Category = "News & Politics", DurationSeconds = 5000 };
        var ac = new List<ScoredComment>();
        for (var i = 0; i < 10; i++) ac.Add(C($"w{i}", "my wife and I discussed this over dinner, taxes and mortgage rates make more sense now"));
        for (var i = 0; i < 6; i++) ac.Add(C($"b{i}", "I was born in 1985 and never had this explained so clearly"));
        for (var i = 0; i < 14; i++) ac.Add(C($"g{i}", "great explanation of inflation and the economy"));
        var aa = Profile(adult, ac).Age;
        Assert.Equal("adults", aa.Primary);
        Assert.Equal(6, aa.StatedAges.Count);
        Assert.True(aa.StatedAges.Median > 35);

        // "I'm 100% sure", "I'm 20 minutes in" and "I'm 2 years late" are not ages
        var noise = new List<ScoredComment> { C("n1", "i'm 100% sure this is fake"), C("n2", "im 20 minutes in and hooked"), C("n3", "i'm 2 years late to this"), C("n4", "I'm 11 years old and I love it") };
        var na = Profile(new VideoInfo { Id = "n" }, noise).Age;
        Assert.Equal(1, na.StatedAges.Count);
        Assert.Equal(11, na.StatedAges.Min);
    }

    [Fact]
    public void FamilyGuideRatesTheCommentSection()
    {
        var clean = new List<ScoredComment>();
        for (var i = 0; i < 40; i++) clean.Add(C($"c{i}", i % 4 == 0 ? "finally understand photosynthesis, thank you teacher!" : "wholesome and beautiful, made my day"));
        var v = new VideoInfo { Id = "e", Title = "Photosynthesis explained", Category = "Education", DurationSeconds = 600 };
        var fg = Profile(v, clean).Family;
        Assert.Equal("green", fg.Rating);
        Assert.True(fg.Learning.Score >= 50, $"learning {fg.Learning.Score}");
        Assert.Contains(fg.SuggestedUses, u => u.Contains("Homework"));
        Assert.Contains(fg.SuggestedUses, u => u.Contains("lesson"));
        Assert.Contains(fg.Checks, c => c.Key == "profanity" && c.Status == "ok");
        Assert.Equal(0, fg.Profanity.Comments);

        var rough = new List<ScoredComment>();
        for (var i = 0; i < 30; i++) rough.Add(C($"r{i}", i % 3 == 0 ? "this is fucking stupid" : i % 3 == 1 ? "what a load of shit" : "nice"));
        var rf = Profile(new VideoInfo { Id = "r", Title = "Rant" }, rough).Family;
        Assert.Equal("red", rf.Rating);
        Assert.Equal(20, rf.Profanity.Comments);
        Assert.All(rf.Profanity.Terms, t => Assert.Contains('*', t.Key));
        Assert.Contains(rf.SuggestedUses, u => u.StartsWith("Not for"));

        var betting = new List<ScoredComment>();
        for (var i = 0; i < 40; i++) betting.Add(C($"b{i}", i % 10 == 0 ? "join 1xbet casino for free spins" : "good match highlights"));
        var bf = Profile(new VideoInfo { Id = "b", Title = "Match highlights", Category = "Sports" }, betting).Family;
        Assert.Equal("red", bf.Rating);
        Assert.Contains(bf.Flags, f => f.Key == "gambling" && f.Count == 4);

        var restricted = Profile(new VideoInfo { Id = "a", Title = "x", AgeLimit = 18 }, clean).Family;
        Assert.Equal("red", restricted.Rating);
        Assert.Contains(restricted.Checks, c => c.Key == "age-limit" && c.Status == "bad");

        var thin = Profile(new VideoInfo { Id = "t" }, clean.Take(5).ToList()).Family;
        Assert.Equal("unknown", thin.Rating);
    }

    [Fact]
    public void BuildFillsAudienceAndSummary()
    {
        var c = new List<ScoredComment>();
        for (var i = 0; i < 30; i++) c.Add(C($"p{i}", "aprendí mucho, gracias profe", "es"));
        for (var i = 0; i < 10; i++) c.Add(C($"q{i}", "love from Mexico 🇲🇽", "en"));
        var r = ReportBuilder.Build(new AnalysisFacts
        {
            Id = "20261111-00000001", Profile = Nlp.ModelCatalog.Profile("fast")!, AppVersion = "1.1.0",
            Video = new VideoInfo { Id = "jNQXAC9IVRw", Title = "Álgebra explicada paso a paso", Channel = "Profe", Category = "Education", DurationSeconds = 900, UploadedAt = DateTimeOffset.UtcNow.AddDays(-10), ChannelFollowers = 120_000 },
            Fetch = new FetchSummary { Source = "test", Fetched = 40, Analysed = 40, Unique = 40, TopLevel = 40 },
            Comments = c, Compute = new ComputeSummary(),
        });
        Assert.Equal(2, r.Schema);
        Assert.NotNull(r.Audience);
        Assert.Equal("education", r.Audience!.Genre.Key);
        Assert.Equal("latin-america", r.Audience.Reach.Regions[0].Key);
        Assert.Contains(r.Audience.Reach.Countries, x => x.Code == "MX" && x.Flags == 10);
        Assert.Equal("green", r.Audience.Family.Rating);
        Assert.Contains("Language practice: Spanish", r.Audience.Family.SuggestedUses.Single(u => u.StartsWith("Language")));
        Assert.NotNull(r.Summary);
        Assert.Contains("Profe", r.Summary);
        Assert.Contains("Latin America", r.Summary);
        output.WriteLine(r.Summary);

        // the JSON round-trips with the audience section (what the browser receives)
        var json = JsonSerializer.Serialize(r, ReportStore.Json);
        Assert.Contains("\"audience\":", json);
        var back = JsonSerializer.Deserialize<Report>(json, ReportStore.Json)!;
        Assert.Equal("education", back.Audience!.Genre.Key);
    }

    [Fact]
    public void EnrichFillsAnOlderReport()
    {
        var c = new List<ScoredComment> { C("a", "great video"), C("b", "loved it") };
        var r = new Report
        {
            Id = "20261101-00000002", Profile = "fast", ProfileName = "Quick", AppVersion = "1.0.0", Video = new VideoInfo { Id = "x", Title = "t" },
            Fetch = new FetchSummary { Source = "test" }, Sentiment = ReportBuilder.Summarise(c), Comments = c,
        };
        Assert.Null(r.Audience);
        ReportBuilder.Enrich(r);
        Assert.NotNull(r.Audience);
        Assert.NotNull(r.Summary);
        Assert.Equal("unknown", r.Audience!.Family.Rating);
    }

    [Fact]
    public void MaskHidesTheWord()
    {
        Assert.Equal("f***", AudienceProfiler.Mask("fuck"));
        Assert.Equal("b*", AudienceProfiler.Mask("bc"));
        Assert.Equal("p***** ***", AudienceProfiler.Mask("putang ina"));
    }

    /// <summary>Runs the profiler over the real reports left in App_Data (if any) and prints what it says, so the output can be eyeballed.</summary>
    [Fact]
    public async Task ProfilesTheStoredReports()
    {
        var dir = Path.Combine(Files.RepoRoot, "src", "Ark.Feedback", "App_Data", "reports");
        if (!Directory.Exists(dir)) return;
        foreach (var f in Directory.EnumerateFiles(dir, "*.json", SearchOption.AllDirectories).Where(f => !f.EndsWith(".meta.json", StringComparison.Ordinal)).OrderBy(f => f))
        {
            Report r;
            await using (var fs = File.OpenRead(f)) r = (await JsonSerializer.DeserializeAsync<Report>(fs, ReportStore.Json))!;
            r.Audience = null;
            r.Summary = null;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            ReportBuilder.Enrich(r);
            sw.Stop();
            var a = r.Audience!;
            output.WriteLine($"=== {r.Video.Title} ({r.Comments.Count} comments, {sw.ElapsedMilliseconds} ms)");
            output.WriteLine($"GENRE  {a.Genre.Icon} {a.Genre.Label} ({a.Genre.Confidence:0.00}) secondary: {string.Join(", ", a.Genre.Secondary.Select(s => s.Label))} | {a.Genre.FormatLabel} | {string.Join(" · ", a.Genre.Evidence)}");
            output.WriteLine($"REACH  {a.Reach.Label} ({a.Reach.Score}) placed {a.Reach.PlacedPct}% | regions: {string.Join(", ", a.Reach.Regions.Select(x => $"{x.Label} {x.Pct}%"))} | countries: {string.Join(", ", a.Reach.Countries.Select(x => $"{x.Name} {x.Comments}{(x.Flags > 0 ? $"/{x.Flags}🏳" : "")}{(x.NamedByVideo ? "*" : "")}"))}");
            output.WriteLine($"       {a.Reach.Note}");
            var terms = new Dictionary<string, Dictionary<string, int>>();
            foreach (var c in r.Comments)
                foreach (var h in AudienceProfiler.PlacesIn(new CommentText(c.Text), strict: false))
                {
                    var d = terms.GetValueOrDefault(h.Code) ?? (terms[h.Code] = new Dictionary<string, int>());
                    d[h.Term ?? "?"] = d.GetValueOrDefault(h.Term ?? "?") + 1;
                }
            output.WriteLine("       terms: " + string.Join(" | ", terms.OrderByDescending(kv => kv.Value.Values.Sum()).Take(12).Select(kv => $"{kv.Key}: {string.Join(", ", kv.Value.OrderByDescending(x => x.Value).Take(4).Select(x => $"{x.Key}×{x.Value}"))}")));
            output.WriteLine($"AGE    {a.Age.Label} ({a.Age.Range}) {a.Age.Confidence} | bands: {string.Join(", ", a.Age.Bands.Select(b => $"{b.Key} {b.Pct}%"))} | stated {a.Age.StatedAges.Count} med {a.Age.StatedAges.Median} | co-view {a.Age.CoViewing} teachers {a.Age.Teachers} school {a.Age.School} college {a.Age.College} slang {a.Age.Slang}");
            foreach (var e in a.Age.Evidence) output.WriteLine($"       - {e}");
            output.WriteLine($"FAMILY {a.Family.Rating}: {a.Family.Label} | learning {a.Family.Learning.Score} ({a.Family.Learning.Level}) | profanity {a.Family.Profanity.Pct}% [{string.Join(" ", a.Family.Profanity.Terms.Select(t => t.Key))}] | wholesome {a.Family.WholesomePct}% | heat {a.Family.DebateHeat}");
            foreach (var x in a.Family.Reasons) output.WriteLine($"       - {x}");
            foreach (var x in a.Family.Flags) output.WriteLine($"       ! {x.Label}: {x.Count} ({x.Pct}%) {string.Join(", ", x.Terms)}");
            output.WriteLine($"       uses: {string.Join(" | ", a.Family.SuggestedUses)}");
            output.WriteLine($"       checks: {string.Join(" | ", a.Family.Checks.Select(c => $"{c.Label}={c.Status}"))}");
            output.WriteLine($"SUMMARY {r.Summary}");
            output.WriteLine("");
            Assert.True(sw.ElapsedMilliseconds < 5000, "profiling should take well under a second for 800 comments");
        }
    }
}
