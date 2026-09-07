using CodeMap.Core.Analysis;
using CodeMap.Core.Models;
using CodeMap.CSharp;
using CodeMap.Storage;
using Microsoft.Data.Sqlite;

namespace CodeMap.Core.Tests;








[Collection("MsBuild")]
public sealed class SameLineDuplicateRelationTests
{
    [Fact]
    public async Task Analyzer_KeepsBothSameLineCallOccurrencesAsDistinctEdges()
    {
        var analyzer = new CSharpLanguageAnalyzer();
        const string source = """
            namespace Fixture;
            public class Repository { public void Save() { } }
            public class Handler
            {
                private readonly Repository repository = new();
                public void Run() { repository.Save(); repository.Save(); }
            }
            """;

        var result = await analyzer.AnalyzeAsync(new AnalysisContext
        {
            ProjectName = "Fixture",
            FilePath = "Handler.cs",
            Content = source,
            RootDirectory = Directory.GetCurrentDirectory()
        }, CancellationToken.None);

        var run = Assert.Single(result.Nodes, n => n.Name == "Run");
        var save = Assert.Single(result.Nodes, n => n.Name == "Save");

        var callEdges = result.Edges.Where(e => e.Kind == EdgeKind.Calls && e.SourceId == run.Id && e.TargetId == save.Id).ToArray();
        Assert.Equal(2, callEdges.Length);
        Assert.Equal(callEdges[0].SourceLocation!.StartLine, callEdges[1].SourceLocation!.StartLine);
        Assert.NotEqual(callEdges[0].SourceLocation!.StartColumn, callEdges[1].SourceLocation!.StartColumn);
    }

    [Fact]
    public async Task IndexAsync_PersistsAndQueriesBothSameLineOccurrences()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "codemap-sameline-" + Guid.NewGuid());
        CopyFixture(GetFixtureDirectory(), workingDirectory);
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);

            var databasePath = Path.Combine(workingDirectory, ".codemap", "index.db");


            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString()))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT COUNT(*) FROM edges e
                    JOIN symbols src ON src.id = e.source_id
                    JOIN symbols tgt ON tgt.id = e.target_id
                    WHERE src.name = 'Run' AND tgt.name = 'Save' AND e.kind = 'Calls'
                    """;
                var count = Convert.ToInt32(await command.ExecuteScalarAsync());
                Assert.Equal(2, count);
            }

            var store = new CodeMapQueryStore(databasePath);
            var graph = await store.LoadAsync();
            var snapshotService = new CodeMapQueryService(graph);
            var run = Assert.Single(graph.Symbols, s => s.Name == "Run");
            var save = Assert.Single(graph.Symbols, s => s.Name == "Save");

            var snapshotRelations = snapshotService.Relations(run.Id, save.Id, EdgeKind.Calls, maxResults: 10);
            Assert.Equal(2, snapshotRelations.Count);
            Assert.NotEqual(snapshotRelations[0].Edge.StartColumn, snapshotRelations[1].Edge.StartColumn);

            await using var connection2 = await store.OpenReadOnlyConnectionAsync();
            await using var sqlService = new CodeMapQueryService(graph, connection2);
            var sqlRelations = sqlService.Relations(run.Id, save.Id, EdgeKind.Calls, maxResults: 10);
            Assert.Equal(2, sqlRelations.Count);
            Assert.NotEqual(sqlRelations[0].Edge.StartColumn, sqlRelations[1].Edge.StartColumn);


            Assert.Equal(
                snapshotRelations.Select(r => r.Edge.StartColumn).OrderBy(c => c),
                sqlRelations.Select(r => r.Edge.StartColumn).OrderBy(c => c));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(workingDirectory))
                Directory.Delete(workingDirectory, recursive: true);
        }
    }

    private static string GetFixtureDirectory()
    {
        var testDir = AppContext.BaseDirectory;
        var solutionRoot = Path.GetFullPath(Path.Combine(testDir, "..", "..", "..", "..", ".."));
        return Path.Combine(solutionRoot, "tests", "Fixtures", "SameLineDuplicateCalls");
    }

    private static void CopyFixture(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*.*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var targetPath = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.Copy(file, targetPath, overwrite: true);
        }
    }
}