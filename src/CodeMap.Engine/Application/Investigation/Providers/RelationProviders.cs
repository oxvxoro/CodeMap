using CodeMap.Core.Contracts;
using CodeMap.Core.Models;
using CodeMap.Core.Models.Investigation;
using CodeMap.Engine.Application;

namespace CodeMap.Engine.Application.Investigation.Providers;

internal sealed class CallersProvider : IInvestigationProvider
{
    public InvestigationProviderKind Kind => InvestigationProviderKind.Callers;

    public Task<InvestigationProviderResult> CollectAsync(ICodeMapGraphReader reader, IndexedSymbol root,
        InvestigationOverrides overrides, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var relations = reader.CallerRelations(root, overrides.MaxResults)
            .Where(relation => (relation.Edge.Confidence ?? 1) >= overrides.MinConfidence)
            .ToArray();
        return Task.FromResult(CreateResult(relations, root, Kind, "callers", overrides.MaxResults));
    }

    internal static InvestigationProviderResult CreateResult(
        IReadOnlyList<IndexedRelation> relations,
        IndexedSymbol root,
        InvestigationProviderKind kind,
        string provider,
        int maxResults) =>
        new(
            relations.Select(relation => InvestigationCandidate.FromRelation(relation, 1, provider)).ToArray(),
            relations.Count >= maxResults
                ? ProviderCoverageStatus.Partial(kind, "max_results", relations.Count)
                : ProviderCoverageStatus.Complete(kind, relations.Count));
}

internal sealed class CalleesProvider : IInvestigationProvider
{
    public InvestigationProviderKind Kind => InvestigationProviderKind.Callees;

    public Task<InvestigationProviderResult> CollectAsync(ICodeMapGraphReader reader, IndexedSymbol root,
        InvestigationOverrides overrides, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var relations = reader.CalleeRelations(root, overrides.Depth, overrides.MaxResults)
            .Where(relation => (relation.Edge.Confidence ?? 1) >= overrides.MinConfidence)
            .ToArray();
        return Task.FromResult(CallersProvider.CreateResult(relations, root, Kind, "callees", overrides.MaxResults));
    }
}

internal sealed class ImplementationsProvider : IInvestigationProvider
{
    public InvestigationProviderKind Kind => InvestigationProviderKind.Implementations;

    public Task<InvestigationProviderResult> CollectAsync(ICodeMapGraphReader reader, IndexedSymbol root,
        InvestigationOverrides overrides, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var relations = reader.ImplementationRelations(root, overrides.MaxResults)
            .Where(relation => (relation.Edge.Confidence ?? 1) >= overrides.MinConfidence)
            .ToArray();
        return Task.FromResult(CallersProvider.CreateResult(relations, root, Kind, "implementations", overrides.MaxResults));
    }
}

internal sealed class FlowProvider : IInvestigationProvider
{
    public InvestigationProviderKind Kind => InvestigationProviderKind.Flow;

    public Task<InvestigationProviderResult> CollectAsync(ICodeMapGraphReader reader, IndexedSymbol root,
        InvestigationOverrides overrides, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var items = reader.Flow(root, "all", overrides.Depth, overrides.MaxResults, overrides.MinConfidence);
        var candidates = items.Select(item => new InvestigationCandidate(
            item.Symbol, item.Via, item.Depth, "flow", item.Via.ResolutionKind.FromResolutionKind(),
            item.Via.Confidence, 0, InvestigationCandidate.EstimateCost(item.Symbol, item.Via))).ToArray();
        var status = items.Count >= overrides.MaxResults
            ? ProviderCoverageStatus.Partial(Kind, "max_results", items.Count)
            : ProviderCoverageStatus.Complete(Kind, items.Count);
        return Task.FromResult(new InvestigationProviderResult(candidates, status));
    }
}

internal sealed class ImpactProvider : IInvestigationProvider
{
    public InvestigationProviderKind Kind => InvestigationProviderKind.Impact;

    public Task<InvestigationProviderResult> CollectAsync(ICodeMapGraphReader reader, IndexedSymbol root,
        InvestigationOverrides overrides, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var items = reader.Impact(root, overrides.Depth, overrides.MaxResults, "app");
        var candidates = items.Select(item => new InvestigationCandidate(
            item.Symbol, item.Via, item.Depth, "impact", item.Via.ResolutionKind.FromResolutionKind(),
            item.Via.Confidence, 0, InvestigationCandidate.EstimateCost(item.Symbol, item.Via))).ToArray();
        var status = items.Count >= overrides.MaxResults
            ? ProviderCoverageStatus.Partial(Kind, "max_results", items.Count)
            : ProviderCoverageStatus.Complete(Kind, items.Count);
        return Task.FromResult(new InvestigationProviderResult(candidates, status));
    }
}

internal sealed class MembersProvider : IInvestigationProvider
{
    public InvestigationProviderKind Kind => InvestigationProviderKind.Members;

    public Task<InvestigationProviderResult> CollectAsync(ICodeMapGraphReader reader, IndexedSymbol root,
        InvestigationOverrides overrides, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var symbols = reader.Members(root, overrides.MaxResults);
        var candidates = symbols.Select(symbol => new InvestigationCandidate(
            symbol, null, 1, "members", CertaintyTier.Semantic, null, 0,
            InvestigationCandidate.EstimateCost(symbol, null))).ToArray();
        var status = symbols.Count >= overrides.MaxResults
            ? ProviderCoverageStatus.Partial(Kind, "max_results", symbols.Count)
            : ProviderCoverageStatus.Complete(Kind, symbols.Count);
        return Task.FromResult(new InvestigationProviderResult(candidates, status));
    }
}
