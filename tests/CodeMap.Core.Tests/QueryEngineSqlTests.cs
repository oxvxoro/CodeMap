using CodeMap.Core.Models;
using CodeMap.Storage;
using Microsoft.Data.Sqlite;

namespace CodeMap.Core.Tests;

[Collection("MsBuild")]
public sealed class QueryEngineSqlTests
{
    [Fact]
    public async Task SqlBackedSingleHopQueries_MatchSnapshotResultsAndHonorLimits()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "codemap-query-sql-" + Guid.NewGuid());
        CopyFixture(GetFixtureDirectory(), workingDirectory);
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);

            var databasePath = Path.Combine(workingDirectory, ".codemap", "index.db");
            var store = new CodeMapQueryStore(databasePath);
            var graph = await store.LoadAsync();
            var snapshotService = new CodeMapQueryService(graph);
            var greet = Assert.Single(graph.Symbols, symbol => symbol.Name == "Greet");
            var call = Assert.Single(graph.Symbols, symbol => symbol.Name == "Call");
            var greeter = Assert.Single(graph.Symbols, symbol => symbol.Name == "Greeter");
            var interfaceSymbol = Assert.Single(graph.Symbols, symbol => symbol.Name == "IGreeter");

            var connection = await store.OpenReadOnlyConnectionAsync();
            await using (var sqlService = new CodeMapQueryService(graph, connection))
            {
                Assert.Equal(
                    snapshotService.ReferencedBy(greeter, 20).Select(symbol => symbol.Id),
                    sqlService.ReferencedBy(greeter, 20).Select(symbol => symbol.Id));
                Assert.Equal(
                    snapshotService.Callers(greet, 20).Select(symbol => symbol.Id),
                    sqlService.Callers(greet, 20).Select(symbol => symbol.Id));
                Assert.Equal(
                    snapshotService.Implementations(interfaceSymbol, 20).Select(symbol => symbol.Id),
                    sqlService.Implementations(interfaceSymbol, 20).Select(symbol => symbol.Id));

                Assert.Equal(
                    snapshotService.Callees(call, 2, 20).Select(symbol => symbol.Id),
                    sqlService.Callees(call, 2, 20).Select(symbol => symbol.Id));
                Assert.Equal(
                    new[] { greet.Id },
                    sqlService.Callees(call, 1, 20).Select(symbol => symbol.Id));
                Assert.Equal(
                    snapshotService.Callees(call, 3, 20).Select(symbol => symbol.Id),
                    sqlService.Callees(call, 3, 20).Select(symbol => symbol.Id));
                Assert.Equal(
                    snapshotService.Callees(call, 2, 1).Select(symbol => symbol.Id),
                    sqlService.Callees(call, 2, 1).Select(symbol => symbol.Id));

                Assert.Contains(sqlService.ReferencedBy(greeter, 20), symbol => symbol.Name == "Caller");
                Assert.Contains(sqlService.ReferencedBy(greeter, 20), symbol => symbol.Name == "CallAgain");
                Assert.Contains(sqlService.Callers(greet, 20), symbol => symbol.Name == "Call");
                Assert.Contains(sqlService.Callers(greet, 20), symbol => symbol.Name == "CallAgain");
                Assert.Contains(sqlService.Implementations(interfaceSymbol, 20), symbol => symbol.Name == "Greeter");
                Assert.Contains(sqlService.Implementations(interfaceSymbol, 20), symbol => symbol.Name == "AlternateGreeter");
                Assert.Contains(sqlService.Implementations(interfaceSymbol, 20), symbol => symbol.Name == "Caller");

                Assert.Equal(
                    snapshotService.ReferencedBy(greeter, 1).Select(symbol => symbol.Id),
                    sqlService.ReferencedBy(greeter, 1).Select(symbol => symbol.Id));
                Assert.Equal(
                    snapshotService.Callers(greet, 1).Select(symbol => symbol.Id),
                    sqlService.Callers(greet, 1).Select(symbol => symbol.Id));
                Assert.Equal(
                    snapshotService.Implementations(interfaceSymbol, 1).Select(symbol => symbol.Id),
                    sqlService.Implementations(interfaceSymbol, 1).Select(symbol => symbol.Id));
                Assert.Empty(sqlService.Callers(interfaceSymbol, 20));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(workingDirectory))
                Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ConnectionOnlyQueries_MatchSnapshotFindResolveAndMembers()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "codemap-query-connection-only-" + Guid.NewGuid());
        CopyFixture(GetFixtureDirectory(), workingDirectory);
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);

            var databasePath = Path.Combine(workingDirectory, ".codemap", "index.db");
            var store = new CodeMapQueryStore(databasePath);
            var graph = await store.LoadAsync();
            var snapshotService = new CodeMapQueryService(graph);
            var greeter = Assert.Single(graph.Symbols, symbol => symbol.Name == "Greeter");

            var connection = await store.OpenReadOnlyConnectionAsync();
            await using var connectionOnlyService = new CodeMapQueryService(connection);

            Assert.Equal(
                snapshotService.Find("Greeter", 10).Select(symbol => symbol.Id),
                connectionOnlyService.Find("Greeter", 10).Select(symbol => symbol.Id));
            Assert.Equal(
                snapshotService.ResolveSymbol("Greeter", callableOnly: false, maxResults: 10).Matches.Select(symbol => symbol.Id),
                connectionOnlyService.ResolveSymbol("Greeter", callableOnly: false, maxResults: 10).Matches.Select(symbol => symbol.Id));
            Assert.Equal(
                snapshotService.Members(greeter, 20).Select(symbol => symbol.Id),
                connectionOnlyService.Members(greeter, 20).Select(symbol => symbol.Id));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(workingDirectory))
                Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task SymbolsInFiles_SqlBackedLargePathSet_MatchesSnapshotOrderingLimitAndDeduplication()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "codemap-symbols-in-files-chunking-" + Guid.NewGuid());
        CopyFixture(GetFixtureDirectory(), workingDirectory);
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);
            var databasePath = Path.Combine(workingDirectory, ".codemap", "index.db");
            var store = new CodeMapQueryStore(databasePath);
            var graph = await store.LoadAsync();
            var snapshotService = new CodeMapQueryService(graph);
            await using var sqlService = new CodeMapQueryService(await store.OpenReadOnlyConnectionAsync());

            var paths = new[] { "Greeter.cs" }
                .Concat(Enumerable.Range(0, CodeMapQueryService.SqliteVariableChunkSize - 1)
                    .Select(index => $"unmatched/path-{index}.cs"))
                .Append("Caller.cs")
                .Append("ProjB/Greeter.cs")
                .ToArray();

            var expected = snapshotService.SymbolsInFiles(paths, maxResults: 1).Select(symbol => symbol.Id).ToArray();
            var actual = sqlService.SymbolsInFiles(paths, maxResults: 1).Select(symbol => symbol.Id).ToArray();
            var allExpected = snapshotService.SymbolsInFiles(paths, maxResults: 20).Select(symbol => symbol.Id).ToArray();
            var allActual = sqlService.SymbolsInFiles(paths, maxResults: 20).Select(symbol => symbol.Id).ToArray();

            Assert.NotEmpty(expected);
            Assert.Equal(expected, actual);
            Assert.Single(actual);
            Assert.Equal(allExpected, allActual);
            Assert.Equal(allActual.Length, allActual.Distinct(StringComparer.Ordinal).Count());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(workingDirectory))
                Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task SymbolsInFiles_LargeBatchCanOverlapIndexUpdate()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "codemap-symbols-in-files-concurrency-" + Guid.NewGuid());
        CopyFixture(GetFixtureDirectory(), workingDirectory);
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);
            var databasePath = Path.Combine(workingDirectory, ".codemap", "index.db");
            var store = new CodeMapQueryStore(databasePath);
            await File.AppendAllTextAsync(Path.Combine(workingDirectory, "ProjB", "Greeter.cs"), Environment.NewLine + "// changed");
            await using var service = new CodeMapQueryService(await store.OpenReadOnlyConnectionAsync());

            var paths = Enumerable.Range(0, CodeMapQueryService.SqliteVariableChunkSize * 20)
                .Select(index => $"unmatched/path-{index}.cs")
                .ToArray();
            var queryTask = Task.Run(() => service.SymbolsInFiles(paths, maxResults: 1));
            var updateTask = new IncrementalCodeMapIndexer().UpdateAsync(workingDirectory, CancellationToken.None);

            await Task.WhenAll(queryTask, updateTask);
            var queryResult = await queryTask;
            var updateResult = await updateTask;

            Assert.Empty(queryResult);
            Assert.Equal(1, updateResult.Updated);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(workingDirectory))
                Directory.Delete(workingDirectory, recursive: true);
        }
    }







    [Theory]
    [InlineData("Greeter")]
    [InlineData("Gree")]
    [InlineData("reet")]
    public async Task Find_TieredSql_MatchesSnapshotAcrossExactPrefixAndContainsTiers(string query)
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "codemap-query-find-tiers-" + Guid.NewGuid());
        CopyFixture(GetFixtureDirectory(), workingDirectory);
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);

            var databasePath = Path.Combine(workingDirectory, ".codemap", "index.db");
            var store = new CodeMapQueryStore(databasePath);
            var graph = await store.LoadAsync();
            var snapshotService = new CodeMapQueryService(graph);

            var connection = await store.OpenReadOnlyConnectionAsync();
            await using var connectionOnlyService = new CodeMapQueryService(connection);

            var expected = snapshotService.Find(query, 20).Select(symbol => symbol.Id).ToArray();
            var actual = connectionOnlyService.Find(query, 20).Select(symbol => symbol.Id).ToArray();
            Assert.NotEmpty(expected);
            Assert.Equal(expected, actual);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(workingDirectory))
                Directory.Delete(workingDirectory, recursive: true);
        }
    }









    [Fact]
    public async Task Find_ContainsTier_ShortQueryBelowTrigramMinimum_StillMatchesSnapshot()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "codemap-find-contains-short-" + Guid.NewGuid());
        CopyFixture(GetFixtureDirectory(), workingDirectory);
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);
            var databasePath = Path.Combine(workingDirectory, ".codemap", "index.db");
            var store = new CodeMapQueryStore(databasePath);
            var graph = await store.LoadAsync();
            var snapshotService = new CodeMapQueryService(graph);
            var connection = await store.OpenReadOnlyConnectionAsync();
            await using var connectionOnlyService = new CodeMapQueryService(connection);

            var expected = snapshotService.Find("re", 20).Select(symbol => symbol.Id).ToArray();
            var actual = connectionOnlyService.Find("re", 20).Select(symbol => symbol.Id).ToArray();
            Assert.NotEmpty(expected);
            Assert.Equal(expected, actual);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(workingDirectory))
                Directory.Delete(workingDirectory, recursive: true);
        }
    }








    [Theory]
    [InlineData("Gree%ter")]
    [InlineData("Gree_ter")]
    [InlineData("Gree\\ter")]
    [InlineData("Gree\"ter")]
    public async Task Find_ContainsTier_LikeAndFtsSpecialCharacters_TreatedAsLiteralNotWildcard(string literalQuery)
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "codemap-find-contains-literal-" + Guid.NewGuid());
        CopyFixture(GetFixtureDirectory(), workingDirectory);
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);
            var databasePath = Path.Combine(workingDirectory, ".codemap", "index.db");
            var store = new CodeMapQueryStore(databasePath);
            var graph = await store.LoadAsync();
            var snapshotService = new CodeMapQueryService(graph);
            var connection = await store.OpenReadOnlyConnectionAsync();
            await using var connectionOnlyService = new CodeMapQueryService(connection);






            var expected = snapshotService.Find(literalQuery, 20).Select(symbol => symbol.Id).ToArray();
            var actual = connectionOnlyService.Find(literalQuery, 20).Select(symbol => symbol.Id).ToArray();
            Assert.Empty(expected);
            Assert.Equal(expected, actual);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(workingDirectory))
                Directory.Delete(workingDirectory, recursive: true);
        }
    }







    [Fact]
    public async Task Find_ContainsTier_Fts5Path_IsCaseInsensitive()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "codemap-find-contains-case-" + Guid.NewGuid());
        CopyFixture(GetFixtureDirectory(), workingDirectory);
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);
            var databasePath = Path.Combine(workingDirectory, ".codemap", "index.db");
            var store = new CodeMapQueryStore(databasePath);
            var connection = await store.OpenReadOnlyConnectionAsync();
            await using var connectionOnlyService = new CodeMapQueryService(connection);



            var upper = connectionOnlyService.Find("REET", 20).Select(symbol => symbol.Id).ToArray();
            var lower = connectionOnlyService.Find("reet", 20).Select(symbol => symbol.Id).ToArray();
            Assert.NotEmpty(upper);
            Assert.Equal(lower, upper);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(workingDirectory))
                Directory.Delete(workingDirectory, recursive: true);
        }
    }







    [Fact]
    public async Task ContainsSearchIndex_SchemaObjectsExist_AndBackfillCoversPreExistingSymbols()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "codemap-fts-schema-" + Guid.NewGuid());
        CopyFixture(GetFixtureDirectory(), workingDirectory);
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);
            var databasePath = Path.Combine(workingDirectory, ".codemap", "index.db");
            var store = new CodeMapQueryStore(databasePath);

            var connection = await store.OpenReadOnlyConnectionAsync();
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT name FROM sqlite_master WHERE type IN ('table', 'trigger') AND name IN " +
                    "('symbols_fts', 'symbols_fts_ai', 'symbols_fts_ad', 'symbols_fts_au')";
                using var reader = command.ExecuteReader();
                var found = new HashSet<string>(StringComparer.Ordinal);
                while (reader.Read())
                    found.Add(reader.GetString(0));
                Assert.Equal(
                    new[] { "symbols_fts", "symbols_fts_ad", "symbols_fts_ai", "symbols_fts_au" },
                    found.OrderBy(name => name, StringComparer.Ordinal));
            }





            await using var connectionOnlyService = new CodeMapQueryService(connection);
            Assert.NotEmpty(connectionOnlyService.Find("reet", 20));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(workingDirectory))
                Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task SqlBackedImpact_MatchesSnapshotIncludingConnectingEdgesAcrossDepths()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "codemap-query-impact-sql-" + Guid.NewGuid());
        CopyFixture(GetFixtureDirectory(), workingDirectory);
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);

            var databasePath = Path.Combine(workingDirectory, ".codemap", "index.db");
            var store = new CodeMapQueryStore(databasePath);
            var graph = await store.LoadAsync();
            var snapshotService = new CodeMapQueryService(graph);
            var interfaceSymbol = Assert.Single(graph.Symbols, symbol => symbol.Name == "IGreeter");
            var greeter = Assert.Single(graph.Symbols, symbol => symbol.Name == "Greeter");

            var connection = await store.OpenReadOnlyConnectionAsync();
            await using (var sqlService = new CodeMapQueryService(graph, connection))
            {
                var snapshotImpact = snapshotService.Impact(interfaceSymbol, 2, 20);
                var sqlImpact = sqlService.Impact(interfaceSymbol, 2, 20);

                Assert.Contains(snapshotImpact, item => item.Depth == 2 && item.Via.TargetId == greeter.Id);
                Assert.Contains(sqlImpact, item => item.Symbol.Name == "Call" && item.Depth == 2 && item.Via.TargetId == greeter.Id);
                Assert.Contains(sqlImpact, item => item.Symbol.Name == "CallAgain" && item.Depth == 2 && item.Via.TargetId == greeter.Id);
                Assert.Equal(
                    snapshotImpact
                        .Select(ToImpactTuple)
                        .OrderBy(item => item.Depth)
                        .ThenBy(item => item.SymbolId, StringComparer.Ordinal)
                        .ThenBy(item => item.SourceId, StringComparer.Ordinal)
                        .ThenBy(item => item.TargetId, StringComparer.Ordinal)
                        .ThenBy(item => item.Kind)
                        .ThenBy(item => item.SourceFileId, StringComparer.Ordinal)
                        .ThenBy(item => item.Line),
                    sqlImpact
                        .Select(ToImpactTuple)
                        .OrderBy(item => item.Depth)
                        .ThenBy(item => item.SymbolId, StringComparer.Ordinal)
                        .ThenBy(item => item.SourceId, StringComparer.Ordinal)
                        .ThenBy(item => item.TargetId, StringComparer.Ordinal)
                        .ThenBy(item => item.Kind)
                        .ThenBy(item => item.SourceFileId, StringComparer.Ordinal)
                        .ThenBy(item => item.Line));

                Assert.Equal(
                    sqlImpact,
                    sqlImpact
                        .OrderBy(item => item.Depth)
                        .ThenBy(item => item.Symbol.QualifiedName, StringComparer.Ordinal)
                        .ThenBy(item => item.Symbol.Id, StringComparer.Ordinal));
                Assert.Equal(2, sqlImpact.Max(item => item.Depth));

                var limitedImpact = sqlService.Impact(interfaceSymbol, 2, 1);
                Assert.Single(limitedImpact);
                Assert.Equal(sqlImpact[0], limitedImpact[0]);
                Assert.Empty(sqlService.Impact(interfaceSymbol, 0, 20));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(workingDirectory))
                Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ProjectScopedMapLoad_ExcludesOtherProjectButUnscopedLoadIncludesBoth()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "codemap-map-scope-" + Guid.NewGuid());
        CopyFixture(GetFixtureDirectory(), workingDirectory);
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);

            var store = new CodeMapQueryStore(Path.Combine(workingDirectory, ".codemap", "index.db"));
            var scoped = await store.LoadProjectScopedAsync("proja");
            var scopedMap = new CodeMapQueryService(scoped).BuildMap(null, "ProjA", 500);
            var full = await store.LoadAsync();
            var fullMap = new CodeMapQueryService(full).BuildMap(null, null, 500);

            Assert.NotEmpty(scoped.Files);
            Assert.All(scoped.Files, file => Assert.Equal("ProjA", file.Project));
            Assert.All(scoped.Symbols, symbol => Assert.Equal("ProjA", symbol.Project));
            Assert.Contains("Caller", scopedMap.Text);
            Assert.Contains("SecondCaller", scopedMap.Text);
            Assert.Contains("Call", scopedMap.Text);
            Assert.Contains("CallAgain", scopedMap.Text);
            Assert.DoesNotContain("Greeter", scopedMap.Text);
            Assert.DoesNotContain("IGreeter", scopedMap.Text);
            Assert.DoesNotContain("AlternateGreeter", scopedMap.Text);
            Assert.DoesNotContain("Greet", scopedMap.Text);
            Assert.DoesNotContain("GetGreeting", scopedMap.Text);
            Assert.Contains("Caller", fullMap.Text);
            Assert.Contains("Greeter", fullMap.Text);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(workingDirectory))
                Directory.Delete(workingDirectory, recursive: true);
        }
    }

    private static (string SymbolId, string SourceId, string TargetId, EdgeKind Kind, string? SourceFileId, int? Line, int Depth)
        ToImpactTuple(ImpactItem item) =>
        (item.Symbol.Id, item.Via.SourceId, item.Via.TargetId, item.Via.Kind, item.Via.SourceFileId, item.Via.Line, item.Depth);

    [Fact]
    public async Task RelationsEvidence_ParityBetweenSnapshotAndSql()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "codemap-relations-evidence-" + Guid.NewGuid());
        CopyFixture(GetWebFixtureDirectory(), workingDirectory);
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);
            var databasePath = Path.Combine(workingDirectory, ".codemap", "index.db");
            var store = new CodeMapQueryStore(databasePath);
            var graph = await store.LoadAsync();
            var snapshotService = new CodeMapQueryService(graph);
            var bind = Assert.Single(graph.Symbols, symbol => symbol.Name == "bind" && symbol.RelativePath == "app.ts");
            var save = Assert.Single(graph.Symbols, symbol => symbol.Name == "save" && symbol.RelativePath == "save.ts");

            var snapshotRelations = snapshotService.Relations(bind.Id, save.Id, EdgeKind.Calls, 20, 0);
            var connection = await store.OpenReadOnlyConnectionAsync();
            await using var sqlService = new CodeMapQueryService(graph, connection);
            var sqlRelations = sqlService.Relations(bind.Id, save.Id, EdgeKind.Calls, 20, 0);

            Assert.Equal(
                snapshotRelations.Select(item => (item.Evidence.Evidence, item.Evidence.File, item.Evidence.Line)),
                sqlRelations.Select(item => (item.Evidence.Evidence, item.Evidence.File, item.Evidence.Line)));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(workingDirectory))
                Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Flow_SqlBackedAndSnapshot_MatchAcrossDepthKindAndConfidence()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "codemap-flow-sql-" + Guid.NewGuid());
        CopyFixture(FixtureRestore.EnsureRestored("AspNetFixture"), workingDirectory);
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);
            var databasePath = Path.Combine(workingDirectory, ".codemap", "index.db");
            var store = new CodeMapQueryStore(databasePath);
            var graph = await store.LoadAsync();
            var snapshotService = new CodeMapQueryService(graph);
            var route = Assert.Single(graph.Symbols, symbol => symbol.QualifiedName == "GET /orders/{id}");

            var connection = await store.OpenReadOnlyConnectionAsync();
            await using var sqlService = new CodeMapQueryService(graph, connection);

            foreach (var depth in new[] { 1, 4, 8 })
            {
                var snapshotFlow = snapshotService.Flow(route, "http", depth, 100, 0);
                var sqlFlow = sqlService.Flow(route, "http", depth, 100, 0);




                var snapshotSet = snapshotFlow.Select(item => (item.Symbol.Id, item.Via.SourceId, item.Via.TargetId, item.Via.Kind, item.Depth)).ToHashSet();
                var sqlSet = sqlFlow.Select(item => (item.Symbol.Id, item.Via.SourceId, item.Via.TargetId, item.Via.Kind, item.Depth)).ToHashSet();
                Assert.Equal(snapshotSet, sqlSet);
            }



            var httpFlow = sqlService.Flow(route, "http", 4, 100, 0);
            Assert.All(httpFlow, item => Assert.DoesNotContain(item.Via.Kind, new[] { EdgeKind.Renders, EdgeKind.BindsTo, EdgeKind.HandlesEvent, EdgeKind.UsesViewModel }));

            var component = Assert.Single(graph.Symbols, symbol => symbol.Kind == NodeKind.RazorComponent && symbol.RelativePath.Contains("OrderForm.razor", StringComparison.Ordinal));
            var uiFlow = sqlService.Flow(component, "ui", 4, 100, 0);
            Assert.All(uiFlow, item => Assert.DoesNotContain(item.Via.Kind, new[] { EdgeKind.RoutesTo, EdgeKind.Registers, EdgeKind.ResolvesTo }));
            Assert.Contains(uiFlow, item => item.Via.Kind == EdgeKind.Renders);

            var allFlow = sqlService.Flow(component, "all", 4, 100, 0);
            Assert.True(allFlow.Count >= uiFlow.Count);



            var highConfidenceFlow = sqlService.Flow(component, "ui", 4, 100, 0.95);
            Assert.DoesNotContain(highConfidenceFlow, item => item.Via.Kind is EdgeKind.Renders or EdgeKind.BindsTo or EdgeKind.HandlesEvent);
            var zeroConfidenceFlow = sqlService.Flow(component, "ui", 4, 100, 0);
            Assert.Contains(zeroConfidenceFlow, item => item.Via.Kind == EdgeKind.Renders);



            Assert.Throws<ArgumentException>(() => sqlService.Flow(route, "bogus", 4, 10, 0));
            var clampedDepth = sqlService.Flow(route, "http", 100, 100, 0);
            var depthEightFlow = sqlService.Flow(route, "http", 8, 100, 0);
            Assert.Equal(depthEightFlow.Select(item => item.Symbol.Id), clampedDepth.Select(item => item.Symbol.Id));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(workingDirectory))
                Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Flow_DiSelectedImplementation_ExcludesUnregisteredImplementer()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "codemap-flow-di-" + Guid.NewGuid());
        CopyFixture(FixtureRestore.EnsureRestored("AspNetFixture"), workingDirectory);
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);
            var databasePath = Path.Combine(workingDirectory, ".codemap", "index.db");
            var store = new CodeMapQueryStore(databasePath);
            var graph = await store.LoadAsync();
            var snapshotService = new CodeMapQueryService(graph);
            var connection = await store.OpenReadOnlyConnectionAsync();
            await using var sqlService = new CodeMapQueryService(graph, connection);

            var iOrderService = Assert.Single(graph.Symbols, symbol => symbol.Kind == NodeKind.Interface && symbol.Name == "IOrderService");
            var orderService = Assert.Single(graph.Symbols, symbol => symbol.Kind == NodeKind.Class && symbol.Name == "OrderService");
            var legacyOrderService = Assert.Single(graph.Symbols, symbol => symbol.Kind == NodeKind.Class && symbol.Name == "LegacyOrderService");





            var snapshotFlow = snapshotService.Flow(iOrderService, "http", 2, 100, 0);
            var sqlFlow = sqlService.Flow(iOrderService, "http", 2, 100, 0);

            Assert.Contains(snapshotFlow, item => item.Symbol.Id == orderService.Id);
            Assert.DoesNotContain(snapshotFlow, item => item.Symbol.Id == legacyOrderService.Id);
            Assert.Contains(sqlFlow, item => item.Symbol.Id == orderService.Id);
            Assert.DoesNotContain(sqlFlow, item => item.Symbol.Id == legacyOrderService.Id);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(workingDirectory))
                Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task FindByIds_SqlBackedAndSnapshot_MatchIndividualFindByIdAndOmitUnknownIds()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "codemap-findbyids-sql-" + Guid.NewGuid());
        CopyFixture(FixtureRestore.EnsureRestored("AspNetFixture"), workingDirectory);
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);
            var databasePath = Path.Combine(workingDirectory, ".codemap", "index.db");
            var store = new CodeMapQueryStore(databasePath);
            var graph = await store.LoadAsync();
            var snapshotService = new CodeMapQueryService(graph);
            var connection = await store.OpenReadOnlyConnectionAsync();
            await using var sqlService = new CodeMapQueryService(graph, connection);
            await using var connectionOnlyService = new CodeMapQueryService(await store.OpenReadOnlyConnectionAsync());

            var route = Assert.Single(graph.Symbols, symbol => symbol.QualifiedName == "GET /orders/{id}");
            var sqlFlow = sqlService.Flow(route, "http", 4, 100, 0);
            var flowSourceIds = sqlFlow.Select(item => item.Via.SourceId).Distinct(StringComparer.Ordinal).ToArray();
            Assert.NotEmpty(flowSourceIds);




            var expectedFromSnapshot = flowSourceIds.ToDictionary(id => id, id => snapshotService.FindById(id)!.Id, StringComparer.Ordinal);
            var expectedFromConnection = flowSourceIds.ToDictionary(id => id, id => connectionOnlyService.FindById(id)!.Id, StringComparer.Ordinal);

            var batchedFromSnapshot = snapshotService.FindByIds(flowSourceIds);
            var batchedFromConnection = connectionOnlyService.FindByIds(flowSourceIds);

            Assert.Equal(expectedFromSnapshot.Keys.OrderBy(id => id, StringComparer.Ordinal), batchedFromSnapshot.Keys.OrderBy(id => id, StringComparer.Ordinal));
            foreach (var id in flowSourceIds)
                Assert.Equal(expectedFromSnapshot[id], batchedFromSnapshot[id].Id);

            Assert.Equal(expectedFromConnection.Keys.OrderBy(id => id, StringComparer.Ordinal), batchedFromConnection.Keys.OrderBy(id => id, StringComparer.Ordinal));
            foreach (var id in flowSourceIds)
                Assert.Equal(expectedFromConnection[id], batchedFromConnection[id].Id);



            var withUnknownId = connectionOnlyService.FindByIds(flowSourceIds.Append("not-a-real-id"));
            Assert.Equal(flowSourceIds.Length, withUnknownId.Count);
            Assert.DoesNotContain("not-a-real-id", withUnknownId.Keys);

            Assert.Empty(connectionOnlyService.FindByIds([]));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(workingDirectory))
                Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task FindByIds_SqlBackedRequestAboveChunkSize_ResolvesKnownIdsAndOmitsUnknownIds()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "codemap-findbyids-chunking-" + Guid.NewGuid());
        CopyFixture(GetFixtureDirectory(), workingDirectory);
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);
            var databasePath = Path.Combine(workingDirectory, ".codemap", "index.db");
            var store = new CodeMapQueryStore(databasePath);
            var graph = await store.LoadAsync();
            var expectedService = new CodeMapQueryService(graph);
            var known = Assert.Single(graph.Symbols, symbol => symbol.Name == "Call");
            var ids = Enumerable.Range(0, CodeMapQueryService.SqliteVariableChunkSize + 1)
                .Select(index => $"not-a-real-id-{index}")
                .Append(known.Id)
                .Append(known.Id)
                .Append("not-a-real-id-0")
                .ToArray();

            await using var service = new CodeMapQueryService(await store.OpenReadOnlyConnectionAsync());
            var expected = expectedService.FindByIds(ids);
            var actual = service.FindByIds(ids);

            Assert.Equal(expected.Keys, actual.Keys);
            Assert.Equal(known.Id, actual[known.Id].Id);
            Assert.Single(actual);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(workingDirectory))
                Directory.Delete(workingDirectory, recursive: true);
        }
    }






    [Fact]
    public async Task FindFilesByIds_SqlBackedAndSnapshot_MatchFullFilesLoadAndOmitUnknownIds()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "codemap-findfilesbyids-sql-" + Guid.NewGuid());
        CopyFixture(FixtureRestore.EnsureRestored("AspNetFixture"), workingDirectory);
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);
            var databasePath = Path.Combine(workingDirectory, ".codemap", "index.db");
            var store = new CodeMapQueryStore(databasePath);
            var graph = await store.LoadAsync();
            var snapshotService = new CodeMapQueryService(graph);
            var connection = await store.OpenReadOnlyConnectionAsync();
            await using var sqlService = new CodeMapQueryService(graph, connection);
            await using var connectionOnlyService = new CodeMapQueryService(await store.OpenReadOnlyConnectionAsync());

            var route = Assert.Single(graph.Symbols, symbol => symbol.QualifiedName == "GET /orders/{id}");
            var sqlFlow = sqlService.Flow(route, "http", 4, 100, 0);
            var flowFileIds = sqlFlow.Select(item => item.Via.SourceFileId).Where(id => id is not null).Select(id => id!).Distinct(StringComparer.Ordinal).ToArray();
            Assert.NotEmpty(flowFileIds);

            var expected = snapshotService.Files().ToDictionary(file => file.Id, file => file, StringComparer.Ordinal);

            var batchedFromSnapshot = snapshotService.FindFilesByIds(flowFileIds);
            var batchedFromConnection = connectionOnlyService.FindFilesByIds(flowFileIds);

            Assert.Equal(flowFileIds.OrderBy(id => id, StringComparer.Ordinal), batchedFromSnapshot.Keys.OrderBy(id => id, StringComparer.Ordinal));
            Assert.Equal(flowFileIds.OrderBy(id => id, StringComparer.Ordinal), batchedFromConnection.Keys.OrderBy(id => id, StringComparer.Ordinal));
            foreach (var id in flowFileIds)
            {
                Assert.Equal(expected[id].RelativePath, batchedFromSnapshot[id].RelativePath);
                Assert.Equal(expected[id].Project, batchedFromSnapshot[id].Project);
                Assert.Equal(expected[id].RelativePath, batchedFromConnection[id].RelativePath);
                Assert.Equal(expected[id].Project, batchedFromConnection[id].Project);
            }



            var withUnknownId = connectionOnlyService.FindFilesByIds(flowFileIds.Append("not-a-real-file-id"));
            Assert.Equal(flowFileIds.Length, withUnknownId.Count);
            Assert.DoesNotContain("not-a-real-file-id", withUnknownId.Keys);

            Assert.Empty(connectionOnlyService.FindFilesByIds([]));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(workingDirectory))
                Directory.Delete(workingDirectory, recursive: true);
        }
    }











    [Theory]
    [InlineData(null, null, 2000)]
    [InlineData("OrderService", null, 2000)]
    [InlineData("Order", null, 2000)]
    [InlineData(null, "AspNetFixture", 2000)]
    [InlineData(null, null, 40)]
    [InlineData("IOrderService", null, 2000)]
    public async Task BuildMap_SqlBackedAndSnapshot_MatchAcrossFocusProjectAndBudget(string? focus, string? project, int tokenBudget)
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "codemap-map-sql-" + Guid.NewGuid());
        CopyFixture(FixtureRestore.EnsureRestored("AspNetFixture"), workingDirectory);
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);
            var databasePath = Path.Combine(workingDirectory, ".codemap", "index.db");
            var store = new CodeMapQueryStore(databasePath);
            var graph = await store.LoadAsync();
            var snapshotService = new CodeMapQueryService(graph);
            var connection = await store.OpenReadOnlyConnectionAsync();
            await using var sqlService = new CodeMapQueryService(graph, connection);

            var snapshotMap = snapshotService.BuildMap(focus, project, tokenBudget);
            var sqlMap = sqlService.BuildMap(focus, project, tokenBudget);

            Assert.Equal(snapshotMap.Text, sqlMap.Text);
            Assert.Equal(snapshotMap.EstimatedTokens, sqlMap.EstimatedTokens);
            Assert.Equal(snapshotMap.TokenBudget, sqlMap.TokenBudget);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(workingDirectory))
                Directory.Delete(workingDirectory, recursive: true);
        }
    }











    [Fact]
    public async Task BuildMap_SqlBackedAndSnapshot_TestOnlyPenaltyMatchesAcrossAllSymbols()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "codemap-map-testonly-" + Guid.NewGuid());
        CopyFixture(GetFixtureDirectory(), workingDirectory);
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);
            var databasePath = Path.Combine(workingDirectory, ".codemap", "index.db");
            var store = new CodeMapQueryStore(databasePath);
            var graph = await store.LoadAsync();
            var snapshotService = new CodeMapQueryService(graph);
            var connection = await store.OpenReadOnlyConnectionAsync();
            await using var sqlService = new CodeMapQueryService(graph, connection);

            var snapshotMap = snapshotService.BuildMap(null, null, 5000);
            var sqlMap = sqlService.BuildMap(null, null, 5000);
            Assert.Equal(snapshotMap.Text, sqlMap.Text);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(workingDirectory))
                Directory.Delete(workingDirectory, recursive: true);
        }
    }







    [Fact]
    public async Task BuildMap_SqlBackedAndSnapshot_UnresolvedFocus_MatchesNoFocusRanking()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "codemap-map-nofocusmatch-" + Guid.NewGuid());
        CopyFixture(FixtureRestore.EnsureRestored("AspNetFixture"), workingDirectory);
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);
            var databasePath = Path.Combine(workingDirectory, ".codemap", "index.db");
            var store = new CodeMapQueryStore(databasePath);
            var graph = await store.LoadAsync();
            var snapshotService = new CodeMapQueryService(graph);
            var connection = await store.OpenReadOnlyConnectionAsync();
            await using var sqlService = new CodeMapQueryService(graph, connection);

            const string noSuchFocus = "ThisSymbolDoesNotExistAnywhereInTheFixture";
            var snapshotMap = snapshotService.BuildMap(noSuchFocus, null, 2000);
            var sqlMap = sqlService.BuildMap(noSuchFocus, null, 2000);
            Assert.Equal(snapshotMap.Text, sqlMap.Text);

            var snapshotNoFocus = snapshotService.BuildMap(null, null, 2000);
            Assert.Equal(snapshotNoFocus.Text, sqlMap.Text);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(workingDirectory))
                Directory.Delete(workingDirectory, recursive: true);
        }
    }






    [Fact]
    public async Task BuildMap_SqlBacked_RepeatedCallsAreDeterministic()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "codemap-map-determinism-" + Guid.NewGuid());
        CopyFixture(FixtureRestore.EnsureRestored("AspNetFixture"), workingDirectory);
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);
            var databasePath = Path.Combine(workingDirectory, ".codemap", "index.db");
            var store = new CodeMapQueryStore(databasePath);
            var connection = await store.OpenReadOnlyConnectionAsync();
            await using var sqlService = new CodeMapQueryService(connection);

            var first = sqlService.BuildMap(null, null, 2000);
            var second = sqlService.BuildMap(null, null, 2000);
            Assert.Equal(first.Text, second.Text);
            Assert.StartsWith("# CodeMap", first.Text, StringComparison.Ordinal);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(workingDirectory))
                Directory.Delete(workingDirectory, recursive: true);
        }
    }












    [Fact]
    public async Task QueryTopOutgoingMapTargets_RequestCountAboveChunkSize_DoesNotThrowAndReturnsExpectedIds()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "codemap-map-chunking-" + Guid.NewGuid());
        CopyFixture(GetFixtureDirectory(), workingDirectory);
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);
            var databasePath = Path.Combine(workingDirectory, ".codemap", "index.db");
            var store = new CodeMapQueryStore(databasePath);
            var connection = await store.OpenReadOnlyConnectionAsync();
            await using var sqlService = new CodeMapQueryService(connection);

            var call = sqlService.Find("Call", 20).Single(symbol => symbol.Name == "Call");
            var requestedIds = Enumerable.Repeat(call.Id, CodeMapQueryService.SqliteVariableChunkSize + 137).ToArray();

            var result = sqlService.QueryTopOutgoingMapTargets(requestedIds);

            Assert.True(result.ContainsKey(call.Id));
            Assert.Single(result.Keys);
            Assert.Contains(result[call.Id], target => target.Name == "Greet");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(workingDirectory))
                Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Impact_SqlBackedAndSnapshot_MatchAcrossProfileAndDepth()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "codemap-impact-sql-" + Guid.NewGuid());
        CopyFixture(FixtureRestore.EnsureRestored("AspNetFixture"), workingDirectory);
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);
            var databasePath = Path.Combine(workingDirectory, ".codemap", "index.db");
            var store = new CodeMapQueryStore(databasePath);
            var graph = await store.LoadAsync();
            var snapshotService = new CodeMapQueryService(graph);
            var connection = await store.OpenReadOnlyConnectionAsync();
            await using var sqlService = new CodeMapQueryService(graph, connection);




            var interfaceMethod = Assert.Single(graph.Symbols, symbol => symbol.Kind == NodeKind.Method
                && symbol.QualifiedName.Contains("IOrderService.GetOrder", StringComparison.Ordinal));

            foreach (var profile in new[] { "code", "app" })
            {
                var snapshotImpact = snapshotService.Impact(interfaceMethod, 4, 100, profile);
                var sqlImpact = sqlService.Impact(interfaceMethod, 4, 100, profile);
                var snapshotSet = snapshotImpact.Select(item => (item.Symbol.Id, item.Via.SourceId, item.Via.TargetId, item.Via.Kind, item.Depth)).ToHashSet();
                var sqlSet = sqlImpact.Select(item => (item.Symbol.Id, item.Via.SourceId, item.Via.TargetId, item.Via.Kind, item.Depth)).ToHashSet();
                Assert.Equal(snapshotSet, sqlSet);
            }




            var codeImpact = sqlService.Impact(interfaceMethod, 4, 100, "code");
            Assert.Contains(codeImpact, item => item.Via.Kind == EdgeKind.Calls);
            Assert.DoesNotContain(codeImpact, item => item.Symbol.QualifiedName == "POST /orders");

            var appImpact = sqlService.Impact(interfaceMethod, 4, 100, "app");
            Assert.Contains(appImpact, item => item.Symbol.QualifiedName == "POST /orders" && item.Via.Kind == EdgeKind.RoutesTo);

            Assert.Throws<ArgumentException>(() => sqlService.Impact(interfaceMethod, 4, 10, "bogus"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(workingDirectory))
                Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Impact_AppProfile_DiSelectedImplementation_ExcludesUnregisteredImplementer()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "codemap-impact-di-" + Guid.NewGuid());
        CopyFixture(FixtureRestore.EnsureRestored("AspNetFixture"), workingDirectory);
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);
            var databasePath = Path.Combine(workingDirectory, ".codemap", "index.db");
            var store = new CodeMapQueryStore(databasePath);
            var graph = await store.LoadAsync();
            var snapshotService = new CodeMapQueryService(graph);
            var connection = await store.OpenReadOnlyConnectionAsync();
            await using var sqlService = new CodeMapQueryService(graph, connection);

            var iOrderService = Assert.Single(graph.Symbols, symbol => symbol.Kind == NodeKind.Interface && symbol.Name == "IOrderService");
            var orderService = Assert.Single(graph.Symbols, symbol => symbol.Kind == NodeKind.Class && symbol.Name == "OrderService");
            var legacyOrderService = Assert.Single(graph.Symbols, symbol => symbol.Kind == NodeKind.Class && symbol.Name == "LegacyOrderService");





            var snapshotFromRegistered = snapshotService.Impact(orderService, 2, 100, "app");
            var sqlFromRegistered = sqlService.Impact(orderService, 2, 100, "app");
            Assert.Contains(snapshotFromRegistered, item => item.Symbol.Id == iOrderService.Id);
            Assert.Contains(sqlFromRegistered, item => item.Symbol.Id == iOrderService.Id);

            var snapshotFromUnregistered = snapshotService.Impact(legacyOrderService, 2, 100, "app");
            var sqlFromUnregistered = sqlService.Impact(legacyOrderService, 2, 100, "app");
            Assert.DoesNotContain(snapshotFromUnregistered, item => item.Symbol.Id == iOrderService.Id);
            Assert.DoesNotContain(sqlFromUnregistered, item => item.Symbol.Id == iOrderService.Id);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(workingDirectory))
                Directory.Delete(workingDirectory, recursive: true);
        }
    }








    [Fact]
    public async Task ImpactUnion_SqlBackedAndSnapshot_MatchAcrossRootCounts()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "codemap-impactunion-sql-" + Guid.NewGuid());
        CopyFixture(GetFixtureDirectory(), workingDirectory);
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);
            var databasePath = Path.Combine(workingDirectory, ".codemap", "index.db");
            var store = new CodeMapQueryStore(databasePath);
            var graph = await store.LoadAsync();
            var snapshotService = new CodeMapQueryService(graph);
            var connection = await store.OpenReadOnlyConnectionAsync();
            await using var sqlService = new CodeMapQueryService(graph, connection);

            var greet = Assert.Single(graph.Symbols, symbol => symbol.Name == "Greet");
            var call = Assert.Single(graph.Symbols, symbol => symbol.Name == "Call");


            Assert.Empty(sqlService.ImpactUnion(Array.Empty<IndexedSymbol>(), 3, 20));


            AssertImpactUnionMatches(snapshotService, sqlService, [greet], 3, 20);


            AssertImpactUnionMatches(snapshotService, sqlService, [greet, call], 3, 20);


            AssertImpactUnionMatches(snapshotService, sqlService, [greet, greet, call], 3, 20);


            AssertImpactUnionMatches(snapshotService, sqlService, [greet, call], 3, 1);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(workingDirectory))
                Directory.Delete(workingDirectory, recursive: true);
        }
    }

    private static void AssertImpactUnionMatches(
        CodeMapQueryService snapshotService,
        CodeMapQueryService sqlService,
        IndexedSymbol[] roots,
        int depth,
        int maxResults,
        string profile = "code")
    {
        var snapshotUnion = snapshotService.ImpactUnion(roots, depth, maxResults, profile);
        var sqlUnion = sqlService.ImpactUnion(roots, depth, maxResults, profile);
        Assert.Equal(
            snapshotUnion.Select(item => (item.Symbol.Id, item.Via.SourceId, item.Via.TargetId, item.Via.Kind, item.Depth, item.RootId)),
            sqlUnion.Select(item => (item.Symbol.Id, item.Via.SourceId, item.Via.TargetId, item.Via.Kind, item.Depth, item.RootId)));
    }







    [Fact]
    public async Task ImpactUnion_SqlBacked_PreservesPerRootOrderingAndDuplicateRootLimits()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "codemap-impactunion-ordering-" + Guid.NewGuid());
        Directory.CreateDirectory(workingDirectory);
        try
        {
            var databasePath = Path.Combine(workingDirectory, "index.db");
            await SeedImpactUnionOrderingDatabaseAsync(databasePath);
            var store = new CodeMapQueryStore(databasePath);
            var graph = await store.LoadAsync();
            await using var sqlService = new CodeMapQueryService(graph, await store.OpenReadOnlyConnectionAsync());

            var primaryRoot = Assert.Single(graph.Symbols, symbol => symbol.Id == "root-primary");
            var secondaryRoot = Assert.Single(graph.Symbols, symbol => symbol.Id == "root-secondary");



            AssertImpactUnionMatchesExistingSqlPath(sqlService, primaryRoot, secondaryRoot, 1);




            AssertImpactUnionMatchesExistingSqlPath(sqlService, primaryRoot, secondaryRoot, 2, duplicatePrimaryRoot: true);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(workingDirectory))
                Directory.Delete(workingDirectory, recursive: true);
        }
    }








    [Fact]
    public async Task ImpactUnion_SqlBacked_PreservesFirstRootWinsAtEqualDepthRegardlessOfInputOrder()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "codemap-impactunion-tie-" + Guid.NewGuid());
        CopyFixture(GetGraphShapesFixtureDirectory(), workingDirectory);
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);
            var databasePath = Path.Combine(workingDirectory, ".codemap", "index.db");
            var store = new CodeMapQueryStore(databasePath);
            var graph = await store.LoadAsync();
            var connection = await store.OpenReadOnlyConnectionAsync();
            await using var sqlService = new CodeMapQueryService(graph, connection);




            var methodB = Assert.Single(graph.Symbols, symbol => symbol.Kind == NodeKind.Method
                && symbol.QualifiedName == "Fixture.Diamond.B");
            var methodC = Assert.Single(graph.Symbols, symbol => symbol.Kind == NodeKind.Method
                && symbol.QualifiedName == "Fixture.Diamond.C");
            var methodA = Assert.Single(graph.Symbols, symbol => symbol.Kind == NodeKind.Method
                && symbol.QualifiedName == "Fixture.Diamond.A");

            var forward = sqlService.ImpactUnion([methodB, methodC], 1, 20);
            var reversed = sqlService.ImpactUnion([methodC, methodB], 1, 20);

            var forwardItem = Assert.Single(forward, item => item.Symbol.Id == methodA.Id);
            var reversedItem = Assert.Single(reversed, item => item.Symbol.Id == methodA.Id);
            Assert.Equal(methodB.Id, forwardItem.RootId);
            Assert.Equal(methodC.Id, reversedItem.RootId);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(workingDirectory))
                Directory.Delete(workingDirectory, recursive: true);
        }
    }













    [Fact]
    public async Task ImpactUnion_SqlBacked_AppProfileWithDiNarrowing_MatchesSnapshotAcrossMultipleRoots()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "codemap-impactunion-di-" + Guid.NewGuid());
        CopyFixture(FixtureRestore.EnsureRestored("AspNetFixture"), workingDirectory);
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);
            var databasePath = Path.Combine(workingDirectory, ".codemap", "index.db");
            var store = new CodeMapQueryStore(databasePath);
            var graph = await store.LoadAsync();
            var snapshotService = new CodeMapQueryService(graph);
            var connection = await store.OpenReadOnlyConnectionAsync();
            await using var sqlService = new CodeMapQueryService(graph, connection);

            var interfaceGetOrder = Assert.Single(graph.Symbols, symbol => symbol.Kind == NodeKind.Method
                && symbol.QualifiedName == "AspNetFixture.Services.IOrderService.GetOrder");
            var registeredGetOrder = Assert.Single(graph.Symbols, symbol => symbol.Kind == NodeKind.Method
                && symbol.QualifiedName == "AspNetFixture.Services.OrderService.GetOrder");
            var unregisteredGetOrder = Assert.Single(graph.Symbols, symbol => symbol.Kind == NodeKind.Method
                && symbol.QualifiedName == "AspNetFixture.Services.LegacyOrderService.GetOrder");

            foreach (var profile in new[] { "code", "app" })
                AssertImpactUnionMatches(snapshotService, sqlService, [registeredGetOrder, unregisteredGetOrder], 2, 100, profile);

            var appUnion = sqlService.ImpactUnion([registeredGetOrder, unregisteredGetOrder], 2, 100, "app");
            Assert.Contains(appUnion, item => item.Symbol.Id == interfaceGetOrder.Id && item.RootId == registeredGetOrder.Id);
            Assert.DoesNotContain(appUnion, item => item.Symbol.Id == interfaceGetOrder.Id && item.RootId == unregisteredGetOrder.Id);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(workingDirectory))
                Directory.Delete(workingDirectory, recursive: true);
        }
    }






    [Fact]
    public async Task ImpactUnion_SqlBacked_RootCountAboveChunkSize_DoesNotThrowAndMatchesSnapshot()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "codemap-impactunion-chunking-" + Guid.NewGuid());
        CopyFixture(GetFixtureDirectory(), workingDirectory);
        try
        {
            var rootCount = (CodeMapQueryService.SqliteVariableChunkSize - 2) / 2 + 2;
            var generatedSource = "namespace Fixture.ProjA;\npublic static class ImpactUnionChunking\n{\n"
                + string.Join("\n", Enumerable.Range(0, rootCount)
                    .Select(index => $"    public static void Root{index:D3}() {{ }} public static void Caller{index:D3}() => Root{index:D3}();"))
                + "\n}\n";
            await File.WriteAllTextAsync(Path.Combine(workingDirectory, "ProjA", "ImpactUnionChunking.cs"), generatedSource);

            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);
            var databasePath = Path.Combine(workingDirectory, ".codemap", "index.db");
            var store = new CodeMapQueryStore(databasePath);
            var graph = await store.LoadAsync();
            var snapshotService = new CodeMapQueryService(graph);
            var connection = await store.OpenReadOnlyConnectionAsync();
            await using var sqlService = new CodeMapQueryService(graph, connection);

            var roots = graph.Symbols
                .Where(symbol => symbol.Kind == NodeKind.Method && symbol.Name.StartsWith("Root", StringComparison.Ordinal))
                .OrderBy(symbol => symbol.Name, StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(rootCount, roots.Length);

            AssertImpactUnionMatches(snapshotService, sqlService, roots, 1, rootCount);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(workingDirectory))
                Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ConnectionOnlyFiles_RepeatedCallsOnSameServiceInstance_ReturnStableResults()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "codemap-files-memoize-" + Guid.NewGuid());
        CopyFixture(GetFixtureDirectory(), workingDirectory);
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);

            var databasePath = Path.Combine(workingDirectory, ".codemap", "index.db");
            var store = new CodeMapQueryStore(databasePath);
            var connection = await store.OpenReadOnlyConnectionAsync();
            await using var connectionOnlyService = new CodeMapQueryService(connection);

            var first = connectionOnlyService.Files();
            var second = connectionOnlyService.Files();

            Assert.NotEmpty(first);
            Assert.Equal(first.Count, second.Count);
            Assert.Equal(
                first.Select(file => (file.Id, file.Project, file.RelativePath, file.Language)),
                second.Select(file => (file.Id, file.Project, file.RelativePath, file.Language)));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(workingDirectory))
                Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Schema_PredicateAlignedEdgeIndexes_ExistAfterIndexAndAfterNoOpUpdate()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "codemap-edge-indexes-" + Guid.NewGuid());
        CopyFixture(GetFixtureDirectory(), workingDirectory);
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);
            var databasePath = Path.Combine(workingDirectory, ".codemap", "index.db");

            Assert.Equal(
                new[] { "ix_edges_source_file", "ix_edges_source_kind", "ix_edges_target_kind" },
                await GetIndexNamesAsync(databasePath, "ix_edges_source_kind", "ix_edges_target_kind", "ix_edges_source_file"));





            await new IncrementalCodeMapIndexer().UpdateAsync(workingDirectory, CancellationToken.None);
            Assert.Equal(
                new[] { "ix_edges_source_file", "ix_edges_source_kind", "ix_edges_target_kind" },
                await GetIndexNamesAsync(databasePath, "ix_edges_source_kind", "ix_edges_target_kind", "ix_edges_source_file"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(workingDirectory))
                Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Schema_SymbolNoCaseIndexes_ExistAfterIndexAndAfterNoOpUpdateWithoutSchemaVersionChange()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "codemap-symbol-nocase-indexes-" + Guid.NewGuid());
        CopyFixture(GetFixtureDirectory(), workingDirectory);
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);
            var databasePath = Path.Combine(workingDirectory, ".codemap", "index.db");

            Assert.Equal(
                new[] { "ix_symbols_name_nocase", "ix_symbols_qualified_name_nocase" },
                await GetIndexNamesAsync(databasePath, "ix_symbols_name_nocase", "ix_symbols_qualified_name_nocase"));




            var storedSchemaVersion = await GetMetadataValueAsync(databasePath, "schema_version");
            Assert.Equal(SqliteCodeMapStore.SchemaVersion, storedSchemaVersion);

            await new IncrementalCodeMapIndexer().UpdateAsync(workingDirectory, CancellationToken.None);
            Assert.Equal(
                new[] { "ix_symbols_name_nocase", "ix_symbols_qualified_name_nocase" },
                await GetIndexNamesAsync(databasePath, "ix_symbols_name_nocase", "ix_symbols_qualified_name_nocase"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(workingDirectory))
                Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Schema_SymbolNoCaseIndexes_AreUsedByExactFindTier()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "codemap-symbol-nocase-plan-" + Guid.NewGuid());
        CopyFixture(GetFixtureDirectory(), workingDirectory);
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);
            var databasePath = Path.Combine(workingDirectory, ".codemap", "index.db");



            await AssertQueryPlanUsesIndexAsync(
                databasePath,
                "ix_symbols_name_nocase",
                "SELECT 1 FROM symbols s WHERE s.name = $value COLLATE NOCASE");
            await AssertQueryPlanUsesIndexAsync(
                databasePath,
                "ix_symbols_qualified_name_nocase",
                "SELECT 1 FROM symbols s WHERE s.qualified_name = $value COLLATE NOCASE");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(workingDirectory))
                Directory.Delete(workingDirectory, recursive: true);
        }
    }

    private static async Task<string?> GetMetadataValueAsync(string databasePath, string key)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM metadata WHERE key = $key";
        command.Parameters.AddWithValue("$key", key);
        return (await command.ExecuteScalarAsync()) as string;
    }

    [Fact]
    public async Task Schema_PredicateAlignedEdgeIndexes_AreUsedByHotQueries()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "codemap-edge-index-plan-" + Guid.NewGuid());
        CopyFixture(GetFixtureDirectory(), workingDirectory);
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);
            var databasePath = Path.Combine(workingDirectory, ".codemap", "index.db");


            await AssertQueryPlanUsesIndexAsync(
                databasePath,
                "ix_edges_source_kind",
                "SELECT 1 FROM edges e WHERE e.source_id = $value AND e.kind = 'Contains'");



            await AssertQueryPlanUsesIndexAsync(
                databasePath,
                "ix_edges_target_kind",
                "SELECT 1 FROM edges e WHERE e.target_id = $value AND e.kind = 'Calls'");



            await AssertQueryPlanUsesIndexAsync(
                databasePath,
                "ix_edges_source_file",
                "SELECT 1 FROM edges e WHERE e.source_file_id = $value");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(workingDirectory))
                Directory.Delete(workingDirectory, recursive: true);
        }
    }

    private static async Task AssertQueryPlanUsesIndexAsync(string databasePath, string expectedIndexName, string sql)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + sql;
        command.Parameters.AddWithValue("$value", "unused");
        await using var reader = await command.ExecuteReaderAsync();
        var planLines = new List<string>();
        while (await reader.ReadAsync())
            planLines.Add(reader.GetString(3));

        Assert.Contains(planLines, line => line.Contains(expectedIndexName, StringComparison.Ordinal));
    }

    private static async Task<string[]> GetIndexNamesAsync(string databasePath, params string[] names)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT name FROM sqlite_master WHERE type = 'index' AND name IN ({string.Join(",", names.Select((_, index) => $"$name{index}"))}) ORDER BY name";
        for (var index = 0; index < names.Length; index++)
            command.Parameters.AddWithValue($"$name{index}", names[index]);
        var found = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            found.Add(reader.GetString(0));
        return found.ToArray();
    }








    [Fact]
    public async Task Callees_Diamond_ReturnsEachReachableSymbolOnceAtItsMinimumDepth()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "codemap-callees-diamond-" + Guid.NewGuid());
        CopyFixture(GetGraphShapesFixtureDirectory(), workingDirectory);
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);
            var databasePath = Path.Combine(workingDirectory, ".codemap", "index.db");
            var store = new CodeMapQueryStore(databasePath);
            var graph = await store.LoadAsync();
            var snapshotService = new CodeMapQueryService(graph);
            var a = Assert.Single(graph.Symbols, s => s.QualifiedName == "Fixture.Diamond.A");
            var d = Assert.Single(graph.Symbols, s => s.QualifiedName == "Fixture.Diamond.D");

            var connection = await store.OpenReadOnlyConnectionAsync();
            await using var sqlService = new CodeMapQueryService(graph, connection);

            var sqlRelations = sqlService.CalleeRelations(a, depth: 2, maxResults: 100);
            var snapshotRelations = snapshotService.CalleeRelations(a, depth: 2, maxResults: 100);


            Assert.Single(sqlRelations, r => r.Symbol.Id == d.Id);
            Assert.Single(snapshotRelations, r => r.Symbol.Id == d.Id);

            Assert.Equal(
                snapshotRelations.Select(r => r.Symbol.Id).OrderBy(id => id, StringComparer.Ordinal),
                sqlRelations.Select(r => r.Symbol.Id).OrderBy(id => id, StringComparer.Ordinal));


            var depthOne = sqlService.CalleeRelations(a, depth: 1, maxResults: 100);
            Assert.DoesNotContain(depthOne, r => r.Symbol.Id == d.Id);


            var limited = sqlService.CalleeRelations(a, depth: 2, maxResults: 1);
            Assert.Single(limited);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(workingDirectory))
                Directory.Delete(workingDirectory, recursive: true);
        }
    }














    [Fact]
    public async Task Callees_Cycle_TerminatesAtMaxDepth()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "codemap-callees-cycle-" + Guid.NewGuid());
        CopyFixture(GetGraphShapesFixtureDirectory(), workingDirectory);
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);
            var databasePath = Path.Combine(workingDirectory, ".codemap", "index.db");
            var store = new CodeMapQueryStore(databasePath);
            var graph = await store.LoadAsync();
            var snapshotService = new CodeMapQueryService(graph);
            var a = Assert.Single(graph.Symbols, s => s.QualifiedName == "Fixture.Cycle.A");
            var b = Assert.Single(graph.Symbols, s => s.QualifiedName == "Fixture.Cycle.B");
            var c = Assert.Single(graph.Symbols, s => s.QualifiedName == "Fixture.Cycle.C");

            var connection = await store.OpenReadOnlyConnectionAsync();
            await using var sqlService = new CodeMapQueryService(graph, connection);

            foreach (var depth in new[] { 1, 2 })
            {
                var sqlRelations = sqlService.CalleeRelations(a, depth, maxResults: 100);
                var snapshotRelations = snapshotService.CalleeRelations(a, depth, maxResults: 100);
                Assert.DoesNotContain(sqlRelations, r => r.Symbol.Id == a.Id);
                Assert.Equal(
                    snapshotRelations.Select(r => r.Symbol.Id).OrderBy(id => id, StringComparer.Ordinal),
                    sqlRelations.Select(r => r.Symbol.Id).OrderBy(id => id, StringComparer.Ordinal));
            }





            var depthThreeSql = sqlService.CalleeRelations(a, depth: 3, maxResults: 100);
            var depthThreeSnapshot = snapshotService.CalleeRelations(a, depth: 3, maxResults: 100);
            Assert.Contains(depthThreeSql, r => r.Symbol.Id == b.Id);
            Assert.Contains(depthThreeSql, r => r.Symbol.Id == c.Id);
            Assert.Contains(depthThreeSql, r => r.Symbol.Id == a.Id);
            Assert.Contains(depthThreeSnapshot, r => r.Symbol.Id == b.Id);
            Assert.Contains(depthThreeSnapshot, r => r.Symbol.Id == c.Id);
            Assert.DoesNotContain(depthThreeSnapshot, r => r.Symbol.Id == a.Id);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(workingDirectory))
                Directory.Delete(workingDirectory, recursive: true);
        }
    }







    [Fact]
    public async Task Callees_DuplicateSameLineCalls_ReachedSymbolIsNotDuplicated()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "codemap-callees-sameline-" + Guid.NewGuid());
        CopyFixture(GetGraphShapesFixtureDirectory(), workingDirectory);
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);
            var databasePath = Path.Combine(workingDirectory, ".codemap", "index.db");
            var store = new CodeMapQueryStore(databasePath);
            var graph = await store.LoadAsync();
            var connection = await store.OpenReadOnlyConnectionAsync();
            await using var sqlService = new CodeMapQueryService(graph, connection);

            var callTwice = Assert.Single(graph.Symbols, s => s.Name == "CallTwice");
            var a = Assert.Single(graph.Symbols, s => s.QualifiedName == "Fixture.Diamond.A");

            var relations = sqlService.CalleeRelations(callTwice, depth: 1, maxResults: 100);
            Assert.Single(relations, r => r.Symbol.Id == a.Id);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(workingDirectory))
                Directory.Delete(workingDirectory, recursive: true);
        }
    }






    [Fact]
    public async Task Impact_Diamond_ReturnsEachReachingSymbolOnceAtItsMinimumDepth()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "codemap-impact-diamond-" + Guid.NewGuid());
        CopyFixture(GetGraphShapesFixtureDirectory(), workingDirectory);
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);
            var databasePath = Path.Combine(workingDirectory, ".codemap", "index.db");
            var store = new CodeMapQueryStore(databasePath);
            var graph = await store.LoadAsync();
            var snapshotService = new CodeMapQueryService(graph);
            var a = Assert.Single(graph.Symbols, s => s.QualifiedName == "Fixture.Diamond.A");
            var b = Assert.Single(graph.Symbols, s => s.QualifiedName == "Fixture.Diamond.B");
            var c = Assert.Single(graph.Symbols, s => s.QualifiedName == "Fixture.Diamond.C");
            var d = Assert.Single(graph.Symbols, s => s.QualifiedName == "Fixture.Diamond.D");

            var connection = await store.OpenReadOnlyConnectionAsync();
            await using var sqlService = new CodeMapQueryService(graph, connection);

            var sqlImpact = sqlService.Impact(d, depth: 2, maxResults: 100);
            var snapshotImpact = snapshotService.Impact(d, depth: 2, maxResults: 100);

            Assert.Single(sqlImpact, item => item.Symbol.Id == a.Id);
            Assert.Single(sqlImpact, item => item.Symbol.Id == b.Id);
            Assert.Single(sqlImpact, item => item.Symbol.Id == c.Id);
            Assert.Contains(sqlImpact, item => item.Symbol.Id == a.Id && item.Depth == 2);
            Assert.Contains(sqlImpact, item => item.Symbol.Id == b.Id && item.Depth == 1);
            Assert.Contains(sqlImpact, item => item.Symbol.Id == c.Id && item.Depth == 1);

            Assert.Equal(
                snapshotImpact.Select(item => (item.Symbol.Id, item.Depth)).OrderBy(t => t.Id, StringComparer.Ordinal),
                sqlImpact.Select(item => (item.Symbol.Id, item.Depth)).OrderBy(t => t.Id, StringComparer.Ordinal));

            var limited = sqlService.Impact(d, depth: 2, maxResults: 1);
            Assert.Single(limited);
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
        return Path.Combine(solutionRoot, "tests", "Fixtures", "MultiProject");
    }

    private static string GetWebFixtureDirectory()
    {
        var testDir = AppContext.BaseDirectory;
        var solutionRoot = Path.GetFullPath(Path.Combine(testDir, "..", "..", "..", "..", ".."));
        return Path.Combine(solutionRoot, "tests", "Fixtures", "WebFixture");
    }

    private static string GetGraphShapesFixtureDirectory()
    {
        var testDir = AppContext.BaseDirectory;
        var solutionRoot = Path.GetFullPath(Path.Combine(testDir, "..", "..", "..", "..", ".."));
        return Path.Combine(solutionRoot, "tests", "Fixtures", "GraphTraversalShapes");
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

    private static async Task SeedImpactUnionOrderingDatabaseAsync(string databasePath)
    {
        await new SqliteCodeMapStore(databasePath).InitializeAsync(CancellationToken.None);
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO files(id, project, relative_path, language, content_hash, mtime_utc, size, indexed_at)
            VALUES ('file', 'Fixture', 'Fixture.cs', 'csharp', 'hash', '2026-01-01T00:00:00.0000000Z', 0, '2026-01-01T00:00:00.0000000Z');
            INSERT INTO symbols(id, file_id, kind, name, qualified_name, signature, start_line, end_line, visibility, language)
            VALUES
                ('root-primary', 'file', 'Method', 'Primary', 'Fixture.Primary', NULL, 1, 1, 'public', 'csharp'),
                ('root-secondary', 'file', 'Method', 'Secondary', 'Fixture.Secondary', NULL, 2, 2, 'public', 'csharp'),
                ('z-id', 'file', 'Method', 'Alpha', 'Fixture.Alpha', NULL, 3, 3, 'public', 'csharp'),
                ('m-id', 'file', 'Method', 'Bravo', 'Fixture.Bravo', NULL, 4, 4, 'public', 'csharp'),
                ('a-id', 'file', 'Method', 'Zulu', 'Fixture.Zulu', NULL, 5, 5, 'public', 'csharp');
            INSERT INTO edges(source_id, target_id, kind, source_file_id, line, start_column, end_line, end_column, resolution_kind, confidence)
            VALUES
                ('z-id', 'root-primary', 'Calls', 'file', 3, NULL, NULL, NULL, 'semantic', NULL),
                ('m-id', 'root-primary', 'Calls', 'file', 4, NULL, NULL, NULL, 'semantic', NULL),
                ('a-id', 'root-primary', 'Calls', 'file', 5, NULL, NULL, NULL, 'semantic', NULL);
            INSERT INTO metadata(key, value)
            VALUES ('schema_version', $schemaVersion), ('analyzer_version_csharp', $csharpAnalyzerVersion);
            """;
        command.Parameters.AddWithValue("$schemaVersion", SqliteCodeMapStore.SchemaVersion);
        command.Parameters.AddWithValue("$csharpAnalyzerVersion", SqliteCodeMapStore.CurrentAnalyzerVersions["csharp"]);
        await command.ExecuteNonQueryAsync();
    }

    private static void AssertImpactUnionMatchesExistingSqlPath(
        CodeMapQueryService service,
        IndexedSymbol primaryRoot,
        IndexedSymbol secondaryRoot,
        int maxResults,
        bool duplicatePrimaryRoot = false)
    {
        var expected = service.ImpactUnion([primaryRoot], 1, maxResults);
        var roots = duplicatePrimaryRoot
            ? new[] { primaryRoot, primaryRoot, secondaryRoot }
            : new[] { primaryRoot, secondaryRoot };
        var actual = service.ImpactUnion(roots, 1, maxResults);
        Assert.Equal(
            expected.Select(item => (item.Symbol.Id, item.Via.SourceId, item.Via.TargetId, item.Via.Kind, item.Depth, item.RootId)),
            actual.Select(item => (item.Symbol.Id, item.Via.SourceId, item.Via.TargetId, item.Via.Kind, item.Depth, item.RootId)));
    }
}