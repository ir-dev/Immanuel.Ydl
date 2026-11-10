using System.Diagnostics;
using Ark.Feedback.Nlp;
using Ark.Feedback.Options;
using Ark.Feedback.YouTube;
using Microsoft.Extensions.Options;

namespace Ark.Feedback.Analysis;

/// <summary>
/// One analysis, start to finish: fetch → clean and de-duplicate → languages → (measure, then maybe sample) →
/// sentiment → embeddings, repeats and themes → toxicity / emotion → report. Every model call runs under the
/// host's inference gate so two analyses never fight for the same cores.
/// </summary>
public sealed class AnalysisPipeline(CommentSourceSelector sources, ModelHost models, ModelCatalog catalog, LanguageDetector lid,
    IOptions<FeedbackOptions> opt, ILogger<AnalysisPipeline> log)
{
    private AnalysisOptions O => opt.Value.Analysis;

    public static string AppVersion => typeof(AnalysisPipeline).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    public async Task<Report> RunAsync(AnalysisJob job, CancellationToken ct)
    {
        var total = Stopwatch.StartNew();
        var profile = job.Profile;

        // ── 1. fetch ────────────────────────────────────────────────────────
        job.Update(JobState.Fetching, "fetching", "Contacting YouTube", 0.02);
        var fetchWatch = Stopwatch.StartNew();
        var request = new FetchRequest(job.Url, profile.MaxComments, O.RepliesPerThread);
        var progress = new Progress<FetchProgress>(p =>
        {
            var frac = p.Total is > 0 ? Math.Min(0.3, 0.05 + 0.25 * Math.Min(1, p.Fetched / (double)Math.Min(p.Total.Value, profile.MaxComments))) : 0.05;
            var msg = p.Total is > 0 ? $"{p.Stage}: {p.Fetched:N0} of ~{Math.Min(p.Total.Value, profile.MaxComments):N0}" : p.Stage;
            job.Update(JobState.Fetching, "fetching", msg, frac, p.Fetched, p.Total);
        });
        var fetched = await FetchAsync(request, progress, ct);
        fetchWatch.Stop();
        var video = fetched.Video;
        job.Update(JobState.Fetching, "fetching", $"{fetched.Comments.Count:N0} comments fetched", 0.3, fetched.Comments.Count, video.CommentCount);

        // ── 2. clean + exact repeats ────────────────────────────────────────
        var all = new List<ScoredComment>(fetched.Comments.Count);
        var byKey = new Dictionary<string, ScoredComment>(StringComparer.Ordinal);
        var repeats = 0;
        foreach (var raw in fetched.Comments)
        {
            var text = TextTools.Clean(raw.Text);
            if (text.Length == 0) continue;
            var sc = new ScoredComment
            {
                Id = raw.Id, ParentId = raw.ParentId, IsReply = raw.IsReply, Text = TextTools.Clip(text, 2000), Author = raw.Author, AuthorId = raw.AuthorId,
                AuthorIsUploader = raw.AuthorIsUploader, AuthorIsVerified = raw.AuthorIsVerified, IsPinned = raw.IsPinned, IsHearted = raw.IsFavorited,
                Likes = raw.LikeCount, PublishedAt = raw.PublishedAt,
            };
            var key = TextTools.DedupKey(text);
            if (key.Length >= 3 && byKey.TryGetValue(key, out var first))
            {
                sc.DuplicateOf = first.Id;
                first.Repeats++;
                repeats++;
            }
            else if (key.Length >= 3) byKey[key] = sc;
            all.Add(sc);
        }
        if (all.Count == 0)
            throw new CommentSourceException(video.CommentCount is > 0 ? "YouTube returned no readable comments for this video." : "This video has no comments yet.");

        // ── 3. languages ────────────────────────────────────────────────────
        job.Update(JobState.Analysing, "languages", "Detecting languages", 0.33);
        foreach (var c in all)
        {
            var src = c.DuplicateOf is null ? c : all.First(x => x.Id == c.DuplicateOf);
            var g = ReferenceEquals(src, c) ? lid.Detect(c.Text) : new LanguageGuess(src.Lang, src.LangConfidence, "dup");
            c.Lang = g.Code;
            c.LangName = LanguageDetector.Name(g.Code);
            c.LangConfidence = Math.Round(g.Confidence, 3);
        }

        // ── 4. models, under the gate ───────────────────────────────────────
        var unique = all.Where(c => c.DuplicateOf is null).ToList();
        var uses = new List<ModelUse>();
        var inferWatch = new Stopwatch();
        var sampled = false;
        string? sampleNote = null;
        double msPer = 0;
        List<ScoredComment> scored;
        float[][]? embeddings = null;
        KMeans.Result? clusters = null;

        job.Update(JobState.Analysing, "waiting", "Waiting for the model slot", 0.34);
        using (await models.AcquireAsync(ct))
        {
            var sentimentRole = profile.SentimentModel;
            var sentimentSpec = catalog[sentimentRole];
            job.Update(JobState.Analysing, "loading", $"Loading {sentimentSpec.DisplayName.Split(' ')[0]} sentiment model", 0.35);
            var classifier = await models.ClassifierAsync(sentimentRole, ct);

            // measure the cost on the first batch, then decide how many to score
            inferWatch.Start();
            var probe = unique.Take(Math.Min(unique.Count, classifier.BatchSize)).ToList();
            var sw = Stopwatch.StartNew();
            var probeProbs = classifier.Predict(probe.Select(c => c.Text).ToList(), null, ct);
            sw.Stop();
            msPer = probe.Count == 0 ? 0 : sw.Elapsed.TotalMilliseconds / probe.Count;
            var factor = 1.0 + (profile.Themes ? 0.45 : 0) + (profile.Toxicity ? 0.5 : 0) + (profile.Emotion ? 0.7 : 0);
            var plan = Sampling.Decide(unique.Count, msPer, factor, O.TimeBudgetSeconds);
            sampled = plan.Sampled;
            sampleNote = plan.Note;
            var chosen = plan.Sampled ? Sampling.Select(unique, plan.Keep).Select(i => unique[i]).ToList() : unique;
            var chosenSet = new HashSet<string>(chosen.Select(c => c.Id), StringComparer.Ordinal);
            // the probe batch is scored already; make sure its members are part of the chosen set
            foreach (var p in probe) if (chosenSet.Add(p.Id)) chosen.Add(p);
            for (var i = 0; i < probe.Count; i++) ApplySentiment(probe[i], probeProbs[i], sentimentSpec);

            var remaining = chosen.Where(c => !probe.Any(p => p.Id == c.Id)).ToList();
            var done = probe.Count;
            var target = chosen.Count;
            job.Update(JobState.Analysing, "sentiment", $"Scoring sentiment {done:N0} / {target:N0}", 0.36, analysed: done);
            if (remaining.Count > 0)
            {
                var probs = classifier.Predict(remaining.Select(c => c.Text).ToList(), n =>
                    job.Update(JobState.Analysing, "sentiment", $"Scoring sentiment {probe.Count + n:N0} / {target:N0}", 0.36 + 0.34 * (probe.Count + n) / Math.Max(1, target), analysed: probe.Count + n), ct);
                for (var i = 0; i < remaining.Count; i++) ApplySentiment(remaining[i], probs[i], sentimentSpec);
            }
            uses.Add(new ModelUse { Role = "sentiment", Slug = sentimentSpec.Slug, Name = sentimentSpec.DisplayName, Items = target, Ms = inferWatch.ElapsedMilliseconds });
            scored = chosen;

            // themes + repeats
            if (profile.Themes && scored.Count >= 24)
            {
                var t0 = inferWatch.ElapsedMilliseconds;
                job.Update(JobState.Analysing, "themes", "Finding themes", 0.72);
                var embedder = await models.EmbedderAsync(ct);
                embeddings = embedder.Embed(scored.Select(c => c.Text).ToList(), n =>
                    job.Update(JobState.Analysing, "themes", $"Embedding comments {n:N0} / {scored.Count:N0}", 0.72 + 0.12 * n / scored.Count), ct);
                var k = Math.Clamp((int)Math.Round(Math.Sqrt(scored.Count / 2.0)), 3, 8);
                clusters = KMeans.Run(embeddings, k);
                for (var i = 0; i < scored.Count; i++) scored[i].Theme = clusters.Assignment[i];
                MarkNearRepeats(scored, embeddings);
                var embSpec = catalog[ModelRole.Embeddings];
                uses.Add(new ModelUse { Role = "embeddings", Slug = embSpec.Slug, Name = embSpec.DisplayName, Items = scored.Count, Ms = inferWatch.ElapsedMilliseconds - t0 });
            }

            if (profile.Toxicity)
            {
                var t0 = inferWatch.ElapsedMilliseconds;
                job.Update(JobState.Analysing, "toxicity", "Checking for toxic comments", 0.85);
                var tox = await models.ClassifierAsync(ModelRole.Toxicity, ct);
                var spec = catalog[ModelRole.Toxicity];
                var toxicIdx = Math.Max(0, spec.Labels.ToList().FindIndex(l => l.Equals("toxic", StringComparison.OrdinalIgnoreCase)));
                var probs = tox.Predict(scored.Select(c => c.Text).ToList(), n => job.Update(JobState.Analysing, "toxicity", $"Checking toxicity {n:N0} / {scored.Count:N0}", 0.85 + 0.06 * n / scored.Count), ct);
                for (var i = 0; i < scored.Count; i++) scored[i].Toxic = Math.Round(probs[i][toxicIdx], 3);
                uses.Add(new ModelUse { Role = "toxicity", Slug = spec.Slug, Name = spec.DisplayName, Items = scored.Count, Ms = inferWatch.ElapsedMilliseconds - t0 });
            }

            if (profile.Emotion)
            {
                var t0 = inferWatch.ElapsedMilliseconds;
                job.Update(JobState.Analysing, "emotion", "Reading emotions", 0.91);
                var emo = await models.ClassifierAsync(ModelRole.Emotion, ct);
                var spec = catalog[ModelRole.Emotion];
                var probs = emo.Predict(scored.Select(c => c.Text).ToList(), n => job.Update(JobState.Analysing, "emotion", $"Reading emotions {n:N0} / {scored.Count:N0}", 0.91 + 0.06 * n / scored.Count), ct);
                for (var i = 0; i < scored.Count; i++)
                {
                    var best = -1;
                    var bestP = 0f;
                    for (var c = 0; c < probs[i].Length && c < spec.Labels.Count; c++)
                        if (probs[i][c] > bestP) { bestP = probs[i][c]; best = c; }
                    if (best >= 0) { scored[i].Emotion = spec.Labels[best]; scored[i].EmotionP = Math.Round(bestP, 3); }
                }
                uses.Add(new ModelUse { Role = "emotion", Slug = spec.Slug, Name = spec.DisplayName, Items = scored.Count, Ms = inferWatch.ElapsedMilliseconds - t0 });
            }
            inferWatch.Stop();
        }

        // ── 5. assemble: repeats inherit scores; unscored comments (sampling) are left out ──
        var scoredIds = new HashSet<string>(scored.Select(c => c.Id), StringComparer.Ordinal);
        var final = new List<ScoredComment>(all.Count);
        var byId = scored.ToDictionary(c => c.Id, StringComparer.Ordinal);
        foreach (var c in all)
        {
            if (scoredIds.Contains(c.Id)) { final.Add(c); continue; }
            if (c.DuplicateOf is not null && byId.TryGetValue(c.DuplicateOf, out var src))
            {
                c.Sentiment = src.Sentiment; c.Score = src.Score; c.PPos = src.PPos; c.PNeu = src.PNeu; c.PNeg = src.PNeg;
                c.Toxic = src.Toxic; c.Emotion = src.Emotion; c.EmotionP = src.EmotionP; c.Theme = src.Theme;
                final.Add(c);
            }
        }
        // the embeddings/clusters are aligned with `scored`; the builder reads Theme off each comment, and the quotes use the similarity array
        var facts = new AnalysisFacts
        {
            Id = job.Id,
            Profile = profile,
            AppVersion = AppVersion,
            Video = video,
            Fetch = new FetchSummary
            {
                Source = fetched.Source, ToolVersion = fetched.ToolVersion, Fetched = fetched.Comments.Count, Analysed = final.Count, Unique = unique.Count,
                ExactRepeats = repeats, TopLevel = final.Count(c => !c.IsReply), Replies = final.Count(c => c.IsReply), TotalOnYouTube = video.CommentCount,
                Sampled = sampled, SampleNote = sampleNote, Warnings = fetched.Warnings.ToList(),
            },
            Comments = AlignForThemes(final, scored),
            Embeddings = embeddings,
            Clusters = clusters is null ? null : Realign(clusters, scored, final),
            Models = uses,
            Compute = new ComputeSummary
            {
                TotalMs = total.ElapsedMilliseconds, FetchMs = fetchWatch.ElapsedMilliseconds, InferMs = inferWatch.ElapsedMilliseconds,
                Threads = models.Threads, MsPerComment = Math.Round(msPer, 1),
            },
        };
        job.Update(JobState.Analysing, "report", "Writing the report", 0.98);
        var report = ReportBuilder.Build(facts);
        log.LogInformation("Analysis {Id}: {Video} — {Analysed} comments in {Ms} ms (fetch {Fetch} ms, models {Infer} ms, {MsPer} ms/comment)",
            job.Id, video.Id, final.Count, total.ElapsedMilliseconds, fetchWatch.ElapsedMilliseconds, inferWatch.ElapsedMilliseconds, Math.Round(msPer, 1));
        return report;
    }

    private static List<ScoredComment> AlignForThemes(List<ScoredComment> final, List<ScoredComment> scored) => final;

    /// <summary>The clusters were computed over `scored`; the report lists `final` (repeats appended). Expand the per-item arrays to match.</summary>
    private static KMeans.Result Realign(KMeans.Result r, List<ScoredComment> scored, List<ScoredComment> final)
    {
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < scored.Count; i++) index[scored[i].Id] = i;
        var assign = new int[final.Count];
        var sim = new float[final.Count];
        for (var i = 0; i < final.Count; i++)
        {
            var src = final[i].DuplicateOf is { } d && index.ContainsKey(d) ? d : final[i].Id;
            if (index.TryGetValue(src, out var j)) { assign[i] = r.Assignment[j]; sim[i] = final[i].DuplicateOf is null ? r.Similarity[j] : r.Similarity[j] - 1; }
            else { assign[i] = -1; }
        }
        for (var i = 0; i < final.Count; i++) if (assign[i] < 0) final[i].Theme = null;
        return new KMeans.Result(assign, r.Centroids, sim);
    }

    private static void ApplySentiment(ScoredComment c, float[] probs, ModelSpec spec)
    {
        double pos = 0, neu = 0, neg = 0;
        for (var i = 0; i < probs.Length && i < spec.Labels.Count; i++)
        {
            switch (spec.Labels[i].ToLowerInvariant())
            {
                case "positive": pos = probs[i]; break;
                case "negative": neg = probs[i]; break;
                default: neu = probs[i]; break;
            }
        }
        c.PPos = Math.Round(pos, 4); c.PNeu = Math.Round(neu, 4); c.PNeg = Math.Round(neg, 4);
        c.Score = Math.Round(pos - neg, 4);
        c.Sentiment = pos >= neu && pos >= neg ? "positive" : neg >= neu && neg >= pos ? "negative" : "neutral";
    }

    /// <summary>Comments whose vectors are nearly identical (cosine ≥ 0.93) are the same thought in different words: count them as repeats of the most-liked one.</summary>
    internal static void MarkNearRepeats(List<ScoredComment> scored, float[][] emb)
    {
        var n = scored.Count;
        if (n > 1500) return; // O(n²) — fine up to the configured maximum, skipped beyond it
        var order = Enumerable.Range(0, n).OrderByDescending(i => scored[i].Likes).ThenBy(i => i).ToArray();
        var taken = new bool[n];
        foreach (var i in order)
        {
            if (taken[i] || scored[i].Text.Length < 15) continue;
            for (var k = 0; k < n; k++)
            {
                var j = order[k];
                if (j == i || taken[j] || scored[j].Text.Length < 15) continue;
                if (KMeans.Dot(emb[i], emb[j]) >= 0.93f)
                {
                    taken[j] = true;
                    scored[i].Repeats += scored[j].Repeats;
                    scored[j].DuplicateOf ??= scored[i].Id;
                }
            }
        }
    }

    private async Task<CommentFetchResult> FetchAsync(FetchRequest request, IProgress<FetchProgress> progress, CancellationToken ct)
    {
        var primary = sources.Primary;
        try
        {
            if (!primary.Available) throw new CommentSourceException(primary.Unavailability ?? "No comment source is available.", retryable: true);
            return await primary.FetchAsync(request, progress, ct);
        }
        catch (CommentSourceException ex) when (ex.Retryable && sources.Fallback is { } fallback && fallback.Available)
        {
            log.LogWarning("{Primary} failed ({Reason}); falling back to {Fallback}", primary.Name, ex.Message, fallback.Name);
            var r = await fallback.FetchAsync(request, progress, ct);
            return r with { Warnings = r.Warnings.Prepend($"{primary.Name} failed ({ex.Message}); {fallback.Name} was used instead.").ToList() };
        }
    }
}
