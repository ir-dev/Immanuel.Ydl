using System.Diagnostics;
using System.Text;

namespace Ark.Feedback.Tools;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr, bool TimedOut);

/// <summary>
/// Starts tools with an argument list (never a shell string), so nothing a user types can
/// become a command. Cancellation kills the whole process tree.
/// </summary>
public static class ProcessRunner
{
    private static ProcessStartInfo Create(string file, IEnumerable<string> args, string? workDir)
    {
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (workDir is not null) psi.WorkingDirectory = workDir;
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUTF8"] = "1";
        psi.Environment["PYTHONUNBUFFERED"] = "1";
        return psi;
    }

    /// <summary>
    /// Runs to completion. Stdout is collected whole (it carries the JSON); every stderr line is handed to
    /// <paramref name="onErrorLine"/> as it arrives (progress). A timeout kills the process and reports TimedOut.
    /// </summary>
    public static async Task<ProcessResult> RunAsync(string file, IReadOnlyList<string> args, TimeSpan timeout,
        Action<string>? onErrorLine, CancellationToken ct, string? workDir = null)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        using var p = new Process { StartInfo = Create(file, args, workDir) };
        var err = new StringBuilder();
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (err) { if (err.Length < 256 * 1024) err.AppendLine(e.Data); }
            try { onErrorLine?.Invoke(e.Data); } catch { /* a progress consumer must never kill the run */ }
        };
        p.Start();
        p.StandardInput.Close();
        p.BeginErrorReadLine();
        var outTask = p.StandardOutput.ReadToEndAsync(cts.Token);
        try
        {
            await p.WaitForExitAsync(cts.Token);
            var stdout = await outTask;
            p.WaitForExit(); // flush the async stderr reader
            string stderr;
            lock (err) stderr = err.ToString();
            return new ProcessResult(p.ExitCode, stdout, stderr, false);
        }
        catch (OperationCanceledException)
        {
            Kill(p);
            if (ct.IsCancellationRequested) throw;
            string stderr;
            lock (err) stderr = err.ToString();
            return new ProcessResult(-1, "", stderr, true);
        }
    }

    /// <summary>Runs and captures all output; a timeout kills the process and reports TimedOut.</summary>
    public static Task<ProcessResult> RunCaptureAsync(string file, IReadOnlyList<string> args, TimeSpan timeout,
        CancellationToken ct, string? workDir = null) => RunAsync(file, args, timeout, null, ct, workDir);

    private static void Kill(Process p)
    {
        try
        {
            if (!p.HasExited) p.Kill(entireProcessTree: true);
        }
        catch
        {
            // already gone
        }
    }
}
