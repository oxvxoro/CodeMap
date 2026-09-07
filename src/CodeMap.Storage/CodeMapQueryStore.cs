using CodeMap.Core.Models;
using Microsoft.Data.Sqlite;

namespace CodeMap.Storage;

public sealed class CodeMapSnapshot
{
    private Dictionary<string, IndexedSymbol>? _byId;

    public required IReadOnlyList<IndexedFile> Files { get; init; }

    public required IReadOnlyList<IndexedSymbol> Symbols { get; init; }

    public required IReadOnlyList<IndexedEdge> Edges { get; init; }

    public IndexedSymbol? FindById(string id) =>
        (_byId ??= Symbols.ToDictionary(s => s.Id, StringComparer.Ordinal)).GetValueOrDefault(id);
}


/// <summary>저장된 시맨틱 그래프를 읽기 전용으로 조회하는 저장소.</summary>
public sealed class CodeMapQueryStore
{
    private readonly SqliteCodeMapStore _store;

    public CodeMapQueryStore(string databasePath) => _store = new SqliteCodeMapStore(databasePath);

    public string DatabasePath => _store.DatabasePath;

    public async Task<SqliteConnection> OpenReadOnlyConnectionAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(DatabasePath))
            throw new FileNotFoundException("No CodeMap index found.", DatabasePath);

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared
        }.ToString());
        try
        {
            await connection.OpenAsync(cancellationToken);
            await EnsureSchemaVersionAsync(connection, cancellationToken);
            await EnsureAnalyzerVersionAsync(connection, cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public async Task<CodeMapSnapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(DatabasePath))
            throw new FileNotFoundException("No CodeMap index found.", DatabasePath);

        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        await EnsureSchemaVersionAsync(connection, cancellationToken);
        await EnsureAnalyzerVersionAsync(connection, cancellationToken);

        var files = new List<IndexedFile>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id, project, relative_path, language FROM files ORDER BY project, relative_path";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                files.Add(new IndexedFile(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        }

        var symbols = new List<IndexedSymbol>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT s.id, s.file_id, f.project, f.relative_path, s.kind, s.name,
                       s.qualified_name, s.signature, s.start_line, s.end_line,
                       s.visibility, s.language
                FROM symbols s JOIN files f ON f.id = s.file_id
                ORDER BY f.project, s.qualified_name, COALESCE(s.signature, ''), s.id
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                symbols.Add(new IndexedSymbol(
                    reader.GetString(0), reader.GetString(2), reader.GetString(1), reader.GetString(3),
                    Enum.Parse<NodeKind>(reader.GetString(4)), reader.GetString(5), reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.IsDBNull(8) ? null : reader.GetInt32(8),
                    reader.IsDBNull(9) ? null : reader.GetInt32(9),
                    reader.IsDBNull(10) ? null : reader.GetString(10), reader.GetString(11)));
            }
        }

        var edges = new List<IndexedEdge>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT source_id, target_id, kind, source_file_id, line, resolution_kind, confidence, start_column, end_line, end_column FROM edges ORDER BY source_id, target_id, kind, line";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                edges.Add(IndexedEdgeReader.ReadWithSpan(reader));
        }

        return new CodeMapSnapshot { Files = files, Symbols = symbols, Edges = edges };
    }

    public async Task<CodeMapSnapshot> LoadProjectScopedAsync(string project, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(DatabasePath))
            throw new FileNotFoundException("No CodeMap index found.", DatabasePath);

        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        await EnsureSchemaVersionAsync(connection, cancellationToken);
        await EnsureAnalyzerVersionAsync(connection, cancellationToken);

        var files = new List<IndexedFile>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id, project, relative_path, language FROM files WHERE project = $project COLLATE NOCASE ORDER BY project, relative_path";
            command.Parameters.AddWithValue("$project", project);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                files.Add(new IndexedFile(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        }

        var symbols = new List<IndexedSymbol>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT s.id, s.file_id, f.project, f.relative_path, s.kind, s.name,
                       s.qualified_name, s.signature, s.start_line, s.end_line,
                       s.visibility, s.language
                FROM symbols s JOIN files f ON f.id = s.file_id
                WHERE f.project = $project COLLATE NOCASE AND s.kind != 'Namespace'
                ORDER BY f.project, s.qualified_name, COALESCE(s.signature, ''), s.id
                """;
            command.Parameters.AddWithValue("$project", project);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                symbols.Add(new IndexedSymbol(
                    reader.GetString(0), reader.GetString(2), reader.GetString(1), reader.GetString(3),
                    Enum.Parse<NodeKind>(reader.GetString(4)), reader.GetString(5), reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.IsDBNull(8) ? null : reader.GetInt32(8),
                    reader.IsDBNull(9) ? null : reader.GetInt32(9),
                    reader.IsDBNull(10) ? null : reader.GetString(10), reader.GetString(11)));
            }
        }

        var edges = new List<IndexedEdge>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT source_id, target_id, kind, source_file_id, line, resolution_kind, confidence, start_column, end_line, end_column FROM edges
                WHERE source_file_id IN (SELECT id FROM files WHERE project = $project COLLATE NOCASE)
                   OR target_id IN (
                       SELECT s.id FROM symbols s JOIN files f ON f.id = s.file_id
                       WHERE f.project = $project COLLATE NOCASE)
                ORDER BY source_id, target_id, kind, line
                """;
            command.Parameters.AddWithValue("$project", project);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                edges.Add(IndexedEdgeReader.ReadWithSpan(reader));
        }

        return new CodeMapSnapshot { Files = files, Symbols = symbols, Edges = edges };
    }

    private static async Task EnsureSchemaVersionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        string? storedVersion;
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM metadata WHERE key = 'schema_version'";
            storedVersion = (await command.ExecuteScalarAsync(cancellationToken)) as string;
        }
        catch (SqliteException)
        {
            storedVersion = null;
        }
        if (!string.Equals(storedVersion, SqliteCodeMapStore.SchemaVersion, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"CodeMap index schema is outdated (found '{storedVersion ?? "none"}', expected '{SqliteCodeMapStore.SchemaVersion}').\nRun: codemap index --force");
    }

    private static async Task EnsureAnalyzerVersionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            var storedVersions = new Dictionary<string, string>(StringComparer.Ordinal);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT key, value FROM metadata WHERE key LIKE 'analyzer_version_%'";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var language = reader.GetString(0)["analyzer_version_".Length..];
                storedVersions[language] = reader.GetString(1);
                if (SqliteCodeMapStore.CurrentAnalyzerVersions.TryGetValue(language, out var expected)
                    && !string.Equals(storedVersions[language], expected, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        $"CodeMap index analyzer ({language}) version is outdated (found '{storedVersions[language]}', expected '{expected}').\nRun: codemap index --force");
            }

            await using var languageCommand = connection.CreateCommand();
            languageCommand.CommandText = "SELECT DISTINCT language FROM files";
            await using var languageReader = await languageCommand.ExecuteReaderAsync(cancellationToken);
            while (await languageReader.ReadAsync(cancellationToken))
            {
                var language = SqliteCodeMapStore.AnalyzerLanguageForFile(languageReader.GetString(0));
                if (language is not null && !storedVersions.ContainsKey(language))
                    throw new InvalidOperationException(
                        $"CodeMap index analyzer ({language}) version metadata is missing.\nRun: codemap index --force");
            }
        }
        catch (SqliteException)
        {
            throw new InvalidOperationException("CodeMap index analyzer version metadata is missing or unreadable.\nRun: codemap index --force");
        }
    }
}
