using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ark.Ydl.Releases;

// ── the release manifest (releases.json, embedded) ──────────────────────────

public sealed class ReleaseManifest
{
    public string Product { get; set; } = "ARK-RAPID";
    public string App { get; set; } = "ark-ydl";
    public string Current { get; set; } = "";
    public List<ReleaseEntry> Releases { get; set; } = [];
    public List<LegacyEntry> Legacy { get; set; } = [];
}

public sealed class ReleaseEntry
{
    public string Version { get; set; } = "";
    public string? Date { get; set; }
    public string Title { get; set; } = "";
    public string? Summary { get; set; }
    public ReleaseChanges Changes { get; set; } = new();
    public ReleaseCompatibility Compatibility { get; set; } = new();
    public List<MigrationStep> Migration { get; set; } = [];
}

public sealed class ReleaseChanges
{
    public List<string> Added { get; set; } = [];
    public List<string> Changed { get; set; } = [];
    public List<string> Fixed { get; set; } = [];
    public List<string> Removed { get; set; } = [];
    public List<string> Security { get; set; } = [];
}

public sealed class ReleaseCompatibility
{
    /// <summary>compatible | config | breaking — the worst of the areas below.</summary>
    public string Level { get; set; } = Compat.Compatible;
    public CompatArea? Api { get; set; }
    public CompatArea? Config { get; set; }
    public CompatArea? Data { get; set; }
    public CompatArea? Deployment { get; set; }
    public CompatArea? Clients { get; set; }

    public IEnumerable<(string Area, CompatArea Value)> Areas()
    {
        if (Api is not null) yield return ("api", Api);
        if (Config is not null) yield return ("config", Config);
        if (Data is not null) yield return ("data", Data);
        if (Deployment is not null) yield return ("deployment", Deployment);
        if (Clients is not null) yield return ("clients", Clients);
    }
}

public sealed class CompatArea
{
    public string Level { get; set; } = Compat.Compatible;
    public string? Notes { get; set; }
}

public sealed class MigrationStep
{
    /// <summary>operator (whoever deploys it) | user (whoever uses the page) | api (scripts calling the API).</summary>
    public string Audience { get; set; } = "operator";
    public bool Required { get; set; }
    public string Title { get; set; } = "";
    public string? Detail { get; set; }
}

/// <summary>An app this one replaced. Not a semver line: both reported assembly version 1.0.0.0.</summary>
public sealed class LegacyEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Date { get; set; }
    public string? Stack { get; set; }
    public string? Summary { get; set; }
    public string UpgradesTo { get; set; } = "1.0.0";
}

public static class Compat
{
    public const string Compatible = "compatible";
    public const string Config = "config";
    public const string Breaking = "breaking";

    public static int Rank(string? level) => level switch
    {
        Breaking => 2,
        Config => 1,
        _ => 0,
    };

    public static string Worst(IEnumerable<string?> levels)
    {
        var worst = 0;
        foreach (var l in levels) worst = Math.Max(worst, Rank(l));
        return worst switch { 2 => Breaking, 1 => Config, _ => Compatible };
    }
}

// ── what the API hands out ───────────────────────────────────────────────────

public sealed record MigrationStepView(string Version, string Audience, bool Required, string Title, string? Detail);

public sealed record CompatAreaView(string Area, string Level, IReadOnlyList<string> Notes);

/// <summary>Everything needed to go from one version to the one running now.</summary>
public sealed class MigrationPlan
{
    public string From { get; init; } = "";
    public string? FromName { get; init; }
    public string To { get; init; } = "";
    /// <summary>upToDate | upgrade | legacy | newer | unknown</summary>
    public string Status { get; init; } = "upgrade";
    public string Level { get; init; } = Compat.Compatible;
    public string Recommendation { get; init; } = "";
    public IReadOnlyList<string> Path { get; init; } = [];
    public IReadOnlyList<CompatAreaView> Areas { get; init; } = [];
    public IReadOnlyList<MigrationStepView> Steps { get; init; } = [];
    public int RequiredSteps => Steps.Count(s => s.Required);
}

public sealed record MatrixRow(string From, string Name, string? Date, string Level, int Steps, int RequiredSteps, string Recommendation);

/// <summary>
/// The release manifest plus the running build's identity. Loaded once: the manifest is embedded in
/// the assembly and the web manifest ships with it, so neither can change while the app runs.
/// </summary>
public sealed class ReleaseCatalog
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public ReleaseCatalog(IWebHostEnvironment env, ILogger<ReleaseCatalog> log)
        : this(LoadEmbedded(), ReadManifestVersion(env, log), typeof(ReleaseCatalog).Assembly)
    {
    }

    internal ReleaseCatalog(ReleaseManifest manifest, string? webManifestVersion, Assembly assembly)
    {
        Manifest = manifest;
        Manifest.Releases = [.. Manifest.Releases.OrderByDescending(r => SemVer.Parse(r.Version))];
        WebManifestVersion = webManifestVersion;
        var info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        (AppVersion, Revision) = SplitInformational(info ?? assembly.GetName().Version?.ToString(3) ?? "0.0.0");
        BuiltAt = File.Exists(assembly.Location) ? File.GetLastWriteTimeUtc(assembly.Location) : null;
    }

    public ReleaseManifest Manifest { get; }

    /// <summary>The version this build reports (csproj &lt;Version&gt;).</summary>
    public string AppVersion { get; }

    /// <summary>Short source revision the SDK stamped into the informational version, when built from git.</summary>
    public string? Revision { get; }

    public DateTime? BuiltAt { get; }

    /// <summary>"version" in wwwroot/manifest.webmanifest — what an installed app was given.</summary>
    public string? WebManifestVersion { get; }

    public ReleaseEntry? CurrentRelease => Find(AppVersion);

    public ReleaseEntry? Newest => Manifest.Releases.FirstOrDefault();

    public ReleaseEntry? Find(string version) =>
        Manifest.Releases.FirstOrDefault(r => SemVer.Parse(r.Version) == SemVer.Parse(version));

    /// <summary>Every step to apply, in order, to bring <paramref name="from"/> up to the running version.</summary>
    public MigrationPlan PlanFrom(string? from)
    {
        var to = AppVersion;
        var key = (from ?? "").Trim();
        if (key.StartsWith('v') || key.StartsWith('V')) key = key[1..];
        if (key.Length == 0)
            return new MigrationPlan { From = "", To = to, Status = "unknown", Recommendation = "Pick the version you are upgrading from." };

        var legacy = Manifest.Legacy.FirstOrDefault(l => l.Id.Equals(key, StringComparison.OrdinalIgnoreCase));
        IEnumerable<ReleaseEntry> path;
        string status;
        if (legacy is not null)
        {
            // A replaced app: start at the release that replaced it, then every one after.
            var start = SemVer.Parse(legacy.UpgradesTo);
            path = Manifest.Releases.Where(r => SemVer.Parse(r.Version) >= start && SemVer.Parse(r.Version) <= SemVer.Parse(to));
            status = "legacy";
        }
        else if (!SemVer.TryParse(key, out var fromV))
        {
            return new MigrationPlan { From = key, To = to, Status = "unknown", Recommendation = $"'{key}' is not a version this app knows." };
        }
        else
        {
            var toV = SemVer.Parse(to);
            if (fromV == toV)
                return new MigrationPlan
                {
                    From = key, To = to, Status = "upToDate", Path = [],
                    Recommendation = "Already on this version. Nothing to do.",
                };
            if (fromV > toV)
                return new MigrationPlan
                {
                    From = key, To = to, Status = "newer", Level = Compat.Breaking,
                    Recommendation = $"{key} is newer than this build ({to}). Rolling back is not covered by these notes; check the newer release's notes before downgrading.",
                };
            path = Manifest.Releases.Where(r => SemVer.Parse(r.Version) > fromV && SemVer.Parse(r.Version) <= toV);
            status = "upgrade";
        }

        var ordered = path.OrderBy(r => SemVer.Parse(r.Version)).ToList();
        var steps = ordered.SelectMany(r => r.Migration.Select(m => new MigrationStepView(r.Version, m.Audience, m.Required, m.Title, m.Detail))).ToList();
        var areas = ordered.SelectMany(r => r.Compatibility.Areas().Select(a => (a.Area, r.Version, a.Value)))
            .GroupBy(a => a.Area)
            .Select(g => new CompatAreaView(g.Key, Compat.Worst(g.Select(x => x.Value.Level)),
                g.Where(x => !string.IsNullOrWhiteSpace(x.Value.Notes)).Select(x => $"{x.Version}: {x.Value.Notes}").ToList()))
            .OrderBy(a => AreaOrder(a.Area))
            .ToList();
        var level = Compat.Worst(ordered.Select(r => r.Compatibility.Level));
        var required = steps.Count(s => s.Required);

        return new MigrationPlan
        {
            From = legacy?.Id ?? key,
            FromName = legacy?.Name,
            To = to,
            Status = status,
            Level = level,
            Path = ordered.Select(r => r.Version).ToList(),
            Areas = areas,
            Steps = steps,
            Recommendation = Recommend(level, status, ordered.Count, required, to),
        };
    }

    /// <summary>One row per earlier version (and replaced app): how hard the move to this build is.</summary>
    public IReadOnlyList<MatrixRow> Matrix()
    {
        var rows = new List<MatrixRow>();
        var current = SemVer.Parse(AppVersion);
        foreach (var r in Manifest.Releases.Where(r => SemVer.Parse(r.Version) < current))
        {
            var p = PlanFrom(r.Version);
            rows.Add(new MatrixRow(r.Version, r.Title, r.Date, p.Level, p.Steps.Count, p.RequiredSteps, p.Recommendation));
        }
        foreach (var l in Manifest.Legacy)
        {
            var p = PlanFrom(l.Id);
            rows.Add(new MatrixRow(l.Id, l.Name, l.Date, p.Level, p.Steps.Count, p.RequiredSteps, p.Recommendation));
        }
        return rows;
    }

    private static string Recommend(string level, string status, int releases, int required, string to)
    {
        var hops = releases == 1 ? "one release" : $"{releases} releases";
        if (status == "legacy")
            return $"A different application: deploy {to} as a new site rather than upgrading in place, then apply the {required} required step(s) below.";
        return level switch
        {
            Compat.Breaking => $"Breaking changes across {hops}: plan a maintenance window and apply every required step ({required}) in order.",
            Compat.Config => required > 0
                ? $"Safe to deploy over the old build. Apply the {required} required step(s); the rest are worth a look."
                : $"Safe to deploy over the old build; the steps below are optional but worth a look.",
            _ => $"Drop-in upgrade across {hops}; no action needed.",
        };
    }

    private static int AreaOrder(string area) => area switch
    {
        "api" => 0, "config" => 1, "data" => 2, "deployment" => 3, "clients" => 4, _ => 9,
    };

    internal static (string Version, string? Revision) SplitInformational(string info)
    {
        var plus = info.IndexOf('+');
        if (plus < 0) return (info, null);
        var rev = info[(plus + 1)..];
        return (info[..plus], rev.Length > 7 ? rev[..7] : rev.Length == 0 ? null : rev);
    }

    internal static ReleaseManifest LoadEmbedded()
    {
        using var s = typeof(ReleaseCatalog).Assembly.GetManifestResourceStream("Ark.Ydl.releases.json")
                      ?? throw new InvalidOperationException("releases.json is not embedded in the assembly.");
        return Parse(s);
    }

    internal static ReleaseManifest Parse(Stream s) =>
        JsonSerializer.Deserialize<ReleaseManifest>(s, Json) ?? throw new InvalidDataException("releases.json is empty.");

    private static string? ReadManifestVersion(IWebHostEnvironment env, ILogger log)
    {
        try
        {
            var file = env.WebRootFileProvider.GetFileInfo("manifest.webmanifest");
            if (!file.Exists) return null;
            using var s = file.CreateReadStream();
            using var doc = JsonDocument.Parse(s);
            return doc.RootElement.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            log.LogWarning(ex, "Couldn't read the version from manifest.webmanifest");
            return null;
        }
    }
}

/// <summary>major.minor.patch, enough for this app's own releases. A -suffix sorts before the plain version.</summary>
public readonly record struct SemVer(int Major, int Minor, int Patch, string? Pre) : IComparable<SemVer>
{
    public static bool TryParse(string? text, out SemVer v)
    {
        v = default;
        var t = (text ?? "").Trim().TrimStart('v', 'V');
        var plus = t.IndexOf('+');
        if (plus >= 0) t = t[..plus];
        string? pre = null;
        var dash = t.IndexOf('-');
        if (dash >= 0)
        {
            pre = t[(dash + 1)..];
            t = t[..dash];
        }
        var parts = t.Split('.');
        if (parts.Length is < 1 or > 4) return false;
        var nums = new int[3];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n)) return false;
            if (i < 3) nums[i] = n;
        }
        v = new SemVer(nums[0], nums[1], nums[2], string.IsNullOrEmpty(pre) ? null : pre);
        return true;
    }

    public static SemVer Parse(string? text) => TryParse(text, out var v) ? v : default;

    public int CompareTo(SemVer other)
    {
        var c = Major.CompareTo(other.Major);
        if (c == 0) c = Minor.CompareTo(other.Minor);
        if (c == 0) c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;
        if (Pre is null) return other.Pre is null ? 0 : 1;
        return other.Pre is null ? -1 : string.CompareOrdinal(Pre, other.Pre);
    }

    public static bool operator <(SemVer a, SemVer b) => a.CompareTo(b) < 0;
    public static bool operator >(SemVer a, SemVer b) => a.CompareTo(b) > 0;
    public static bool operator <=(SemVer a, SemVer b) => a.CompareTo(b) <= 0;
    public static bool operator >=(SemVer a, SemVer b) => a.CompareTo(b) >= 0;

    public override string ToString() => $"{Major}.{Minor}.{Patch}" + (Pre is null ? "" : "-" + Pre);
}
