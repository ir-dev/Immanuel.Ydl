using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ark.Ydl.Releases;

namespace Ark.Ydl.Tests;

public class ReleaseTests(AppFactory factory) : IClassFixture<AppFactory>
{
    internal static string Root
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ark-ydl.sln"))) dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("ark-ydl.sln not found above the test output folder.");
        }
    }

    private static string Src(params string[] parts) => Path.Combine([Root, "src", "Ark.Ydl", .. parts]);

    private static string CsprojVersion() =>
        Regex.Match(File.ReadAllText(Src("Ark.Ydl.csproj")), "<Version>([^<]+)</Version>").Groups[1].Value;

    /// <summary>The one release version, in every place it is written. Cutting a release that misses one fails here.</summary>
    [Fact]
    public void VersionIsTheSameEverywhere()
    {
        var v = CsprojVersion();
        Assert.Matches(@"^\d+\.\d+\.\d+$", v);

        var asm = typeof(ReleaseCatalog).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        Assert.StartsWith(v, asm);

        var manifest = ReleaseCatalog.LoadEmbedded();
        Assert.Equal(v, manifest.Current);
        Assert.Equal(v, manifest.Releases.OrderByDescending(r => SemVer.Parse(r.Version)).First().Version);

        using var web = JsonDocument.Parse(File.ReadAllText(Src("wwwroot", "manifest.webmanifest")));
        Assert.Equal(v, web.RootElement.GetProperty("version").GetString());

        Assert.Contains($"CLIENT_VERSION = '{v}'", File.ReadAllText(Src("wwwroot", "js", "version.js")));
        Assert.Contains($"const CACHE = 'ark-ydl-shell-{v}'", File.ReadAllText(Src("wwwroot", "sw.js")));
    }

    [Fact]
    public void ManifestIsWellFormed()
    {
        var m = ReleaseCatalog.LoadEmbedded();
        string[] levels = [Compat.Compatible, Compat.Config, Compat.Breaking];
        Assert.NotEmpty(m.Releases);
        Assert.Equal(m.Releases.Count, m.Releases.Select(r => r.Version).Distinct().Count());
        foreach (var r in m.Releases)
        {
            Assert.True(SemVer.TryParse(r.Version, out _), r.Version);
            Assert.False(string.IsNullOrWhiteSpace(r.Title), r.Version);
            Assert.True(DateOnly.TryParse(r.Date, out _), $"{r.Version}: date '{r.Date}'");
            Assert.Contains(r.Compatibility.Level, levels);
            foreach (var (area, value) in r.Compatibility.Areas()) Assert.Contains(value.Level, levels);
            // The overall level is the worst of its areas.
            Assert.Equal(Compat.Worst(r.Compatibility.Areas().Select(a => a.Value.Level)), r.Compatibility.Level);
            Assert.All(r.Migration, s => Assert.Contains(s.Audience, new[] { "operator", "user", "api" }));
            Assert.All(r.Migration, s => Assert.False(string.IsNullOrWhiteSpace(s.Title)));
        }
        foreach (var l in m.Legacy)
        {
            Assert.StartsWith("legacy-", l.Id);
            Assert.Contains(m.Releases, r => r.Version == l.UpgradesTo);
        }
        // Newest first, dates never going forward in time down the list.
        var dates = m.Releases.Select(r => DateOnly.Parse(r.Date!)).ToList();
        Assert.Equal(dates.OrderByDescending(d => d), dates);
    }

    [Theory]
    [InlineData("1.0.0", "1.0.1", -1)]
    [InlineData("1.10.0", "1.9.9", 1)]
    [InlineData("v2.0.0", "2.0.0", 0)]
    [InlineData("1.1.0-beta", "1.1.0", -1)]
    [InlineData("1.1.0+abc", "1.1.0", 0)]
    [InlineData("1.1", "1.1.0", 0)]
    public void SemVerOrders(string a, string b, int sign) =>
        Assert.Equal(sign, Math.Sign(SemVer.Parse(a).CompareTo(SemVer.Parse(b))));

    [Fact]
    public void InformationalVersionSplitsOffTheRevision()
    {
        Assert.Equal(("1.1.0", "abcdef1"), ReleaseCatalog.SplitInformational("1.1.0+abcdef1234567890"));
        Assert.Equal(("1.1.0", null), ReleaseCatalog.SplitInformational("1.1.0"));
    }

    private static ReleaseCatalog Catalog(string running = "1.1.0") => new(
        new ReleaseManifest
        {
            Current = running,
            Releases =
            [
                Rel("1.0.0", Compat.Breaking, required: true),
                Rel("1.1.0", Compat.Config, required: true),
                Rel("1.2.0", Compat.Compatible, required: false),
            ],
            Legacy = [new LegacyEntry { Id = "legacy-2017", Name = "Old app", UpgradesTo = "1.0.0" }],
        }, running, new FakeAssembly(running));

    private static ReleaseEntry Rel(string v, string level, bool required) => new()
    {
        Version = v, Date = "2026-11-01", Title = "Release " + v,
        Compatibility = new ReleaseCompatibility { Level = level, Config = new CompatArea { Level = level, Notes = "notes " + v } },
        Migration = [new MigrationStep { Title = "step " + v, Required = required }],
    };

    private sealed class FakeAssembly(string version) : Assembly
    {
        public override object[] GetCustomAttributes(Type attributeType, bool inherit) =>
            attributeType == typeof(AssemblyInformationalVersionAttribute)
                ? new Attribute[] { new AssemblyInformationalVersionAttribute(version + "+0123456789") }
                : Array.Empty<Attribute>();
        public override object[] GetCustomAttributes(bool inherit) => GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), inherit);
        public override string Location => "";
        public override AssemblyName GetName() => new("Fake") { Version = new Version(version) };
    }

    [Fact]
    public void PlansChainEveryStepAfterTheStartingVersion()
    {
        var c = Catalog("1.2.0");
        Assert.Equal("0123456", c.Revision);

        var p = c.PlanFrom("1.0.0");
        Assert.Equal("upgrade", p.Status);
        Assert.Equal(new[] { "1.1.0", "1.2.0" }, p.Path);
        Assert.Equal(Compat.Config, p.Level); // worst of 1.1.0 (config) and 1.2.0 (compatible)
        Assert.Equal(new[] { "1.1.0", "1.2.0" }, p.Steps.Select(s => s.Version));
        Assert.Equal(1, p.RequiredSteps);
        Assert.Contains("1.1.0: notes 1.1.0", p.Areas.Single(a => a.Area == "config").Notes);

        var legacy = c.PlanFrom("legacy-2017");
        Assert.Equal("legacy", legacy.Status);
        Assert.Equal(new[] { "1.0.0", "1.1.0", "1.2.0" }, legacy.Path);
        Assert.Equal(Compat.Breaking, legacy.Level);
        Assert.Equal("Old app", legacy.FromName);

        Assert.Equal("upToDate", c.PlanFrom("v1.2.0").Status);
        Assert.Equal("newer", c.PlanFrom("2.0.0").Status);
        Assert.Equal("unknown", c.PlanFrom("banana").Status);
        Assert.Equal("unknown", c.PlanFrom("").Status);

        var matrix = c.Matrix();
        Assert.Equal(new[] { "1.1.0", "1.0.0", "legacy-2017" }, matrix.Select(m => m.From));
        Assert.Equal(Compat.Compatible, matrix[0].Level);
    }

    [Fact]
    public void TheRealManifestPlansFromEveryEarlierVersion()
    {
        var real = ReleaseCatalog.LoadEmbedded();
        var c = new ReleaseCatalog(real, real.Current, typeof(ReleaseCatalog).Assembly);
        foreach (var row in c.Matrix())
        {
            var plan = c.PlanFrom(row.From);
            Assert.Contains(plan.Status, new[] { "upgrade", "legacy" });
            Assert.NotEmpty(plan.Steps);
            Assert.Equal(c.AppVersion, plan.Path[^1]);
        }
    }

    [Fact]
    public async Task VersionEndpointShowsAppManifestAndLatest()
    {
        var v = CsprojVersion();
        var res = await factory.CreateClient().GetAsync("/api/version");
        res.EnsureSuccessStatusCode();
        Assert.Equal("no-cache", res.Headers.CacheControl?.ToString());
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(v, body.GetProperty("version").GetString());
        Assert.Equal(v, body.GetProperty("manifest").GetString());
        Assert.True(body.GetProperty("manifestMatches").GetBoolean());
        Assert.Equal(v, body.GetProperty("latest").GetProperty("version").GetString());
        Assert.Equal("local", body.GetProperty("latest").GetProperty("source").GetString()); // the feed is off in tests
        Assert.False(body.GetProperty("updateAvailable").GetBoolean());

        var sys = await factory.CreateClient().GetFromJsonAsync<JsonElement>("/api/system");
        Assert.Equal(v, sys.GetProperty("version").GetString());
        Assert.Equal(v, sys.GetProperty("release").GetProperty("version").GetString());
        Assert.True(sys.GetProperty("audit").GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task ReleasesAndMigrationEndpoints()
    {
        var client = factory.CreateClient();
        var all = await client.GetFromJsonAsync<JsonElement>("/api/releases");
        Assert.Equal(CsprojVersion(), all.GetProperty("current").GetString());
        Assert.True(all.GetProperty("releases").GetArrayLength() >= 2);
        Assert.True(all.GetProperty("matrix").GetArrayLength() >= 1);
        Assert.Equal("legacy-2020", all.GetProperty("legacy")[0].GetProperty("id").GetString());

        var plan = await client.GetFromJsonAsync<JsonElement>("/api/releases/migration?from=1.0.0");
        Assert.Equal("upgrade", plan.GetProperty("status").GetString());
        Assert.True(plan.GetProperty("steps").GetArrayLength() > 0);
        Assert.True(plan.GetProperty("requiredSteps").GetInt32() >= 1);

        var bad = await client.GetAsync("/api/releases/migration?from=banana");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task PageCarriesTheVersionChipAndReleaseDialog()
    {
        var page = await factory.CreateClient().GetStringAsync("/");
        Assert.Contains("id=\"btn-version\"", page);
        Assert.Contains("id=\"release-dlg\"", page);
        var js = await factory.CreateClient().GetAsync("/js/release.js");
        js.EnsureSuccessStatusCode();
    }
}
