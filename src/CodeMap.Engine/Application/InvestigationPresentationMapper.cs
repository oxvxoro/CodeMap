using System.Text.Json;
using System.Text.Json.Serialization;
using CodeMap.Core.Models;
using CodeMap.Core.Models.Investigation;
using CodeMap.CSharp;
using CodeMap.Engine.Application.Investigation;

namespace CodeMap.Engine.Application;

/// <summary>Single presentation contract shared by CLI and MCP investigation responses.</summary>
public static class InvestigationPresentationMapper
{
    public static InvestigationResponseDto ToResponse(
        string query, InvestigationGoal goal, InvestigationResult result, bool stale) =>
        new(
            1,
            query,
            goal.ToString().ToLowerInvariant(),
            ToMatch(result.Resolution.Root),
            result.Resolution.IsAmbiguous,
            result.Resolution.AmbiguousCandidates.Select(ToMatch).ToArray(),
            result.Items.Select(ToItem).ToArray(),
            result.SourceSpans.Select(ToSpan).ToArray(),
            ToBudget(result.Budget),
            ToCoverage(result.Coverage),
            stale,
            Array.Empty<string>(),
            null,
            null);

    public static InvestigationResponseDto ToAmbiguousResponse(
        string query, InvestigationGoal goal, InvestigationResult result, bool stale) =>
        ToResponse(query, goal, result, stale) with
        {
            Root = null,
            IsAmbiguous = true,
            Items = Array.Empty<InvestigationItemDto>(),
            SourceSpans = Array.Empty<InvestigationSourceSpanDto>(),
            Reason = "ambiguous"
        };

    public static InvestigationErrorResponseDto ToError(
        string query, string goal, QueryError error, bool stale) =>
        new(1, query, goal, new InvestigationErrorDto(error.Code, error.Message), error.Code, stale);

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);

    public static InvestigationMatchDto? ToMatch(IndexedSymbol? symbol) => symbol is null
        ? null
        : new(symbol.Id, symbol.Project, symbol.Kind.ToString(), symbol.Name,
            symbol.QualifiedName, symbol.Signature, symbol.RelativePath, symbol.StartLine, symbol.EndLine, symbol.Language);

    private static InvestigationItemDto ToItem(InvestigationCandidate candidate) =>
        new(ToMatch(candidate.Symbol)!, candidate.Via is null ? null : ToVia(candidate), candidate.Depth,
            candidate.Provider, candidate.AlsoFoundBy, candidate.LocalEvidence is null ? null : ToLocalEvidence(candidate.LocalEvidence));

    private static InvestigationViaDto ToVia(InvestigationCandidate candidate)
    {
        var edge = candidate.Via!;
        var edgeLocation = candidate.EvidenceLocation?.Origin == InvestigationEvidenceLocationOrigin.EdgeSource
            ? candidate.EvidenceLocation
            : null;
        var hasLocation = edge.SourceFileId is not null || edge.Line is not null;
        return new(edge.Kind.ToString(), edge.ResolutionKind.ToString().ToLowerInvariant(), edge.Confidence,
            !hasLocation ? null : new InvestigationLocationDto(
                edgeLocation?.File ?? candidate.Symbol.RelativePath,
                edgeLocation?.StartLine ?? edge.Line,
                edgeLocation?.StartColumn ?? edge.StartColumn,
                edgeLocation?.EndLine ?? edge.EndLine,
                edgeLocation?.EndColumn ?? edge.EndColumn));
    }

    private static InvestigationLocalEvidenceDto ToLocalEvidence(LocalSliceEvidence evidence) =>
        new(evidence.ScopeSymbolId, evidence.ItemId, evidence.OperationKind.ToString(), evidence.SymbolName,
            evidence.Display, evidence.File, evidence.Location, evidence.Dependencies);

    private static InvestigationSourceSpanDto ToSpan(InvestigationSourceSpan span) =>
        new(span.File, span.StartLine, span.EndLine, span.Text, span.CandidateIds);

    private static InvestigationBudgetDto ToBudget(InvestigationSelection budget) =>
        new(budget.RequestedBudget, budget.EstimatedTokens, budget.Truncated, budget.TruncationReason,
            budget.Cost.Envelope, budget.Cost.Structural, budget.Cost.Source, budget.Cost.TotalEstimated);

    private static InvestigationCoverageDto ToCoverage(InvestigationCoverage coverage) =>
        new(coverage.Providers.Select(status => new InvestigationProviderCoverageDto(
                status.Provider == InvestigationProviderKind.LocalSlice ? "localslice" : status.Provider.ToString().ToLowerInvariant(),
                status.State.ToString().ToLowerInvariant(), status.Reason, status.FoundCount)).ToArray(),
            coverage.Remaining, coverage.NegativeEvidence);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };
}

public sealed record InvestigationResponseDto(
    int Version,
    string Query,
    string Goal,
    InvestigationMatchDto? Root,
    bool IsAmbiguous,
    IReadOnlyList<InvestigationMatchDto?> AmbiguousCandidates,
    IReadOnlyList<InvestigationItemDto> Items,
    IReadOnlyList<InvestigationSourceSpanDto> SourceSpans,
    InvestigationBudgetDto Budget,
    InvestigationCoverageDto Coverage,
    bool Stale,
    IReadOnlyList<string> Warnings,
    string? Reason,
    InvestigationErrorDto? Error);

public sealed record InvestigationErrorResponseDto(
    int Version,
    string Query,
    string Goal,
    InvestigationErrorDto Error,
    string Reason,
    bool Stale);

public sealed record InvestigationMatchDto(
    string Id, string Project, string Kind, string Name, string QualifiedName,
    string? Signature, string File, int? StartLine, int? EndLine, string Language);

public sealed record InvestigationItemDto(
    InvestigationMatchDto Symbol,
    InvestigationViaDto? Via,
    int Depth,
    string Provider,
    IReadOnlyList<string> AlsoFoundBy,
    InvestigationLocalEvidenceDto? LocalEvidence);

public sealed record InvestigationViaDto(
    string EdgeKind,
    string ResolutionKind,
    double? Confidence,
    InvestigationLocationDto? Location);

public sealed record InvestigationLocationDto(
    string File, int? StartLine, int? StartColumn, int? EndLine, int? EndColumn);

public sealed record InvestigationLocalEvidenceDto(
    string ScopeSymbolId,
    int ItemId,
    string OperationKind,
    string? SymbolName,
    string Display,
    string File,
    SourceLocation Location,
    IReadOnlyList<SliceDependency> Dependencies);

public sealed record InvestigationSourceSpanDto(
    string File, int StartLine, int EndLine, string Text, IReadOnlyList<string> CandidateIds);

public sealed record InvestigationBudgetDto(
    int Requested,
    int Estimated,
    bool Truncated,
    string? Reason,
    int Envelope,
    int Structural,
    int Source,
    int TotalEstimated);

public sealed record InvestigationCoverageDto(
    IReadOnlyList<InvestigationProviderCoverageDto> Providers,
    IReadOnlyDictionary<string, int> Remaining,
    IReadOnlyList<string> NegativeEvidence);

public sealed record InvestigationProviderCoverageDto(
    string Provider, string State, string? Reason, int FoundCount);

public sealed record InvestigationErrorDto(string Code, string Message);
