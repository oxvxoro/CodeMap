using CodeMap.Storage;
using Microsoft.Data.Sqlite;

namespace CodeMap.Core.Tests;

[Collection("MsBuild")]
public sealed class SymbolLocationsTests
{
    [Fact]
    public async Task IndexAsync_RecordsAllPartialDeclarationLocations()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "codemap-symbol-locations-" + Guid.NewGuid());
        CopyFixture(GetFixtureDirectory(), workingDirectory);
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);

            var databasePath = Path.Combine(workingDirectory, ".codemap", "index.db");
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT sl.symbol_id, f.relative_path, sl.start_line, sl.is_primary
                FROM symbol_locations sl JOIN files f ON f.id = sl.file_id
                JOIN symbols s ON s.id = sl.symbol_id
                WHERE s.name = 'Foo'
                ORDER BY f.relative_path
                """;
            await using var reader = await command.ExecuteReaderAsync();
            var rows = new List<(string SymbolId, string Path, int StartLine, int IsPrimary)>();
            while (await reader.ReadAsync())
                rows.Add((reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3)));

            Assert.Equal(2, rows.Count);
            Assert.All(rows, row => Assert.Equal("sym://T:PartialType.Foo", row.SymbolId));
            Assert.Equal(new[] { "Foo.Extra.cs", "Foo.cs" }, rows.Select(row => row.Path).ToArray());
            Assert.Equal(2, rows.Select(row => row.StartLine).Distinct().Count());
            Assert.Single(rows, row => row.IsPrimary == 1);
            Assert.Single(rows, row => row.IsPrimary == 0);
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
        return Path.Combine(solutionRoot, "tests", "Fixtures", "PartialType");
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