using CodeMap.Core.Models;

namespace CodeMap.Engine.Application.Investigation;

public sealed record InvestigationCandidate(
    IndexedSymbol Symbol,
    IndexedEdge? Via,
    int Depth,
    string Provider,
    CertaintyTier CertaintyTier,
    double? Confidence,
    double ProfileRelevance,
    int EstimatedCost)
{
    public IReadOnlyList<string> AlsoFoundBy { get; init; } = Array.Empty<string>();

    /// <summary>Confidence is an evidence score, not a calibrated probability.</summary>
    public static InvestigationCandidate FromRelation(IndexedRelation relation, int depth, string provider, double relevance = 0)
    {
        var tier = relation.Edge.ResolutionKind.FromResolutionKind();
        return new InvestigationCandidate(
            relation.Symbol,
            relation.Edge,
            depth,
            provider,
            tier,
            relation.Edge.Confidence,
            relevance,
            EstimateCost(relation.Symbol, relation.Edge));
    }

    public static int EstimateCost(IndexedSymbol symbol, IndexedEdge? edge) =>
        Math.Max(1, (symbol.DisplayName.Length + (edge?.Kind.ToString().Length ?? 0) + 12 + 3) / 4);
}
