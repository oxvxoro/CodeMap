using CodeMap.Storage;
using Microsoft.Data.Sqlite;

namespace CodeMap.Core.Tests;

[Collection("MsBuild")]
public sealed class OrphanEdgeSweepTests
{
    [Fact]
    public async Task UpdateAsync_RemovingTargetProjectSweepsEdgesFromUntouchedProject()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "codemap-orphan-sweep-" + Guid.NewGuid());
        CopyFixture(GetFixtureDirectory(), workingDirectory);
        try
        {
            var indexer = new IncrementalCodeMapIndexer();
            await indexer.IndexAsync(workingDirectory, force: true, CancellationToken.None);

            var databasePath = Path.Combine(workingDirectory, ".codemap", "index.db");
            Assert.True(await CountOrphanEdgesAsync(databasePath) == 0);
            Assert.True(await CountEdgesTargetingMissingProjectSymbolsAsync(databasePath) > 0);

            var solutionPath = Path.Combine(workingDirectory, "MultiProject.slnx");
            var solution = await File.ReadAllTextAsync(solutionPath);
            await File.WriteAllTextAsync(solutionPath, solution.Replace("  <Project Path=\"ProjB/ProjB.csproj\" />\r\n", string.Empty, StringComparison.Ordinal));
            Directory.Delete(Path.Combine(workingDirectory, "ProjB"), recursive: true);

            await indexer.UpdateAsync(workingDirectory, CancellationToken.None);

            Assert.Equal(0, await CountOrphanEdgesAsync(databasePath));
            Assert.Equal(0, await CountEdgesTargetingMissingProjectSymbolsAsync(databasePath));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(workingDirectory))
                Directory.Delete(workingDirectory, recursive: true);
        }
    }

    private static async Task<int> CountOrphanEdgesAsync(string databasePath)
    {
        await using var connection = await OpenConnectionAsync(databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM edges WHERE target_id NOT IN (SELECT id FROM symbols)";
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task<int> CountEdgesTargetingMissingProjectSymbolsAsync(string databasePath)
    {
        await using var connection = await OpenConnectionAsync(databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM edges WHERE target_id LIKE 'sym://T:Fixture.ProjB.%'";
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task<SqliteConnection> OpenConnectionAsync(string databasePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
        await connection.OpenAsync();
        return connection;
    }

    private static string GetFixtureDirectory()
    {
        var testDir = AppContext.BaseDirectory;
        var solutionRoot = Path.GetFullPath(Path.Combine(testDir, "..", "..", "..", "..", ".."));
        return Path.Combine(solutionRoot, "tests", "Fixtures", "MultiProject");
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