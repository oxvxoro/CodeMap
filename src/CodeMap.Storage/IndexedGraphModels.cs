using CodeMap.Core.Models;
using Microsoft.Data.Sqlite;

namespace CodeMap.Storage;

internal static class IndexedEdgeReader
{







    internal static IndexedEdge Read(SqliteDataReader reader, int offset = 0) => new(
        reader.GetString(offset),
        reader.GetString(offset + 1),
        Enum.Parse<EdgeKind>(reader.GetString(offset + 2)),
        reader.IsDBNull(offset + 3) ? null : reader.GetString(offset + 3),
        reader.IsDBNull(offset + 4) ? null : reader.GetInt32(offset + 4),
        ParseResolutionKind(reader.GetString(offset + 5)),
        reader.IsDBNull(offset + 6) ? null : reader.GetDouble(offset + 6));





    internal static IndexedEdge ReadWithSpan(SqliteDataReader reader, int offset = 0)
    {
        var edge = Read(reader, offset);
        return edge with
        {
            StartColumn = reader.IsDBNull(offset + 7) ? null : reader.GetInt32(offset + 7),
            EndLine = reader.IsDBNull(offset + 8) ? null : reader.GetInt32(offset + 8),
            EndColumn = reader.IsDBNull(offset + 9) ? null : reader.GetInt32(offset + 9)
        };
    }

    private static EdgeResolutionKind ParseResolutionKind(string value) =>
        Enum.Parse<EdgeResolutionKind>(value, ignoreCase: true);
}

public sealed record IndexedFile(
    string Id,
    string Project,
    string RelativePath,
    string Language);

public sealed record IndexedSymbol(
    string Id,
    string Project,
    string FileId,
    string RelativePath,
    NodeKind Kind,
    string Name,
    string QualifiedName,
    string? Signature,
    int? StartLine,
    int? EndLine,
    string? Visibility,
    string Language)
{
    public string DisplayName => Kind == NodeKind.Constructor
        ? QualifiedName + (Signature ?? string.Empty)
        : QualifiedName + (Kind is NodeKind.Method ? Signature ?? string.Empty : string.Empty);
}

public sealed record IndexedEdge(
    string SourceId,
    string TargetId,
    EdgeKind Kind,
    string? SourceFileId,
    int? Line,
    EdgeResolutionKind ResolutionKind = EdgeResolutionKind.Semantic,
    double? Confidence = null)
{




    public int? StartColumn { get; init; }

    public int? EndLine { get; init; }

    public int? EndColumn { get; init; }
}

public sealed record IndexedRelation(IndexedSymbol Symbol, IndexedEdge Edge);

public sealed record RelationEvidence(
    string Evidence,
    string? File,
    int? Line,
    EdgeResolutionKind ResolutionKind,
    double? Confidence);

public sealed record RelationQueryResult(
    IndexedSymbol Source,
    IndexedSymbol Target,
    IndexedEdge Edge,
    RelationEvidence Evidence);

public static class RelationConfidence
{
    public const string InvalidMessage = "min-confidence must be a finite number between 0 and 1.";

    public static bool IsValid(double value) =>
        double.IsFinite(value) && value is >= 0 and <= 1;
}
