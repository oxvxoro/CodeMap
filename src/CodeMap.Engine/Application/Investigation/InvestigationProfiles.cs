using CodeMap.Core.Models;
using CodeMap.Engine.Application.Investigation.Providers;

namespace CodeMap.Engine.Application.Investigation;

internal static class InvestigationProfiles
{
    public static IReadOnlyList<InvestigationProviderPlan> Create(
        InvestigationGoal goal,
        Func<IndexedSymbol, string, CancellationToken, Task<CodeMap.Storage.SemanticSliceResult>>? slice = null) => goal switch
    {
        InvestigationGoal.Debug =>
        Plans(new LocalSliceProvider(slice), new CalleesProvider(), new CallersProvider(), new ImplementationsProvider()),
        InvestigationGoal.Trace => Plans(new FlowProvider(), new CalleesProvider()),
        InvestigationGoal.Impact => Plans(new ImpactProvider(), new CallersProvider()),
        InvestigationGoal.Understand => Plans(new MembersProvider(), new CalleesProvider(), new CallersProvider(), new ImplementationsProvider()),
        _ => throw new ArgumentOutOfRangeException(nameof(goal), goal, null)
    };

    private static IReadOnlyList<InvestigationProviderPlan> Plans(params IInvestigationProvider[] providers) =>
        providers.Select(provider => new InvestigationProviderPlan(provider, ProviderPlan.For(provider.Kind))).ToArray();
}
