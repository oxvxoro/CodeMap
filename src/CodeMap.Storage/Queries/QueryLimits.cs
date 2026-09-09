namespace CodeMap.Storage.Queries;

/// <summary>
/// Defaults and normalization rules shared by all query entry points.
/// Keeping these values in one place prevents the snapshot and SQLite paths
/// from acquiring different implicit limits during refactoring.
/// </summary>
public static class QueryLimits
{
    public const int DefaultMaxResults = 20;
    public const int DefaultSymbolsInFilesMaxResults = 500;
    public const int DefaultMemberMaxResults = 8;

    public const int FlowMinDepth = 1;
    public const int FlowMaxDepth = 8;
    public const int FlowDefaultDepth = 4;

    public const double DefaultMinConfidence = 0;

    public static int NormalizeMaxResults(int value) => Math.Max(1, value);

    public static int NormalizeImpactDepth(int value) => Math.Max(0, value);

    public static int NormalizeTraversalDepth(int value) => Math.Max(1, value);

    public static int ClampFlowDepth(int value) => Math.Clamp(value, FlowMinDepth, FlowMaxDepth);

    public static int NormalizeTokenBudget(int value) => Math.Max(1, value);
}
