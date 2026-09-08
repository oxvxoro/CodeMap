using Microsoft.Data.Sqlite;

namespace CodeMap.Storage;

public sealed record CodeMapIndexStatus(
    string DatabasePath,
    string IndexState,
    DateTimeOffset? LastIndexedAtUtc,
    string? SchemaVersion,
    bool SchemaOutdated,
    IReadOnlyDictionary<string, string> AnalyzerVersions,
    bool AnalyzerVersionsOutdated,
    int Symbols,
    int Edges);

public static class CodeMapIndexStatusReader
{
    public static async Task<CodeMapIndexStatus> ReadAsync(string databasePath, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(databasePath);
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared
        }.ToString());
        await connection.OpenAsync(cancellationToken);

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT key, value FROM metadata";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                metadata[reader.GetString(0)] = reader.GetString(1);
        }
        catch (SqliteException)
        {
            // Indexes created before metadata was introduced remain inspectable.
        }

        var (symbols, edges) = await ReadCountsAsync(connection, cancellationToken);
        var analyzerVersions = metadata
            .Where(item => item.Key.StartsWith("analyzer_version_", StringComparison.Ordinal))
            .ToDictionary(item => item.Key["analyzer_version_".Length..], item => item.Value, StringComparer.Ordinal);
        var indexedAnalyzerLanguages = await ReadIndexedAnalyzerLanguagesAsync(connection, cancellationToken);
        var expectedAnalyzerVersions = indexedAnalyzerLanguages is null
            ? SqliteCodeMapStore.CurrentAnalyzerVersions
            : SqliteCodeMapStore.CurrentAnalyzerVersions
                .Where(expected => indexedAnalyzerLanguages.Contains(expected.Key))
                .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        var analyzerVersionsOutdated = expectedAnalyzerVersions.Any(expected =>
            !analyzerVersions.TryGetValue(expected.Key, out var actual)
            || !string.Equals(actual, expected.Value, StringComparison.Ordinal));
        var schemaVersion = metadata.GetValueOrDefault("schema_version");
        var lastIndexedAtUtc = metadata.TryGetValue("last_indexed_at_utc", out var indexedAt)
            && DateTimeOffset.TryParse(indexedAt, out var parsedIndexedAt)
            ? (DateTimeOffset?)parsedIndexedAt
            : null;

        return new CodeMapIndexStatus(
            fullPath,
            metadata.GetValueOrDefault("index_state")
                ?? (schemaVersion is null ? "not_indexed" : "ready"),
            lastIndexedAtUtc,
            schemaVersion,
            !string.Equals(schemaVersion, SqliteCodeMapStore.SchemaVersion, StringComparison.Ordinal),
            analyzerVersions,
            analyzerVersionsOutdated,
            symbols,
            edges);
    }

    private static async Task<HashSet<string>?> ReadIndexedAnalyzerLanguagesAsync(
        SqliteConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT DISTINCT language FROM files";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var languages = new HashSet<string>(StringComparer.Ordinal);
            while (await reader.ReadAsync(cancellationToken))
            {
                var analyzerLanguage = SqliteCodeMapStore.AnalyzerLanguageForFile(reader.GetString(0));
                if (analyzerLanguage is not null)
                    languages.Add(analyzerLanguage);
            }
            return languages;
        }
        catch (SqliteException)
        {
            // Legacy indexes may not have a files table; compare all known metadata in that case.
            return null;
        }
    }

    private static async Task<(int Symbols, int Edges)> ReadCountsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT (SELECT COUNT(*) FROM symbols), (SELECT COUNT(*) FROM edges)";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
                return (reader.GetInt32(0), reader.GetInt32(1));
        }
        catch (SqliteException)
        {
            // A partially created or legacy index can still report its metadata.
        }

        return (0, 0);
    }
}
