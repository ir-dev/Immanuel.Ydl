namespace Ark.Feedback.Analysis;

/// <summary>
/// Keeps an analysis inside its CPU budget: when scoring every comment would take longer than allowed, a sample is
/// scored instead — the pinned and creator comments, the most-liked third, and a time-spread random draw of the rest.
/// </summary>
public static class Sampling
{
    public sealed record Plan(int Keep, bool Sampled, string? Note);

    /// <summary>How many comments to score given a measured per-comment cost and the work each profile adds on top of sentiment.</summary>
    public static Plan Decide(int available, double msPerComment, double workFactor, int budgetSeconds, int minimum = 60)
    {
        if (available <= 0) return new Plan(0, false, null);
        var budgetMs = Math.Max(5, budgetSeconds) * 1000.0;
        var perComment = Math.Max(0.5, msPerComment) * Math.Max(1, workFactor);
        var affordable = (int)Math.Floor(budgetMs / perComment);
        if (affordable >= available) return new Plan(available, false, null);
        var keep = Math.Clamp(affordable, Math.Min(minimum, available), available);
        var note = $"Scoring all {available:N0} comments would take about {TimeSpanText(available * perComment)} on this server, so {keep:N0} were scored: pinned and creator comments, the most-liked third, and a random draw spread over time.";
        return new Plan(keep, true, note);
    }

    /// <summary>Indices of the comments to score, in original order.</summary>
    public static int[] Select(IReadOnlyList<ScoredComment> comments, int keep, int seed = 11)
    {
        var n = comments.Count;
        if (keep >= n) return Enumerable.Range(0, n).ToArray();
        var chosen = new HashSet<int>();
        for (var i = 0; i < n; i++)
            if (comments[i].IsPinned || comments[i].AuthorIsUploader) chosen.Add(i);
        var byLikes = Enumerable.Range(0, n).Where(i => !chosen.Contains(i)).OrderByDescending(i => comments[i].Likes).ThenBy(i => i).ToList();
        var topShare = Math.Max(0, keep / 3 - chosen.Count);
        foreach (var i in byLikes.Take(topShare)) chosen.Add(i);
        // the rest: a seeded draw spread over time (comments come newest/top first; stride through them)
        var rest = Enumerable.Range(0, n).Where(i => !chosen.Contains(i)).OrderBy(i => comments[i].PublishedAt ?? DateTimeOffset.MinValue).ThenBy(i => i).ToList();
        var need = keep - chosen.Count;
        if (need > 0 && rest.Count > 0)
        {
            var rnd = new Random(seed);
            var stride = (double)rest.Count / need;
            for (var k = 0; k < need; k++)
            {
                var lo = (int)Math.Floor(k * stride);
                var hi = Math.Min(rest.Count - 1, (int)Math.Floor((k + 1) * stride) - 1);
                chosen.Add(rest[lo >= hi ? lo : rnd.Next(lo, hi + 1)]);
            }
        }
        return chosen.OrderBy(i => i).ToArray();
    }

    private static string TimeSpanText(double ms)
    {
        var s = ms / 1000;
        if (s < 90) return $"{s:0} seconds";
        var m = s / 60;
        return m < 90 ? $"{m:0} minutes" : $"{m / 60:0.#} hours";
    }
}
