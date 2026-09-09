using CodeMap.Core.Contracts;
using CodeMap.Core.Models;
using CodeMap.Core.Models.Investigation;

namespace CodeMap.Engine.Application.Investigation;

internal interface IInvestigationProvider
{
    InvestigationProviderKind Kind { get; }

    Task<InvestigationProviderResult> CollectAsync(
        ICodeMapGraphReader reader,
        IndexedSymbol root,
        InvestigationOverrides overrides,
        CancellationToken cancellationToken);
}

public sealed record InvestigationProviderResult(
    IReadOnlyList<InvestigationCandidate> Candidates,
    ProviderCoverageStatus Status);

public sealed record InvestigationOverrides(
    int MaxResults,
    int Depth,
    double MinConfidence,
    bool IncludeHeuristic,
    int TokenBudget,
    string? ProjectRoot = null);
