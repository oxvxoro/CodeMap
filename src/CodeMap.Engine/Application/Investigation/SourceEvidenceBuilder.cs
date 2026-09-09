using CodeMap.Core.Models.Investigation;

namespace CodeMap.Engine.Application.Investigation;

internal sealed class SourceEvidenceBuilder
{
    private sealed record SpanRequest(string File, int StartLine, int EndLine, string CandidateId, int Priority);

    public IReadOnlyList<InvestigationSourceSpan> BuildSpans(
        IReadOnlyList<InvestigationCandidate> selected,
        SourceEvidenceMode mode,
        int remainingTokenBudget,
        IFileTextAccessor fileAccessor)
    {
        if (mode == SourceEvidenceMode.None || remainingTokenBudget <= 0)
            return Array.Empty<InvestigationSourceSpan>();

        var requests = selected
            .Select(candidate => ToRequest(candidate, mode))
            .Where(request => request is not null)
            .Select(request => request!)
            .ToArray();
        if (requests.Length == 0)
            return Array.Empty<InvestigationSourceSpan>();

        var merged = MergeRanges(requests);
        var hasFocusedEvidence = merged.Any(span => span.Priority < 2);
        var reserve = hasFocusedEvidence ? Math.Min(remainingTokenBudget / 4, 64) : 0;
        var usedTokens = 0;
        var result = new List<InvestigationSourceSpan>();

        foreach (var span in merged)
        {
            var text = fileAccessor.Read(span.File, span.StartLine, span.EndLine);
            if (text is null)
                continue;

            var cost = Math.Max(1, (int)Math.Ceiling(text.Length / 4d));
            var available = remainingTokenBudget - usedTokens;
            if (span.Priority >= 2 && hasFocusedEvidence)
                available -= reserve;
            if (cost > available)
                continue;

            result.Add(new InvestigationSourceSpan(
                span.File,
                span.StartLine,
                span.EndLine,
                text,
                span.CandidateIds.OrderBy(id => id, StringComparer.Ordinal).ToArray()));
            usedTokens += cost;
        }

        return result;
    }

    private static SpanRequest? ToRequest(InvestigationCandidate candidate, SourceEvidenceMode mode)
    {
        var location = candidate.EvidenceLocation;
        var line = location?.StartLine ?? candidate.Via?.Line ?? candidate.Symbol.StartLine;
        if (line is null)
            return null;

        var start = mode == SourceEvidenceMode.Scope
            ? location?.StartLine ?? candidate.Symbol.StartLine ?? line.Value
            : Math.Max(1, line.Value - 2);
        var end = mode == SourceEvidenceMode.Scope
            ? location?.EndLine ?? candidate.Symbol.EndLine ?? line.Value
            : Math.Max(start, (location?.EndLine ?? candidate.Via?.EndLine ?? line.Value) + 2);
        return new SpanRequest(
            location?.File ?? candidate.Symbol.RelativePath,
            start,
            Math.Max(start, end),
            candidate.Symbol.Id,
            PriorityOf(candidate));
    }

    private static IReadOnlyList<MergedSpan> MergeRanges(IEnumerable<SpanRequest> requests)
    {
        var merged = new List<MergedSpan>();
        foreach (var request in requests
                     .OrderBy(request => request.File, StringComparer.Ordinal)
                     .ThenBy(request => request.StartLine)
                     .ThenBy(request => request.EndLine)
                     .ThenBy(request => request.CandidateId, StringComparer.Ordinal))
        {
            var previous = merged.LastOrDefault();
            if (previous is null
                || !string.Equals(previous.File, request.File, StringComparison.Ordinal)
                || request.StartLine > previous.EndLine + 2)
            {
                merged.Add(new MergedSpan(request.File, request.StartLine, request.EndLine,
                    [request.CandidateId], request.Priority));
                continue;
            }

            merged[^1] = previous with
            {
                EndLine = Math.Max(previous.EndLine, request.EndLine),
                CandidateIds = previous.CandidateIds
                    .Append(request.CandidateId)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray(),
                Priority = Math.Min(previous.Priority, request.Priority)
            };
        }

        return merged
            .OrderBy(span => span.Priority)
            .ThenBy(span => span.File, StringComparer.Ordinal)
            .ThenBy(span => span.StartLine)
            .ToArray();
    }

    private sealed record MergedSpan(
        string File,
        int StartLine,
        int EndLine,
        IReadOnlyList<string> CandidateIds,
        int Priority);

    private static int PriorityOf(InvestigationCandidate candidate) =>
        candidate.LocalEvidence is not null ? 0 : candidate.Depth <= 1 ? 1 : 2;
}
