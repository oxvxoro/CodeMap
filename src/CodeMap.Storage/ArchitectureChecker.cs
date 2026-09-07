using System.Text.Json;
using CodeMap.Core.Models;

namespace CodeMap.Storage;

public sealed record ArchitectureRules(
    IReadOnlyList<string> Layers,
    IReadOnlyList<IReadOnlyList<string>> Forbid,
    int MaxFanIn,
    int MaxFanOut);

public sealed record ArchitectureViolation(string Kind, string Message, string? Source, string? Target);

public static class ArchitectureChecker
{
    public static ArchitectureRules LoadRules(string projectRoot)
    {
        var path = Path.Combine(projectRoot, ".codemap", "architecture.json");
        if (!File.Exists(path))
            return new ArchitectureRules([], [], 40, 40);

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        var layers = root.TryGetProperty("layers", out var layersElement)
            ? layersElement.EnumerateArray().Select(item => item.GetString() ?? "").Where(item => item.Length > 0).ToArray()
            : [];
        var forbid = new List<IReadOnlyList<string>>();
        if (root.TryGetProperty("forbid", out var forbidElement))
        {
            foreach (var pair in forbidElement.EnumerateArray())
            {
                var items = pair.EnumerateArray().Select(item => item.GetString() ?? "").Where(item => item.Length > 0).ToArray();
                if (items.Length >= 2)
                    forbid.Add(items);
            }
        }
        var maxFanIn = root.TryGetProperty("maxFanIn", out var fanIn) ? fanIn.GetInt32() : 40;
        var maxFanOut = root.TryGetProperty("maxFanOut", out var fanOut) ? fanOut.GetInt32() : 40;
        return new ArchitectureRules(layers, forbid, maxFanIn, maxFanOut);
    }

    public static IReadOnlyList<ArchitectureViolation> Check(
        CodeMapSnapshot graph,
        IReadOnlyDictionary<string, string[]> projectReferences,
        ArchitectureRules rules)
    {
        var violations = new List<ArchitectureViolation>();
        AddCycles(projectReferences, violations);
        AddLayerViolations(graph, rules, violations);
        AddFanViolations(graph, rules, violations);
        AddOrphans(graph, violations);
        return violations;
    }

    public static IReadOnlyDictionary<string, string[]> LoadProjectReferences(string projectRoot)
    {
        var path = Path.Combine(projectRoot, ".codemap", "state.json");
        if (!File.Exists(path))
            return new Dictionary<string, string[]>(StringComparer.Ordinal);

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var map = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (!document.RootElement.TryGetProperty("projects", out var projects))
            return map;
        foreach (var project in projects.EnumerateArray())
        {
            var name = project.GetProperty("projectName").GetString();
            if (string.IsNullOrWhiteSpace(name))
                continue;
            var refs = project.TryGetProperty("referencedProjects", out var referenced) && referenced.ValueKind == JsonValueKind.Array
                ? referenced.EnumerateArray().Select(item => item.GetString() ?? "").Where(item => item.Length > 0).ToArray()
                : [];
            map[name] = refs;
        }
        return map;
    }

    private static void AddCycles(IReadOnlyDictionary<string, string[]> projectReferences, List<ArchitectureViolation> violations)
    {
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var stack = new List<string>();

        void Dfs(string node)
        {
            if (visited.Contains(node))
                return;
            if (!visiting.Add(node))
            {
                var cycleStart = stack.IndexOf(node);
                var cycle = cycleStart >= 0 ? string.Join(" -> ", stack.Skip(cycleStart).Append(node)) : node;
                violations.Add(new ArchitectureViolation("cycle", $"Circular project reference: {cycle}", node, null));
                return;
            }
            stack.Add(node);
            foreach (var next in projectReferences.GetValueOrDefault(node, []))
                Dfs(next);
            stack.RemoveAt(stack.Count - 1);
            visiting.Remove(node);
            visited.Add(node);
        }

        foreach (var project in projectReferences.Keys.OrderBy(name => name, StringComparer.Ordinal))
            Dfs(project);
    }

    private static void AddLayerViolations(CodeMapSnapshot graph, ArchitectureRules rules, List<ArchitectureViolation> violations)
    {
        if (rules.Forbid.Count == 0 || rules.Layers.Count == 0)
            return;


        var byId = graph.Symbols.Where(IsRepositorySymbol).ToDictionary(symbol => symbol.Id, StringComparer.Ordinal);
        foreach (var edge in graph.Edges.Where(edge => edge.Kind is EdgeKind.UsesType or EdgeKind.Calls or EdgeKind.Imports or EdgeKind.Inherits))
        {
            if (!byId.TryGetValue(edge.SourceId, out var source) || !byId.TryGetValue(edge.TargetId, out var target))
                continue;
            var from = LayerOf(source, rules.Layers);
            var to = LayerOf(target, rules.Layers);
            if (from is null || to is null)
                continue;
            if (rules.Forbid.Any(pair =>
                    string.Equals(pair[0], from, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(pair[1], to, StringComparison.OrdinalIgnoreCase)))
            {
                violations.Add(new ArchitectureViolation(
                    "layer",
                    $"{from} must not reference {to}: {source.QualifiedName} -> {target.QualifiedName}",
                    source.Id,
                    target.Id));
            }
        }
    }

    private static string? LayerOf(IndexedSymbol symbol, IReadOnlyList<string> layers)
    {
        foreach (var layer in layers)
        {
            if (symbol.Project.Contains(layer, StringComparison.OrdinalIgnoreCase)
                || symbol.RelativePath.Contains(layer, StringComparison.OrdinalIgnoreCase))
                return layer;
        }
        return null;
    }

    private static void AddFanViolations(CodeMapSnapshot graph, ArchitectureRules rules, List<ArchitectureViolation> violations)
    {



        var byId = graph.Symbols.Where(IsRepositorySymbol).ToDictionary(symbol => symbol.Id, StringComparer.Ordinal);
        var incoming = graph.Edges.Where(NotStructural).GroupBy(edge => edge.TargetId).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var outgoing = graph.Edges.Where(NotStructural).GroupBy(edge => edge.SourceId).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        foreach (var (id, count) in incoming.Where(pair => pair.Value > rules.MaxFanIn))
        {
            if (byId.TryGetValue(id, out var symbol))
                violations.Add(new ArchitectureViolation("fan-in", $"{symbol.QualifiedName} fan-in {count} exceeds {rules.MaxFanIn}", symbol.Id, null));
        }
        foreach (var (id, count) in outgoing.Where(pair => pair.Value > rules.MaxFanOut))
        {
            if (byId.TryGetValue(id, out var symbol))
                violations.Add(new ArchitectureViolation("fan-out", $"{symbol.QualifiedName} fan-out {count} exceeds {rules.MaxFanOut}", symbol.Id, null));
        }
    }

    private static void AddOrphans(CodeMapSnapshot graph, List<ArchitectureViolation> violations)
    {
        var incoming = graph.Edges.ToLookup(edge => edge.TargetId, StringComparer.Ordinal);
        foreach (var symbol in graph.Symbols.Where(symbol =>
                     IsRepositorySymbol(symbol)
                     && string.Equals(symbol.Visibility, "public", StringComparison.OrdinalIgnoreCase)
                     && symbol.Kind is NodeKind.Class or NodeKind.Interface or NodeKind.Method
                     && !CodeMapQueryService.IsTestOnly(symbol)))
        {
            if (incoming[symbol.Id].All(edge => edge.Kind is EdgeKind.Contains or EdgeKind.Defines))
                violations.Add(new ArchitectureViolation("orphan", $"Public symbol has no inbound relations: {symbol.QualifiedName}", symbol.Id, null));
        }
    }


    // 외부 어셈블리의 공개 API는 이 저장소의 구조 규칙 대상이 아니다.
    private static bool IsRepositorySymbol(IndexedSymbol symbol) => !SqliteCodeMapStore.IsExternalProject(symbol.Project);

    private static bool NotStructural(IndexedEdge edge) =>
        edge.Kind is not (EdgeKind.Contains or EdgeKind.Defines);
}
