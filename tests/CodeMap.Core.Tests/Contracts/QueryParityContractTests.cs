using CodeMap.Core.Models;
using CodeMap.Core.Models.Investigation;
using CodeMap.Core.Contracts;
using CodeMap.CSharp;
using CodeMap.Engine.Application;
using CodeMap.Engine.Application.Investigation;
using CodeMap.Mcp;
using CodeMap.Storage;
using CodeMap.Storage.Queries;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace CodeMap.Core.Tests.Contracts;

[Collection("MsBuild")]
public sealed class QueryParityContractTests
{
    [Fact]
    public async Task PublicQueryMatrix_PreservesOrderedResultsAndRelationMetadata()
    {
        await using var fixture = await QueryParityFixture.CreateAsync("MultiProject");
        var snapshot = fixture.SnapshotService;
        var sql = fixture.SqlService;
        var greeter = Assert.Single(fixture.Snapshot.Symbols, symbol => symbol.Name == "Greeter");
        var greet = Assert.Single(fixture.Snapshot.Symbols, symbol => symbol.Name == "Greet");
        var caller = Assert.Single(fixture.Snapshot.Symbols, symbol => symbol.Name == "Call");
        var interfaceSymbol = Assert.Single(fixture.Snapshot.Symbols, symbol => symbol.Name == "IGreeter");

        QueryParityAssert.SameSymbols(snapshot.Find("Gree", 2), sql.Find("Gree", 2));
        QueryParityAssert.SameSymbols(
            snapshot.ResolveSymbol("Greeter", callableOnly: false, maxResults: 20).Matches,
            sql.ResolveSymbol("Greeter", callableOnly: false, maxResults: 20).Matches);
        QueryParityAssert.SameSymbols(snapshot.Members(greeter, 2), sql.Members(greeter, 2));
        QueryParityAssert.SameRelations(snapshot.CallerRelations(greet, 20), sql.CallerRelations(greet, 20));
        QueryParityAssert.SameRelations(snapshot.ImplementationRelations(interfaceSymbol, 20), sql.ImplementationRelations(interfaceSymbol, 20));
        QueryParityAssert.SameImpact(snapshot.Impact(caller, 2, 20), sql.Impact(caller, 2, 20));
    }

    [Fact]
    public async Task InvestigationPages_PreserveSnapshotSqliteParityAndHasMore()
    {
        await using var fixture = await QueryParityFixture.CreateAsync("MultiProject");
        var snapshot = fixture.SnapshotService;
        var sql = fixture.SqlService;
        var greet = Assert.Single(fixture.Snapshot.Symbols, symbol => symbol.Name == "Greet");

        var snapshotFirst = snapshot.CallerRelationsPaged(greet, 1, 0);
        var sqlFirst = sql.CallerRelationsPaged(greet, 1, 0);
        Assert.Equal(snapshotFirst.HasMore, sqlFirst.HasMore);
        QueryParityAssert.SameRelations(snapshotFirst.Items, sqlFirst.Items);

        var snapshotSecond = snapshot.CallerRelationsPaged(greet, 1, 1);
        var sqlSecond = sql.CallerRelationsPaged(greet, 1, 1);
        Assert.Equal(snapshotSecond.HasMore, sqlSecond.HasMore);
        QueryParityAssert.SameRelations(snapshotSecond.Items, sqlSecond.Items);
    }

    [Fact]
    public async Task ImplementationRelations_PreserveSnapshotEdgeChoiceAcrossBothDirections()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), "codemap-implementation-parity-" + Guid.NewGuid() + ".db");
        var file = new IndexedFile("file", "Fixture", "Fixture.cs", "csharp");
        var interfaceSymbol = Symbol("interface", "IGreeter", "Fixture.IGreeter", NodeKind.Interface, 1);
        var implementation = Symbol("implementation", "Greeter", "Fixture.Greeter", NodeKind.Class, 5);
        var implementedBy = new IndexedEdge(interfaceSymbol.Id, implementation.Id, EdgeKind.ImplementedBy, file.Id, 6);
        var implements = new IndexedEdge(implementation.Id, interfaceSymbol.Id, EdgeKind.Implements, file.Id, 5);
        var snapshot = new CodeMapSnapshot
        {
            Files = [file],
            Symbols = [interfaceSymbol, implementation],
            Edges = [implementedBy, implements]
        };

        try
        {
            await SeedImplementationParityDatabaseAsync(databasePath, file, interfaceSymbol, implementation, implementedBy, implements);
            await using var sql = new CodeMapQueryService(
                await new CodeMapQueryStore(databasePath).OpenReadOnlyConnectionAsync());
            var snapshotRelation = Assert.Single(new CodeMapQueryService(snapshot).ImplementationRelations(interfaceSymbol, 20));
            var sqlRelation = Assert.Single(sql.ImplementationRelations(interfaceSymbol, 20));

            Assert.Equal(implementedBy, snapshotRelation.Edge);
            Assert.Equal(snapshotRelation.Edge, sqlRelation.Edge);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(databasePath))
                File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task ReferencedByRelations_DeduplicateBeforeApplyingLimit()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), "codemap-referenced-by-parity-" + Guid.NewGuid() + ".db");
        var file = new IndexedFile("file", "Fixture", "Fixture.cs", "csharp");
        var target = Symbol("target", "Target", "Fixture.Target", NodeKind.Method, 10);
        var first = Symbol("first", "AFirst", "Fixture.AFirst", NodeKind.Method, 1);
        var second = Symbol("second", "BSecond", "Fixture.BSecond", NodeKind.Method, 2);
        var firstCall = new IndexedEdge(first.Id, target.Id, EdgeKind.Calls, file.Id, 1);
        var firstReference = new IndexedEdge(first.Id, target.Id, EdgeKind.References, file.Id, 2);
        var secondCall = new IndexedEdge(second.Id, target.Id, EdgeKind.Calls, file.Id, 3);
        var snapshot = new CodeMapSnapshot
        {
            Files = [file],
            Symbols = [target, first, second],
            Edges = [firstCall, firstReference, secondCall]
        };

        try
        {
            await SeedReferencedByParityDatabaseAsync(databasePath, file, target, first, second, firstCall, firstReference, secondCall);
            await using var sql = new CodeMapQueryService(
                await new CodeMapQueryStore(databasePath).OpenReadOnlyConnectionAsync());
            var snapshotRelations = new CodeMapQueryService(snapshot).ReferencedByRelations(target, 2);
            var sqlRelations = sql.ReferencedByRelations(target, 2);

            QueryParityAssert.SameRelations(snapshotRelations, sqlRelations);
            Assert.Equal([first.Id, second.Id], sqlRelations.Select(relation => relation.Symbol.Id));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(databasePath))
                File.Delete(databasePath);
        }
    }

    [Fact]
    public void InvestigationPresentationMapper_SerializesBothV1SchemaBranches()
    {
        var solutionRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        using var schemaDocument = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(solutionRoot, "docs", "cli-investigation-v1.schema.json")));
        var oneOf = schemaDocument.RootElement.GetProperty("oneOf");
        Assert.Equal(2, oneOf.GetArrayLength());
        Assert.Equal("#/$defs/result", oneOf[0].GetProperty("$ref").GetString());
        Assert.Equal("#/$defs/errorResponse", oneOf[1].GetProperty("$ref").GetString());

        var root = Symbol("root", "Process", "Fixture.Process", NodeKind.Method, 1);
        var response = InvestigationPresentationMapper.ToResponse(
            "Process",
            InvestigationGoal.Debug,
            new InvestigationResult(
                new InvestigationResponse(1, "Process", InvestigationGoal.Debug, root, false, []),
                [],
                new InvestigationBudgetAllocator().Allocate([], 200),
                InvestigationCoverage.Empty,
                []),
            stale: false);
        var resultJson = InvestigationPresentationMapper.Serialize(response);
        var errorJson = InvestigationPresentationMapper.Serialize(
            new InvestigationErrorResponseDto(1, "Missing", "debug",
                new InvestigationErrorDto("no_matches", "not found"), "no_matches", false));

        using var resultDocument = JsonDocument.Parse(resultJson);
        using var errorDocument = JsonDocument.Parse(errorJson);
        Assert.Equal(1, resultDocument.RootElement.GetProperty("version").GetInt32());
        Assert.Equal("debug", resultDocument.RootElement.GetProperty("goal").GetString());
        Assert.True(resultDocument.RootElement.GetProperty("items").ValueKind == JsonValueKind.Array);
        Assert.Equal("Method", resultDocument.RootElement.GetProperty("root").GetProperty("kind").GetString());
        Assert.Equal(1, errorDocument.RootElement.GetProperty("version").GetInt32());
        Assert.Equal("no_matches", errorDocument.RootElement.GetProperty("reason").GetString());
        Assert.False(errorDocument.RootElement.TryGetProperty("items", out _));
    }

    [Fact(Timeout = 90_000)]
    public async Task Investigate_CliAndMcpProduceTheSameJsonShape()
    {
        await using var fixture = await QueryParityFixture.CreateAsync("MultiProject");
        var root = fixture.WorkingDirectory.Replace('\\', '/');
        var cli = await CliProcess.RunAsync(
            $"investigate Fixture.ProjB.Greeter --goal trace --root \"{root}\" --tokens 200 --max-results 5 --source-mode none --json");
        Assert.Equal(0, cli.ExitCode);

        var context = new CodeMapMcpContext(fixture.WorkingDirectory);
        var application = new CodeMapApplication(graphReaderFactory: OpenReaderAsync);
        var mcpJson = await CodeMapTools.Investigate(
            context, application, "Fixture.ProjB.Greeter", "trace",
            root: fixture.WorkingDirectory, tokens: 200, maxResults: 5, sourceMode: "none");

        using var cliDocument = JsonDocument.Parse(cli.StdOut);
        using var mcpDocument = JsonDocument.Parse(mcpJson);
        Assert.Equal(
            JsonSerializer.Serialize(cliDocument.RootElement),
            JsonSerializer.Serialize(mcpDocument.RootElement));
    }

    private static async Task SeedImplementationParityDatabaseAsync(
        string databasePath,
        IndexedFile file,
        IndexedSymbol interfaceSymbol,
        IndexedSymbol implementation,
        IndexedEdge implementedBy,
        IndexedEdge implements)
    {
        await new SqliteCodeMapStore(databasePath).InitializeAsync();
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO files(id, project, relative_path, language, content_hash, mtime_utc, size, indexed_at)
            VALUES ($fileId, $project, $path, $language, 'hash', '2026-01-01T00:00:00.0000000Z', 0, '2026-01-01T00:00:00.0000000Z');
            INSERT INTO symbols(id, file_id, kind, name, qualified_name, signature, start_line, end_line, visibility, language)
            VALUES ($interfaceId, $fileId, $interfaceKind, $interfaceName, $interfaceQualifiedName, NULL, $interfaceStart, $interfaceEnd, 'public', $language),
                   ($implementationId, $fileId, $implementationKind, $implementationName, $implementationQualifiedName, NULL, $implementationStart, $implementationEnd, 'public', $language);
            INSERT INTO edges(source_id, target_id, kind, source_file_id, line, start_column, end_line, end_column, resolution_kind, confidence)
            VALUES ($implementedBySource, $implementedByTarget, $implementedByKind, $fileId, $implementedByLine, NULL, NULL, NULL, 'semantic', NULL),
                   ($implementsSource, $implementsTarget, $implementsKind, $fileId, $implementsLine, NULL, NULL, NULL, 'semantic', NULL);
            INSERT INTO metadata(key, value)
            VALUES ('schema_version', $schemaVersion), ('analyzer_version_csharp', $csharpAnalyzerVersion);
            """;
        command.Parameters.AddWithValue("$fileId", file.Id);
        command.Parameters.AddWithValue("$project", file.Project);
        command.Parameters.AddWithValue("$path", file.RelativePath);
        command.Parameters.AddWithValue("$language", file.Language);
        command.Parameters.AddWithValue("$interfaceId", interfaceSymbol.Id);
        command.Parameters.AddWithValue("$interfaceKind", interfaceSymbol.Kind.ToString());
        command.Parameters.AddWithValue("$interfaceName", interfaceSymbol.Name);
        command.Parameters.AddWithValue("$interfaceQualifiedName", interfaceSymbol.QualifiedName);
        command.Parameters.AddWithValue("$interfaceStart", interfaceSymbol.StartLine);
        command.Parameters.AddWithValue("$interfaceEnd", interfaceSymbol.EndLine);
        command.Parameters.AddWithValue("$implementationId", implementation.Id);
        command.Parameters.AddWithValue("$implementationKind", implementation.Kind.ToString());
        command.Parameters.AddWithValue("$implementationName", implementation.Name);
        command.Parameters.AddWithValue("$implementationQualifiedName", implementation.QualifiedName);
        command.Parameters.AddWithValue("$implementationStart", implementation.StartLine);
        command.Parameters.AddWithValue("$implementationEnd", implementation.EndLine);
        command.Parameters.AddWithValue("$implementedBySource", implementedBy.SourceId);
        command.Parameters.AddWithValue("$implementedByTarget", implementedBy.TargetId);
        command.Parameters.AddWithValue("$implementedByKind", implementedBy.Kind.ToString());
        command.Parameters.AddWithValue("$implementedByLine", implementedBy.Line);
        command.Parameters.AddWithValue("$implementsSource", implements.SourceId);
        command.Parameters.AddWithValue("$implementsTarget", implements.TargetId);
        command.Parameters.AddWithValue("$implementsKind", implements.Kind.ToString());
        command.Parameters.AddWithValue("$implementsLine", implements.Line);
        command.Parameters.AddWithValue("$schemaVersion", SqliteCodeMapStore.SchemaVersion);
        command.Parameters.AddWithValue("$csharpAnalyzerVersion", SqliteCodeMapStore.CurrentAnalyzerVersions["csharp"]);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task SeedReferencedByParityDatabaseAsync(
        string databasePath,
        IndexedFile file,
        IndexedSymbol target,
        IndexedSymbol first,
        IndexedSymbol second,
        IndexedEdge firstCall,
        IndexedEdge firstReference,
        IndexedEdge secondCall)
    {
        await new SqliteCodeMapStore(databasePath).InitializeAsync();
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO files(id, project, relative_path, language, content_hash, mtime_utc, size, indexed_at)
            VALUES ($fileId, $project, $path, $language, 'hash', '2026-01-01T00:00:00.0000000Z', 0, '2026-01-01T00:00:00.0000000Z');
            INSERT INTO symbols(id, file_id, kind, name, qualified_name, signature, start_line, end_line, visibility, language)
            VALUES ($targetId, $fileId, $targetKind, $targetName, $targetQualifiedName, NULL, $targetStart, $targetEnd, 'public', $language),
                   ($firstId, $fileId, $firstKind, $firstName, $firstQualifiedName, NULL, $firstStart, $firstEnd, 'public', $language),
                   ($secondId, $fileId, $secondKind, $secondName, $secondQualifiedName, NULL, $secondStart, $secondEnd, 'public', $language);
            INSERT INTO edges(source_id, target_id, kind, source_file_id, line, start_column, end_line, end_column, resolution_kind, confidence)
            VALUES ($firstCallSource, $firstCallTarget, $firstCallKind, $fileId, $firstCallLine, NULL, NULL, NULL, 'semantic', NULL),
                   ($firstReferenceSource, $firstReferenceTarget, $firstReferenceKind, $fileId, $firstReferenceLine, NULL, NULL, NULL, 'semantic', NULL),
                   ($secondCallSource, $secondCallTarget, $secondCallKind, $fileId, $secondCallLine, NULL, NULL, NULL, 'semantic', NULL);
            INSERT INTO metadata(key, value)
            VALUES ('schema_version', $schemaVersion), ('analyzer_version_csharp', $csharpAnalyzerVersion);
            """;
        command.Parameters.AddWithValue("$fileId", file.Id);
        command.Parameters.AddWithValue("$project", file.Project);
        command.Parameters.AddWithValue("$path", file.RelativePath);
        command.Parameters.AddWithValue("$language", file.Language);
        AddSymbolParameters(command, "target", target);
        AddSymbolParameters(command, "first", first);
        AddSymbolParameters(command, "second", second);
        AddEdgeParameters(command, "firstCall", firstCall);
        AddEdgeParameters(command, "firstReference", firstReference);
        AddEdgeParameters(command, "secondCall", secondCall);
        command.Parameters.AddWithValue("$schemaVersion", SqliteCodeMapStore.SchemaVersion);
        command.Parameters.AddWithValue("$csharpAnalyzerVersion", SqliteCodeMapStore.CurrentAnalyzerVersions["csharp"]);
        await command.ExecuteNonQueryAsync();
    }

    private static void AddSymbolParameters(SqliteCommand command, string prefix, IndexedSymbol symbol)
    {
        command.Parameters.AddWithValue("$" + prefix + "Id", symbol.Id);
        command.Parameters.AddWithValue("$" + prefix + "Kind", symbol.Kind.ToString());
        command.Parameters.AddWithValue("$" + prefix + "Name", symbol.Name);
        command.Parameters.AddWithValue("$" + prefix + "QualifiedName", symbol.QualifiedName);
        command.Parameters.AddWithValue("$" + prefix + "Start", symbol.StartLine);
        command.Parameters.AddWithValue("$" + prefix + "End", symbol.EndLine);
    }

    private static void AddEdgeParameters(SqliteCommand command, string prefix, IndexedEdge edge)
    {
        command.Parameters.AddWithValue("$" + prefix + "Source", edge.SourceId);
        command.Parameters.AddWithValue("$" + prefix + "Target", edge.TargetId);
        command.Parameters.AddWithValue("$" + prefix + "Kind", edge.Kind.ToString());
        command.Parameters.AddWithValue("$" + prefix + "Line", edge.Line);
    }

    private static IndexedSymbol Symbol(string id, string name, string qualifiedName, NodeKind kind, int line) =>
        new(id, "file", "file", "Fixture.cs", kind, name, qualifiedName, null, line, line + 1, "public", "csharp");

    private static async Task<ICodeMapGraphReader> OpenReaderAsync(
        string databasePath, CancellationToken cancellationToken)
    {
        var connection = await new CodeMapQueryStore(databasePath).OpenReadOnlyConnectionAsync(cancellationToken);
        return new SqliteCodeMapGraphReader(connection);
    }
}
