using CodeMap.Core.Models;

namespace CodeMap.Engine.Application.Investigation;

internal readonly record struct InvestigationCandidateKey(
    string SymbolId,
    EdgeKind? EdgeKind,
    string? SourceId,
    string? TargetId)
{
    public static InvestigationCandidateKey From(InvestigationCandidate candidate) => new(
        candidate.Symbol.Id,
        candidate.Via?.Kind,
        candidate.Via?.SourceId,
        candidate.Via?.TargetId);
}
