using CodeMap.Core.Models;

namespace CodeMap.Web;

public static class WebRelationBuilder
{
    public static IReadOnlyList<CodeEdge> SemanticEdges(AnalysisResult result) => result.Edges.ToArray();
}
