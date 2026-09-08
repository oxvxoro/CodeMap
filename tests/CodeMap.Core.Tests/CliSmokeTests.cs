using System.Text.Json;
using CodeMap.Scip.Protocol;
using CodeMap.Storage;
using Google.Protobuf;
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
    public async Task SliceJson_UsesFreshIndexAndReturnsSemanticItems()
    {
        var workingDirectory = CopyFixture("MultiProject");
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);

            var output = await CliProcess.RunAsync($"slice Fixture.ProjB.Greeter.Greet --root \"{workingDirectory}\" --include-source --json");

            Assert.Equal(0, output.ExitCode);
            using var document = JsonDocument.Parse(output.StdOut);
            Assert.Equal(1, document.RootElement.GetProperty("version").GetInt32());
            Assert.Equal("backward", document.RootElement.GetProperty("direction").GetString());
            Assert.Contains("Greet", document.RootElement.GetProperty("scope").GetProperty("displayName").GetString());
            Assert.Equal(JsonValueKind.Array, document.RootElement.GetProperty("items").ValueKind);
            Assert.Contains("Greet", document.RootElement.GetProperty("source").GetString());
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }

    [Fact(Timeout = 90_000)]
    public async Task StatusJson_ReturnsReadOnlyIndexMetadata()
    {
        var workingDirectory = CopyFixture("MultiProject");
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);

            var output = await CliProcess.RunAsync($"status \"{workingDirectory}\" --json --check-freshness");

            Assert.Equal(0, output.ExitCode);
            using var document = JsonDocument.Parse(output.StdOut);
            Assert.Equal(1, document.RootElement.GetProperty("version").GetInt32());
            Assert.Equal("ready", document.RootElement.GetProperty("indexState").GetString());
            Assert.False(document.RootElement.GetProperty("schemaOutdated").GetBoolean());
            Assert.False(document.RootElement.GetProperty("analyzerVersionsOutdated").GetBoolean());
            Assert.True(document.RootElement.GetProperty("symbols").GetInt32() > 0);
            Assert.True(document.RootElement.GetProperty("edges").GetInt32() > 0);
            Assert.True(document.RootElement.GetProperty("freshnessChecked").GetBoolean());
            Assert.False(document.RootElement.GetProperty("stale").GetBoolean());

            var textOutput = await CliProcess.RunAsync($"status \"{workingDirectory}\" --check-freshness");
            Assert.Equal(0, textOutput.ExitCode);
            Assert.Contains("State: ready", textOutput.StdOut);
            Assert.Contains("Analyzers: current", textOutput.StdOut);
            Assert.Contains("Freshness: current", textOutput.StdOut);
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }

    [Fact(Timeout = 15_000)]
    public async Task StatusJson_NoIndex_ReturnsErrorEnvelope()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "codemap-cli-status-noindex-" + Guid.NewGuid());
        Directory.CreateDirectory(workingDirectory);
        try
        {
            var output = await CliProcess.RunAsync($"status \"{workingDirectory}\" --json");

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
    public async Task SliceJson_ForwardDirectionUsesV1Envelope()
    {
        var workingDirectory = CopyFixture("MultiProject");
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);

            var output = await CliProcess.RunAsync($"slice Fixture.ProjB.Greeter.Greet --direction forward --root \"{workingDirectory}\" --json");

            Assert.Equal(0, output.ExitCode);
            using var document = JsonDocument.Parse(output.StdOut);
            Assert.Equal(1, document.RootElement.GetProperty("version").GetInt32());
            Assert.Equal(JsonValueKind.Array, document.RootElement.GetProperty("items").ValueKind);
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }

    [Fact(Timeout = 90_000)]
    public async Task SliceJson_StaleIndexUsesSemanticSliceErrorEnvelope()
    {
        var workingDirectory = CopyFixture("MultiProject");
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);
            await File.AppendAllTextAsync(Path.Combine(workingDirectory, "ProjB", "Greeter.cs"), "\n// stale");

            var output = await CliProcess.RunAsync($"slice Fixture.ProjB.Greeter.Greet --root \"{workingDirectory}\" --json");

            Assert.Equal(1, output.ExitCode);
            using var document = JsonDocument.Parse(output.StdOut);
            Assert.Equal(1, document.RootElement.GetProperty("version").GetInt32());
            Assert.Equal("semantic_slice_stale_index", document.RootElement.GetProperty("error").GetProperty("code").GetString());
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }

    [Fact(Timeout = 90_000)]
    public async Task SliceJson_AmbiguousQueryReturnsV1ErrorEnvelope()
    {
        var workingDirectory = CopyFixture("CollidingProjects");
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);
            var output = await CliProcess.RunAsync($"slice Widget --root \"{workingDirectory}\" --json");

            Assert.Equal(1, output.ExitCode);
            using var document = JsonDocument.Parse(output.StdOut);
            Assert.Equal(1, document.RootElement.GetProperty("version").GetInt32());
            Assert.Equal("ambiguous", document.RootElement.GetProperty("error").GetProperty("code").GetString());
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }

    [Fact(Timeout = 90_000)]
    public async Task ScipCli_ImportListAndRemoveUseV1Lifecycle()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "codemap-cli-scip-" + Guid.NewGuid());
        Directory.CreateDirectory(workingDirectory);
        var artifactPath = Path.Combine(workingDirectory, "app.scip");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(workingDirectory, "app.py"), "def run():\n    return 1\n");
            var index = new CodeMap.Scip.Protocol.Index
            {
                Documents = { new Document
                {
                    RelativePath = "app.py", Language = "python",
                    Symbols = { new SymbolInformation { Symbol = "run", DisplayName = "run", Kind = 17 } },
                    Occurrences = { new Occurrence { Symbol = "run", SymbolRoles = 1, SingleLineRange = new SingleLineRange { Line = 0, StartCharacter = 4, EndCharacter = 7 } } }
                } }
            };
            await File.WriteAllBytesAsync(artifactPath, index.ToByteArray());
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);

            var imported = await CliProcess.RunAsync($"scip import \"{artifactPath}\" --name py --root \"{workingDirectory}\" --json");
            Assert.Equal(0, imported.ExitCode);
            using (var document = JsonDocument.Parse(imported.StdOut))
            {
                Assert.Equal(1, document.RootElement.GetProperty("version").GetInt32());
                Assert.Equal("scip:py", document.RootElement.GetProperty("import").GetProperty("project").GetString());
            }

            var listed = await CliProcess.RunAsync($"scip list --root \"{workingDirectory}\" --json");
            Assert.Equal(0, listed.ExitCode);
            using (var document = JsonDocument.Parse(listed.StdOut))
                Assert.Contains(document.RootElement.GetProperty("providers").EnumerateArray(), item => item.GetProperty("name").GetString() == "py");

            var removed = await CliProcess.RunAsync($"scip remove py --root \"{workingDirectory}\"");
            Assert.Equal(0, removed.ExitCode);
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
