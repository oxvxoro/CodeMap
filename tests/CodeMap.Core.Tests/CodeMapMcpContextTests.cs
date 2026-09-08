using System.Text.Json;
using CodeMap.Mcp;
using CodeMap.Storage;
using Microsoft.Data.Sqlite;

namespace CodeMap.Core.Tests;









[Collection("MsBuild")]
public sealed class CodeMapMcpContextTests
{
    [Fact]
    public async Task IsUpToDateAsync_InvalidatedDuringProbe_ReturnsStaleForCurrentCall()
    {
        var root = Path.Combine(Path.GetTempPath(), "codemap-mcp-freshness-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var context = new CodeMapMcpContext(root)
        {
            WatcherFactory = _ => throw new IOException("Simulated FileSystemWatcher construction failure."),
            FreshnessProbe = async (_, _) =>
            {
                entered.SetResult();
                return await completed.Task;
            }
        };
        try
        {
            var upToDate = context.IsUpToDateAsync(root, CancellationToken.None);
            await entered.Task;

            context.InvalidateRoot(root);
            completed.SetResult(true);

            Assert.False(await upToDate);
        }
        finally
        {
            context.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task IsUpToDateAsync_ReturnsCachedValue_UntilExplicitlyInvalidated()
    {
        var workingDirectory = CopyFixtureToTempDirectory();
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);
            var context = new CodeMapMcpContext(workingDirectory);

            Assert.True(await context.IsUpToDateAsync(workingDirectory, CancellationToken.None));




            var statePath = Path.Combine(workingDirectory, ".codemap", "state.json");
            var displacedStatePath = statePath + ".displaced";
            File.Move(statePath, displacedStatePath);
            Assert.True(await context.IsUpToDateAsync(workingDirectory, CancellationToken.None));



            context.InvalidateRoot(workingDirectory);
            Assert.False(await context.IsUpToDateAsync(workingDirectory, CancellationToken.None));

            File.Move(displacedStatePath, statePath);
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }

    [Fact(Timeout = 20_000)]
    public async Task IsUpToDateAsync_FileSystemChange_InvalidatesCacheAndRecomputes()
    {
        var workingDirectory = CopyFixtureToTempDirectory();
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);
            var context = new CodeMapMcpContext(workingDirectory);


            Assert.True(await context.IsUpToDateAsync(workingDirectory, CancellationToken.None));

            await File.AppendAllTextAsync(Path.Combine(workingDirectory, "ProjB", "Greeter.cs"), Environment.NewLine + "// changed");



            var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
            var sawStale = false;
            while (DateTimeOffset.UtcNow < deadline)
            {
                if (!await context.IsUpToDateAsync(workingDirectory, CancellationToken.None))
                {
                    sawStale = true;
                    break;
                }
                await Task.Delay(200);
            }

            Assert.True(sawStale, "Expected the file-system watcher to invalidate the cached freshness value after a tracked file changed.");
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }





    [Fact(Timeout = 20_000)]
    public async Task IsUpToDateAsync_RenameTrackedFileIntoIgnoredLocation_InvalidatesCache()
    {
        var workingDirectory = CopyFixtureToTempDirectory();
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);
            var context = new CodeMapMcpContext(workingDirectory);

            Assert.True(await context.IsUpToDateAsync(workingDirectory, CancellationToken.None));

            var trackedPath = Path.Combine(workingDirectory, "ProjB", "Greeter.cs");
            var ignoredDirectory = Path.Combine(workingDirectory, "ProjB", "bin");
            Directory.CreateDirectory(ignoredDirectory);
            File.Move(trackedPath, Path.Combine(ignoredDirectory, "Greeter.cs"));

            var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
            var sawStale = false;
            while (DateTimeOffset.UtcNow < deadline)
            {
                if (!await context.IsUpToDateAsync(workingDirectory, CancellationToken.None))
                {
                    sawStale = true;
                    break;
                }
                await Task.Delay(200);
            }

            Assert.True(sawStale, "Expected renaming a tracked file into an ignored location to invalidate the cached freshness value.");
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }

    [Fact]
    public async Task IsUpToDateAsync_WatcherConstructionFails_DoesNotCacheDurably()
    {
        var workingDirectory = CopyFixtureToTempDirectory();
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);
            var context = new CodeMapMcpContext(workingDirectory)
            {
                WatcherFactory = _ => throw new IOException("Simulated FileSystemWatcher construction failure.")
            };



            Assert.True(await context.IsUpToDateAsync(workingDirectory, CancellationToken.None));






            var statePath = Path.Combine(workingDirectory, ".codemap", "state.json");
            File.Move(statePath, statePath + ".displaced");
            Assert.False(await context.IsUpToDateAsync(workingDirectory, CancellationToken.None));

            File.Move(statePath + ".displaced", statePath);
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }

    [Fact]
    public void Dispose_IsSafe_WithEntriesThatNeverObtainedAWatcher()
    {
        var workingDirectory = CopyFixtureToTempDirectory();
        try
        {
            var context = new CodeMapMcpContext(workingDirectory)
            {
                WatcherFactory = _ => throw new IOException("Simulated FileSystemWatcher construction failure.")
            };



            var exception = Record.Exception(context.Dispose);
            Assert.Null(exception);
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }

    [Fact]
    public async Task FindSymbol_MultipleSolutionsDuringFreshnessCheck_ReturnsStaleIndexedResult()
    {
        var workingDirectory = CopyFixtureToTempDirectory();
        try
        {
            var solution = Path.Combine(workingDirectory, "MultiProject.slnx");
            await new IncrementalCodeMapIndexer().IndexAsync(solution, force: true, CancellationToken.None);
            File.Copy(solution, Path.Combine(workingDirectory, "Second.slnx"));

            var response = await CodeMapTools.FindSymbol(
                new CodeMapMcpContext(workingDirectory), "Greeter", workingDirectory, cancellationToken: CancellationToken.None);
            using var document = System.Text.Json.JsonDocument.Parse(response);

            Assert.True(document.RootElement.GetProperty("stale").GetBoolean());
            Assert.NotEmpty(document.RootElement.GetProperty("matches").EnumerateArray());
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }

    [Fact]
    public async Task FindSymbol_CancelledDuringFreshnessProbe_PropagatesAndIndexRemainsReusable()
    {
        var workingDirectory = CopyFixtureToTempDirectory();
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var context = new CodeMapMcpContext(workingDirectory)
            {
                FreshnessProbe = async (_, cancellationToken) =>
                {
                    entered.SetResult();
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                    return true;
                }
            };
            using var cancellation = new CancellationTokenSource();
            var find = CodeMapTools.FindSymbol(context, "Greeter", workingDirectory, cancellationToken: cancellation.Token);

            await entered.Task;
            await cancellation.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => find);

            var databasePath = Path.Combine(workingDirectory, ".codemap", "index.db");
            var graph = await new CodeMapQueryStore(databasePath).LoadAsync();
            Assert.Contains(graph.Symbols, symbol => symbol.Name == "Greeter");
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }

    [Fact]
    public async Task FindSymbol_FreshnessProbeVersionMismatch_PropagatesAndIndexRemainsReusable()
    {
        var workingDirectory = CopyFixtureToTempDirectory();
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);
            using var context = new CodeMapMcpContext(workingDirectory)
            {
                FreshnessProbe = (_, _) => Task.FromException<bool>(new InvalidOperationException("CodeMap index schema is outdated."))
            };

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                CodeMapTools.FindSymbol(context, "Greeter", workingDirectory, cancellationToken: CancellationToken.None));

            var databasePath = Path.Combine(workingDirectory, ".codemap", "index.db");
            var graph = await new CodeMapQueryStore(databasePath).LoadAsync();
            Assert.Contains(graph.Symbols, symbol => symbol.Name == "Greeter");
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }

    [Fact]
    public async Task FindSymbol_FreshnessProbeIOException_ReturnsStale()
    {
        var workingDirectory = CopyFixtureToTempDirectory();
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);
            using var context = new CodeMapMcpContext(workingDirectory)
            {
                FreshnessProbe = (_, _) => Task.FromException<bool>(new IOException("Simulated freshness probe I/O failure."))
            };

            var response = await CodeMapTools.FindSymbol(context, "Greeter", workingDirectory, cancellationToken: CancellationToken.None);
            using var document = JsonDocument.Parse(response);

            Assert.True(document.RootElement.GetProperty("stale").GetBoolean());
            Assert.NotEmpty(document.RootElement.GetProperty("matches").EnumerateArray());
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }









    [Fact]
    public async Task IsUpToDateAsync_AlreadyCancelledToken_PropagatesAndDoesNotCacheDurably()
    {
        var workingDirectory = CopyFixtureToTempDirectory();
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);
            var context = new CodeMapMcpContext(workingDirectory);

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => context.IsUpToDateAsync(workingDirectory, cancelled.Token));





            var statePath = Path.Combine(workingDirectory, ".codemap", "state.json");
            File.Move(statePath, statePath + ".displaced");
            Assert.False(await context.IsUpToDateAsync(workingDirectory, CancellationToken.None));

            File.Move(statePath + ".displaced", statePath);
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }
















    [Fact]
    public async Task RefreshIndex_FromSubdirectoryOfExistingIndex_UpdatesExistingRootNotANestedOne()
    {
        var workingDirectory = CopyFixtureToTempDirectory();
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);
            var projADirectory = Path.Combine(workingDirectory, "ProjA");

            await File.AppendAllTextAsync(Path.Combine(workingDirectory, "ProjB", "Greeter.cs"), Environment.NewLine + "// changed");

            var context = new CodeMapMcpContext(workingDirectory);
            await CodeMapTools.RefreshIndex(context, projADirectory, force: false, CancellationToken.None);

            Assert.False(Directory.Exists(Path.Combine(projADirectory, ".codemap")), "RefreshIndex from a subdirectory must not create a nested .codemap directory there.");

            var databasePath = Path.Combine(workingDirectory, ".codemap", "index.db");
            var graph = await new CodeMapQueryStore(databasePath).LoadAsync();
            Assert.Contains(graph.Symbols, s => s.Name == "Greeter");
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }




    [Fact]
    public async Task RefreshIndex_NoExistingIndex_FallsBackToCreatingOneAtRequestedRoot()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "codemap-mcp-refresh-noindex-" + Guid.NewGuid());
        Directory.CreateDirectory(workingDirectory);
        try
        {
            var testDir = AppContext.BaseDirectory;
            var solutionRoot = Path.GetFullPath(Path.Combine(testDir, "..", "..", "..", "..", ".."));
            var source = Path.Combine(solutionRoot, "tests", "Fixtures", "MultiProject");


            foreach (var file in Directory.EnumerateFiles(source, "*.*", SearchOption.AllDirectories)
                         .Where(file => !file.Contains(Path.DirectorySeparatorChar + ".codemap" + Path.DirectorySeparatorChar)))
            {
                var relative = Path.GetRelativePath(source, file);
                var targetPath = Path.Combine(workingDirectory, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                File.Copy(file, targetPath, overwrite: true);
            }

            var context = new CodeMapMcpContext(workingDirectory);
            await CodeMapTools.RefreshIndex(context, workingDirectory, force: false, CancellationToken.None);

            Assert.True(File.Exists(Path.Combine(workingDirectory, ".codemap", "index.db")));
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }

    [Fact]
    public async Task RefreshIndex_AlreadyCancelledToken_ThrowsAndDoesNotInvalidateOrCorruptState()
    {
        var workingDirectory = CopyFixtureToTempDirectory();
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);

            var statePath = Path.Combine(workingDirectory, ".codemap", "state.json");
            var originalState = await File.ReadAllTextAsync(statePath);


            await File.AppendAllTextAsync(Path.Combine(workingDirectory, "ProjB", "Greeter.cs"), Environment.NewLine + "// changed");

            var context = new CodeMapMcpContext(workingDirectory);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => CodeMapTools.RefreshIndex(context, workingDirectory, force: false, cancelled.Token));

            var stateAfterCancel = await File.ReadAllTextAsync(statePath);
            Assert.Equal(originalState, stateAfterCancel);

            var leftoverTempFiles = Directory.GetFiles(Path.Combine(workingDirectory, ".codemap"), "state.json.*.tmp");
            Assert.Empty(leftoverTempFiles);
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }

    [Fact(Timeout = 90_000)]
    public async Task SymbolResolvingTools_RejectAmbiguousMatches_AndPreserveUniqueResolution()
    {
        var workingDirectory = CopyCollisionFixtureToTempDirectory();
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);

            var databasePath = Path.Combine(workingDirectory, ".codemap", "index.db");
            var graph = await new CodeMapQueryStore(databasePath).LoadAsync();
            var resolution = new CodeMapQueryService(graph).ResolveSymbol("Widget", callableOnly: false, maxResults: 20);
            Assert.True(resolution.IsAmbiguous);
            Assert.Equal(2, resolution.Matches.Count);

            var context = new CodeMapMcpContext(workingDirectory);
            using var impact = JsonDocument.Parse(await CodeMapTools.GetImpact(
                context, "Widget", root: workingDirectory, cancellationToken: CancellationToken.None));
            AssertAmbiguous(impact, "Widget");

            using var flow = JsonDocument.Parse(await CodeMapTools.GetFlow(
                context, "Widget", root: workingDirectory, cancellationToken: CancellationToken.None));
            AssertAmbiguous(flow, "Widget");

            using var sourceRelation = JsonDocument.Parse(await CodeMapTools.ExplainRelation(
                context, "Widget", "CallB", root: workingDirectory, cancellationToken: CancellationToken.None));
            AssertAmbiguous(sourceRelation, "Widget");

            using var targetRelation = JsonDocument.Parse(await CodeMapTools.ExplainRelation(
                context, "CallB", "Widget", root: workingDirectory, cancellationToken: CancellationToken.None));
            AssertAmbiguous(targetRelation, "Widget");

            using var uniqueImpact = JsonDocument.Parse(await CodeMapTools.GetImpact(
                context, "CallB", root: workingDirectory, cancellationToken: CancellationToken.None));
            Assert.True(uniqueImpact.RootElement.TryGetProperty("symbol", out _), uniqueImpact.RootElement.ToString());
            Assert.False(uniqueImpact.RootElement.TryGetProperty("error", out _), uniqueImpact.RootElement.ToString());

            using var noMatchImpact = JsonDocument.Parse(await CodeMapTools.GetImpact(
                context, "MissingSymbol", root: workingDirectory, cancellationToken: CancellationToken.None));
            Assert.Equal("no_matches", noMatchImpact.RootElement.GetProperty("error").GetString());


            Assert.True(noMatchImpact.RootElement.TryGetProperty("stale", out var staleProperty), noMatchImpact.RootElement.ToString());
            Assert.Equal(JsonValueKind.False, staleProperty.ValueKind);
        }
        finally
        {
            CleanUp(workingDirectory);
        }

        static void AssertAmbiguous(JsonDocument document, string query)
        {
            Assert.Equal("ambiguous", document.RootElement.GetProperty("error").GetString());
            Assert.Equal(query, document.RootElement.GetProperty("query").GetString());
            Assert.True(document.RootElement.TryGetProperty("stale", out _));
        }
    }





    [Fact(Timeout = 120_000)]
    public async Task GetFlow_IncludeEvidenceDefaultsTrue_AndFalseOmitsEvidenceButKeepsTopology()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "codemap-mcp-flow-evidence-" + Guid.NewGuid());
        var source = FixtureRestore.EnsureRestored("AspNetFixture");
        Directory.CreateDirectory(workingDirectory);
        foreach (var file in Directory.EnumerateFiles(source, "*.*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var targetPath = Path.Combine(workingDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.Copy(file, targetPath, overwrite: true);
        }
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);
            var context = new CodeMapMcpContext(workingDirectory);

            using var defaultResponse = JsonDocument.Parse(await CodeMapTools.GetFlow(
                context, "GET /orders/{id}", root: workingDirectory, cancellationToken: CancellationToken.None));
            using var evidenceOnResponse = JsonDocument.Parse(await CodeMapTools.GetFlow(
                context, "GET /orders/{id}", root: workingDirectory, includeEvidence: true, cancellationToken: CancellationToken.None));
            using var evidenceOffResponse = JsonDocument.Parse(await CodeMapTools.GetFlow(
                context, "GET /orders/{id}", root: workingDirectory, includeEvidence: false, cancellationToken: CancellationToken.None));

            var defaultRelations = defaultResponse.RootElement.GetProperty("relations").EnumerateArray().ToArray();
            var evidenceOnRelations = evidenceOnResponse.RootElement.GetProperty("relations").EnumerateArray().ToArray();
            var evidenceOffRelations = evidenceOffResponse.RootElement.GetProperty("relations").EnumerateArray().ToArray();

            Assert.NotEmpty(defaultRelations);
            Assert.Equal(defaultRelations.Length, evidenceOnRelations.Length);
            Assert.Equal(defaultRelations.Length, evidenceOffRelations.Length);

            for (var i = 0; i < defaultRelations.Length; i++)
            {

                Assert.True(defaultRelations[i].TryGetProperty("location", out _));
                Assert.True(defaultRelations[i].TryGetProperty("evidence", out _));
                Assert.Equal(evidenceOnRelations[i].GetProperty("sourceId").GetString(), defaultRelations[i].GetProperty("sourceId").GetString());
                Assert.Equal(evidenceOnRelations[i].GetProperty("targetId").GetString(), defaultRelations[i].GetProperty("targetId").GetString());
                Assert.Equal(evidenceOnRelations[i].GetProperty("evidence").GetString(), defaultRelations[i].GetProperty("evidence").GetString());


                var lean = evidenceOffRelations[i];
                Assert.False(lean.TryGetProperty("location", out _), lean.ToString());
                Assert.False(lean.TryGetProperty("evidence", out _), lean.ToString());
                Assert.Equal(defaultRelations[i].GetProperty("sourceId").GetString(), lean.GetProperty("sourceId").GetString());
                Assert.Equal(defaultRelations[i].GetProperty("targetId").GetString(), lean.GetProperty("targetId").GetString());
                Assert.Equal(defaultRelations[i].GetProperty("depth").GetInt32(), lean.GetProperty("depth").GetInt32());
                Assert.Equal(defaultRelations[i].GetProperty("edgeKind").GetString(), lean.GetProperty("edgeKind").GetString());
                Assert.Equal(defaultRelations[i].GetProperty("confidence").GetDouble(), lean.GetProperty("confidence").GetDouble());
            }
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }

    private static string CopyFixtureToTempDirectory()
    {
        var testDir = AppContext.BaseDirectory;
        var solutionRoot = Path.GetFullPath(Path.Combine(testDir, "..", "..", "..", "..", ".."));
        var source = Path.Combine(solutionRoot, "tests", "Fixtures", "MultiProject");
        var destination = Path.Combine(Path.GetTempPath(), "codemap-mcp-freshness-" + Guid.NewGuid());
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

    private static string CopyCollisionFixtureToTempDirectory()
    {
        var testDir = AppContext.BaseDirectory;
        var solutionRoot = Path.GetFullPath(Path.Combine(testDir, "..", "..", "..", "..", ".."));
        var source = Path.Combine(solutionRoot, "tests", "Fixtures", "CollidingProjectsWithReference");
        var destination = Path.Combine(Path.GetTempPath(), "codemap-mcp-ambiguity-" + Guid.NewGuid());
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
