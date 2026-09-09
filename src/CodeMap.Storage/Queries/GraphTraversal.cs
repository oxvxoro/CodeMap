using CodeMap.Core.Models;
using CodeMap.Storage;

namespace CodeMap.Storage.Queries;

internal sealed record TraversalItem(IndexedSymbol Symbol, IndexedEdge Via, int Depth);

internal sealed class GraphTraversal
{
    public IReadOnlyList<TraversalItem> TraverseForward(
        ICodeMapQueryBackend backend,
        IndexedSymbol root,
        IReadOnlySet<EdgeKind> kinds,
        int maxDepth,
        int maxResults,
        double minConfidence = 0)
    {
        return Traverse(backend, root, kinds, maxDepth, maxResults, minConfidence, forward: true);
    }

    public IReadOnlyList<TraversalItem> TraverseReverse(
        ICodeMapQueryBackend backend,
        IndexedSymbol root,
        IReadOnlySet<EdgeKind> kinds,
        int maxDepth,
        int maxResults,
        double minConfidence = 0)
    {
        return Traverse(backend, root, kinds, maxDepth, maxResults, minConfidence, forward: false);
    }

    private static IReadOnlyList<TraversalItem> Traverse(
        ICodeMapQueryBackend backend,
        IndexedSymbol root,
        IReadOnlySet<EdgeKind> kinds,
        int maxDepth,
        int maxResults,
        double minConfidence,
        bool forward)
    {
        var result = new List<TraversalItem>();
        var visited = new HashSet<string>(StringComparer.Ordinal) { root.Id };
        var frontier = new[] { root.Id };
        for (var depth = 1; depth <= Math.Max(0, maxDepth) && frontier.Length > 0; depth++)
        {
            var next = new List<string>();
            foreach (var id in frontier.OrderBy(value => value, StringComparer.Ordinal))
            {
                var edges = (forward ? backend.Outgoing(id, kinds) : backend.Incoming(id, kinds))
                    .Where(edge => (edge.Confidence ?? 1.0) >= minConfidence)
                    .OrderBy(edge => forward ? edge.TargetId : edge.SourceId, StringComparer.Ordinal)
                    .ThenBy(edge => edge.Kind)
                    .ThenBy(edge => edge.Line ?? int.MaxValue);
                foreach (var edge in edges)
                {
                    var targetId = forward ? edge.TargetId : edge.SourceId;
                    if (!visited.Add(targetId))
                        continue;
                    var target = backend.FindById(targetId);
                    if (target is null)
                        continue;
                    result.Add(new TraversalItem(target, edge, depth));
                    next.Add(targetId);
                    if (result.Count >= Math.Max(1, maxResults))
                        return result;
                }
            }
            frontier = next.ToArray();
        }
        return result;
    }
}
