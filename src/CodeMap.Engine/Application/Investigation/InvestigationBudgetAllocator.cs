namespace CodeMap.Engine.Application.Investigation;

public sealed record InvestigationSelection(
    IReadOnlyList<InvestigationCandidate> Selected,
    IReadOnlyList<InvestigationCandidate> Excluded,
    int EstimatedTokens,
    bool Truncated,
    string? TruncationReason)
{
    public int RequestedBudget { get; init; }
}

public sealed class InvestigationBudgetAllocator
{
    private const int MinimumEnvelopeTokens = 16;

    public InvestigationSelection Allocate(
        IReadOnlyList<InvestigationCandidate> ranked,
        int requestedBudget)
    {
        var budget = Math.Max(0, requestedBudget);
        var selected = new List<InvestigationCandidate>();
        var excluded = new List<InvestigationCandidate>();
        var estimated = 0;

        for (var index = 0; index < ranked.Count; index++)
        {
            var candidate = ranked[index];
            var cost = Math.Max(1, candidate.EstimatedCost);
            if (estimated + cost <= budget)
            {
                selected.Add(candidate);
                estimated += cost;
            }
            else
            {
                excluded.AddRange(ranked.Skip(index));
                break;
            }
        }

        var reason = excluded.Count == 0
            ? null
            : selected.Count == 0 && budget < MinimumEnvelopeTokens
                ? "minimum_envelope"
                : "budget";
        return new InvestigationSelection(selected, excluded, estimated, excluded.Count > 0, reason)
        {
            RequestedBudget = requestedBudget
        };
    }
}
