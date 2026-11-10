using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Ark.Feedback.Nlp;

/// <summary>Session options tuned for a shared CPU: bounded threads, no spin-waiting while idle, full graph optimisation.</summary>
public static class OnnxSessions
{
    public static Microsoft.ML.OnnxRuntime.SessionOptions Create(int intraOpThreads)
    {
        var so = new Microsoft.ML.OnnxRuntime.SessionOptions
        {
            IntraOpNumThreads = Math.Max(1, intraOpThreads),
            InterOpNumThreads = 1,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
        };
        // Spinning threads keep a core at 100 % between batches — exactly what a shared host penalises.
        so.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
        so.AddSessionConfigEntry("session.inter_op.allow_spinning", "0");
        return so;
    }
}

/// <summary>A loaded transformer with its tokenizer: the common input building for classifiers and the embedder.</summary>
public abstract class OnnxTextModel : IDisposable
{
    private readonly InferenceSession _session;
    private readonly string _idsName, _maskName;
    private readonly string? _typeName;
    private readonly string _outputName;

    protected OnnxTextModel(ModelSpec spec, string modelPath, ITextTokenizer tokenizer, int threads, int maxTokens, int batchSize)
    {
        Spec = spec;
        Tokenizer = tokenizer;
        MaxTokens = Math.Clamp(maxTokens, 8, 512);
        BatchSize = Math.Clamp(batchSize, 1, 64);
        _session = new InferenceSession(modelPath, OnnxSessions.Create(threads));
        var inputs = _session.InputMetadata.Keys.ToList();
        _idsName = inputs.FirstOrDefault(n => n.Contains("input_ids", StringComparison.OrdinalIgnoreCase)) ?? inputs[0];
        _maskName = inputs.FirstOrDefault(n => n.Contains("attention_mask", StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidDataException($"{spec.Slug}: the model has no attention_mask input.");
        _typeName = inputs.FirstOrDefault(n => n.Contains("token_type", StringComparison.OrdinalIgnoreCase));
        _outputName = PickOutput(_session.OutputMetadata);
    }

    public ModelSpec Spec { get; }
    public ITextTokenizer Tokenizer { get; }
    public int MaxTokens { get; }
    public int BatchSize { get; }

    protected abstract string PickOutput(IReadOnlyDictionary<string, NodeMetadata> outputs);

    /// <summary>Tokenizes every text, runs them in length-sorted batches (less padding, less work) and returns one result per text, in input order.</summary>
    protected T[] RunAll<T>(IReadOnlyList<string> texts, Func<Tensor<float>, long[,], int, T> read, Action<int>? progress, CancellationToken ct)
    {
        var encoded = new IReadOnlyList<int>[texts.Count];
        for (var i = 0; i < texts.Count; i++) encoded[i] = Tokenizer.Encode(texts[i], MaxTokens);
        var order = Enumerable.Range(0, texts.Count).OrderBy(i => encoded[i].Count).ToArray();
        var results = new T[texts.Count];
        for (var b = 0; b < order.Length; b += BatchSize)
        {
            ct.ThrowIfCancellationRequested();
            var idx = order.Skip(b).Take(BatchSize).ToArray();
            var len = idx.Max(i => encoded[i].Count);
            var ids = new DenseTensor<long>([idx.Length, len]);
            var mask = new DenseTensor<long>([idx.Length, len]);
            var maskArr = new long[idx.Length, len];
            for (var r = 0; r < idx.Length; r++)
            {
                var e = encoded[idx[r]];
                for (var c = 0; c < len; c++)
                {
                    var inRange = c < e.Count;
                    ids[r, c] = inRange ? e[c] : Tokenizer.PadId;
                    mask[r, c] = inRange ? 1 : 0;
                    maskArr[r, c] = inRange ? 1 : 0;
                }
            }
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor(_idsName, ids),
                NamedOnnxValue.CreateFromTensor(_maskName, mask),
            };
            if (_typeName is not null) inputs.Add(NamedOnnxValue.CreateFromTensor(_typeName, new DenseTensor<long>([idx.Length, len])));
            using var output = _session.Run(inputs, [_outputName]);
            var tensor = output.First().AsTensor<float>();
            for (var r = 0; r < idx.Length; r++) results[idx[r]] = read(tensor, maskArr, r);
            progress?.Invoke(Math.Min(b + idx.Length, order.Length));
        }
        return results;
    }

    public void Dispose() => _session.Dispose();
}

/// <summary>Sequence classification: returns one probability per label (softmax) or per independent label (sigmoid).</summary>
public sealed class OnnxTextClassifier(ModelSpec spec, string modelPath, ITextTokenizer tokenizer, int threads, int maxTokens, int batchSize)
    : OnnxTextModel(spec, modelPath, tokenizer, threads, maxTokens, batchSize)
{
    protected override string PickOutput(IReadOnlyDictionary<string, NodeMetadata> outputs) =>
        outputs.Keys.FirstOrDefault(k => k.Equals("logits", StringComparison.OrdinalIgnoreCase)) ?? outputs.Keys.First();

    public float[][] Predict(IReadOnlyList<string> texts, Action<int>? progress = null, CancellationToken ct = default)
    {
        if (texts.Count == 0) return [];
        var sigmoid = Spec.Head == HeadKind.Sigmoid;
        return RunAll(texts, (t, _, row) =>
        {
            var n = t.Dimensions[1];
            var p = new float[n];
            if (sigmoid)
            {
                for (var c = 0; c < n; c++) p[c] = 1f / (1f + MathF.Exp(-t[row, c]));
            }
            else
            {
                var max = float.NegativeInfinity;
                for (var c = 0; c < n; c++) max = MathF.Max(max, t[row, c]);
                var sum = 0f;
                for (var c = 0; c < n; c++) { p[c] = MathF.Exp(t[row, c] - max); sum += p[c]; }
                for (var c = 0; c < n; c++) p[c] /= sum;
            }
            return p;
        }, progress, ct);
    }
}

/// <summary>Sentence embeddings: mean-pooled last hidden state over real tokens, L2-normalised (what sentence-transformers does for this model).</summary>
public sealed class OnnxEmbedder(ModelSpec spec, string modelPath, ITextTokenizer tokenizer, int threads, int maxTokens, int batchSize)
    : OnnxTextModel(spec, modelPath, tokenizer, threads, maxTokens, batchSize)
{
    protected override string PickOutput(IReadOnlyDictionary<string, NodeMetadata> outputs) =>
        outputs.FirstOrDefault(o => o.Key.Contains("last_hidden_state", StringComparison.OrdinalIgnoreCase) || o.Key.Contains("token_embeddings", StringComparison.OrdinalIgnoreCase)).Key
        ?? outputs.FirstOrDefault(o => o.Value.Dimensions.Length == 3).Key
        ?? outputs.Keys.First();

    public float[][] Embed(IReadOnlyList<string> texts, Action<int>? progress = null, CancellationToken ct = default)
    {
        if (texts.Count == 0) return [];
        return RunAll(texts, (t, mask, row) =>
        {
            var len = t.Dimensions[1];
            var dim = t.Dimensions[2];
            var v = new float[dim];
            var n = 0;
            for (var c = 0; c < len; c++)
            {
                if (mask[row, c] == 0) continue;
                n++;
                for (var d = 0; d < dim; d++) v[d] += t[row, c, d];
            }
            var norm = 0f;
            for (var d = 0; d < dim; d++) { v[d] /= Math.Max(1, n); norm += v[d] * v[d]; }
            norm = MathF.Sqrt(norm);
            if (norm > 0) for (var d = 0; d < dim; d++) v[d] /= norm;
            return v;
        }, progress, ct);
    }
}
