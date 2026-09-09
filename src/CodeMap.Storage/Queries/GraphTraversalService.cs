using CodeMap.Core.Models;

namespace CodeMap.Storage.Queries;

public sealed record GraphTraversalResult(string SymbolId, IndexedEdge Via, int Depth);

/// <summary>Deterministic, cycle-safe traversal primitive shared by graph use cases.</summary>
public static class GraphTraversalService
{
    public static IReadOnlyList<GraphTraversalResult> Traverse(
        string rootId,
        Func<string, IEnumerable<IndexedEdge>> adjacency,
        Func<string, IndexedSymbol?> resolve,
        int maxDepth,
        int maxResults,
        double minConfidence = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootId);
        ArgumentNullException.ThrowIfNull(adjacency);
        ArgumentNullException.ThrowIfNull(resolve);
        var result = new List<GraphTraversalResult>();
        var visited = new HashSet<string>(StringComparer.Ordinal) { rootId };
        var frontier = new[] { rootId };
        for (var depth = 1; depth <= Math.Max(0, maxDepth) && frontier.Length > 0; depth++)
        {
            var next = new List<string>();
            foreach (var sourceId in frontier.OrderBy(id => id, StringComparer.Ordinal))
            {
                foreach (var edge in adjacency(sourceId)
                    .Where(edge => (edge.Confidence ?? 1) >= minConfidence)
                    .OrderBy(edge => edge.TargetId, StringComparer.Ordinal)
                    .ThenBy(edge => edge.Kind)
                    .ThenBy(edge => edge.Line ?? int.MaxValue))
                {
                    if (!visited.Add(edge.TargetId) || resolve(edge.TargetId) is null)
                        continue;
                    result.Add(new GraphTraversalResult(edge.TargetId, edge, depth));
                    next.Add(edge.TargetId);
                    if (result.Count >= Math.Max(1, maxResults))
                        return result;
                }
            }
            frontier = next.ToArray();
        }
        return result;
    }
}
