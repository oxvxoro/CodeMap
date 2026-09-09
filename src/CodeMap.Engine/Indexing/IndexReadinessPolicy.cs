using CodeMap.Core.Models;
using CodeMap.Engine.Application;

namespace CodeMap.Engine.Indexing;

public sealed record IndexReadinessDecision(
    bool CanQuery,
    bool Stale,
    QueryError? Error = null);

public static class IndexReadinessPolicy
{
    public static IndexReadinessDecision Evaluate(
        IndexLifecycleState state,
        bool schemaOutdated = false,
        bool analyzerVersionsOutdated = false,
        bool previousReadySnapshotAvailable = false)
    {
        if (schemaOutdated)
            return new(false, false, new("schema_outdated", "The CodeMap index schema is outdated. Run: codemap index --force"));
        if (analyzerVersionsOutdated)
            return new(false, false, new("analyzer_outdated", "The CodeMap analyzer version is outdated. Run: codemap index --force"));

        return state switch
        {
            IndexLifecycleState.Missing or IndexLifecycleState.RebuildRequired =>
                new(false, false, new("index_not_found", "No CodeMap index found.\nRun: codemap index")),
            IndexLifecycleState.Building =>
                new(false, true, new("index_building", "CodeMap index is being rebuilt. Try again shortly.")),
            IndexLifecycleState.Failed =>
                new(false, true, new("index_failed", "The CodeMap index update failed. Run: codemap index --force")),
            IndexLifecycleState.Stale => new(true, true),
            IndexLifecycleState.Updating when previousReadySnapshotAvailable => new(true, true),
            IndexLifecycleState.Updating => new(false, true, new("index_updating", "CodeMap index is being updated. Try again shortly.")),
            _ => new(true, false)
        };
    }
}
