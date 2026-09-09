using CodeMap.Core.Models;

namespace CodeMap.Engine.Application.Investigation;

public sealed class InvestigationRankingPolicy
{
    private static readonly IReadOnlyDictionary<(InvestigationGoal Goal, EdgeKind Kind), double> Relevance =
        new Dictionary<(InvestigationGoal, EdgeKind), double>
        {
            [(InvestigationGoal.Debug, EdgeKind.Calls)] = 0.90,
            [(InvestigationGoal.Debug, EdgeKind.Implements)] = 0.85,
            [(InvestigationGoal.Debug, EdgeKind.ImplementedBy)] = 0.85,
            [(InvestigationGoal.Trace, EdgeKind.Calls)] = 1.00,
            [(InvestigationGoal.Trace, EdgeKind.RoutesTo)] = 1.00,
            [(InvestigationGoal.Trace, EdgeKind.Renders)] = 0.95,
            [(InvestigationGoal.Trace, EdgeKind.BindsTo)] = 0.90,
            [(InvestigationGoal.Impact, EdgeKind.Calls)] = 1.00,
            [(InvestigationGoal.Impact, EdgeKind.RoutesTo)] = 0.95,
            [(InvestigationGoal.Impact, EdgeKind.ImplementedBy)] = 0.90,
            [(InvestigationGoal.Understand, EdgeKind.Contains)] = 1.00,
            [(InvestigationGoal.Understand, EdgeKind.Calls)] = 0.85,
            [(InvestigationGoal.Understand, EdgeKind.Implements)] = 0.80
        };

    public IReadOnlyList<InvestigationCandidate> Rank(
        InvestigationGoal goal,
        IEnumerable<InvestigationCandidate> candidates)
    {
        return candidates
            .Select(candidate => candidate with
            {
                ProfileRelevance = GetRelevance(goal, candidate)
            })
            .OrderByDescending(candidate => candidate.ProfileRelevance)
            .ThenByDescending(candidate => candidate.CertaintyTier)
            .ThenBy(candidate => candidate.Depth)
            .ThenByDescending(candidate => candidate.Confidence ?? 1.0)
            .ThenBy(candidate => Math.Max(1, candidate.EstimatedCost))
            .ThenBy(candidate => candidate.Symbol.Id, StringComparer.Ordinal)
            .ToArray();
    }

    public static double GetRelevance(InvestigationGoal goal, InvestigationCandidate candidate) =>
        goal == InvestigationGoal.Debug && candidate.LocalEvidence is not null
            ? 1.10
            : candidate.Via is not null && Relevance.TryGetValue((goal, candidate.Via.Kind), out var value)
            ? value
            : 0.5;
}
