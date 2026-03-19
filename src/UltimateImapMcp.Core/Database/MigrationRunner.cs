using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace UltimateImapMcp.Core.Database;

/// <summary>
/// Applies numbered SQL migration files embedded as resources.
/// Migration files must be named like "001_initial.sql" and embedded
/// under the prefix "UltimateImapMcp.Core.Database.Migrations.".
/// </summary>
public static class MigrationRunner
{
    private const string MigrationPrefix = "UltimateImapMcp.Core.Database.Migrations.";
    private static readonly Regex VersionPattern = new(@"^(\d+)_", RegexOptions.Compiled);

    public static void Migrate(AppDatabase db)
    {
        var conn = db.GetWriteConnection();

        // Ensure schema_version table exists
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS schema_version (
                    version     INTEGER PRIMARY KEY,
                    applied_at  TEXT NOT NULL DEFAULT (datetime('now'))
                );
                """;
            cmd.ExecuteNonQuery();
        }

        // Get already-applied versions
        var appliedVersions = new HashSet<int>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT version FROM schema_version ORDER BY version;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                appliedVersions.Add(reader.GetInt32(0));
        }

        // Discover migration resources sorted by version
        var assembly = typeof(MigrationRunner).Assembly;
        var migrations = assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(MigrationPrefix, StringComparison.Ordinal)
                           && name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .Select(name =>
            {
                var filename = name[MigrationPrefix.Length..];
                var match = VersionPattern.Match(filename);
                return match.Success
                    ? (version: int.Parse(match.Groups[1].Value), resourceName: name)
                    : (version: -1, resourceName: name);
            })
            .Where(m => m.version > 0)
            .OrderBy(m => m.version)
            .ToList();

        foreach (var (version, resourceName) in migrations)
        {
            if (appliedVersions.Contains(version))
                continue;

            string sql;
            using (var stream = assembly.GetManifestResourceStream(resourceName)!)
            using (var reader = new StreamReader(stream))
                sql = reader.ReadToEnd();

            using var transaction = conn.BeginTransaction();
            try
            {
                // Execute migration SQL
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = transaction;
                    cmd.CommandText = sql;
                    cmd.ExecuteNonQuery();
                }

                // Record the applied version
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = transaction;
                    cmd.CommandText = "INSERT INTO schema_version (version) VALUES (@v);";
                    cmd.Parameters.AddWithValue("@v", version);
                    cmd.ExecuteNonQuery();
                }

                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }
    }
}
