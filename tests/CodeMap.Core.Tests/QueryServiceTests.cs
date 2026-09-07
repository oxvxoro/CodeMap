using CodeMap.Core.Models;
using CodeMap.Storage;

namespace CodeMap.Core.Tests;

public sealed class QueryServiceTests
{
    [Fact]
    public void Find_PrefersExactTierOverContainingMembers()
    {
        var graph = Graph(
            Symbol("type", NodeKind.Class, "ApprovalViewModel", "App.ApprovalViewModel"),
            Symbol("method", NodeKind.Method, "Load", "App.ApprovalViewModel.Load"));

        var matches = new CodeMapQueryService(graph).Find("ApprovalViewModel", 20);

        var match = Assert.Single(matches);
        Assert.Equal("type", match.Id);
    }

    [Fact]
    public void ResolveOverloadCandidates_IsAmbiguous()
    {
        var graph = Graph(
            Symbol("one", NodeKind.Method, "HandleAsync", "App.Handler.HandleAsync", "HandleAsync(string)"),
            Symbol("two", NodeKind.Method, "HandleAsync", "App.Handler.HandleAsync", "HandleAsync(int)"));

        var result = new CodeMapQueryService(graph).ResolveCallable("App.Handler.HandleAsync");

        Assert.True(result.IsAmbiguous);
        Assert.Equal(2, result.Matches.Count);
    }

    [Fact]
    public void Impact_IsCycleSafeAndHonorsDepth()
    {
        var graph = Graph(
            Symbol("a", NodeKind.Method, "A", "App.A"),
            Symbol("b", NodeKind.Method, "B", "App.B"),
            Symbol("c", NodeKind.Method, "C", "App.C"),
            new IndexedEdge("b", "a", EdgeKind.Calls, null, null),
            new IndexedEdge("c", "b", EdgeKind.Calls, null, null),
            new IndexedEdge("a", "c", EdgeKind.Calls, null, null));

        var result = new CodeMapQueryService(graph).Impact(graph.Symbols[0], 3, 20);

        Assert.Equal(new[] { "b", "c" }, result.Select(item => item.Symbol.Id));
        Assert.Equal(new[] { 1, 2 }, result.Select(item => item.Depth));
    }

    [Fact]
    public void ResolveCallable_IncludesFunctionNodes()
    {
        var graph = Graph(Symbol("local", NodeKind.Function, "LocalHelper", "App.Handler.LocalHelper"));

        var result = new CodeMapQueryService(graph).ResolveCallable("App.Handler.LocalHelper");

        Assert.False(result.IsAmbiguous);
        Assert.Equal("local", Assert.Single(result.Matches).Id);
    }

    [Fact]
    public void Impact_DefaultProfile_MatchesExplicitCodeProfile()
    {
        var graph = Graph(
            Symbol("a", NodeKind.Method, "A", "App.A"),
            Symbol("b", NodeKind.Method, "B", "App.B"),
            new IndexedEdge("b", "a", EdgeKind.Calls, null, null));
        var service = new CodeMapQueryService(graph);

        var defaultResult = service.Impact(graph.Symbols[0], 2, 20);
        var explicitCodeResult = service.Impact(graph.Symbols[0], 2, 20, "code");

        Assert.Equal(defaultResult.Select(item => item.Symbol.Id), explicitCodeResult.Select(item => item.Symbol.Id));
    }

    [Fact]
    public void Impact_CodeProfile_DoesNotFollowRoutesTo()
    {



        var graph = Graph(
            Symbol("route", NodeKind.Route, "GET /orders", "GET /orders"),
            Symbol("handler", NodeKind.Function, "Handler", "App.Handler"),
            Symbol("service", NodeKind.Method, "GetOrder", "App.Service.GetOrder"),
            new IndexedEdge("route", "handler", EdgeKind.RoutesTo, null, null),
            new IndexedEdge("handler", "service", EdgeKind.Calls, null, null));
        var service = new CodeMapQueryService(graph);
        var root = graph.Symbols.Single(symbol => symbol.Id == "service");

        var result = service.Impact(root, 5, 20, "code");

        Assert.Contains(result, item => item.Symbol.Id == "handler");
        Assert.DoesNotContain(result, item => item.Symbol.Id == "route");
    }

    [Fact]
    public void Impact_AppProfile_FollowsRoutesToAcrossDepth()
    {
        var graph = Graph(
            Symbol("route", NodeKind.Route, "GET /orders", "GET /orders"),
            Symbol("handler", NodeKind.Function, "Handler", "App.Handler"),
            Symbol("service", NodeKind.Method, "GetOrder", "App.Service.GetOrder"),
            new IndexedEdge("route", "handler", EdgeKind.RoutesTo, null, null),
            new IndexedEdge("handler", "service", EdgeKind.Calls, null, null));
        var service = new CodeMapQueryService(graph);
        var root = graph.Symbols.Single(symbol => symbol.Id == "service");

        var result = service.Impact(root, 5, 20, "app");

        Assert.Contains(result, item => item.Symbol.Id == "handler" && item.Depth == 1);
        Assert.Contains(result, item => item.Symbol.Id == "route" && item.Depth == 2);
    }

    [Fact]
    public void Impact_AppProfile_ExcludesUnregisteredImplementer()
    {





        var graph = Graph(
            Symbol("iface", NodeKind.Interface, "IOrderService", "App.IOrderService"),
            Symbol("registered", NodeKind.Class, "OrderService", "App.OrderService"),
            Symbol("unregistered", NodeKind.Class, "LegacyOrderService", "App.LegacyOrderService"),
            Symbol("registration", NodeKind.DependencyRegistration, "AddSingleton", "App.Startup.AddSingleton"),
            new IndexedEdge("iface", "registered", EdgeKind.ImplementedBy, null, null),
            new IndexedEdge("iface", "unregistered", EdgeKind.ImplementedBy, null, null),
            new IndexedEdge("registration", "iface", EdgeKind.Registers, null, null),
            new IndexedEdge("registration", "registered", EdgeKind.ResolvesTo, null, null));
        var service = new CodeMapQueryService(graph);
        var registered = graph.Symbols.Single(symbol => symbol.Id == "registered");
        var unregistered = graph.Symbols.Single(symbol => symbol.Id == "unregistered");

        var fromRegistered = service.Impact(registered, 2, 20, "app");
        var fromUnregistered = service.Impact(unregistered, 2, 20, "app");

        Assert.Contains(fromRegistered, item => item.Symbol.Id == "iface");
        Assert.DoesNotContain(fromUnregistered, item => item.Symbol.Id == "iface");
    }

    [Fact]
    public void Impact_RejectsUnsupportedProfile()
    {
        var graph = Graph(Symbol("a", NodeKind.Method, "A", "App.A"));
        var service = new CodeMapQueryService(graph);

        Assert.Throws<ArgumentException>(() => service.Impact(graph.Symbols[0], 2, 20, "bogus"));
    }

    [Fact]
    public void IsValidImpactProfile_AcceptsCodeAndAppOnly()
    {
        Assert.True(CodeMapQueryService.IsValidImpactProfile("code"));
        Assert.True(CodeMapQueryService.IsValidImpactProfile("app"));
        Assert.False(CodeMapQueryService.IsValidImpactProfile("bogus"));
    }

    [Fact]
    public void Map_IsDeterministicAndStaysWithinBudget()
    {
        var graph = Graph(
            Symbol("service", NodeKind.Class, "OrderService", "App.OrderService", visibility: "public"),
            Symbol("test", NodeKind.Class, "OrderServiceTests", "App.OrderServiceTests", "", "Tests/OrderServiceTests.cs", visibility: "public"),
            new IndexedEdge("test", "service", EdgeKind.Calls, null, null));
        var service = new CodeMapQueryService(graph);

        var first = service.BuildMap(null, null, 20);
        var second = service.BuildMap(null, null, 20);

        Assert.Equal(first.Text, second.Text);
        Assert.InRange(first.EstimatedTokens, 0, 20);
        Assert.StartsWith("# CodeMap", first.Text, StringComparison.Ordinal);
    }

    private static CodeMapSnapshot Graph(params object[] values)
    {
        var symbols = values.OfType<IndexedSymbol>().ToArray();
        var edges = values.OfType<IndexedEdge>().ToArray();
        return new CodeMapSnapshot
        {
            Files = symbols.Select(symbol => new IndexedFile(symbol.FileId, symbol.Project, symbol.RelativePath, "csharp")).DistinctBy(file => file.Id).ToArray(),
            Symbols = symbols,
            Edges = edges
        };
    }

    private static IndexedSymbol Symbol(
        string id,
        NodeKind kind,
        string name,
        string qualifiedName,
        string? signature = null,
        string relativePath = "Order.cs",
        string visibility = "public") =>
        new(id, "Fixture", "file-" + relativePath, relativePath, kind, name, qualifiedName, signature, 1, 1, visibility, "csharp");
}