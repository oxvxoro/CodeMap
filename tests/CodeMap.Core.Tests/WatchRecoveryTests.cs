using CodeMap.Cli;
using CodeMap.Storage;
using Microsoft.Data.Sqlite;

namespace CodeMap.Core.Tests;

[Collection("MsBuild")]
public sealed class WatchRecoveryTests
{
    [Fact(Timeout = 90_000)]
    public async Task WatchRecoveryUpdate_UsesNonDestructiveIncrementalPath()
    {
        var workingDirectory = CopyFixture();
        try
        {
            var indexer = new IncrementalCodeMapIndexer();
            await indexer.IndexAsync(workingDirectory, force: true, CancellationToken.None);
            var databasePath = Path.Combine(workingDirectory, ".codemap", "index.db");
            var before = new FileInfo(databasePath).Length;

            await File.AppendAllTextAsync(Path.Combine(workingDirectory, "ProjB", "Greeter.cs"), Environment.NewLine);
            await indexer.UpdateAsync(workingDirectory, CancellationToken.None);

            Assert.True(File.Exists(databasePath));
            Assert.True(new FileInfo(databasePath).Length > 0);
            Assert.True(before > 0);
            await using var connection = new SqliteConnection($"Data Source={databasePath}");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM symbols";
            Assert.True(Convert.ToInt32(await command.ExecuteScalarAsync()) > 0);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(workingDirectory))
                Directory.Delete(workingDirectory, recursive: true);
        }
    }

    private static string CopyFixture()
    {
        var solutionRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var source = Path.Combine(solutionRoot, "tests", "Fixtures", "MultiProject");
        var destination = Path.Combine(Path.GetTempPath(), "codemap-watch-recovery-" + Guid.NewGuid());
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*.*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
        return destination;
    }
}
