using CodeMap.Core.Models;

namespace CodeMap.Storage;

public static class RelationEvidenceMapper
{












    internal static string Map(IndexedEdge edge, IndexedSymbol source, IndexedSymbol target)
    {
        if (edge.ResolutionKind == EdgeResolutionKind.Semantic)
            return "semantic";

        switch (edge.Kind)
        {
            case EdgeKind.UsesCss:
                return "css-selector";
            case EdgeKind.UsesElement:
                return "dom-selector";
            case EdgeKind.Renders:
                return "razor-component-tag";
            case EdgeKind.HandlesEvent:
                return "markup-event-binding";
            case EdgeKind.UsesViewModel:
                return "xaml-viewmodel-reference";
            case EdgeKind.BindsTo:
                return "markup-data-binding";
            case EdgeKind.Imports or EdgeKind.References:
                return edge.Confidence switch
                {
                    >= 0.85 and <= 0.90 => "static-import",
                    >= 0.70 and < 0.85 => "same-file-fallback",
                    >= 0.60 and < 0.70 => "name-fallback",
                    _ => "unknown"
                };
            default:
                return "unknown";
        }
    }

    public static RelationEvidence FromEdge(IndexedEdge edge, IndexedSymbol source, IndexedSymbol target, string? file, int? line) =>
        new(Map(edge, source, target), file, line, edge.ResolutionKind, edge.Confidence);
}
