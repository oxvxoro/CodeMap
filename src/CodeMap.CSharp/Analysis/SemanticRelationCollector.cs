using CodeMap.Core.Models;

namespace CodeMap.CSharp.Analysis;

public static class SemanticRelationCollector
{
    public static IReadOnlyList<CodeEdge> Collect(AnalysisResult result) =>
        result.Edges.Where(edge => edge.ResolutionKind == EdgeResolutionKind.Semantic).ToArray();
}
