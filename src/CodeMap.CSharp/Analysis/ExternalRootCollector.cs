using CodeMap.Core.Models;

namespace CodeMap.CSharp.Analysis;

public static class ExternalRootCollector
{
    public static IReadOnlySet<string> Collect(AnalysisResult result) =>
        result.Edges
            .Where(edge => edge.TargetId.StartsWith("external:", StringComparison.Ordinal))
            .Select(edge => edge.TargetId)
            .ToHashSet(StringComparer.Ordinal);
}
