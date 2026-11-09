using System.Diagnostics;
using System.Text;

namespace Ark.Ydl.Tools;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr, bool TimedOut);

/// <summary>
/// Starts tools with an argument list (never a shell string), so nothing a user types can
/// become a command. Cancellation kills the whole process tree (yt-dlp spawns ffmpeg).
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

    /// <summary>Runs to completion, handing every stdout/stderr line to <paramref name="onLine"/> (isError = stderr).</summary>
    public static async Task<int> RunStreamingAsync(string file, IReadOnlyList<string> args, Action<string, bool> onLine,
        string? workDir, CancellationToken ct)
    {
        using var p = new Process { StartInfo = Create(file, args, workDir) };
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) onLine(e.Data, false); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) onLine(e.Data, true); };
        p.Start();
        p.StandardInput.Close();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        try
        {
            await p.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            Kill(p);
            throw;
        }
        return p.ExitCode;
    }

    /// <summary>Runs and captures all output; a timeout kills the process and reports TimedOut.</summary>
    public static async Task<ProcessResult> RunCaptureAsync(string file, IReadOnlyList<string> args, TimeSpan timeout,
        CancellationToken ct, string? workDir = null)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        using var p = new Process { StartInfo = Create(file, args, workDir) };
        p.Start();
        p.StandardInput.Close();
        var outTask = p.StandardOutput.ReadToEndAsync(cts.Token);
        var errTask = p.StandardError.ReadToEndAsync(cts.Token);
        try
        {
            await p.WaitForExitAsync(cts.Token);
            return new ProcessResult(p.ExitCode, await outTask, await errTask, false);
        }
        catch (OperationCanceledException)
        {
            Kill(p);
            if (ct.IsCancellationRequested) throw;
            return new ProcessResult(-1, "", "", true);
        }
    }

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
