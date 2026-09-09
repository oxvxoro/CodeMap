namespace CodeMap.Core.Models.Investigation;

public enum SourceEvidenceMode
{
    None,
    Minimal,
    Scope
}

public sealed record InvestigationSourceSpan(
    string File,
    int StartLine,
    int EndLine,
    string Text,
    IReadOnlyList<string> CandidateIds);
