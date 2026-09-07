namespace CodeMap.Core.Models;

public sealed class AnalysisResult
{
    public IReadOnlyList<CodeNode> Nodes { get; init; } = Array.Empty<CodeNode>();

    public IReadOnlyList<CodeEdge> Edges { get; init; } = Array.Empty<CodeEdge>();
}
