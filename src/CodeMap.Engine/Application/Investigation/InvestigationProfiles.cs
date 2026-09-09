using CodeMap.Core.Models;
using CodeMap.Engine.Application.Investigation.Providers;

namespace CodeMap.Engine.Application.Investigation;

internal static class InvestigationProfiles
{
    public static IReadOnlyList<IInvestigationProvider> Create(
        InvestigationGoal goal,
        Func<IndexedSymbol, string, CancellationToken, Task<CodeMap.Storage.SemanticSliceResult>>? slice = null) => goal switch
    {
        InvestigationGoal.Debug =>
        [new LocalSliceProvider(slice), new CalleesProvider(), new CallersProvider(), new ImplementationsProvider()],
        InvestigationGoal.Trace => [new FlowProvider(), new CalleesProvider()],
        InvestigationGoal.Impact => [new ImpactProvider(), new CallersProvider()],
        InvestigationGoal.Understand => [new MembersProvider(), new CalleesProvider(), new CallersProvider(), new ImplementationsProvider()],
        _ => throw new ArgumentOutOfRangeException(nameof(goal), goal, null)
    };
}
