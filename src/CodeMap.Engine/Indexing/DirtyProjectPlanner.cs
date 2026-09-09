namespace CodeMap.Engine.Indexing;

public sealed record DirtyProjectPlan(
    IReadOnlySet<string> ProjectsToAnalyze,
    IReadOnlySet<string> RemovedProjects,
    IReadOnlyDictionary<string, string> SkippedDependents,
    RepositoryChangeSet Changes);

/// <summary>Combines change detection with public-surface propagation without analyzer or database access.</summary>
public sealed class DirtyProjectPlanner
{
    public DirtyProjectPlan Plan(
        RepositoryChangeSet changes,
        ProjectDependencyGraph graph,
        IReadOnlyDictionary<string, string?> previousFingerprints,
        IReadOnlyDictionary<string, string?> newFingerprints)
    {
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(previousFingerprints);
        ArgumentNullException.ThrowIfNull(newFingerprints);
        var propagation = PublicSurfacePropagation.Plan(changes.DirtyProjects, graph, previousFingerprints, newFingerprints);
        return new DirtyProjectPlan(propagation.ProjectsToAnalyze, changes.RemovedProjects, propagation.SkippedDependents, changes);
    }
}
