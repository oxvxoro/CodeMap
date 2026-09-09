using CodeMap.Core.Contracts;
using CodeMap.Core.Models.Investigation;
using CodeMap.Core.Models;
using CodeMap.Engine.Application;
using CodeMap.CSharp;
using CodeMap.Storage;

namespace CodeMap.Engine.Application.Investigation.Providers;

internal sealed class LocalSliceProvider(
    Func<IndexedSymbol, string, CancellationToken, Task<SemanticSliceResult>>? slice = null) : IInvestigationProvider
{
    public InvestigationProviderKind Kind => InvestigationProviderKind.LocalSlice;

    public async Task<InvestigationProviderResult> CollectAsync(ICodeMapGraphReader reader, IndexedSymbol root,
        InvestigationOverrides overrides, CancellationToken cancellationToken)
    {
        if (!string.Equals(root.Language, "csharp", StringComparison.OrdinalIgnoreCase))
            return new([], ProviderCoverageStatus.Unsupported(Kind, "unsupported_language"));
        if (slice is null || string.IsNullOrWhiteSpace(overrides.ProjectRoot))
            return new([], ProviderCoverageStatus.Unavailable(Kind, "slice_unconfigured"));

        try
        {
            var result = await slice(root, overrides.ProjectRoot, cancellationToken);
            var candidates = result.Items
                .Where(item => !string.IsNullOrWhiteSpace(item.Symbol))
                .SelectMany(item => reader.Find(item.Symbol!, 1).Select(symbol => new InvestigationCandidate(
                    symbol, null, 1, "localSlice", CertaintyTier.Semantic, null, 0,
                    InvestigationCandidate.EstimateCost(symbol, null))))
                .Take(overrides.MaxResults)
                .ToArray();
            return new(candidates, ProviderCoverageStatus.Complete(Kind, candidates.Length));
        }
        catch (SemanticSliceException exception) when (exception.Code is "unsupported_language" or "unsupported_symbol_kind" or "unsupported_scope")
        {
            return new([], ProviderCoverageStatus.Unsupported(Kind, exception.Code));
        }
        catch (SemanticSliceException exception) when (exception.Code is "semantic_slice_stale_index" or "source_not_found")
        {
            return new([], ProviderCoverageStatus.Unavailable(Kind, exception.Code));
        }
    }
}
