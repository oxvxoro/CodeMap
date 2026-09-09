namespace CodeMap.Storage.Queries;

/// <summary>
/// Validation predicates for transport-independent query contracts.
/// </summary>
public static class QueryValidation
{
    public static bool IsValidImpactProfile(string? profile) => profile is "code" or "app";

    public static bool IsValidFlowKind(string? kind) => kind is "http" or "ui" or "all";

    public static bool IsValidFlowDepth(int depth) => depth is >= QueryLimits.FlowMinDepth and <= QueryLimits.FlowMaxDepth;

    public static bool IsValidConfidence(double value) =>
        double.IsFinite(value) && value is >= 0 and <= 1;

    public static void ValidateConfidence(double value, string parameterName = "minConfidence")
    {
        if (!IsValidConfidence(value))
            throw new ArgumentOutOfRangeException(parameterName, value, "Confidence must be a finite number between 0 and 1.");
    }
}
