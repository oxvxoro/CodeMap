using CodeMap.Core.Models;
using CodeMap.Storage;
using Microsoft.Data.Sqlite;

namespace CodeMap.Core.Tests;

[Collection("MsBuild")]
public sealed class SymbolCollisionTests
{
    [Fact]
    public async Task IndexAsync_PreservesCollidingSymbolsAndRecordsCollision()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "codemap-collision-" + Guid.NewGuid());
        CopyFixture(GetFixtureDirectory(), workingDirectory);
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);

            var graph = await new CodeMapQueryStore(Path.Combine(workingDirectory, ".codemap", "index.db")).LoadAsync();
            var service = new CodeMapQueryService(graph);
            var widgets = service.Find("Widget", maxResults: 10);
            Assert.Equal(2, widgets.Count);
            Assert.Equal(new[] { "ProjA", "ProjB" }, widgets.Select(symbol => symbol.Project).OrderBy(project => project).ToArray());
            Assert.Contains(widgets, symbol => symbol.Id == "sym://T:Shared.Widget");
            Assert.Contains(widgets, symbol => symbol.Id == "sym://T:Shared.Widget#2");

            var projAWidget = Assert.Single(widgets, symbol => symbol.Project == "ProjA");
            var projBWidget = Assert.Single(widgets, symbol => symbol.Project == "ProjB");
            Assert.Equal("sym://T:Shared.Widget", projAWidget.Id);
            Assert.Equal("sym://T:Shared.Widget#2", projBWidget.Id);

            var projAMembers = service.Members(projAWidget, maxResults: 20);
            Assert.Contains(projAMembers, symbol => symbol.Name == "A");
            Assert.Contains(projAMembers, symbol => symbol.Name == "GetA");
            Assert.DoesNotContain(projAMembers, symbol => symbol.Name == "B");
            Assert.DoesNotContain(projAMembers, symbol => symbol.Name == "GetB");

            var projBMembers = service.Members(projBWidget, maxResults: 20);
            Assert.Contains(projBMembers, symbol => symbol.Name == "B");
            Assert.Contains(projBMembers, symbol => symbol.Name == "GetB");
            Assert.DoesNotContain(projBMembers, symbol => symbol.Name == "A");
            Assert.DoesNotContain(projBMembers, symbol => symbol.Name == "GetA");

            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(workingDirectory, ".codemap", "index.db")
            }.ToString());
            await connection.OpenAsync();

            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    SELECT e.kind, e.source_id, e.target_id, s.name
                    FROM edges e
                    JOIN symbols s ON s.id = e.target_id
                    WHERE e.kind = 'Contains'
                      AND e.source_id IN ($projAWidget, $projBWidget)
                    ORDER BY e.source_id, s.name
                    """;
                command.Parameters.AddWithValue("$projAWidget", projAWidget.Id);
                command.Parameters.AddWithValue("$projBWidget", projBWidget.Id);
                await using var reader = await command.ExecuteReaderAsync();
                var containsRows = new List<(string SourceId, string TargetName)>();
                while (await reader.ReadAsync())
                    containsRows.Add((reader.GetString(1), reader.GetString(3)));

                Assert.Contains(containsRows, row => row.SourceId == projAWidget.Id && row.TargetName is "A" or "GetA");
                Assert.Contains(containsRows, row => row.SourceId == projBWidget.Id && row.TargetName is "B" or "GetB");
                Assert.DoesNotContain(containsRows, row => row.SourceId == projBWidget.Id && row.TargetName is "A" or "GetA");
                Assert.DoesNotContain(containsRows, row => row.SourceId == projAWidget.Id && row.TargetName is "B" or "GetB");
            }

            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    SELECT e.kind, e.source_id, e.target_id
                    FROM edges e
                    WHERE e.kind = 'Defines'
                      AND e.target_id IN ($projAWidget, $projBWidget)
                    """;
                command.Parameters.AddWithValue("$projAWidget", projAWidget.Id);
                command.Parameters.AddWithValue("$projBWidget", projBWidget.Id);
                await using var reader = await command.ExecuteReaderAsync();
                var definesTargets = new List<string>();
                while (await reader.ReadAsync())
                    definesTargets.Add(reader.GetString(2));

                Assert.Contains(definesTargets, id => id == projAWidget.Id);
                Assert.Contains(definesTargets, id => id == projBWidget.Id);
            }

            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT id, project_a, project_b, detected_at FROM symbol_collisions WHERE id = $id";
                command.Parameters.AddWithValue("$id", "sym://T:Shared.Widget");
                await using var reader = await command.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal("sym://T:Shared.Widget", reader.GetString(0));
                Assert.Equal("ProjA", reader.GetString(1));
                Assert.Equal("ProjB", reader.GetString(2));
                Assert.True(DateTimeOffset.TryParse(reader.GetString(3), out _));
                Assert.False(await reader.ReadAsync());
            }
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
        return Path.Combine(solutionRoot, "tests", "Fixtures", "CollidingProjects");
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