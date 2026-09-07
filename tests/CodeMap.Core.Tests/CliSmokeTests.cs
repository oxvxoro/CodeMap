using System.Text.Json;
using CodeMap.Storage;
using Microsoft.Data.Sqlite;

namespace CodeMap.Core.Tests;

[Collection("MsBuild")]
public class CliSmokeTests
{
    [Fact(Timeout = 15_000)]
    public async Task HelpCommand_ExitsSuccessfully()
    {
        var output = await CliProcess.RunAsync("--help");

        Assert.Equal(0, output.ExitCode);
        Assert.Contains("CodeMap", output.StdOut, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(Timeout = 15_000)]
    public async Task VersionCommand_PrintsVersion()
    {
        var output = await CliProcess.RunAsync("version");

        Assert.Equal(0, output.ExitCode);
        Assert.Contains("codemap", output.StdOut, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("0.1.0", output.StdOut);
    }

    [Fact(Timeout = 15_000)]
    public async Task FindJson_NoIndex_ReturnsErrorEnvelope()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "codemap-cli-noindex-" + Guid.NewGuid());
        Directory.CreateDirectory(workingDirectory);
        try
        {
            var output = await CliProcess.RunAsync($"find MissingSymbol --root \"{workingDirectory}\" --json");
            Assert.Equal(1, output.ExitCode);

            using var document = JsonDocument.Parse(output.StdOut);
            Assert.Equal("index_not_found", document.RootElement.GetProperty("error").GetProperty("code").GetString());
        }
        finally
        {
            if (Directory.Exists(workingDirectory))
                Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact(Timeout = 90_000)]
    public async Task FindJson_NoMatchesAndSuccess_UseQueryResponseEnvelope()
    {
        var workingDirectory = CopyFixture("MultiProject");
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);

            var missOutput = await CliProcess.RunAsync($"find DefinitelyMissing --root \"{workingDirectory}\" --json");
            Assert.Equal(2, missOutput.ExitCode);
            using (var missDocument = JsonDocument.Parse(missOutput.StdOut))
            {
                Assert.Equal("no_matches", missDocument.RootElement.GetProperty("reason").GetString());
                Assert.Equal(JsonValueKind.Array, missDocument.RootElement.GetProperty("matches").ValueKind);
                Assert.Empty(missDocument.RootElement.GetProperty("matches").EnumerateArray());
            }

            var hitOutput = await CliProcess.RunAsync($"find Greeter --root \"{workingDirectory}\" --json");
            Assert.Equal(0, hitOutput.ExitCode);
            using var hitDocument = JsonDocument.Parse(hitOutput.StdOut);
            Assert.True(hitDocument.RootElement.GetProperty("matches").GetArrayLength() > 0);
            Assert.False(hitDocument.RootElement.TryGetProperty("error", out _));
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }

    [Fact(Timeout = 90_000)]
    public async Task ImplJson_AmbiguousQuery_ReturnsCandidatesWithReason()
    {
        var workingDirectory = CopyFixture("CollidingProjects");
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);

            var output = await CliProcess.RunAsync($"impl Widget --root \"{workingDirectory}\" --json");
            Assert.Equal(2, output.ExitCode);

            using var document = JsonDocument.Parse(output.StdOut);
            Assert.Equal("ambiguous", document.RootElement.GetProperty("reason").GetString());
            Assert.True(document.RootElement.GetProperty("matches").GetArrayLength() > 1);
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }

    [Fact(Timeout = 90_000)]
    public async Task IndexForce_RebuildsDatabase()
    {
        var workingDirectory = CopyFixture("MultiProject");
        try
        {
            var indexer = new IncrementalCodeMapIndexer();
            await indexer.IndexAsync(workingDirectory, force: true, CancellationToken.None);
            var databasePath = Path.Combine(workingDirectory, ".codemap", "index.db");
            var firstIndexedAt = DateTimeOffset.Parse(await ReadIndexedAtAsync(databasePath));

            await indexer.IndexAsync(workingDirectory, force: true, CancellationToken.None);
            var secondIndexedAt = DateTimeOffset.Parse(await ReadIndexedAtAsync(databasePath));

            Assert.True(secondIndexedAt >= firstIndexedAt);
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }

    private static async Task<string> ReadIndexedAtAsync(string databasePath)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM metadata WHERE key = 'last_indexed_at_utc'";
        return Convert.ToString(await command.ExecuteScalarAsync()) ?? string.Empty;
    }

    private static string CopyFixture(string fixtureName)
    {
        var testDir = AppContext.BaseDirectory;
        var solutionRoot = Path.GetFullPath(Path.Combine(testDir, "..", "..", "..", "..", ".."));
        var source = Path.Combine(solutionRoot, "tests", "Fixtures", fixtureName);
        var destination = Path.Combine(Path.GetTempPath(), $"codemap-cli-{fixtureName}-" + Guid.NewGuid());
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*.*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var targetPath = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.Copy(file, targetPath, overwrite: true);
        }
        return destination;
    }

    private static void CleanUp(string workingDirectory)
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(workingDirectory))
            Directory.Delete(workingDirectory, recursive: true);
    }
}