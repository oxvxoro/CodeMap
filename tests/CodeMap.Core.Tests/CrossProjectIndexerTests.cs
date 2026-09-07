using CodeMap.CSharp;
using CodeMap.Core.Models;
using CodeMap.Storage;
using Microsoft.Data.Sqlite;

namespace CodeMap.Core.Tests;










[Collection("MsBuild")]
public sealed class CrossProjectIndexerTests
{
    [Fact]
    public async Task AnalyzeAsync_ResolvesCallAndInheritsEdgesAcrossProjects()
    {
        var indexer = new CSharpWorkspaceIndexer();
        var projects = await indexer.AnalyzeAsync(GetFixtureSlnxPath(), projectNames: null, CancellationToken.None);

        var projA = Assert.Single(projects, p => p.ProjectName == "ProjA");
        var projB = Assert.Single(projects, p => p.ProjectName == "ProjB");
        Assert.All(projA.Result.Edges.Concat(projB.Result.Edges), edge =>
            Assert.Equal(EdgeResolutionKind.Semantic, edge.ResolutionKind));

        var call = projA.Result.Nodes.Single(n => n.Kind == NodeKind.Method && n.Name == "Call");
        var caller = projA.Result.Nodes.Single(n => n.Kind == NodeKind.Class && n.Name == "Caller");
        var greet = projB.Result.Nodes.Single(n => n.Kind == NodeKind.Method && n.Name == "Greet");
        var greeter = projB.Result.Nodes.Single(n => n.Kind == NodeKind.Class && n.Name == "Greeter");




        Assert.Contains(projA.Result.Edges, edge => edge.Kind == EdgeKind.Calls && edge.SourceId == call.Id && edge.TargetId == greet.Id);
        Assert.Contains(projA.Result.Edges, edge => edge.Kind == EdgeKind.Inherits && edge.SourceId == caller.Id && edge.TargetId == greeter.Id);
    }

    [Fact]
    public async Task IndexAsync_PersistsCrossProjectEdgesAndQueryServiceFindsThem()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "codemap-crossproject-" + Guid.NewGuid());
        CopyFixture(GetFixtureDirectory(), workingDirectory);
        try
        {
            var indexer = new IncrementalCodeMapIndexer();
            await indexer.IndexAsync(workingDirectory, force: true, CancellationToken.None);

            var databasePath = Path.Combine(workingDirectory, ".codemap", "index.db");
            await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT resolution_kind, confidence FROM edges";
                await using var reader = await command.ExecuteReaderAsync();
                var edgeCount = 0;
                while (await reader.ReadAsync())
                {
                    edgeCount++;
                    Assert.Equal("semantic", reader.GetString(0));
                    Assert.Equal(1.0, reader.GetDouble(1));
                }
                Assert.True(edgeCount > 0);
            }
            var graph = await new CodeMapQueryStore(databasePath).LoadAsync();
            var service = new CodeMapQueryService(graph);

            var greet = Assert.Single(service.Find("Greet", maxResults: 10));
            var referencedBy = service.ReferencedBy(greet, maxResults: 10);

            Assert.Contains(referencedBy, symbol => symbol.Name == "Call");
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
        return Path.Combine(solutionRoot, "tests", "Fixtures", "MultiProject");
    }

    private static string GetFixtureSlnxPath() => Path.Combine(GetFixtureDirectory(), "MultiProject.slnx");

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