using System.Net;
using System.Security.Cryptography;

namespace Ark.Feedback.Tools;

/// <summary>Progress of one file download: bytes so far and, when the server said, the total.</summary>
public sealed record DownloadProgress(long Bytes, long? Total)
{
    public double? Fraction => Total is > 0 ? Math.Clamp((double)Bytes / Total.Value, 0, 1) : null;
}

/// <summary>
/// GET with retries (timeouts, dropped connections, 408/429/5xx), streamed to disk while hashing, verified
/// against an expected SHA-256 when one is known. Shared by the yt-dlp bootstrap and the model store.
/// </summary>
public sealed class HttpDownloader(IHttpClientFactory http, ILogger<HttpDownloader> log)
{
    private const int Attempts = 3;

    /// <summary>Wait before the first retry (tests shorten it).</summary>
    internal TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(2);

    private static bool IsTransient(HttpStatusCode s) =>
        s is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    /// <summary>A successful response with its headers read (body still streaming), or an exception naming the host and status.</summary>
    public async Task<HttpResponseMessage> GetAsync(string url, CancellationToken ct, string? accept = null, string client = "downloads")
    {
        var c = http.CreateClient(client);
        accept ??= url.StartsWith("https://api.github.com/", StringComparison.OrdinalIgnoreCase) ? "application/octet-stream" : null;
        for (var attempt = 1; ; attempt++)
        {
            var delay = RetryDelay * (attempt == 1 ? 1 : 3);
            HttpResponseMessage resp;
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Get, url);
                if (accept is not null) req.Headers.Accept.ParseAdd(accept);
                resp = await c.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (Exception ex) when (attempt < Attempts && !ct.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException)
            {
                log.LogWarning("GET {Url} failed ({Reason}); trying again in {Delay}", url, ex.Message, delay);
                await Task.Delay(delay, ct);
                continue;
            }
            if (resp.IsSuccessStatusCode) return resp;

            var (status, reason) = (resp.StatusCode, resp.ReasonPhrase);
            resp.Dispose();
            var answer = $"{new Uri(url).Host} answered {(int)status} ({reason})";
            if (attempt >= Attempts || !IsTransient(status))
                throw new HttpRequestException(attempt > 1 ? $"{answer}, {attempt} times." : answer + ".", null, status);
            log.LogWarning("GET {Url}: {Answer}; trying again in {Delay}", url, answer, delay);
            await Task.Delay(delay, ct);
        }
    }

    public async Task<string> GetStringAsync(string url, CancellationToken ct, string? accept = null)
    {
        using var resp = await GetAsync(url, ct, accept);
        return await resp.Content.ReadAsStringAsync(ct);
    }

    /// <summary>
    /// Streams <paramref name="url"/> into <paramref name="dest"/> (written as dest + ".part" and renamed at the end)
    /// and returns the file's SHA-256. With <paramref name="expectedSha256"/>, a mismatch deletes the file and throws.
    /// </summary>
    public async Task<string> DownloadAsync(string url, string dest, string? expectedSha256, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        var part = dest + ".part";
        string actual;
        try
        {
            using var resp = await GetAsync(url, ct);
            var total = resp.Content.Headers.ContentLength;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buf = new byte[1 << 16];
            long done = 0;
            var lastReport = DateTime.UtcNow;
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                hash.AppendData(buf, 0, n);
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                if (progress is not null && (DateTime.UtcNow - lastReport).TotalMilliseconds > 250)
                {
                    progress.Report(new DownloadProgress(done, total));
                    lastReport = DateTime.UtcNow;
                }
            }
            progress?.Report(new DownloadProgress(done, total ?? done));
            actual = Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        catch
        {
            TryDelete(part);
            throw;
        }
        if (expectedSha256 is not null && !string.Equals(expectedSha256, actual, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(part);
            throw new InvalidOperationException($"Checksum mismatch for {Path.GetFileName(dest)}: expected {expectedSha256}, got {actual}.");
        }
        File.Move(part, dest, overwrite: true);
        return actual;
    }

    public static string Sha256Of(string file)
    {
        using var fs = File.OpenRead(file);
        return Convert.ToHexStringLower(SHA256.HashData(fs));
    }

    private static void TryDelete(string file)
    {
        try { if (File.Exists(file)) File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
