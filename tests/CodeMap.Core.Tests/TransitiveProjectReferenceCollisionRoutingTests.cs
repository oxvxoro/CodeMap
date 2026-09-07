using CodeMap.Core.Models;
using CodeMap.Storage;
using Microsoft.Data.Sqlite;

namespace CodeMap.Core.Tests;


























[Collection("MsBuild")]
public sealed class TransitiveProjectReferenceCollisionRoutingTests
{
    [Fact]
    public async Task IndexAsync_RoutesEdgeThroughTransitiveReferenceToCorrectProject()
    {
        var fixtureDirectory = FixtureRestore.EnsureRestored("CollidingProjectsWithTransitiveReference");
        var workingDirectory = Path.Combine(Path.GetTempPath(), "codemap-transitive-collision-" + Guid.NewGuid());
        CopyFixture(fixtureDirectory, workingDirectory);
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