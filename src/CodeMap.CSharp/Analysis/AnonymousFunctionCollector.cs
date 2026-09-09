using CodeMap.Core.Models;

namespace CodeMap.CSharp.Analysis;

public static class AnonymousFunctionCollector
{
    public static IReadOnlyList<CodeNode> Collect(AnalysisResult result) =>
        result.Nodes.Where(node => node.Kind is NodeKind.Function && node.Name.Contains("lambda", StringComparison.OrdinalIgnoreCase)).ToArray();
}
