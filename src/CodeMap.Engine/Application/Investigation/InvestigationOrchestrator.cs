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
    Func<InvestigationGoal, IReadOnlyList<InvestigationProviderPlan>> providerFactory)
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

        for (var providerIndex = 0; providerIndex < providers.Count; providerIndex++)
        {
            var executionPlan = providers[providerIndex];
            var provider = executionPlan.Provider;
            cancellationToken.ThrowIfCancellationRequested();
            if (candidates.Count >= overrides.MaxResults)
            {
                AddNotRun(statuses, providers.Skip(providerIndex), "global_cap");
                break;
            }

            try
            {
                var plan = executionPlan.Policy;
                var providerCandidates = new List<InvestigationCandidate>();
                var offset = 0;
                var window = Math.Min(plan.InitialWindow, Math.Max(1, overrides.MaxResults - candidates.Count));
                var finalStatus = ProviderCoverageStatus.Complete(provider.Kind, 0);
                while (window > 0 && offset < plan.MaxWindow)
                {
                    var result = await provider.CollectAsync(reader, root, overrides, cancellationToken, offset, window);
                    providerCandidates.AddRange(result.Candidates);
                    offset += result.Candidates.Count;
                    finalStatus = result.Status;
                    if (result.Status.State != ProviderCoverageState.Partial || result.Candidates.Count == 0)
                        break;
                    window = Math.Min(plan.ExpansionWindow, plan.MaxWindow - offset);
                    window = Math.Min(window, Math.Max(0, overrides.MaxResults - candidates.Count - providerCandidates.Count));
                }

                var available = Math.Max(0, overrides.MaxResults - candidates.Count);
                var reachedGlobalCap = providerCandidates.Count > available
                    || providerCandidates.Count == available && finalStatus.State == ProviderCoverageState.Partial;
                candidates.AddRange(providerCandidates.Take(available));
                statuses.Add(reachedGlobalCap
                    ? ProviderCoverageStatus.Partial(provider.Kind, "global_cap", candidates.Count)
                    : finalStatus with { FoundCount = providerCandidates.Count });

                if (goal == InvestigationGoal.Understand && provider.Kind == InvestigationProviderKind.Members)
                {
                    var members = providerCandidates.Take(5).Select(candidate => candidate.Symbol).ToArray();
                    foreach (var member in members)
                    {
                        foreach (var secondaryPlan in providers.Where(candidateProvider => candidateProvider.Provider.Kind != InvestigationProviderKind.Members))
                        {
                            var secondary = secondaryPlan.Provider;
                            try
                            {
                                if (candidates.Count >= overrides.MaxResults)
                                {
                                    statuses.Add(ProviderCoverageStatus.NotRun(secondary.Kind, "global_cap"));
                                    continue;
                                }
                                var memberResult = await secondary.CollectAsync(
                                    reader, member, overrides, cancellationToken, 0,
                                    Math.Min(secondaryPlan.Policy.InitialWindow,
                                        overrides.MaxResults - candidates.Count));
                                var memberAvailable = Math.Max(0, overrides.MaxResults - candidates.Count);
                                candidates.AddRange(memberResult.Candidates.Take(memberAvailable));
                                var memberReachedGlobalCap = memberResult.Candidates.Count > memberAvailable
                                    || memberResult.Candidates.Count == memberAvailable
                                        && memberResult.Status.State == ProviderCoverageState.Partial;
                                statuses.Add(memberReachedGlobalCap
                                    ? ProviderCoverageStatus.Partial(secondary.Kind, "global_cap", memberAvailable)
                                    : memberResult.Status with { FoundCount = Math.Min(memberResult.Candidates.Count, memberAvailable) });
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
                if (goal == InvestigationGoal.Trace && provider.Kind == InvestigationProviderKind.Flow && providerCandidates.Count > 0)
                {
                    AddNotRun(statuses, providers.Skip(providerIndex + 1), "early_stop");
                    break;
                }
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

    private static void AddNotRun(
        ICollection<ProviderCoverageStatus> statuses,
        IEnumerable<InvestigationProviderPlan> providers,
        string reason)
    {
        foreach (var provider in providers)
            statuses.Add(ProviderCoverageStatus.NotRun(provider.Provider.Kind, reason));
    }
}
