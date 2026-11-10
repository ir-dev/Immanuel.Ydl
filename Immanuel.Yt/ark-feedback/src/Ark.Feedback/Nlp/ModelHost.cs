using System.Collections.Concurrent;
using System.Diagnostics;
using Ark.Feedback.Options;
using Microsoft.Extensions.Options;
using Microsoft.ML.Tokenizers;

namespace Ark.Feedback.Nlp;

public sealed record LoadedModelInfo(ModelRole Role, string Slug, DateTimeOffset LoadedAt, DateTimeOffset LastUsedAt, long LoadMs);

public sealed class ModelUnavailableException(ModelRole role, string message) : Exception(message)
{
    public ModelRole Role { get; } = role;
}

/// <summary>
/// Loads models on first use and keeps them while they are being used. After Feedback:Analysis:UnloadIdleMinutes of
/// silence they are disposed, returning several hundred MB to the host. Tokenizers are shared between models that use
/// the same file (XLM-T and MiniLM share the XLM-R SentencePiece model). The inference gate limits how many analyses
/// may use the CPU at once.
/// </summary>
public sealed class ModelHost(ModelCatalog catalog, ModelStore store, IOptions<FeedbackOptions> opt, ILogger<ModelHost> log) : BackgroundService
{
    private readonly ConcurrentDictionary<ModelRole, Lazy<Task<OnnxTextModel>>> _models = new();
    private readonly ConcurrentDictionary<ModelRole, LoadedModelInfo> _info = new();
    private readonly ConcurrentDictionary<string, Lazy<ITextTokenizer>> _tokenizers = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(Math.Max(1, opt.Value.Analysis.MaxConcurrentAnalyses), Math.Max(1, opt.Value.Analysis.MaxConcurrentAnalyses));
    private int _inUse;

    private AnalysisOptions O => opt.Value.Analysis;

    public int Threads => O.InferenceThreads > 0 ? O.InferenceThreads : Math.Clamp(Environment.ProcessorCount / 2, 1, 4);

    public IReadOnlyCollection<LoadedModelInfo> Loaded => _info.Values.ToList();

    public int Waiting => Math.Max(0, Math.Max(1, O.MaxConcurrentAnalyses) - _gate.CurrentCount) ;

    /// <summary>One analysis at a time per permit: the CPU is the scarce resource, not memory bandwidth.</summary>
    public async Task<IDisposable> AcquireAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        Interlocked.Increment(ref _inUse);
        return new Releaser(() => { Interlocked.Decrement(ref _inUse); _gate.Release(); });
    }

    public async Task<OnnxTextClassifier> ClassifierAsync(ModelRole role, CancellationToken ct) => (OnnxTextClassifier)await GetAsync(role, ct);

    public async Task<OnnxEmbedder> EmbedderAsync(CancellationToken ct) => (OnnxEmbedder)await GetAsync(ModelRole.Embeddings, ct);

    private async Task<OnnxTextModel> GetAsync(ModelRole role, CancellationToken ct)
    {
        var spec = catalog[role];
        if (!spec.Enabled) throw new ModelUnavailableException(role, $"The {role} model is disabled by configuration.");
        if (!store.IsReady(role))
        {
            var state = store.State(role);
            throw new ModelUnavailableException(role, state.State switch
            {
                "downloading" => $"The {spec.DisplayName} model is still downloading ({state.Fraction:P0}).",
                "failed" => $"The {spec.DisplayName} model could not be downloaded: {state.Error}",
                _ => $"The {spec.DisplayName} model is not on this server yet.",
            });
        }
        var lazy = _models.GetOrAdd(role, r => new Lazy<Task<OnnxTextModel>>(() => Task.Run(() => Load(r), CancellationToken.None)));
        try
        {
            var model = await lazy.Value.WaitAsync(ct);
            _info.AddOrUpdate(role, _ => Info(role), (_, i) => i with { LastUsedAt = DateTimeOffset.UtcNow });
            return model;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _models.TryRemove(role, out _);
            throw new ModelUnavailableException(role, $"The {spec.DisplayName} model failed to load: {ex.Message}");
        }
    }

    private LoadedModelInfo Info(ModelRole role) => new(role, catalog[role].Slug, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0);

    private OnnxTextModel Load(ModelRole role)
    {
        var spec = catalog[role];
        var sw = Stopwatch.StartNew();
        var tokenizer = Tokenizer(spec);
        OnnxTextModel model = spec.Head == HeadKind.MeanPooling
            ? new OnnxEmbedder(spec, store.ModelPath(spec), tokenizer, Threads, O.MaxSequenceTokens, O.BatchSize)
            : new OnnxTextClassifier(spec, store.ModelPath(spec), tokenizer, Threads, O.MaxSequenceTokens, O.BatchSize);
        log.LogInformation("Loaded {Slug} in {Ms} ms ({Threads} threads)", spec.Slug, sw.ElapsedMilliseconds, Threads);
        _info[role] = new LoadedModelInfo(role, spec.Slug, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, sw.ElapsedMilliseconds);
        return model;
    }

    private ITextTokenizer Tokenizer(ModelSpec spec)
    {
        var path = store.TokenizerPath(spec);
        var key = spec.Tokenizer.Sha256 ?? path;
        return _tokenizers.GetOrAdd(key, _ => new Lazy<ITextTokenizer>(() => spec.TokenizerKind switch
        {
            TokenizerKind.XlmRoberta => XlmRobertaTokenizer.FromFile(path),
            _ => WordPieceTokenizer.FromFile(path),
        })).Value;
    }

    /// <summary>Releases every loaded model (admin "free memory", or idle).</summary>
    public void UnloadAll(string reason)
    {
        foreach (var role in _models.Keys.ToList())
        {
            if (!_models.TryRemove(role, out var lazy)) continue;
            _info.TryRemove(role, out _);
            if (lazy.IsValueCreated && lazy.Value.IsCompletedSuccessfully) lazy.Value.Result.Dispose();
        }
        _tokenizers.Clear();
        GC.Collect();
        log.LogInformation("Models unloaded ({Reason})", reason);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var idle = TimeSpan.FromMinutes(Math.Max(1, O.UnloadIdleMinutes));
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                if (_models.IsEmpty || Volatile.Read(ref _inUse) > 0) continue;
                var lastUse = _info.Values.Select(i => i.LastUsedAt).DefaultIfEmpty(DateTimeOffset.MinValue).Max();
                if (DateTimeOffset.UtcNow - lastUse > idle) UnloadAll($"idle for {idle.TotalMinutes:0} min");
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    public override void Dispose()
    {
        UnloadAll("shutdown");
        _gate.Dispose();
        base.Dispose();
    }

    private sealed class Releaser(Action release) : IDisposable
    {
        private int _done;
        public void Dispose() { if (Interlocked.Exchange(ref _done, 1) == 0) release(); }
    }
}
