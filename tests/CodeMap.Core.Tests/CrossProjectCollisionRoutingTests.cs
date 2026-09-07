using CodeMap.Core.Models;
using CodeMap.Storage;
using Microsoft.Data.Sqlite;

namespace CodeMap.Core.Tests;













[Collection("MsBuild")]
public sealed class CrossProjectCollisionRoutingTests
{
    [Fact]
    public async Task IndexAsync_RoutesCallerEdgeToTheReferencedProjectsWidget()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "codemap-collision-routing-" + Guid.NewGuid());
        CopyFixture(GetFixtureDirectory(), workingDirectory);
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);

            var databasePath = Path.Combine(workingDirectory, ".codemap", "index.db");
            var graph = await new CodeMapQueryStore(databasePath).LoadAsync();
            var service = new CodeMapQueryService(graph);

            var widgets = service.Find("Widget", maxResults: 10);
            Assert.Equal(2, widgets.Count);
            var projAWidget = Assert.Single(widgets, symbol => symbol.Project == "ProjA");
            var projBWidget = Assert.Single(widgets, symbol => symbol.Project == "ProjB");

            var callB = Assert.Single(service.Find("CallB", maxResults: 10));

            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath
            }.ToString());
            await connection.OpenAsync();

            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT kind, target_id FROM edges WHERE source_id = $source";
            command.Parameters.AddWithValue("$source", callB.Id);
            await using var reader = await command.ExecuteReaderAsync();
            var targets = new List<string>();
            while (await reader.ReadAsync())
                targets.Add(reader.GetString(0) + "->" + reader.GetString(1));







            Assert.Contains("References->" + projBWidget.Id, targets);
            Assert.DoesNotContain("References->" + projAWidget.Id, targets);
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
        return Path.Combine(solutionRoot, "tests", "Fixtures", "CollidingProjectsWithReference");
    }

    private static void CopyFixture(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*.*", SearchOption.AllDirectories)
                     .Where(file => !file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)
                         && !file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)))
        {
            var relative = Path.GetRelativePath(source, file);
            var targetPath = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.Copy(file, targetPath, overwrite: true);
        }
    }
}