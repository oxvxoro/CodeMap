using CodeMap.Core.Models;
using CodeMap.Storage;

namespace CodeMap.Core.Tests;

public sealed class ArchitectureCheckerTests
{
    [Fact]
    public void DetectsProjectCycle()
    {
        var references = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["A"] = ["B"],
            ["B"] = ["A"]
        };
        var graph = EmptyGraph();
        var violations = ArchitectureChecker.Check(graph, references, new ArchitectureRules([], [], 100, 100));
        Assert.Contains(violations, violation => violation.Kind == "cycle");
    }

    [Fact]
    public void DetectsOrphanPublicSymbol()
    {
        var symbol = new IndexedSymbol("id", "P", "f", "Foo.cs", NodeKind.Class, "Foo", "Foo", null, 1, 1, "public", "csharp");
        var graph = new CodeMapSnapshot
        {
            Files = [new IndexedFile("f", "P", "Foo.cs", "csharp")],
            Symbols = [symbol],
            Edges = []
        };
        var violations = ArchitectureChecker.Check(graph, new Dictionary<string, string[]>(), new ArchitectureRules([], [], 100, 100));
        Assert.Contains(violations, violation => violation.Kind == "orphan");
    }

    private static CodeMapSnapshot EmptyGraph() => new()
    {
        Files = [],
        Symbols = [],
        Edges = []
    };
}