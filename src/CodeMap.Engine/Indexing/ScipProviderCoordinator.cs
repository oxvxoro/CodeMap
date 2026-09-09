namespace CodeMap.Engine.Indexing;

public sealed record ScipProviderArtifact(string Name, string Path, string ContentHash);
public sealed record ScipProviderPlan(IReadOnlyList<ScipProviderArtifact> Added, IReadOnlyList<ScipProviderArtifact> Updated, IReadOnlyList<ScipProviderArtifact> Unchanged, IReadOnlyList<ScipProviderArtifact> Removed);

/// <summary>Deterministic reconciliation of optional SCIP provider artifacts.</summary>
public static class ScipProviderCoordinator
{
    public static ScipProviderPlan Reconcile(IEnumerable<ScipProviderArtifact> previous, IEnumerable<ScipProviderArtifact> current)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);
        var oldByName = previous.ToDictionary(item => item.Name, StringComparer.Ordinal);
        var newByName = current.ToDictionary(item => item.Name, StringComparer.Ordinal);
        var added = new List<ScipProviderArtifact>();
        var updated = new List<ScipProviderArtifact>();
        var unchanged = new List<ScipProviderArtifact>();
        foreach (var item in newByName.Values.OrderBy(item => item.Name, StringComparer.Ordinal))
        {
            if (!oldByName.TryGetValue(item.Name, out var old)) added.Add(item);
            else if (!string.Equals(old.ContentHash, item.ContentHash, StringComparison.Ordinal)) updated.Add(item);
            else unchanged.Add(item);
        }
        var removed = oldByName.Values.Where(item => !newByName.ContainsKey(item.Name)).OrderBy(item => item.Name, StringComparer.Ordinal).ToArray();
        return new ScipProviderPlan(added, updated, unchanged, removed);
    }
}
