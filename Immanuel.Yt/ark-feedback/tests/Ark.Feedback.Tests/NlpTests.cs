using Ark.Feedback.Nlp;
using Ark.Feedback.Options;

namespace Ark.Feedback.Tests;

public class NlpTests
{
    [Theory]
    [InlineData("இந்த வீடியோ மிகவும் அருமையாக இருக்கிறது", "Taml", "ta")]
    [InlineData("これは本当に素晴らしい動画です", "Jpan", "ja")]
    [InlineData("정말 좋은 영상이에요", "Hang", "ko")]
    [InlineData("Это ужасно", "Cyrl", "ru")]
    [InlineData("hello there", "Latn", null)]
    [InlineData("😍😍 🔥", "Zyyy", null)]
    public void ScriptDetectionKnowsTheObviousScripts(string text, string script, string? lang)
    {
        var (s, l, _) = LanguageDetector.Script(text);
        Assert.Equal(script, s);
        Assert.Equal(lang, l);
    }

    [Fact]
    public void DetectorAnswersForCommonLanguages()
    {
        using var lid = new LanguageDetector(Logs.Of<LanguageDetector>());
        Assert.Equal("ta", lid.Detect("இந்த வீடியோ மிகவும் அருமையாக இருக்கிறது").Code);
        Assert.Equal("und", lid.Detect("👍👍👍").Code);
        if (!lid.UsesModel) return; // script heuristic only on this platform
        Assert.Equal("fr", lid.Detect("C'est magnifique, merci beaucoup pour ce travail").Code);
        Assert.Equal("en", lid.Detect("This is terrible. Worst 10 minutes of my life.").Code);
        Assert.Equal("Tamil", LanguageDetector.Name("ta"));
        Assert.Equal("Cebuano", LanguageDetector.Name("ceb"));
    }

    [Fact]
    public void TextToolsCleanDedupAndWords()
    {
        Assert.Equal("see http now", TextTools.Clean("see   https://example.com/x?y=1 \n now"));
        Assert.Equal(TextTools.DedupKey("SOOO good!!!"), TextTools.DedupKey("so good"));
        Assert.NotEqual(TextTools.DedupKey("so good"), TextTools.DedupKey("so bad"));
        var words = TextTools.Words("The audio quality is terrible, the music too loud", "en").ToList();
        Assert.Contains("audio", words);
        Assert.Contains("terrible", words);
        Assert.DoesNotContain("the", words);
        Assert.Equal(["😍", "🔥"], TextTools.Emoji("love it 😍🔥").ToList());
        Assert.True(TextTools.IsMostlyEmojiOrSymbols("😍😍"));
        Assert.False(TextTools.IsMostlyEmojiOrSymbols("ok 😍"));
    }

    [Fact]
    public void KMeansGroupsObviousClusters()
    {
        var vectors = new List<float[]>();
        var rnd = new Random(1);
        for (var i = 0; i < 30; i++) vectors.Add(Unit([1 + Noise(rnd), Noise(rnd), Noise(rnd)]));
        for (var i = 0; i < 30; i++) vectors.Add(Unit([Noise(rnd), 1 + Noise(rnd), Noise(rnd)]));
        var r = KMeans.Run(vectors.ToArray(), 2);
        Assert.Equal(60, r.Assignment.Length);
        Assert.True(r.Assignment.Take(30).Distinct().Count() == 1);
        Assert.True(r.Assignment.Skip(30).Distinct().Count() == 1);
        Assert.NotEqual(r.Assignment[0], r.Assignment[59]);
        static float Noise(Random rnd) => (float)(rnd.NextDouble() * 0.1);
        static float[] Unit(float[] v) { var n = MathF.Sqrt(v.Sum(x => x * x)); return v.Select(x => x / n).ToArray(); }
    }

    [Fact]
    public void CatalogResolvesProfilesAndHonoursOverrides()
    {
        var o = new AnalysisOptions { AllowedProfiles = ["fast", "balanced"], DefaultProfile = "balanced", MaxComments = 400 };
        var catalog = new ModelCatalog(o);
        Assert.Equal("balanced", catalog.Resolve(null, o).Id);
        Assert.Equal("balanced", catalog.Resolve("thorough", o).Id); // not allowed → default
        Assert.Equal("fast", catalog.Resolve("FAST", o).Id);
        Assert.Equal(400, catalog.Resolve("balanced", o).MaxComments); // capped by MaxComments

        var o2 = new AnalysisOptions { Models = { ["embeddings"] = new ModelOverride { Enabled = false }, ["sentiment"] = new ModelOverride { Repo = "me/my-model", File = "model.onnx" } } };
        var c2 = new ModelCatalog(o2);
        Assert.False(c2[ModelRole.Embeddings].Enabled);
        Assert.False(c2.Resolve("balanced", o2).Themes);
        Assert.Equal("me/my-model", c2[ModelRole.Sentiment].Repo);
        Assert.Null(c2[ModelRole.Sentiment].Model.Sha256); // a new file without a hash is accepted unverified
        Assert.Equal(5, c2.All.Count);
        Assert.All(ModelCatalog.Defaults(), d => Assert.Equal(64, d.Model.Sha256!.Length));
    }
}
