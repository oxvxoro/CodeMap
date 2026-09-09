namespace CodeMap.Core.Models;

public enum IndexLifecycleState
{
    Missing,
    Building,
    Ready,
    Updating,
    Stale,
    Failed,
    RebuildRequired
}

public static class IndexLifecycleStateParser
{
    public static IndexLifecycleState Parse(string? value) => value?.ToLowerInvariant() switch
    {
        "building" => IndexLifecycleState.Building,
        "ready" => IndexLifecycleState.Ready,
        "updating" => IndexLifecycleState.Updating,
        "stale" => IndexLifecycleState.Stale,
        "failed" => IndexLifecycleState.Failed,
        "rebuild_required" => IndexLifecycleState.RebuildRequired,
        _ => IndexLifecycleState.Missing
    };
}
