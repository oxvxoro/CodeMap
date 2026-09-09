namespace CodeMap.Engine.Application.Investigation;

public static class InvestigationCandidateDeduplicator
{
    public static IReadOnlyList<InvestigationCandidate> Deduplicate(IEnumerable<InvestigationCandidate> candidates)
    {
        var merged = new Dictionary<InvestigationCandidateKey, InvestigationCandidate>();
        foreach (var candidate in candidates)
        {
            var key = InvestigationCandidateKey.From(candidate);

            if (!merged.TryGetValue(key, out var existing))
            {
                merged[key] = candidate;
                continue;
            }

            var preferred = IsPreferred(candidate, existing) ? candidate : existing;
            var providers = existing.AlsoFoundBy
                .Append(existing.Provider)
                .Concat(candidate.AlsoFoundBy)
                .Append(candidate.Provider)
                .Where(provider => !string.Equals(provider, preferred.Provider, StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(provider => provider, StringComparer.Ordinal)
                .ToArray();
            merged[key] = preferred with { AlsoFoundBy = providers };
        }

        return merged.Values.ToArray();
    }

    private static bool IsPreferred(InvestigationCandidate candidate, InvestigationCandidate existing) =>
        candidate.CertaintyTier > existing.CertaintyTier
        || candidate.CertaintyTier == existing.CertaintyTier && candidate.Depth < existing.Depth
        || candidate.CertaintyTier == existing.CertaintyTier && candidate.Depth == existing.Depth
            && string.CompareOrdinal(candidate.Provider, existing.Provider) < 0;
}
