using System.Runtime.InteropServices;
using Ark.Feedback.Options;

namespace Ark.Feedback.Nlp;

public enum ModelRole { Sentiment, SentimentFast, Toxicity, Emotion, Embeddings }

public enum TokenizerKind { WordPiece, XlmRoberta }

/// <summary>What the model's output means: class probabilities (softmax), independent label probabilities (sigmoid), or token vectors to mean-pool.</summary>
public enum HeadKind { Softmax, Sigmoid, MeanPooling }

/// <summary>One file of a model repo: its path in the repo, its size and SHA-256 (null for small non-LFS files).</summary>
public sealed record ModelFile(string Path, long Size, string? Sha256)
{
    public string LocalName => Path.Replace('/', '_');
}

/// <summary>Everything needed to download, load and interpret one model.</summary>
public sealed record ModelSpec(
    ModelRole Role,
    string Slug,
    string DisplayName,
    string Repo,
    string Revision,
    ModelFile Model,
    ModelFile Tokenizer,
    TokenizerKind TokenizerKind,
    HeadKind Head,
    IReadOnlyList<string> Labels,
    string License,
    string Languages,
    string Note,
    bool Enabled = true)
{
    public long DownloadBytes => Model.Size + Tokenizer.Size;
}

/// <summary>How deep an analysis goes, and therefore how much CPU it costs.</summary>
public sealed record AnalysisProfile(string Id, string Name, string Blurb, ModelRole SentimentModel, bool Themes, bool Toxicity, bool Emotion, int MaxComments)
{
    public IEnumerable<ModelRole> Roles
    {
        get
        {
            yield return SentimentModel;
            if (Themes) yield return ModelRole.Embeddings;
            if (Toxicity) yield return ModelRole.Toxicity;
            if (Emotion) yield return ModelRole.Emotion;
        }
    }
}

/// <summary>
/// The open-source models this app runs on the CPU, all as 8-bit quantized ONNX exports published on Hugging Face,
/// SHA-256 pinned (verified 2026-11-10). Operators may swap any of them with Feedback:Analysis:Models.
/// </summary>
public sealed class ModelCatalog
{
    private static readonly string[] GoEmotions =
    [
        "admiration", "amusement", "anger", "annoyance", "approval", "caring", "confusion", "curiosity", "desire", "disappointment",
        "disapproval", "disgust", "embarrassment", "excitement", "fear", "gratitude", "grief", "joy", "love", "nervousness",
        "optimism", "pride", "realization", "relief", "remorse", "sadness", "surprise", "neutral",
    ];

    private static readonly ModelFile XlmrSentencePiece = new("sentencepiece.bpe.model", 5_069_051, "cfc8146abe2a0488e9e2a0c56de7952f7c11ab059eca145a0a727afce0db2865");
    private static readonly ModelFile MBertVocab = new("vocab.txt", 995_526, null);

    private static ModelFile EmbeddingsFile() =>
        RuntimeInformation.ProcessArchitecture is Architecture.Arm64 or Architecture.Arm
            ? new ModelFile("onnx/model_qint8_arm64.onnx", 118_412_398, "783fea82d71a58179b830a4dbd2d58447e640609e98eedf9ffa12622d375a672")
            : new ModelFile("onnx/model_quint8_avx2.onnx", 118_453_870, "98a01d88b7de996cdea58c32ca71208c09968d143798814b2ea09d3439dc334f");

    /// <summary>The defaults, before configuration overrides.</summary>
    public static IReadOnlyList<ModelSpec> Defaults() =>
    [
        new(ModelRole.Sentiment, "xlm-t-sentiment", "XLM-T sentiment (XLM-RoBERTa base, 8 languages trained, 100 understood)",
            "onnx-community/twitter-xlm-roberta-base-sentiment-ONNX", "main",
            new ModelFile("onnx/model_quantized.onnx", 279_270_447, "6c7c4e804129149d58eb1d11a576129f4270f46134dd074315b8aea05d178e20"),
            XlmrSentencePiece, TokenizerKind.XlmRoberta, HeadKind.Softmax, ["negative", "neutral", "positive"],
            "MIT (cardiffnlp/twitter-xlm-roberta-base-sentiment)", "ar, en, fr, de, hi, it, es, pt + XLM-R's 100 languages",
            "The reference multilingual social-media sentiment model (Barbieri et al., 2022); 278M parameters, ~45 ms per comment on one modern core."),

        new(ModelRole.SentimentFast, "distilbert-multilingual-sentiment", "DistilBERT multilingual sentiment (student of mDeBERTa zero-shot)",
            "Xenova/distilbert-base-multilingual-cased-sentiments-student", "main",
            new ModelFile("onnx/model_quantized.onnx", 135_953_840, "9570fdd726d952dbaf559ba0b9f38a523554f08a8038d62d9263a90dcbb3cd3e"),
            MBertVocab, TokenizerKind.WordPiece, HeadKind.Softmax, ["positive", "neutral", "negative"],
            "Apache-2.0 (lxyuan/distilbert-base-multilingual-cased-sentiments-student)", "en, ar, de, es, fr, ja, zh, id, hi, it, ms, pt",
            "Half the size and about 2.5x the speed of XLM-T; good for small hosts and the Quick profile."),

        new(ModelRole.Toxicity, "distilbert-multilingual-toxicity", "DistilBERT multilingual toxicity",
            "onnx-community/distilbert-multilingual-toxicity-classifier-ONNX", "main",
            new ModelFile("onnx/model_quantized.onnx", 135_908_348, "52e703cb3d8a3aac8c5d35a350dbc20fd5f0a54b00f01b4b88fc5bb2052f7700"),
            MBertVocab, TokenizerKind.WordPiece, HeadKind.Softmax, ["not-toxic", "toxic"],
            "OpenRAIL++ (gravitee-io/distilbert-multilingual-toxicity-classifier)", "en, ru, uk, de, es, am, zh, ar, hi, it, fr, he, ja, tt",
            "Trained on the TextDetox multilingual toxicity corpus; flags insults and hate rather than mere negativity."),

        new(ModelRole.Emotion, "mbert-multilingual-go-emotions", "Multilingual GoEmotions (mBERT, 28 emotions)",
            "jdp8/multilingual_go_emotions-ONNX", "main",
            new ModelFile("onnx/model_quantized.onnx", 179_064_452, "ffde309d9d77f5bde789f70d2b2c3e77d9547b4fafc8d043c29d2270f8e0e89a"),
            MBertVocab, TokenizerKind.WordPiece, HeadKind.Sigmoid, GoEmotions,
            "Hugging Face model card (AnasAlokla/multilingual_go_emotions)", "ar, en, fr, es, nl, tr (others approximate)",
            "Multi-label: a comment can be both grateful and amused. Used for the emotion mix in the Deep profile."),

        new(ModelRole.Embeddings, "paraphrase-multilingual-minilm", "paraphrase-multilingual-MiniLM-L12-v2 (sentence embeddings)",
            "sentence-transformers/paraphrase-multilingual-MiniLM-L12-v2", "main",
            EmbeddingsFile(),
            XlmrSentencePiece, TokenizerKind.XlmRoberta, HeadKind.MeanPooling, [],
            "Apache-2.0 (sentence-transformers)", "50+ languages",
            "384-dimensional sentence vectors; comments that say the same thing in different languages land close together. Powers the themes and the repeat detection."),
    ];

    public static readonly IReadOnlyList<AnalysisProfile> Profiles =
    [
        new("fast", "Quick", "Sentiment and languages only, on the smaller model. Best for a busy or small server.", ModelRole.SentimentFast, Themes: false, Toxicity: false, Emotion: false, MaxComments: 300),
        new("balanced", "Standard", "XLM-T sentiment, languages, themes and repeat detection.", ModelRole.Sentiment, Themes: true, Toxicity: false, Emotion: false, MaxComments: 600),
        new("thorough", "Deep", "Everything in Standard plus toxicity and a 28-emotion mix.", ModelRole.Sentiment, Themes: true, Toxicity: true, Emotion: true, MaxComments: 800),
    ];

    private readonly Dictionary<ModelRole, ModelSpec> _specs = new();

    public ModelCatalog(AnalysisOptions options)
    {
        foreach (var d in Defaults())
        {
            var spec = d;
            if (options.Models.TryGetValue(d.Role.ToString(), out var o) || options.Models.TryGetValue(RoleKey(d.Role), out o))
            {
                if (!string.IsNullOrWhiteSpace(o.Repo)) spec = spec with { Repo = o.Repo.Trim(), Note = "Configured by the operator (Feedback:Analysis:Models)." };
                if (!string.IsNullOrWhiteSpace(o.Revision)) spec = spec with { Revision = o.Revision.Trim() };
                if (!string.IsNullOrWhiteSpace(o.File) || !string.IsNullOrWhiteSpace(o.Sha256))
                {
                    var sha = string.IsNullOrWhiteSpace(o.Sha256) ? (string.IsNullOrWhiteSpace(o.File) ? spec.Model.Sha256 : null) : o.Sha256.Trim().ToLowerInvariant();
                    spec = spec with { Model = new ModelFile(string.IsNullOrWhiteSpace(o.File) ? spec.Model.Path : o.File.Trim(), 0, sha) };
                }
                if (o.Enabled is { } e) spec = spec with { Enabled = e };
            }
            _specs[d.Role] = spec;
        }
    }

    private static string RoleKey(ModelRole r) => char.ToLowerInvariant(r.ToString()[0]) + r.ToString()[1..];

    public ModelSpec this[ModelRole role] => _specs[role];

    public IReadOnlyCollection<ModelSpec> All => _specs.Values;

    public static AnalysisProfile? Profile(string? id) =>
        Profiles.FirstOrDefault(p => p.Id.Equals(id?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The profile to use for a request: the asked-for one when allowed, else the default; models disabled by configuration are dropped.</summary>
    public AnalysisProfile Resolve(string? requested, AnalysisOptions o)
    {
        var allowed = o.AllowedProfiles.Count == 0 ? Profiles.Select(p => p.Id).ToList() : o.AllowedProfiles;
        var fallback = Profile(o.DefaultProfile) ?? Profiles[1];
        var p = Profile(requested) is { } r && allowed.Contains(r.Id, StringComparer.OrdinalIgnoreCase) ? r : fallback;
        if (!_specs[p.SentimentModel].Enabled)
            p = p with { SentimentModel = p.SentimentModel == ModelRole.Sentiment ? ModelRole.SentimentFast : ModelRole.Sentiment };
        if (p.Themes && !_specs[ModelRole.Embeddings].Enabled) p = p with { Themes = false };
        if (p.Toxicity && !_specs[ModelRole.Toxicity].Enabled) p = p with { Toxicity = false };
        if (p.Emotion && !_specs[ModelRole.Emotion].Enabled) p = p with { Emotion = false };
        return p with { MaxComments = Math.Min(p.MaxComments, Math.Max(20, o.MaxComments)) };
    }
}
