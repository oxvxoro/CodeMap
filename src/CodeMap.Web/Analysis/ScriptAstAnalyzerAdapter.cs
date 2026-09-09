using CodeMap.Core.Models;

namespace CodeMap.Web;

/// <summary>Adapter seam for AST script analysis; the legacy analyzer remains the source of truth during migration.</summary>
public static class ScriptAstAnalyzerAdapter
{
    public static IReadOnlyList<CodeEdge> ExtractRelations(AnalysisResult result) => result.Edges.ToArray();
}
