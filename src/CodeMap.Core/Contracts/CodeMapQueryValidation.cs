namespace CodeMap.Core.Contracts;

public static class CodeMapQueryValidation
{
    public static bool IsValidImpactProfile(string profile) =>
        string.Equals(profile, "code", StringComparison.OrdinalIgnoreCase)
        || string.Equals(profile, "app", StringComparison.OrdinalIgnoreCase);

    public static bool IsValidFlowKind(string kind) =>
        string.Equals(kind, "http", StringComparison.OrdinalIgnoreCase)
        || string.Equals(kind, "ui", StringComparison.OrdinalIgnoreCase)
        || string.Equals(kind, "all", StringComparison.OrdinalIgnoreCase);

    public static bool IsValidFlowDepth(int depth) => depth is >= 1 and <= 8;
}
