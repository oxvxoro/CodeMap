using CodeMap.Storage;
using Microsoft.Data.Sqlite;

namespace CodeMap.Core.Tests;

public sealed class CodeMapIndexStatusReaderTests
{
    [Fact]
    public async Task ReadAsync_ReturnsNotIndexedForMissingDatabaseMetadata()
    {
        var directory = Path.Combine(Path.GetTempPath(), "codemap-status-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "index.db");
        try
        {
            await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE metadata (key TEXT PRIMARY KEY, value TEXT NOT NULL);";
                await command.ExecuteNonQueryAsync();
            }

            var status = await CodeMapIndexStatusReader.ReadAsync(databasePath, CancellationToken.None);

            Assert.Equal("not_indexed", status.IndexState);
            Assert.True(status.SchemaOutdated);
            Assert.Equal(0, status.Symbols);
            Assert.Equal(0, status.Edges);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ReadAsync_ReportsMetadataAndCountsWithoutWriting()
    {
        var directory = Path.Combine(Path.GetTempPath(), "codemap-status-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "index.db");
        try
        {
            await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE metadata (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                    CREATE TABLE symbols (id TEXT);
                    CREATE TABLE edges (id TEXT);
                    INSERT INTO metadata (key, value) VALUES
                        ('schema_version', '4'),
                        ('last_indexed_at_utc', '2026-09-09T00:00:00+00:00'),
                        ('index_state', 'ready'),
                        ('analyzer_version_csharp', '1'),
                        ('analyzer_version_web', '1');
                    INSERT INTO symbols VALUES ('one'), ('two');
                    INSERT INTO edges VALUES ('one');
                    """;
                await command.ExecuteNonQueryAsync();
            }

            var status = await CodeMapIndexStatusReader.ReadAsync(databasePath, CancellationToken.None);

            Assert.Equal("ready", status.IndexState);
            Assert.Equal("4", status.SchemaVersion);
            Assert.Equal(2, status.Symbols);
            Assert.Equal(1, status.Edges);
            Assert.Equal("1", status.AnalyzerVersions["csharp"]);
            Assert.Equal("1", status.AnalyzerVersions["web"]);
            Assert.Equal(new DateTimeOffset(2026, 9, 9, 0, 0, 0, TimeSpan.Zero), status.LastIndexedAtUtc);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }
}
