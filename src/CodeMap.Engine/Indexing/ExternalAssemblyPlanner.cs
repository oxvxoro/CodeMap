namespace CodeMap.Engine.Indexing;

public sealed record ExternalAssemblyPlan(IReadOnlySet<string> RequiredAssemblyPaths, IReadOnlySet<string> OrphanedAssemblyPaths);

/// <summary>Calculates external assembly retention and orphan cleanup only.</summary>
public static class ExternalAssemblyPlanner
{
    public static ExternalAssemblyPlan Plan(IEnumerable<string> previouslyRequiredAssemblyPaths, IEnumerable<string> currentlyRequiredAssemblyPaths)
    {
        ArgumentNullException.ThrowIfNull(previouslyRequiredAssemblyPaths);
        ArgumentNullException.ThrowIfNull(currentlyRequiredAssemblyPaths);
        var previous = Normalize(previouslyRequiredAssemblyPaths);
        var current = Normalize(currentlyRequiredAssemblyPaths);
        return new ExternalAssemblyPlan(current, previous.Except(current, StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    private static HashSet<string> Normalize(IEnumerable<string> paths) => paths.Where(path => !string.IsNullOrWhiteSpace(path)).Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
}
