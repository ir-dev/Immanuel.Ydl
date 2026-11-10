using Ark.Feedback.Nlp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ark.Feedback.Tests;

/// <summary>Where the real model files live when the app has been run on this machine (the integration tests skip without them).</summary>
public static class Files
{
    public static string RepoRoot
    {
        get
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d is not null && !File.Exists(Path.Combine(d.FullName, "ark-feedback.sln"))) d = d.Parent;
            return d?.FullName ?? AppContext.BaseDirectory;
        }
    }

    public static string Models => Path.Combine(RepoRoot, "src", "Ark.Feedback", "App_Data", "models");
    public static string TestData(string name) => Path.Combine(AppContext.BaseDirectory, "TestData", name);
    public static string? ModelFile(string slug, string file)
    {
        var p = Path.Combine(Models, slug, file);
        return File.Exists(p) ? p : null;
    }
}

public static class Logs
{
    public static ILogger<T> Of<T>() => NullLogger<T>.Instance;
}
