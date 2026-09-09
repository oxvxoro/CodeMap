using CodeMap.Core.Models;

namespace CodeMap.CSharp.Analysis;

/// <summary>
/// Stable, location-aware identity shared by declaration, semantic, and
/// application-enrichment stages. Keeping this value object independent from
/// Roslyn objects prevents duplicate edges when stages are merged.
/// </summary>
internal readonly record struct EdgeIdentity(
    string SourceId,
    string TargetId,
    EdgeKind Kind,
    int? StartLine,
    int? StartColumn,
    int? EndLine,
    int? EndColumn)
{
    public static EdgeIdentity From(CodeEdge edge) => new(
        edge.SourceId,
        edge.TargetId,
        edge.Kind,
        edge.SourceLocation?.StartLine,
        edge.SourceLocation?.StartColumn,
        edge.SourceLocation?.EndLine,
        edge.SourceLocation?.EndColumn);

    public override string ToString() =>
        $"{SourceId}\u001f{TargetId}\u001f{Kind}\u001f{StartLine}\u001f{StartColumn}\u001f{EndLine}\u001f{EndColumn}";
}
