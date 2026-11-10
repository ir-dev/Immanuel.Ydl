using System.Collections.Concurrent;
using Ark.Feedback.Options;
using Ark.Feedback.Tools;
using Microsoft.Extensions.Options;

namespace Ark.Feedback.Nlp;

public sealed record ModelFileState(string State, long Bytes, long? Total, string? Error, DateTimeOffset? ReadyAt)
{
    public bool Ready => State == "ready";
    public double? Fraction => Total is > 0 ? Math.Clamp((double)Bytes / Total.Value, 0, 1) : null;
}

/// <summary>
/// Keeps the model files on disk: {DataRoot}/models/{slug}/. A file is downloaded from the model host, hashed while it
/// streams and accepted only when the SHA-256 matches the catalog; a ".ok" marker beside it records the verified hash so
/// a restart doesn't re-read 280 MB. Nothing here blocks a request: a missing model makes the analysis queue wait and
/// the UI say "preparing models".
/// </summary>
public sealed class ModelStore(ModelCatalog catalog, FeedbackPaths paths, HttpDownloader http, IOptions<FeedbackOptions> opt, ILogger<ModelStore> log)
{
    private readonly ConcurrentDictionary<ModelRole, ModelFileState> _state = new();
    private readonly ConcurrentDictionary<ModelRole, SemaphoreSlim> _locks = new();

    public string Dir(ModelSpec spec) => Path.Combine(paths.Models, spec.Slug);
    public string ModelPath(ModelSpec spec) => Path.Combine(Dir(spec), spec.Model.LocalName);
    public string TokenizerPath(ModelSpec spec) => Path.Combine(Dir(spec), spec.Tokenizer.LocalName);

    public ModelFileState State(ModelRole role) =>
        _state.TryGetValue(role, out var s) ? s : IsReadyOnDisk(catalog[role]) ? new ModelFileState("ready", catalog[role].DownloadBytes, catalog[role].DownloadBytes, null, null) : new ModelFileState("missing", 0, catalog[role].DownloadBytes, null, null);

    public IReadOnlyDictionary<ModelRole, ModelFileState> States() =>
        catalog.All.ToDictionary(s => s.Role, s => State(s.Role));

    public bool IsReady(ModelRole role) => State(role).Ready;

    /// <summary>Files present with a verified marker (the model) and a plausible size (the tokenizer).</summary>
    internal bool IsReadyOnDisk(ModelSpec spec)
    {
        var model = ModelPath(spec);
        var tok = TokenizerPath(spec);
        if (!File.Exists(model) || !File.Exists(tok)) return false;
        if (new FileInfo(tok).Length == 0) return false;
        if (spec.Model.Sha256 is null) return new FileInfo(model).Length > 0;
        var marker = model + ".ok";
        return File.Exists(marker) && File.ReadAllText(marker).Trim().Equals(spec.Model.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Downloads whatever is missing for <paramref name="role"/>. Safe to call concurrently; the second caller waits for the first.</summary>
    public async Task<bool> EnsureAsync(ModelRole role, CancellationToken ct)
    {
        var spec = catalog[role];
        if (IsReadyOnDisk(spec))
        {
            _state[role] = new ModelFileState("ready", spec.DownloadBytes, spec.DownloadBytes, null, DateTimeOffset.UtcNow);
            return true;
        }
        var gate = _locks.GetOrAdd(role, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (IsReadyOnDisk(spec)) return true;
            if (!opt.Value.Analysis.AutoDownloadModels)
            {
                _state[role] = new ModelFileState("missing", 0, spec.DownloadBytes, $"Model files are missing in {Dir(spec)} and Feedback:Analysis:AutoDownloadModels is off.", null);
                return false;
            }
            Directory.CreateDirectory(Dir(spec));
            var host = opt.Value.Analysis.ModelHost.TrimEnd('/') + "/";
            long baseBytes = 0;
            try
            {
                var tok = TokenizerPath(spec);
                if (!File.Exists(tok) || new FileInfo(tok).Length == 0)
                {
                    _state[role] = new ModelFileState("downloading", 0, spec.DownloadBytes, null, null);
                    await http.DownloadAsync(Url(host, spec, spec.Tokenizer), tok, spec.Tokenizer.Sha256, null, ct);
                }
                baseBytes = spec.Tokenizer.Size;
                var model = ModelPath(spec);
                var marker = model + ".ok";
                if (!File.Exists(marker) || !IsReadyOnDisk(spec))
                {
                    log.LogInformation("Downloading model {Slug} ({Repo}/{File}, {Mb} MB)", spec.Slug, spec.Repo, spec.Model.Path, spec.Model.Size / 1_000_000);
                    var progress = new Progress<DownloadProgress>(p =>
                        _state[role] = new ModelFileState("downloading", baseBytes + p.Bytes, spec.Model.Size > 0 ? spec.DownloadBytes : baseBytes + (p.Total ?? p.Bytes), null, null));
                    _state[role] = new ModelFileState("downloading", baseBytes, spec.DownloadBytes, null, null);
                    var sha = await http.DownloadAsync(Url(host, spec, spec.Model), model, spec.Model.Sha256, progress, ct);
                    await File.WriteAllTextAsync(marker, sha, ct);
                    log.LogInformation("Model {Slug} ready (sha256 {Sha})", spec.Slug, sha);
                }
                _state[role] = new ModelFileState("ready", spec.DownloadBytes, spec.DownloadBytes, null, DateTimeOffset.UtcNow);
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                log.LogWarning(ex, "Model {Slug} could not be downloaded", spec.Slug);
                _state[role] = new ModelFileState("failed", baseBytes, spec.DownloadBytes, ex.Message, null);
                return false;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private static string Url(string host, ModelSpec spec, ModelFile file) =>
        $"{host}{spec.Repo}/resolve/{Uri.EscapeDataString(spec.Revision)}/{file.Path}";

    /// <summary>Size on disk of every model folder.</summary>
    public long BytesOnDisk()
    {
        if (!Directory.Exists(paths.Models)) return 0;
        return Directory.EnumerateFiles(paths.Models, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
    }
}

/// <summary>
/// Downloads the models the allowed profiles need, in the order a visitor is most likely to need them (the default
/// profile's sentiment model first), retrying a failed download 10 minutes later, then doubling up to 6 hours.
/// </summary>
public sealed class ModelMaintenanceService(ModelStore store, ModelCatalog catalog, IOptions<FeedbackOptions> opt, ILogger<ModelMaintenanceService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = opt.Value.Analysis;
        if (!o.AutoDownloadModels) return;
        var wait = TimeSpan.FromMinutes(10);
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                var roles = Wanted(o);
                var failed = false;
                foreach (var role in roles)
                {
                    if (store.IsReady(role)) continue;
                    if (!await store.EnsureAsync(role, stoppingToken)) failed = true;
                }
                if (!failed)
                {
                    log.LogInformation("All models for the allowed profiles are on disk");
                    return;
                }
                log.LogWarning("Some models are still missing; next try in {Wait}", wait);
                await Task.Delay(wait, stoppingToken);
                wait = TimeSpan.FromTicks(Math.Min(wait.Ticks * 2, TimeSpan.FromHours(6).Ticks));
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    internal IReadOnlyList<ModelRole> Wanted(AnalysisOptions o)
    {
        var list = new List<ModelRole>();
        var def = catalog.Resolve(null, o);
        list.AddRange(def.Roles);
        var allowed = o.AllowedProfiles.Count == 0 ? ModelCatalog.Profiles : ModelCatalog.Profiles.Where(p => o.AllowedProfiles.Contains(p.Id, StringComparer.OrdinalIgnoreCase));
        foreach (var p in allowed) list.AddRange(catalog.Resolve(p.Id, o).Roles);
        return list.Where(r => catalog[r].Enabled).Distinct().ToList();
    }
}
