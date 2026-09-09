using CodeMap.Core.Models;
using CodeMap.CSharp;

namespace CodeMap.Engine.Application.Investigation;

public enum InvestigationEvidenceLocationOrigin
{
    EdgeSource,
    SymbolDeclaration,
    LocalSlice
}

public sealed record InvestigationEvidenceLocation(
    string? File,
    int? StartLine,
    int? StartColumn,
    int? EndLine,
    int? EndColumn,
    InvestigationEvidenceLocationOrigin Origin);

public sealed record LocalSliceEvidence(
    string ScopeSymbolId,
    int ItemId,
    SliceOperationKind OperationKind,
    string? SymbolName,
    string Display,
    string File,
    SourceLocation Location,
    IReadOnlyList<SliceDependency> Dependencies);
