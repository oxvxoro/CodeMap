using CodeMap.Core.Models.Investigation;

namespace CodeMap.Engine.Application.Investigation;

internal sealed class SourceEvidenceBuilder
{
    public IReadOnlyList<InvestigationSourceSpan> BuildSpans(
        IReadOnlyList<InvestigationCandidate> selected,
        SourceEvidenceMode mode,
        int remainingTokenBudget,
        IFileTextAccessor fileAccessor)
    {
        if (mode == SourceEvidenceMode.None || remainingTokenBudget <= 0)
            return Array.Empty<InvestigationSourceSpan>();

        var spans = new List<InvestigationSourceSpan>();
        var usedTokens = 0;
        foreach (var candidate in selected)
        {
            var location = candidate.EvidenceLocation;
            var line = location?.StartLine ?? candidate.Via?.Line ?? candidate.Symbol.StartLine;
            if (line is null)
                continue;
            var start = mode == SourceEvidenceMode.Scope
                ? location?.StartLine ?? candidate.Symbol.StartLine ?? line.Value
                : Math.Max(1, line.Value - 2);
            var end = mode == SourceEvidenceMode.Scope
                ? location?.EndLine ?? candidate.Symbol.EndLine ?? line.Value
                : Math.Max(start, (location?.EndLine ?? candidate.Via?.EndLine ?? line.Value) + 2);
            var file = location?.File ?? candidate.Symbol.RelativePath;
            var text = fileAccessor.Read(file, start, end);
            if (text is null)
                continue;
            var cost = (int)Math.Ceiling(text.Length / 4d);
            if (usedTokens + cost > remainingTokenBudget)
                break;
            spans.Add(new InvestigationSourceSpan(
                file,
                start,
                end,
                text,
                [candidate.Symbol.Id]));
            usedTokens += cost;
        }
        return MergeSpans(spans);
    }

    private static IReadOnlyList<InvestigationSourceSpan> MergeSpans(IEnumerable<InvestigationSourceSpan> spans)
    {
        var merged = new List<InvestigationSourceSpan>();
        foreach (var span in spans.OrderBy(span => span.File, StringComparer.Ordinal).ThenBy(span => span.StartLine))
        {
            var previous = merged.LastOrDefault();
            if (previous is null
                || !string.Equals(previous.File, span.File, StringComparison.Ordinal)
                || span.StartLine > previous.EndLine + 2)
            {
                merged.Add(span);
                continue;
            }

            merged[^1] = previous with
            {
                EndLine = Math.Max(previous.EndLine, span.EndLine),
                Text = string.Join(Environment.NewLine, new[] { previous.Text, span.Text }.Distinct(StringComparer.Ordinal)),
                CandidateIds = previous.CandidateIds.Concat(span.CandidateIds).Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToArray()
            };
        }
        return merged;
    }
}
