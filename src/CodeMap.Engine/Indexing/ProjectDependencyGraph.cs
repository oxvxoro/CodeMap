namespace CodeMap.Engine.Indexing;

public sealed class ProjectDependencyGraph
{
    public ProjectDependencyGraph(IReadOnlyDictionary<string, IReadOnlySet<string>> references)
    {
        References = references;
        Referencing = BuildReverse(references);
    }

    public IReadOnlyDictionary<string, IReadOnlySet<string>> References { get; }

    public IReadOnlyDictionary<string, IReadOnlySet<string>> Referencing { get; }

    public static ProjectDependencyGraph Create(IReadOnlyDictionary<string, IReadOnlyCollection<string>> references) =>
        new(references.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlySet<string>)pair.Value.ToHashSet(StringComparer.Ordinal),
            StringComparer.Ordinal));

    private static IReadOnlyDictionary<string, IReadOnlySet<string>> BuildReverse(
        IReadOnlyDictionary<string, IReadOnlySet<string>> references)
    {
        var reverse = references.Keys.ToDictionary(
            key => key,
            _ => (IReadOnlySet<string>)new HashSet<string>(StringComparer.Ordinal),
            StringComparer.Ordinal);
        foreach (var (project, dependencies) in references)
            foreach (var dependency in dependencies)
            {
                if (!reverse.TryGetValue(dependency, out var dependents))
                    reverse[dependency] = dependents = new HashSet<string>(StringComparer.Ordinal);
                ((HashSet<string>)dependents).Add(project);
            }
        return reverse;
    }
}
