namespace Ark.Feedback.Nlp;

/// <summary>Spherical k-means on unit vectors (cosine similarity), k-means++ seeding with a fixed seed so reports are reproducible.</summary>
public static class KMeans
{
    public sealed record Result(int[] Assignment, float[][] Centroids, float[] Similarity);

    public static Result Run(float[][] vectors, int k, int iterations = 25, int seed = 7)
    {
        var n = vectors.Length;
        if (n == 0) return new Result([], [], []);
        k = Math.Clamp(k, 1, n);
        var dim = vectors[0].Length;
        var rnd = new Random(seed);
        var centroids = new float[k][];
        // k-means++
        centroids[0] = (float[])vectors[rnd.Next(n)].Clone();
        var best = new float[n];
        for (var c = 1; c < k; c++)
        {
            var total = 0.0;
            for (var i = 0; i < n; i++)
            {
                var d = float.MaxValue;
                for (var j = 0; j < c; j++) d = MathF.Min(d, 1f - Dot(vectors[i], centroids[j]));
                best[i] = d * d;
                total += best[i];
            }
            var r = rnd.NextDouble() * total;
            var pick = n - 1;
            for (var i = 0; i < n; i++)
            {
                r -= best[i];
                if (r <= 0) { pick = i; break; }
            }
            centroids[c] = (float[])vectors[pick].Clone();
        }

        var assign = new int[n];
        var sim = new float[n];
        for (var it = 0; it < iterations; it++)
        {
            var changed = false;
            for (var i = 0; i < n; i++)
            {
                var bestC = 0;
                var bestS = float.NegativeInfinity;
                for (var c = 0; c < k; c++)
                {
                    var s = Dot(vectors[i], centroids[c]);
                    if (s > bestS) { bestS = s; bestC = c; }
                }
                if (assign[i] != bestC) { assign[i] = bestC; changed = true; }
                sim[i] = bestS;
            }
            var sums = new float[k][];
            var counts = new int[k];
            for (var c = 0; c < k; c++) sums[c] = new float[dim];
            for (var i = 0; i < n; i++)
            {
                counts[assign[i]]++;
                var v = vectors[i];
                var s = sums[assign[i]];
                for (var d = 0; d < dim; d++) s[d] += v[d];
            }
            for (var c = 0; c < k; c++)
            {
                if (counts[c] == 0) { centroids[c] = (float[])vectors[rnd.Next(n)].Clone(); changed = true; continue; }
                var norm = 0f;
                for (var d = 0; d < dim; d++) norm += sums[c][d] * sums[c][d];
                norm = MathF.Sqrt(norm);
                for (var d = 0; d < dim; d++) centroids[c][d] = norm > 0 ? sums[c][d] / norm : 0;
            }
            if (!changed && it > 0) break;
        }
        return new Result(assign, centroids, sim);
    }

    public static float Dot(float[] a, float[] b)
    {
        var s = 0f;
        var n = Math.Min(a.Length, b.Length);
        for (var i = 0; i < n; i++) s += a[i] * b[i];
        return s;
    }
}
