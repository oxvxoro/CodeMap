using CodeMap.Core.Models;

namespace CodeMap.Storage;

public sealed record RiskScore(
    string Level,
    int PublicApis,
    int Callers,
    int CrossProject,
    int Untested,
    double WeightedTotal);

public static class RiskScorer
{
    public const double HighThreshold = 8;
    public const double MediumThreshold = 4;

    public static RiskScore Score(
        IReadOnlyList<IndexedSymbol> changedSymbols,
        IReadOnlyList<ImpactItem> impact)
    {
        var publicApis = impact.Concat(changedSymbols.Select(symbol => new ImpactItem(symbol, DummyEdge, 0)))
            .Select(item => item.Symbol)
            .DistinctBy(symbol => symbol.Id)
            .Count(IsPublicApi);

        var callers = impact.Count(item => item.Via.Kind == EdgeKind.Calls);
        var changedProjects = changedSymbols.Select(symbol => symbol.Project).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var crossProject = impact.Count(item => !changedProjects.Contains(item.Symbol.Project));
        var untested = changedSymbols.Count(symbol =>
            !CodeMapQueryService.IsTestOnly(symbol)
            && !impact.Any(item => item.RootId == symbol.Id && CodeMapQueryService.IsTestOnly(item.Symbol)));

        var confidenceWeight = impact.Count == 0
            ? 1.0
            : impact.Average(item => item.Via.Confidence is null or 0 ? 1.0 : item.Via.Confidence.Value);

        var weighted = publicApis * 2
            + callers * confidenceWeight
            + crossProject * 2
            + untested * 3;
        var level = weighted >= HighThreshold ? "high" : weighted >= MediumThreshold ? "medium" : "low";
        return new RiskScore(level, publicApis, callers, crossProject, untested, Math.Round(weighted, 2));
    }

    private static bool IsPublicApi(IndexedSymbol symbol) =>
        string.Equals(symbol.Visibility, "public", StringComparison.OrdinalIgnoreCase)
        && symbol.Kind is NodeKind.Class or NodeKind.Struct or NodeKind.Record or NodeKind.Interface
            or NodeKind.Method or NodeKind.Property or NodeKind.Delegate;

    private static readonly IndexedEdge DummyEdge = new("", "", EdgeKind.References, null, null);
}
