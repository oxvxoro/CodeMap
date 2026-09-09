using CodeMap.Core.Models;

namespace CodeMap.CSharp.Analysis;

public static class DeclarationCollector
{
    public static IReadOnlyList<CodeNode> Collect(AnalysisResult result) =>
        result.Nodes.Where(node => node.Kind is not NodeKind.File).ToArray();
}
