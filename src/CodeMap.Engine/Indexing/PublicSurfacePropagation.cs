namespace CodeMap.Engine.Indexing;

public sealed record PropagationPlan(
    IReadOnlySet<string> ProjectsToAnalyze,
    IReadOnlyDictionary<string, string> SkippedDependents);

/// <summary>
/// Pure breadth-first propagation over the reverse project graph.
/// </summary>
public static class PublicSurfacePropagation
{
    public static PropagationPlan Plan(
        IReadOnlySet<string> dirtyProjects,
        ProjectDependencyGraph graph,
        IReadOnlyDictionary<string, string?> previousFingerprints,
        IReadOnlyDictionary<string, string?> newFingerprints)
    {
        var selected = dirtyProjects.ToHashSet(StringComparer.Ordinal);
        var skipped = new Dictionary<string, string>(StringComparer.Ordinal);
        var queue = new Queue<string>(dirtyProjects.OrderBy(project => project, StringComparer.Ordinal));
        var visited = new HashSet<string>(dirtyProjects, StringComparer.Ordinal);
        while (queue.Count > 0)
        {
            var project = queue.Dequeue();
            if (!graph.Referencing.TryGetValue(project, out var dependents))
                continue;
            var unchanged = newFingerprints.TryGetValue(project, out var current)
                && current is not null
                && previousFingerprints.TryGetValue(project, out var previous)
                && previous is not null
                && string.Equals(previous, current, StringComparison.Ordinal);
            foreach (var dependent in dependents.OrderBy(item => item, StringComparer.Ordinal))
            {
                if (unchanged)
                {
                    skipped[dependent] = $"{project} public surface fingerprint unchanged";
                    continue;
                }
                if (visited.Add(dependent))
                {
                    selected.Add(dependent);
                    queue.Enqueue(dependent);
                }
            }
        }
        return new PropagationPlan(selected, skipped);
    }
}
