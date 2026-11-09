using System.Collections.Concurrent;
using Ark.Rapid;
using Ark.Ydl.Options;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Ark.Ydl.Audit;

/// <summary>
/// "ArkDatabase:Databases" — every database this app talks to, by name (Ark.Rapid.Database IMPLEMENTATION.md §1).
/// Only "audit" exists today. Its default is an SQLite file under {DataRoot}/audit.
/// </summary>
public sealed class ArkDatabaseOptions
{
    public const string SectionName = "ArkDatabase";
    public const string Audit = "audit";

    public Dictionary<string, ArkDatabaseConnectionOptions> Databases { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ArkDatabaseConnectionOptions
{
    /// <summary>Sqlite | Maria | Postgres. The audit log is written and tested against Sqlite.</summary>
    public string Provider { get; set; } = "Sqlite";

    /// <summary>Empty = Data Source={DataRoot}/audit/ark-ydl-audit.db.</summary>
    public string? ConnectionString { get; set; }
}

public static class ArkDatabaseServiceCollectionExtensions
{
    public const string DefaultAuditFile = "ark-ydl-audit.db";

    /// <summary>
    /// A keyed factory, <c>Func&lt;string, ArkDbManager&gt;</c>, cached per name (IMPLEMENTATION.md §2).
    /// Managers only wrap a connection string, so holding one per name is cheap.
    /// </summary>
    public static IServiceCollection AddArkRapidDatabase(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ArkDatabaseOptions>(configuration.GetSection(ArkDatabaseOptions.SectionName));
        services.AddSingleton<Func<string, ArkDbManager>>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<ArkDatabaseOptions>>().Value;
            var paths = sp.GetRequiredService<YdlPaths>();
            var cache = new ConcurrentDictionary<string, ArkDbManager>(StringComparer.OrdinalIgnoreCase);
            return name => cache.GetOrAdd(name, key =>
            {
                if (!options.Databases.TryGetValue(key, out var db))
                {
                    if (!key.Equals(ArkDatabaseOptions.Audit, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException($"No ArkDatabase:Databases entry named '{key}' in configuration.");
                    db = new ArkDatabaseConnectionOptions();
                }
                var provider = (db.Provider ?? "Sqlite").Trim().ToLowerInvariant();
                var cs = db.ConnectionString;
                if (provider == "sqlite")
                    cs = SqliteConnectionString(string.IsNullOrWhiteSpace(cs) ? null : cs, paths);
                else if (string.IsNullOrWhiteSpace(cs))
                    throw new InvalidOperationException($"ArkDatabase:Databases:{key} needs a ConnectionString for provider '{db.Provider}'.");
                return provider switch
                {
                    "sqlite" => new SqliteManager(cs),
                    "maria" => new MariaManager(cs!),
                    "postgres" => new PostgresManager(cs!),
                    _ => throw new NotSupportedException($"Unknown ArkDatabase provider '{db.Provider}' for database '{key}'."),
                };
            });
        });
        return services;
    }

    /// <summary>
    /// The audit file's connection string, with its folder created. Pooling is off so that a failed batch
    /// can never hand a connection with an open transaction back to a pool; Default Timeout makes a
    /// writer wait for a reader (and the other way round) instead of failing with "database is locked".
    /// </summary>
    internal static string SqliteConnectionString(string? configured, YdlPaths paths)
    {
        var b = configured is null
            ? new SqliteConnectionStringBuilder { DataSource = Path.Combine(paths.Audit, DefaultAuditFile) }
            : new SqliteConnectionStringBuilder(configured);
        if (!string.IsNullOrEmpty(b.DataSource) && b.DataSource != ":memory:" && b.Mode != SqliteOpenMode.Memory)
        {
            var full = Path.GetFullPath(Path.IsPathRooted(b.DataSource) ? b.DataSource : Path.Combine(paths.DataRoot, b.DataSource));
            b.DataSource = full;
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        }
        if (configured is null || !configured.Contains("Pooling", StringComparison.OrdinalIgnoreCase)) b.Pooling = false;
        if (configured is null || !configured.Contains("Timeout", StringComparison.OrdinalIgnoreCase)) b.DefaultTimeout = 30;
        return b.ToString();
    }

    /// <summary>The file behind an SQLite manager, for the status page. Null for other providers.</summary>
    internal static string? SqliteFile(IOptions<ArkDatabaseOptions> options, YdlPaths paths)
    {
        options.Value.Databases.TryGetValue(ArkDatabaseOptions.Audit, out var db);
        if (db is not null && !string.Equals(db.Provider, "sqlite", StringComparison.OrdinalIgnoreCase)) return null;
        var b = new SqliteConnectionStringBuilder(SqliteConnectionString(string.IsNullOrWhiteSpace(db?.ConnectionString) ? null : db.ConnectionString, paths));
        return b.DataSource;
    }
}
