using CodeMap.Core.Contracts;
using CodeMap.Core.Models.Investigation;
using CodeMap.Core.Models;
using CodeMap.Storage;

namespace CodeMap.Engine.Application.Investigation.Providers;

internal sealed class LocalSliceProvider(
    Func<IndexedSymbol, string, CancellationToken, Task<SemanticSliceResult>>? slice = null) : IInvestigationProvider
{
    public InvestigationProviderKind Kind => InvestigationProviderKind.LocalSlice;

    public async Task<InvestigationProviderResult> CollectAsync(ICodeMapGraphReader reader, IndexedSymbol root,
        InvestigationOverrides overrides, CancellationToken cancellationToken, int offset = 0, int? window = null)
    {
        if (!string.Equals(root.Language, "csharp", StringComparison.OrdinalIgnoreCase))
            return new([], ProviderCoverageStatus.Unsupported(Kind, "unsupported_language"));
        if (slice is null || string.IsNullOrWhiteSpace(overrides.ProjectRoot))
            return new([], ProviderCoverageStatus.Unavailable(Kind, "slice_unconfigured"));

        try
        {
            var result = await slice(root, overrides.ProjectRoot, cancellationToken);
            var normalizedOffset = Math.Max(0, offset);
            var candidates = result.Items
                .Select(item => new LocalSliceEvidence(
                    result.Scope.SymbolId,
                    item.Id,
                    item.Kind,
                    item.Symbol,
                    item.Display,
                    result.Scope.File,
                    item.Location,
                    result.Dependencies
                        .Where(dependency => dependency.Source == item.Id || dependency.Target == item.Id)
                        .ToArray()))
                .Select(evidence => InvestigationCandidate.FromLocalSlice(root, evidence))
                .Skip(normalizedOffset)
                .Take(Math.Min(overrides.MaxResults, Math.Max(1, window ?? overrides.MaxResults)))
                .ToArray();
            var hasMore = result.Items.Count > normalizedOffset + candidates.Length;
            var status = result.Truncated
                ? ProviderCoverageStatus.Partial(Kind, "max_results", candidates.Length)
                : hasMore
                    ? ProviderCoverageStatus.Partial(Kind, "window", candidates.Length)
                : ProviderCoverageStatus.Complete(Kind, candidates.Length);
            return new(candidates, status);
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
