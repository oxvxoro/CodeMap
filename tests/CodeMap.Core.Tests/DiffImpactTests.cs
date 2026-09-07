using CodeMap.Core.Models;
using CodeMap.Storage;
using Microsoft.Data.Sqlite;

namespace CodeMap.Core.Tests;

[Collection("MsBuild")]
public sealed class DiffImpactTests
{
    [Fact]
    public async Task SymbolsInFiles_ReturnsSymbolsForRelativePaths()
    {
        var workingDirectory = CopyFixture("MultiProject");
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);
            var graph = await new CodeMapQueryStore(Path.Combine(workingDirectory, ".codemap", "index.db")).LoadAsync();
            var service = new CodeMapQueryService(graph);

            var symbols = service.SymbolsInFiles([CodeMapPath.Normalize("ProjA/Caller.cs")]);
            Assert.Contains(symbols, symbol => symbol.Name == "Call");
            Assert.Contains(symbols, symbol => symbol.Name == "Caller");
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }

    [Fact]
    public async Task ImpactUnion_MergesMultipleRoots()
    {
        var workingDirectory = CopyFixture("MultiProject");
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);
            var graph = await new CodeMapQueryStore(Path.Combine(workingDirectory, ".codemap", "index.db")).LoadAsync();
            var service = new CodeMapQueryService(graph);
            var greet = Assert.Single(service.Find("Greet", 5));
            var call = Assert.Single(service.Find("Call", 5));
            var impact = service.ImpactUnion([greet, call], depth: 2, maxResults: 20);
            Assert.NotEmpty(impact);
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }

    [Fact]
    public void ImpactUnion_PreservesTheRootThatReachedEachResult()
    {
        var rootA = Symbol("RootA", NodeKind.Method, "public") with { Id = "root-a" };
        var rootB = Symbol("RootB", NodeKind.Method, "public") with { Id = "root-b" };
        var callerA = Symbol("CallerA", NodeKind.Method, "public") with { Id = "caller-a" };
        var callerB = Symbol("CallerB", NodeKind.Method, "public") with { Id = "caller-b" };
        var graph = new CodeMapSnapshot
        {
            Files = [],
            Symbols = [rootA, rootB, callerA, callerB],
            Edges =
            [
                new IndexedEdge(callerA.Id, rootA.Id, EdgeKind.Calls, null, 1),
                new IndexedEdge(callerB.Id, rootB.Id, EdgeKind.Calls, null, 1)
            ]
        };

        var impact = new CodeMapQueryService(graph).ImpactUnion([rootA, rootB], depth: 1, maxResults: 20);

        Assert.Equal("root-a", Assert.Single(impact, item => item.Symbol.Id == "caller-a").RootId);
        Assert.Equal("root-b", Assert.Single(impact, item => item.Symbol.Id == "caller-b").RootId);
    }

    [Fact]
    public void ImpactUnion_PassesProfileToEachRootTraversal()
    {




        var routeA = Symbol("RouteA", NodeKind.Route, "public") with { Id = "route-a" };
        var handlerA = Symbol("HandlerA", NodeKind.Function, "public") with { Id = "handler-a" };
        var rootA = Symbol("RootA", NodeKind.Method, "public") with { Id = "root-a" };
        var routeB = Symbol("RouteB", NodeKind.Route, "public") with { Id = "route-b" };
        var handlerB = Symbol("HandlerB", NodeKind.Function, "public") with { Id = "handler-b" };
        var rootB = Symbol("RootB", NodeKind.Method, "public") with { Id = "root-b" };
        var graph = new CodeMapSnapshot
        {
            Files = [],
            Symbols = [routeA, handlerA, rootA, routeB, handlerB, rootB],
            Edges =
            [
                new IndexedEdge(routeA.Id, handlerA.Id, EdgeKind.RoutesTo, null, 1),
                new IndexedEdge(handlerA.Id, rootA.Id, EdgeKind.Calls, null, 1),
                new IndexedEdge(routeB.Id, handlerB.Id, EdgeKind.RoutesTo, null, 1),
                new IndexedEdge(handlerB.Id, rootB.Id, EdgeKind.Calls, null, 1)
            ]
        };
        var service = new CodeMapQueryService(graph);

        var codeImpact = service.ImpactUnion([rootA, rootB], depth: 2, maxResults: 20, profile: "code");
        var appImpact = service.ImpactUnion([rootA, rootB], depth: 2, maxResults: 20, profile: "app");

        Assert.DoesNotContain(codeImpact, item => item.Symbol.Id is "route-a" or "route-b");
        Assert.Contains(appImpact, item => item.Symbol.Id == "route-a");
        Assert.Contains(appImpact, item => item.Symbol.Id == "route-b");
    }

    [Fact]
    public void RiskScorer_ProducesLevelFromSignals()
    {
        var changed = new[]
        {
            Symbol("Greeter", NodeKind.Class, "public"),
            Symbol("Greet", NodeKind.Method, "public")
        };
        var impact = new[]
        {
            new ImpactItem(Symbol("Call", NodeKind.Method, "public"), new IndexedEdge("a", "b", EdgeKind.Calls, null, null, EdgeResolutionKind.Semantic, 1.0), 1)
        };
        var risk = RiskScorer.Score(changed, impact);
        Assert.True(risk.Level is "high" or "medium" or "low");
        Assert.True(risk.PublicApis >= 1);
    }

    [Fact]
    public void RiskScorer_ScoresHigherWhenGivenTheBroaderAppProfileImpact()
    {



        var route = Symbol("Route", NodeKind.Route, "public") with { Id = "route" };
        var handler = Symbol("Handler", NodeKind.Function, "public") with { Id = "handler" };
        var root = Symbol("Root", NodeKind.Method, "public") with { Id = "root" };
        var graph = new CodeMapSnapshot
        {
            Files = [],
            Symbols = [route, handler, root],
            Edges =
            [
                new IndexedEdge(route.Id, handler.Id, EdgeKind.RoutesTo, null, 1),
                new IndexedEdge(handler.Id, root.Id, EdgeKind.Calls, null, 1)
            ]
        };
        var service = new CodeMapQueryService(graph);
        var changed = new[] { root };

        var codeImpact = service.Impact(root, 2, 20, "code");
        var appImpact = service.Impact(root, 2, 20, "app");
        var codeRisk = RiskScorer.Score(changed, codeImpact);
        var appRisk = RiskScorer.Score(changed, appImpact);

        Assert.True(appImpact.Count > codeImpact.Count);
        Assert.True(appRisk.WeightedTotal >= codeRisk.WeightedTotal);
    }

    [Fact]
    public void RiskScorer_UntestedIsPerRoot_TestOnOneRootDoesNotCoverAnother()
    {
        var changedA = Symbol("ChangedA", NodeKind.Method, "public") with { Id = "changed-a" };
        var changedB = Symbol("ChangedB", NodeKind.Method, "public") with { Id = "changed-b" };
        var testForA = Symbol("ChangedATests", NodeKind.Method, "public") with { Id = "test-a", RelativePath = "ProjA.Tests/ChangedATests.cs" };
        var impact = new[]
        {
            new ImpactItem(testForA, new IndexedEdge("x", "y", EdgeKind.Calls, null, null), 1, RootId: "changed-a")
        };

        var risk = RiskScorer.Score([changedA, changedB], impact);

        Assert.Equal(1, risk.Untested);
    }

    [Fact]
    public void RiskScorer_UntestedIsZero_WhenEachRootHasItsOwnTest()
    {
        var changedA = Symbol("ChangedA", NodeKind.Method, "public") with { Id = "changed-a" };
        var changedB = Symbol("ChangedB", NodeKind.Method, "public") with { Id = "changed-b" };
        var testForA = Symbol("ChangedATests", NodeKind.Method, "public") with { Id = "test-a", RelativePath = "ProjA.Tests/ChangedATests.cs" };
        var testForB = Symbol("ChangedBTests", NodeKind.Method, "public") with { Id = "test-b", RelativePath = "ProjA.Tests/ChangedBTests.cs" };
        var impact = new[]
        {
            new ImpactItem(testForA, new IndexedEdge("x", "y", EdgeKind.Calls, null, null), 1, RootId: "changed-a"),
            new ImpactItem(testForB, new IndexedEdge("x", "y", EdgeKind.Calls, null, null), 1, RootId: "changed-b")
        };

        var risk = RiskScorer.Score([changedA, changedB], impact);

        Assert.Equal(0, risk.Untested);
    }

    [Fact]
    public void RiskScorer_UntestedExcludesChangedSymbolThatIsItselfTestOnly()
    {
        var changedTest = Symbol("SomeTests", NodeKind.Method, "public") with { Id = "changed-test" };

        var risk = RiskScorer.Score([changedTest], []);

        Assert.Equal(0, risk.Untested);
    }

    [Fact]
    public void RiskScorer_UntestedIsConservative_WhenSharedTestOnlyReachesOneRootAsWinner()
    {



        var rootA = Symbol("RootA", NodeKind.Method, "public") with { Id = "root-a" };
        var rootB = Symbol("RootB", NodeKind.Method, "public") with { Id = "root-b" };
        var sharedTest = Symbol("SharedTests", NodeKind.Method, "public") with { Id = "shared-test" };
        var impact = new[]
        {
            new ImpactItem(sharedTest, new IndexedEdge("x", "y", EdgeKind.Calls, null, null), 1, RootId: "root-a")
        };

        var risk = RiskScorer.Score([rootA, rootB], impact);

        Assert.Equal(1, risk.Untested);
    }

    private static IndexedSymbol Symbol(string name, NodeKind kind, string visibility) => new(
        $"id:{name}", "ProjA", "file", "ProjA/Foo.cs", kind, name, $"Fixture.{name}", null, 1, 1, visibility, "csharp");

    private static async Task<SqliteConnection> OpenConnectionAsync(string databasePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
        await connection.OpenAsync();
        return connection;
    }

    private static string CopyFixture(string fixtureName)
    {
        var testDir = AppContext.BaseDirectory;
        var solutionRoot = Path.GetFullPath(Path.Combine(testDir, "..", "..", "..", "..", ".."));
        var source = Path.Combine(solutionRoot, "tests", "Fixtures", fixtureName);
        var destination = Path.Combine(Path.GetTempPath(), $"codemap-diff-{fixtureName}-" + Guid.NewGuid());
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