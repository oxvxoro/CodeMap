using System.Security.Cryptography;
using System.Reflection;
using System.Text;
using CodeMap.CSharp;
using CodeMap.Core;
using CodeMap.Core.Models;
using Microsoft.Data.Sqlite;
using CodeMap.Web;

namespace CodeMap.Storage;

public sealed record StoredFile(
    string Id,
    string Project,
    string RelativePath,
    string ContentHash,
    DateTimeOffset IndexedAt);

public sealed record IndexSummary(
    int IndexedFiles,
    int Added,
    int Updated,
    int Removed,
    int Skipped,
    int Symbols,
    int Edges,
    TimeSpan Elapsed,
    IReadOnlyList<string> AnalyzedProjects = null!)
{
    public IReadOnlyList<string> AnalyzedProjects { get; init; } = AnalyzedProjects ?? Array.Empty<string>();

    public override string ToString() =>
        $"Indexed {IndexedFiles} files\nAdded: {Added}\nUpdated: {Updated}\nRemoved: {Removed}\nSkipped: {Skipped}\nSymbols: {Symbols}\nEdges: {Edges}\nElapsed: {Elapsed.TotalMilliseconds:0} ms";
}


public sealed class SqliteCodeMapStore
{











    public const string SchemaVersion = "4";

    internal static IReadOnlyDictionary<string, string> CurrentAnalyzerVersions { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["csharp"] = CSharpLanguageAnalyzer.AnalyzerVersion,
            ["web"] = WebLanguageAnalyzer.AnalyzerVersion
        };

    internal static string ConfigHash => ComputeContentHash("config:v1",
        string.Equals(Environment.GetEnvironmentVariable("CODEMAP_WEB_BINDINGS"), "0", StringComparison.Ordinal) ? "webBindings=disabled" : "webBindings=enabled");

    internal static string ToolVersion =>
        typeof(IncrementalCodeMapIndexer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(IncrementalCodeMapIndexer).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    internal static string? AnalyzerLanguageForFile(string language) => language switch
    {
        "csharp" or "razor" or "xaml" => "csharp",
        "html" or "css" or "javascript" or "typescript" => "web",
        _ => null
    };

    public SqliteCodeMapStore(string databasePath)
    {
        DatabasePath = Path.GetFullPath(databasePath);
    }

    public string DatabasePath { get; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        await using var connection = await OpenAsync(cancellationToken);
        await ExecuteAsync(connection, null, Schema, cancellationToken);
        await EnsureContainsSearchIndexPopulatedAsync(connection, cancellationToken);
    }














    private static async Task EnsureContainsSearchIndexPopulatedAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var checkCommand = connection.CreateCommand();
        checkCommand.CommandText = "SELECT 1 FROM metadata WHERE key = 'fts_rebuilt_v1'";
        if (await checkCommand.ExecuteScalarAsync(cancellationToken) is not null)
            return;

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await ExecuteAsync(connection, transaction, "INSERT INTO symbols_fts(symbols_fts) VALUES('rebuild')", cancellationToken);
        await SetMetadataAsync(connection, transaction, "fts_rebuilt_v1", "1", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    internal async Task<IReadOnlyList<StoredFile>> GetFilesAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, project, relative_path, content_hash, indexed_at FROM files";
        var result = new List<StoredFile>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new StoredFile(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), DateTimeOffset.Parse(reader.GetString(4))));
        return result;
    }

    internal async Task ReplaceProjectsAsync(
        IReadOnlyCollection<AnalyzedProject> projects,
        IReadOnlyCollection<string> removedProjects,
        CancellationToken cancellationToken)
    {
        SqliteConnection.ClearAllPools();
        var isNewIndex = !File.Exists(DatabasePath);
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);

        if (isNewIndex)
        {
            await using var buildingTransaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await SetMetadataAsync(connection, buildingTransaction, "index_state", "building", cancellationToken);
            await buildingTransaction.CommitAsync(cancellationToken);
        }






        var touchedProjects = new HashSet<string>(projects.Select(p => p.ProjectName), StringComparer.Ordinal);
        touchedProjects.UnionWith(removedProjects);
        var declaredIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var project in projects)
            foreach (var node in project.Result.Nodes)
                if (node.Kind != NodeKind.File)
                    declaredIds.Add(node.Id);






        var requiredOriginalIds = new HashSet<string>(declaredIds, StringComparer.Ordinal);
        foreach (var project in projects)
            foreach (var edge in project.Result.Edges)
            {
                requiredOriginalIds.Add(edge.SourceId);
                requiredOriginalIds.Add(edge.TargetId);
            }











        var existingOwners = new Dictionary<string, SymbolOwner>(StringComparer.Ordinal);
        var candidatesByOriginalId = new Dictionary<string, List<(string Project, string StoredId)>>(StringComparer.Ordinal);
        var allStoredSymbolIds = new HashSet<string>(StringComparer.Ordinal);
        await LoadUntouchedSymbolUniverseAsync(connection, touchedProjects, requiredOriginalIds, existingOwners, candidatesByOriginalId, allStoredSymbolIds, cancellationToken);
        declaredIds.UnionWith(existingOwners.Keys);

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        foreach (var projectName in removedProjects.Concat(projects.Select(p => p.ProjectName)).Distinct(StringComparer.Ordinal))
            await DeleteProjectAsync(connection, transaction, projectName, cancellationToken);



        using var fileCommand = CreateFileCommand(connection, transaction);
        using var symbolCommand = CreateSymbolCommand(connection, transaction);
        using var symbolLocationCommand = CreateSymbolLocationCommand(connection, transaction);
        using var collisionCommand = CreateCollisionCommand(connection, transaction);
        using var edgeCommand = CreateEdgeCommand(connection, transaction);
        var symbolsWrittenInBatch = new Dictionary<string, SymbolOwner>(StringComparer.Ordinal);
        var collisionSuffixes = new Dictionary<string, int>(StringComparer.Ordinal);

        var projectContexts = new List<ProjectPersistContext>();

        foreach (var project in projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileNodeIdsByPath = project.Result.Nodes
                .Where(n => n.Kind == NodeKind.File && n.FilePath is not null)
                .ToDictionary(n => n.FilePath!, n => n.Id, StringComparer.OrdinalIgnoreCase);
            var fileIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in project.Files)
            {
                var id = fileNodeIdsByPath.GetValueOrDefault(file.RelativePath) ?? $"file://{project.ProjectName}/{file.RelativePath}";
                fileIds[file.RelativePath] = id;
                await ExecuteFileInsertAsync(fileCommand, project, file, id, cancellationToken);
            }

            var symbolNodes = project.Result.Nodes.Where(n => n.Kind != NodeKind.File && n.FilePath is not null).ToArray();
            var storedSymbolIds = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var node in symbolNodes)
            {
                if (!fileIds.TryGetValue(node.FilePath!, out var fileId))
                    continue;
                var owner = new SymbolOwner(project.ProjectName, fileId, node.SourceLocation?.StartLine);
                var symbolId = node.Id;
                var collisionOwner = existingOwners.GetValueOrDefault(node.Id);
                if (collisionOwner is null)
                    symbolsWrittenInBatch.TryGetValue(node.Id, out collisionOwner);
                if (collisionOwner is not null && !SameLocation(collisionOwner, owner))
                {
                    var suffix = await FindUnusedSuffixAsync(connection, transaction, node.Id,
                        collisionSuffixes.GetValueOrDefault(node.Id, 2), cancellationToken);
                    collisionSuffixes[node.Id] = suffix + 1;
                    symbolId = $"{node.Id}#{suffix}";
                    await ExecuteCollisionInsertAsync(collisionCommand, node.Id, collisionOwner.Project,
                        project.ProjectName, cancellationToken);
                }
                await ExecuteSymbolInsertAsync(symbolCommand, node, fileId, symbolId, project.ProjectName, cancellationToken);
                storedSymbolIds[node.Id] = symbolId;
                AddCandidate(candidatesByOriginalId, node.Id, project.ProjectName, symbolId);
                allStoredSymbolIds.Add(symbolId);
                symbolsWrittenInBatch.TryAdd(node.Id, owner);
                if (node.SourceLocation is not null)
                    await ExecuteSymbolLocationInsertAsync(symbolLocationCommand, symbolId, fileId, node.SourceLocation, true, cancellationToken);
                foreach (var location in node.AdditionalLocations)
                {
                    if (fileIds.TryGetValue(location.FilePath, out var additionalFileId))
                        await ExecuteSymbolLocationInsertAsync(symbolLocationCommand, symbolId, additionalFileId, location.Location, false, cancellationToken);
                }
            }

            projectContexts.Add(new ProjectPersistContext(project, fileIds, storedSymbolIds));
        }

        foreach (var context in projectContexts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var nodeById = context.Project.Result.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
            foreach (var edge in context.Project.Result.Edges)
            {
                if (!nodeById.TryGetValue(edge.SourceId, out var source) || source.FilePath is null
                    || !context.FileIds.TryGetValue(source.FilePath, out var sourceFileId))
                    continue;
                if (!declaredIds.Contains(edge.TargetId))
                    continue;
                var storedSourceId = ResolveStoredSymbolId(
                    edge.SourceId, null, context.StoredSymbolIds, candidatesByOriginalId);
                var storedTargetId = ResolveStoredSymbolId(
                    edge.TargetId, edge.TargetProject, context.StoredSymbolIds, candidatesByOriginalId);



                if (storedSourceId is null || storedTargetId is null || !allStoredSymbolIds.Contains(storedTargetId))
                    continue;
                await ExecuteEdgeInsertAsync(edgeCommand, edge, sourceFileId, storedSourceId, storedTargetId, cancellationToken);
            }
        }
        await SweepOrphanEdgesAsync(connection, transaction, touchedProjects, cancellationToken);
        await SetMetadataAsync(connection, transaction, "schema_version", SchemaVersion, cancellationToken);
        foreach (var language in projects.SelectMany(project => project.Files.Select(file => AnalyzerLanguageForFile(file.Language)))
            .OfType<string>().Distinct(StringComparer.Ordinal))
            if (CurrentAnalyzerVersions.TryGetValue(language, out var version))
                await SetMetadataAsync(connection, transaction, $"analyzer_version_{language}", version, cancellationToken);
        await SetMetadataAsync(connection, transaction, "last_indexed_at_utc", DateTimeOffset.UtcNow.ToString("O"), cancellationToken);
        await SetMetadataAsync(connection, transaction, "index_state", "ready", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }








    private const int SubsetScanIdThreshold = 400;




















    private static async Task LoadUntouchedSymbolUniverseAsync(
        SqliteConnection connection,
        IReadOnlySet<string> excludedProjects,
        IReadOnlySet<string> requiredOriginalIds,
        Dictionary<string, SymbolOwner> existingOwners,
        Dictionary<string, List<(string Project, string StoredId)>> candidatesByOriginalId,
        HashSet<string> allStoredSymbolIds,
        CancellationToken cancellationToken)
    {
        if (requiredOriginalIds.Count > SubsetScanIdThreshold)
        {
            await LoadUntouchedSymbolUniverseFullScanAsync(
                connection, excludedProjects, existingOwners, candidatesByOriginalId, allStoredSymbolIds, cancellationToken);
            return;
        }

        var requiredIdList = requiredOriginalIds.ToArray();
        for (var offset = 0; offset < requiredIdList.Length; offset += SubsetScanIdThreshold)
        {
            var chunk = requiredIdList.Skip(offset).Take(SubsetScanIdThreshold).ToArray();
            await using var command = connection.CreateCommand();
            var whereClauses = new List<string>(chunk.Length * 2);
            for (var i = 0; i < chunk.Length; i++)
            {
                whereClauses.Add($"s.id = $eq{i}");
                whereClauses.Add($"s.id LIKE $like{i} ESCAPE '\\'");
                command.Parameters.AddWithValue($"$eq{i}", chunk[i]);
                command.Parameters.AddWithValue($"$like{i}", EscapeLikePattern(chunk[i]) + "#%");
            }
            command.CommandText = "SELECT s.id, f.project, s.file_id, s.start_line FROM symbols s JOIN files f ON f.id = s.file_id " +
                $"WHERE {string.Join(" OR ", whereClauses)}";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var storedId = reader.GetString(0);
                var project = reader.GetString(1);
                if (excludedProjects.Contains(project))
                    continue;
                var originalId = GetOriginalSymbolId(storedId);
                if (!requiredOriginalIds.Contains(originalId))
                    continue;
                existingOwners[originalId] = new SymbolOwner(project, reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetInt32(3));
                AddCandidate(candidatesByOriginalId, originalId, project, storedId);
                allStoredSymbolIds.Add(storedId);
            }
        }
    }

    private static async Task LoadUntouchedSymbolUniverseFullScanAsync(
        SqliteConnection connection,
        IReadOnlySet<string> excludedProjects,
        Dictionary<string, SymbolOwner> existingOwners,
        Dictionary<string, List<(string Project, string StoredId)>> candidatesByOriginalId,
        HashSet<string> allStoredSymbolIds,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT s.id, f.project, s.file_id, s.start_line FROM symbols s JOIN files f ON f.id = s.file_id";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var storedId = reader.GetString(0);
            var project = reader.GetString(1);
            if (excludedProjects.Contains(project))
                continue;
            var originalId = GetOriginalSymbolId(storedId);
            existingOwners[originalId] = new SymbolOwner(project, reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetInt32(3));
            AddCandidate(candidatesByOriginalId, originalId, project, storedId);
            allStoredSymbolIds.Add(storedId);
        }
    }


    private static string EscapeLikePattern(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");






    private static void AddCandidate(
        Dictionary<string, List<(string Project, string StoredId)>> candidatesByOriginalId,
        string originalId,
        string project,
        string storedId)
    {
        if (!candidatesByOriginalId.TryGetValue(originalId, out var candidates))
            candidatesByOriginalId[originalId] = candidates = [];
        candidates.RemoveAll(candidate => string.Equals(candidate.Project, project, StringComparison.Ordinal));
        candidates.Add((project, storedId));
    }

    private sealed record ProjectPersistContext(
        AnalyzedProject Project,
        Dictionary<string, string> FileIds,
        Dictionary<string, string> StoredSymbolIds);

    private sealed record SymbolOwner(string Project, string FileId, int? StartLine);

    internal static string GetOriginalSymbolId(string storedId)
    {
        var hashIndex = storedId.LastIndexOf('#');
        if (hashIndex < 0)
            return storedId;
        var suffix = storedId[(hashIndex + 1)..];
        if (suffix.Length > 0 && suffix.All(char.IsDigit))
            return storedId[..hashIndex];
        return storedId;
    }














    private static string? ResolveStoredSymbolId(
        string originalId,
        string? targetProjectHint,
        Dictionary<string, string> projectStoredSymbolIds,
        Dictionary<string, List<(string Project, string StoredId)>> candidatesByOriginalId)
    {
        if (projectStoredSymbolIds.TryGetValue(originalId, out var localStoredId))
            return localStoredId;

        if (!candidatesByOriginalId.TryGetValue(originalId, out var matches) || matches.Count == 0)
            return originalId;
        if (matches.Count == 1)
            return matches[0].StoredId;

        if (targetProjectHint is not null)
        {
            var hinted = matches.Where(match => string.Equals(match.Project, targetProjectHint, StringComparison.Ordinal)).ToArray();
            if (hinted.Length == 1)
                return hinted[0].StoredId;
        }

        return null;
    }

    private static bool SameLocation(SymbolOwner first, SymbolOwner second) =>
        first.FileId == second.FileId && first.StartLine == second.StartLine;

    private static async Task<int> FindUnusedSuffixAsync(SqliteConnection connection, SqliteTransaction transaction,
        string originalId, int startingSuffix, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM symbols WHERE id = $id LIMIT 1";
        var parameter = command.Parameters.Add("$id", SqliteType.Text);
        for (var suffix = Math.Max(2, startingSuffix); ; suffix++)
        {
            parameter.Value = $"{originalId}#{suffix}";
            if (await command.ExecuteScalarAsync(cancellationToken) is null)
                return suffix;
        }
    }

    private static async Task SweepOrphanEdgesAsync(SqliteConnection connection, SqliteTransaction transaction,
        IReadOnlySet<string> touchedProjects, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var projectParameters = touchedProjects.Select((_, index) => $"$touched{index}").ToArray();
        command.CommandText = $"DELETE FROM edges WHERE target_id NOT IN (SELECT id FROM symbols) " +
            $"AND source_file_id IN (SELECT id FROM files WHERE project NOT IN ({string.Join(",", projectParameters)}))";
        var index = 0;
        foreach (var project in touchedProjects)
            command.Parameters.AddWithValue(projectParameters[index++], project);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<(int Symbols, int Edges)> GetCountsAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        return (await ScalarAsync(connection, "SELECT COUNT(*) FROM symbols", cancellationToken), await ScalarAsync(connection, "SELECT COUNT(*) FROM edges", cancellationToken));
    }

    private async Task DeleteProjectAsync(SqliteConnection connection, SqliteTransaction transaction, string projectName, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM edges WHERE source_file_id IN (SELECT id FROM files WHERE project = $project); DELETE FROM symbols WHERE file_id IN (SELECT id FROM files WHERE project = $project); DELETE FROM files WHERE project = $project;";
        command.Parameters.AddWithValue("$project", projectName);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static SqliteCommand CreateFileCommand(SqliteConnection connection, SqliteTransaction transaction)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO files(id, project, relative_path, language, content_hash, mtime_utc, size, indexed_at) VALUES($id,$project,$path,$language,$hash,$mtime,$size,$indexed)";
        foreach (var name in new[] { "$id", "$project", "$path", "$language", "$hash", "$mtime", "$size", "$indexed" })
            command.Parameters.Add(new SqliteParameter { ParameterName = name });
        command.Prepare();
        return command;
    }

    private static async Task ExecuteFileInsertAsync(SqliteCommand command, AnalyzedProject project, AnalyzedSourceFile file, string id, CancellationToken cancellationToken)
    {
        var info = new FileInfo(file.FilePath);
        command.Parameters["$id"].Value = id;
        command.Parameters["$project"].Value = project.ProjectName;
        command.Parameters["$path"].Value = file.RelativePath;
        command.Parameters["$language"].Value = file.Language;
        command.Parameters["$hash"].Value = ComputeContentHash(file.RelativePath, file.Content);
        command.Parameters["$mtime"].Value = info.Exists ? info.LastWriteTimeUtc.ToString("O") : DateTime.UtcNow.ToString("O");
        command.Parameters["$size"].Value = info.Exists ? info.Length : Encoding.UTF8.GetByteCount(file.Content);
        command.Parameters["$indexed"].Value = DateTimeOffset.UtcNow.ToString("O");
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static SqliteCommand CreateSymbolCommand(SqliteConnection connection, SqliteTransaction transaction)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT OR REPLACE INTO symbols(id,file_id,kind,name,qualified_name,signature,start_line,end_line,visibility,language,origin_kind) VALUES($id,$file,$kind,$name,$qualified,$signature,$start,$end,$visibility,$language,$origin)";
        foreach (var name in new[] { "$id", "$file", "$kind", "$name", "$qualified", "$signature", "$start", "$end", "$visibility", "$language", "$origin" })
            command.Parameters.Add(new SqliteParameter { ParameterName = name });
        command.Prepare();
        return command;
    }

    private static async Task ExecuteSymbolInsertAsync(SqliteCommand command, CodeNode node, string fileId, string symbolId, string projectName, CancellationToken cancellationToken)
    {
        command.Parameters["$id"].Value = symbolId;
        command.Parameters["$file"].Value = fileId;
        command.Parameters["$kind"].Value = node.Kind.ToString();
        command.Parameters["$name"].Value = node.Name;
        command.Parameters["$qualified"].Value = node.QualifiedName;
        command.Parameters["$signature"].Value = (object?)node.Signature ?? DBNull.Value;
        command.Parameters["$start"].Value = (object?)node.SourceLocation?.StartLine ?? DBNull.Value;
        command.Parameters["$end"].Value = (object?)node.SourceLocation?.EndLine ?? DBNull.Value;
        command.Parameters["$visibility"].Value = (object?)node.Visibility ?? DBNull.Value;
        command.Parameters["$language"].Value = node.Language;
        command.Parameters["$origin"].Value = IsExternalProject(projectName) ? "decompiled" : "declared";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }







    internal static bool IsExternalProject(string projectName) =>
        projectName.StartsWith("external:", StringComparison.Ordinal);

    private static SqliteCommand CreateCollisionCommand(SqliteConnection connection, SqliteTransaction transaction)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT OR IGNORE INTO symbol_collisions(id,project_a,project_b,detected_at) VALUES($id,$projectA,$projectB,$detectedAt)";
        foreach (var name in new[] { "$id", "$projectA", "$projectB", "$detectedAt" })
            command.Parameters.Add(new SqliteParameter { ParameterName = name });
        command.Prepare();
        return command;
    }

    private static SqliteCommand CreateSymbolLocationCommand(SqliteConnection connection, SqliteTransaction transaction)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT OR IGNORE INTO symbol_locations(symbol_id,file_id,start_line,end_line,is_primary) VALUES($symbol,$file,$start,$end,$primary)";
        foreach (var name in new[] { "$symbol", "$file", "$start", "$end", "$primary" })
            command.Parameters.Add(new SqliteParameter { ParameterName = name });
        command.Prepare();
        return command;
    }

    private static async Task ExecuteSymbolLocationInsertAsync(SqliteCommand command, string symbolId, string fileId,
        SourceLocation location, bool isPrimary, CancellationToken cancellationToken)
    {
        command.Parameters["$symbol"].Value = symbolId;
        command.Parameters["$file"].Value = fileId;
        command.Parameters["$start"].Value = location.StartLine;
        command.Parameters["$end"].Value = location.EndLine;
        command.Parameters["$primary"].Value = isPrimary ? 1 : 0;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ExecuteCollisionInsertAsync(SqliteCommand command, string id, string projectA, string projectB, CancellationToken cancellationToken)
    {
        command.Parameters["$id"].Value = id;
        command.Parameters["$projectA"].Value = projectA;
        command.Parameters["$projectB"].Value = projectB;
        command.Parameters["$detectedAt"].Value = DateTimeOffset.UtcNow.ToString("O");
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static SqliteCommand CreateEdgeCommand(SqliteConnection connection, SqliteTransaction transaction)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT OR IGNORE INTO edges(source_id,target_id,kind,source_file_id,line,start_column,end_line,end_column,resolution_kind,confidence) " +
            "VALUES($source,$target,$kind,$file,$line,$startColumn,$endLine,$endColumn,$resolutionKind,$confidence)";
        foreach (var name in new[] { "$source", "$target", "$kind", "$file", "$line", "$startColumn", "$endLine", "$endColumn", "$resolutionKind", "$confidence" })
            command.Parameters.Add(new SqliteParameter { ParameterName = name });
        command.Prepare();
        return command;
    }

    private static async Task ExecuteEdgeInsertAsync(SqliteCommand command, CodeEdge edge, string sourceFileId,
        string storedSourceId, string storedTargetId, CancellationToken cancellationToken)
    {
        command.Parameters["$source"].Value = storedSourceId;
        command.Parameters["$target"].Value = storedTargetId;
        command.Parameters["$kind"].Value = edge.Kind.ToString();
        command.Parameters["$file"].Value = sourceFileId;
        command.Parameters["$line"].Value = (object?)edge.SourceLocation?.StartLine ?? DBNull.Value;
        command.Parameters["$startColumn"].Value = (object?)edge.SourceLocation?.StartColumn ?? DBNull.Value;
        command.Parameters["$endLine"].Value = (object?)edge.SourceLocation?.EndLine ?? DBNull.Value;
        command.Parameters["$endColumn"].Value = (object?)edge.SourceLocation?.EndColumn ?? DBNull.Value;
        command.Parameters["$resolutionKind"].Value = edge.ResolutionKind.ToString().ToLowerInvariant();
        command.Parameters["$confidence"].Value = (object?)edge.Confidence ?? DBNull.Value;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task SetMetadataAsync(SqliteConnection connection, SqliteTransaction transaction, string key, string value, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT OR REPLACE INTO metadata(key, value) VALUES($key, $value)";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, SqliteTransaction? transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<int> ScalarAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    internal static string ComputeContentHash(string relativePath, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(relativePath.Replace('\\', '/') + content);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private const string Schema = """
        PRAGMA foreign_keys = ON;
        PRAGMA journal_mode = WAL;
        PRAGMA synchronous = NORMAL;
        CREATE TABLE IF NOT EXISTS files (
          id TEXT PRIMARY KEY, project TEXT NOT NULL, relative_path TEXT NOT NULL,
          language TEXT NOT NULL, content_hash TEXT NOT NULL, mtime_utc TEXT NOT NULL,
          size INTEGER NOT NULL, indexed_at TEXT NOT NULL,
          UNIQUE(project, relative_path));
        CREATE TABLE IF NOT EXISTS symbols (
          id TEXT PRIMARY KEY, file_id TEXT NOT NULL REFERENCES files(id) ON DELETE CASCADE, kind TEXT NOT NULL, name TEXT NOT NULL,
          qualified_name TEXT NOT NULL, signature TEXT, start_line INTEGER, end_line INTEGER,
          visibility TEXT, language TEXT NOT NULL, origin_kind TEXT NOT NULL DEFAULT 'declared');
        CREATE TABLE IF NOT EXISTS symbol_collisions (
          id TEXT NOT NULL, project_a TEXT NOT NULL, project_b TEXT NOT NULL,
          detected_at TEXT NOT NULL, PRIMARY KEY (id, project_a, project_b));
        CREATE TABLE IF NOT EXISTS symbol_locations (
          symbol_id TEXT NOT NULL REFERENCES symbols(id) ON DELETE CASCADE,
          file_id TEXT NOT NULL REFERENCES files(id) ON DELETE CASCADE,
          start_line INTEGER NOT NULL,
          end_line INTEGER NOT NULL,
          is_primary INTEGER NOT NULL DEFAULT 0,
          PRIMARY KEY (symbol_id, file_id, start_line));
        CREATE TABLE IF NOT EXISTS edges (
          source_id TEXT NOT NULL, target_id TEXT NOT NULL, kind TEXT NOT NULL,
          source_file_id TEXT, line INTEGER, start_column INTEGER, end_line INTEGER, end_column INTEGER,
          resolution_kind TEXT NOT NULL DEFAULT 'semantic', confidence REAL,
          PRIMARY KEY(source_id, target_id, kind, source_file_id, line, start_column, end_line, end_column));
        CREATE TABLE IF NOT EXISTS metadata (key TEXT PRIMARY KEY, value TEXT NOT NULL);
        CREATE VIRTUAL TABLE IF NOT EXISTS symbols_fts USING fts5(
          qualified_name, content='symbols', content_rowid='rowid', tokenize='trigram');
        CREATE TRIGGER IF NOT EXISTS symbols_fts_ai AFTER INSERT ON symbols BEGIN
          INSERT INTO symbols_fts(rowid, qualified_name) VALUES (new.rowid, new.qualified_name);
        END;
        CREATE TRIGGER IF NOT EXISTS symbols_fts_ad AFTER DELETE ON symbols BEGIN
          INSERT INTO symbols_fts(symbols_fts, rowid, qualified_name) VALUES('delete', old.rowid, old.qualified_name);
        END;
        CREATE TRIGGER IF NOT EXISTS symbols_fts_au AFTER UPDATE ON symbols BEGIN
          INSERT INTO symbols_fts(symbols_fts, rowid, qualified_name) VALUES('delete', old.rowid, old.qualified_name);
          INSERT INTO symbols_fts(rowid, qualified_name) VALUES (new.rowid, new.qualified_name);
        END;
        CREATE INDEX IF NOT EXISTS ix_symbols_name ON symbols(name);
        CREATE INDEX IF NOT EXISTS ix_symbols_qualified_name ON symbols(qualified_name);
        CREATE INDEX IF NOT EXISTS ix_symbols_name_nocase ON symbols(name COLLATE NOCASE);
        CREATE INDEX IF NOT EXISTS ix_symbols_qualified_name_nocase ON symbols(qualified_name COLLATE NOCASE);
        CREATE INDEX IF NOT EXISTS ix_symbol_locations_symbol ON symbol_locations(symbol_id);
        CREATE INDEX IF NOT EXISTS ix_edges_source ON edges(source_id);
        CREATE INDEX IF NOT EXISTS ix_edges_target ON edges(target_id);
        CREATE INDEX IF NOT EXISTS ix_edges_kind ON edges(kind);
        CREATE INDEX IF NOT EXISTS ix_edges_source_kind ON edges(source_id, kind);
        CREATE INDEX IF NOT EXISTS ix_edges_target_kind ON edges(target_id, kind);
        CREATE INDEX IF NOT EXISTS ix_edges_source_file ON edges(source_file_id);
        CREATE INDEX IF NOT EXISTS ix_files_relative_path ON files(relative_path);
        """;
}
