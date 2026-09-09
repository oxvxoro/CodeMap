using CodeMap.Core.Contracts;
using CodeMap.Core.Models.Investigation;
using CodeMap.Core.Models;

namespace CodeMap.Engine.Application.Investigation;

internal sealed record InvestigationOrchestrationResult(
    IReadOnlyList<InvestigationCandidate> Candidates,
    InvestigationSelection Selection,
    IReadOnlyList<ProviderCoverageStatus> ProviderStatuses);

internal sealed class InvestigationOrchestrator(
    InvestigationRankingPolicy rankingPolicy,
    InvestigationBudgetAllocator budgetAllocator,
    Func<InvestigationGoal, IReadOnlyList<IInvestigationProvider>> providerFactory)
{
    public async Task<InvestigationOrchestrationResult> RunAsync(
        InvestigationGoal goal,
        ICodeMapGraphReader reader,
        IndexedSymbol root,
        InvestigationOverrides overrides,
        CancellationToken cancellationToken)
    {
        var candidates = new List<InvestigationCandidate>();
        var statuses = new List<ProviderCoverageStatus>();
        var providers = providerFactory(goal);

        foreach (var provider in providers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var result = await provider.CollectAsync(reader, root, overrides, cancellationToken);
                statuses.Add(result.Status);
                candidates.AddRange(result.Candidates);

                if (goal == InvestigationGoal.Understand && provider.Kind == InvestigationProviderKind.Members)
                {
                    var members = result.Candidates.Take(5).Select(candidate => candidate.Symbol).ToArray();
                    foreach (var member in members)
                    {
                        foreach (var secondary in providers.Where(candidateProvider => candidateProvider.Kind != InvestigationProviderKind.Members))
                        {
                            try
                            {
                                var memberResult = await secondary.CollectAsync(reader, member, overrides, cancellationToken);
                                statuses.Add(memberResult.Status);
                                candidates.AddRange(memberResult.Candidates);
                            }
                            catch (OperationCanceledException)
                            {
                                throw;
                            }
                            catch (Exception exception)
                            {
                                statuses.Add(ProviderCoverageStatus.Error(secondary.Kind, exception.Message));
                            }
                        }
                    }
                    break;
                }
                if (goal == InvestigationGoal.Trace && provider.Kind == InvestigationProviderKind.Flow && result.Candidates.Count > 0)
                    break;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                statuses.Add(ProviderCoverageStatus.Error(provider.Kind, exception.Message));
            }
        }

        if (!overrides.IncludeHeuristic)
            candidates.RemoveAll(candidate => candidate.CertaintyTier == CertaintyTier.Heuristic);

        var deduplicated = InvestigationCandidateDeduplicator.Deduplicate(candidates);
        var ranked = rankingPolicy.Rank(goal, deduplicated);
        var selection = budgetAllocator.Allocate(ranked, overrides.TokenBudget);
        return new InvestigationOrchestrationResult(selection.Selected, selection, statuses);
    }
}
