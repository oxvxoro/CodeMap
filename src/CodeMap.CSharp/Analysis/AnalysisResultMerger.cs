using CodeMap.Core.Models;

namespace CodeMap.CSharp.Analysis;

public static class AnalysisResultMerger
{
    public static AnalysisResult Merge(params AnalysisResult[] results)
    {
        var nodes = results.SelectMany(result => result.Nodes)
            .GroupBy(result => result.Id, StringComparer.Ordinal)
            .Select(group => group.First()).ToArray();
        var edges = results.SelectMany(result => result.Edges)
            .GroupBy(EdgeIdentity.From)
            .Select(group => group.First()).ToArray();
        return new AnalysisResult { Nodes = nodes, Edges = edges };
    }
}
