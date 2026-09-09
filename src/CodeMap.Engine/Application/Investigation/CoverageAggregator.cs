using CodeMap.Core.Contracts;
using CodeMap.Core.Models;
using CodeMap.Core.Models.Investigation;

namespace CodeMap.Engine.Application.Investigation;

internal sealed class CoverageAggregator
{
    public InvestigationCoverage Aggregate(
        IReadOnlyList<ProviderCoverageStatus> providerStatuses,
        IReadOnlyList<InvestigationCandidate> allCandidatesBeforeBudget,
        IReadOnlyList<InvestigationCandidate> selectedAfterBudget,
        ICodeMapGraphReader reader,
        IndexedSymbol root,
        InvestigationRequest request)
    {
        var selectedIds = selectedAfterBudget
            .Select(InvestigationCandidateKey.From)
            .ToHashSet();
        var excluded = allCandidatesBeforeBudget
            .Where(candidate => !selectedIds.Contains(InvestigationCandidateKey.From(candidate)))
            .ToArray();
        var remaining = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["semantic"] = excluded.Count(candidate => candidate.CertaintyTier == CertaintyTier.Semantic),
            ["syntactic"] = excluded.Count(candidate => candidate.CertaintyTier == CertaintyTier.Syntactic),
            ["heuristic"] = excluded.Count(candidate => candidate.CertaintyTier == CertaintyTier.Heuristic),
            ["external"] = excluded.Count(candidate => candidate.Symbol.Project.StartsWith("external:", StringComparison.OrdinalIgnoreCase))
        };
        var excludedByProvider = excluded
            .SelectMany(candidate => ProviderNames(candidate).Select(provider => (provider, candidate)))
            .GroupBy(item => item.provider, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var negativeEvidence = providerStatuses
            .Where(status => status.State == ProviderCoverageState.Complete)
            .Where(status => !excludedByProvider.TryGetValue(ProviderName(status.Provider), out var count) || count == 0)
            .Select(status => $"{ProviderName(status.Provider)}: complete, remaining=0")
            .ToArray();
        return new InvestigationCoverage(providerStatuses, remaining, negativeEvidence);
    }

    private static IEnumerable<string> ProviderNames(InvestigationCandidate candidate) =>
        new[] { candidate.Provider }.Concat(candidate.AlsoFoundBy).Select(provider => provider.ToLowerInvariant()).Distinct(StringComparer.OrdinalIgnoreCase);

    private static string ProviderName(InvestigationProviderKind provider) => provider switch
    {
        InvestigationProviderKind.LocalSlice => "localslice",
        _ => provider.ToString().ToLowerInvariant()
    };
}
