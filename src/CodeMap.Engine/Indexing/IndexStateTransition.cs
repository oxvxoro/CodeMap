using CodeMap.Core.Models;

namespace CodeMap.Engine.Indexing;

public static class IndexStateTransition
{
    public static bool CanTransition(IndexLifecycleState from, IndexLifecycleState to) => (from, to) switch
    {
        (IndexLifecycleState.Missing, IndexLifecycleState.Building) => true,
        (IndexLifecycleState.Building, IndexLifecycleState.Ready) => true,
        (IndexLifecycleState.Building, IndexLifecycleState.Failed) => true,
        (IndexLifecycleState.Ready, IndexLifecycleState.Updating) => true,
        (IndexLifecycleState.Ready, IndexLifecycleState.Stale) => true,
        (IndexLifecycleState.Updating, IndexLifecycleState.Ready) => true,
        (IndexLifecycleState.Updating, IndexLifecycleState.Failed) => true,
        (IndexLifecycleState.Failed, IndexLifecycleState.Ready) => true,
        (IndexLifecycleState.Failed, IndexLifecycleState.RebuildRequired) => true,
        (IndexLifecycleState.Stale, IndexLifecycleState.Updating) => true,
        (IndexLifecycleState.Stale, IndexLifecycleState.Ready) => true,
        (IndexLifecycleState.RebuildRequired, IndexLifecycleState.Building) => true,
        _ when from == to => true,
        _ => false
    };

    public static IndexLifecycleState Require(IndexLifecycleState from, IndexLifecycleState to)
    {
        if (!CanTransition(from, to))
            throw new InvalidOperationException($"Invalid CodeMap index lifecycle transition: {from} -> {to}.");
        return to;
    }
}
