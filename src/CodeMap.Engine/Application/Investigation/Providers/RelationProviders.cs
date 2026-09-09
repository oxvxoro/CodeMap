using CodeMap.Core.Contracts;
using CodeMap.Core.Models;
using CodeMap.Core.Models.Investigation;
using CodeMap.Engine.Application;

namespace CodeMap.Engine.Application.Investigation.Providers;

internal sealed class CallersProvider : IInvestigationProvider
{
    public InvestigationProviderKind Kind => InvestigationProviderKind.Callers;

    public Task<InvestigationProviderResult> CollectAsync(ICodeMapGraphReader reader, IndexedSymbol root,
        InvestigationOverrides overrides, CancellationToken cancellationToken, int offset = 0, int? window = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var page = reader.CallerRelationsPaged(root, EffectiveWindow(overrides, window), offset, overrides.MinConfidence);
        return Task.FromResult(CreateResult(page.Items, root, Kind, "callers", EffectiveWindow(overrides, window), page.HasMore));
    }

    internal static InvestigationProviderResult CreateResult(
        IReadOnlyList<IndexedRelation> relations,
        IndexedSymbol root,
        InvestigationProviderKind kind,
        string provider,
        int maxResults,
        bool hasMore = false) =>
        new(
            relations.Select(relation => InvestigationCandidate.FromRelation(relation, 1, provider)).ToArray(),
            hasMore
                ? ProviderCoverageStatus.Partial(kind, "has_more", relations.Count)
                : ProviderCoverageStatus.Complete(kind, relations.Count));

    private static int EffectiveWindow(InvestigationOverrides overrides, int? window) =>
        Math.Min(overrides.MaxResults, Math.Max(1, window ?? overrides.MaxResults));
}

internal sealed class CalleesProvider : IInvestigationProvider
{
    public InvestigationProviderKind Kind => InvestigationProviderKind.Callees;

    public Task<InvestigationProviderResult> CollectAsync(ICodeMapGraphReader reader, IndexedSymbol root,
        InvestigationOverrides overrides, CancellationToken cancellationToken, int offset = 0, int? window = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var page = reader.CalleeRelationsPaged(root, overrides.Depth, EffectiveWindow(overrides, window), offset, overrides.MinConfidence);
        return Task.FromResult(CallersProvider.CreateResult(page.Items, root, Kind, "callees", EffectiveWindow(overrides, window), page.HasMore));
    }

    private static int EffectiveWindow(InvestigationOverrides overrides, int? window) =>
        Math.Min(overrides.MaxResults, Math.Max(1, window ?? overrides.MaxResults));
}

internal sealed class ImplementationsProvider : IInvestigationProvider
{
    public InvestigationProviderKind Kind => InvestigationProviderKind.Implementations;

    public Task<InvestigationProviderResult> CollectAsync(ICodeMapGraphReader reader, IndexedSymbol root,
        InvestigationOverrides overrides, CancellationToken cancellationToken, int offset = 0, int? window = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var page = reader.ImplementationRelationsPaged(root, EffectiveWindow(overrides, window), offset, overrides.MinConfidence);
        return Task.FromResult(CallersProvider.CreateResult(page.Items, root, Kind, "implementations", EffectiveWindow(overrides, window), page.HasMore));
    }

    private static int EffectiveWindow(InvestigationOverrides overrides, int? window) =>
        Math.Min(overrides.MaxResults, Math.Max(1, window ?? overrides.MaxResults));
}

internal sealed class FlowProvider : IInvestigationProvider
{
    public InvestigationProviderKind Kind => InvestigationProviderKind.Flow;

    public Task<InvestigationProviderResult> CollectAsync(ICodeMapGraphReader reader, IndexedSymbol root,
        InvestigationOverrides overrides, CancellationToken cancellationToken, int offset = 0, int? window = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var page = reader.FlowPaged(root, "all", overrides.Depth, EffectiveWindow(overrides, window), offset, overrides.MinConfidence);
        var items = page.Items;
        var candidates = items.Select(item => new InvestigationCandidate(
            item.Symbol, item.Via, item.Depth, "flow", item.Via.ResolutionKind.FromResolutionKind(),
            item.Via.Confidence, 0, InvestigationCandidate.EstimateCost(item.Symbol, item.Via))).ToArray();
        var status = page.HasMore
            ? ProviderCoverageStatus.Partial(Kind, "has_more", items.Count)
            : ProviderCoverageStatus.Complete(Kind, items.Count);
        return Task.FromResult(new InvestigationProviderResult(candidates, status));
    }

    private static int EffectiveWindow(InvestigationOverrides overrides, int? window) =>
        Math.Min(overrides.MaxResults, Math.Max(1, window ?? overrides.MaxResults));
}

internal sealed class ImpactProvider : IInvestigationProvider
{
    public InvestigationProviderKind Kind => InvestigationProviderKind.Impact;

    public Task<InvestigationProviderResult> CollectAsync(ICodeMapGraphReader reader, IndexedSymbol root,
        InvestigationOverrides overrides, CancellationToken cancellationToken, int offset = 0, int? window = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var page = reader.ImpactPaged(root, overrides.Depth, EffectiveWindow(overrides, window), offset, "app");
        var items = page.Items;
        var candidates = items.Select(item => new InvestigationCandidate(
            item.Symbol, item.Via, item.Depth, "impact", item.Via.ResolutionKind.FromResolutionKind(),
            item.Via.Confidence, 0, InvestigationCandidate.EstimateCost(item.Symbol, item.Via))).ToArray();
        var status = page.HasMore
            ? ProviderCoverageStatus.Partial(Kind, "has_more", items.Count)
            : ProviderCoverageStatus.Complete(Kind, items.Count);
        return Task.FromResult(new InvestigationProviderResult(candidates, status));
    }

    private static int EffectiveWindow(InvestigationOverrides overrides, int? window) =>
        Math.Min(overrides.MaxResults, Math.Max(1, window ?? overrides.MaxResults));
}

internal sealed class MembersProvider : IInvestigationProvider
{
    public InvestigationProviderKind Kind => InvestigationProviderKind.Members;

    public Task<InvestigationProviderResult> CollectAsync(ICodeMapGraphReader reader, IndexedSymbol root,
        InvestigationOverrides overrides, CancellationToken cancellationToken, int offset = 0, int? window = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var page = reader.MembersPaged(root, EffectiveWindow(overrides, window), offset);
        var symbols = page.Items;
        var candidates = symbols.Select(symbol => new InvestigationCandidate(
            symbol, null, 1, "members", CertaintyTier.Semantic, null, 0,
            InvestigationCandidate.EstimateCost(symbol, null))).ToArray();
        var status = page.HasMore
            ? ProviderCoverageStatus.Partial(Kind, "has_more", symbols.Count)
            : ProviderCoverageStatus.Complete(Kind, symbols.Count);
        return Task.FromResult(new InvestigationProviderResult(candidates, status));
    }

    private static int EffectiveWindow(InvestigationOverrides overrides, int? window) =>
        Math.Min(overrides.MaxResults, Math.Max(1, window ?? overrides.MaxResults));
}
