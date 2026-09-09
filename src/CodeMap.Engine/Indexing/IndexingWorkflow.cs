using CodeMap.Core.Models;

namespace CodeMap.Engine.Indexing;

public sealed record IndexWorkflowResult(
    DirtyProjectPlan Plan,
    IReadOnlyList<AnalyzedProject> AnalyzedProjects);

/// <summary>
/// Explicit indexing pipeline: plan, analyze, then commit. The legacy
/// IncrementalCodeMapIndexer can adopt this workflow incrementally without
/// changing its public IndexAsync/UpdateAsync contract.
/// </summary>
public sealed class IndexingWorkflow(
    DirtyProjectPlanner planner,
    AnalysisCoordinator analysis,
    IndexCommitter committer)
{
    public async Task<IndexWorkflowResult> RunAsync(
        RepositoryChangeSet changes,
        ProjectDependencyGraph graph,
        IReadOnlyDictionary<string, string?> previousFingerprints,
        IReadOnlyDictionary<string, string?> newFingerprints,
        IEnumerable<AnalysisWorkItem> workItems,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(workItems);
        var plan = planner.Plan(changes, graph, previousFingerprints, newFingerprints);
        var selected = workItems.Where(item => plan.ProjectsToAnalyze.Contains(item.ProjectName)).ToArray();
        var analyzed = await analysis.AnalyzeAsync(selected, cancellationToken).ConfigureAwait(false);
        await committer.CommitAsync(
            new IndexCommitRequest(analyzed, plan.RemovedProjects, metadata ?? new Dictionary<string, string>(StringComparer.Ordinal)),
            cancellationToken).ConfigureAwait(false);
        return new IndexWorkflowResult(plan, analyzed);
    }
}
