using CodeMap.Core.Models;

namespace CodeMap.Storage.Queries;

/// <summary>Optional evidence shaping; callers can skip it for topology-only queries.</summary>
public sealed class QueryEvidenceService
{
    public RelationEvidence For(
        IndexedEdge edge,
        IndexedSymbol source,
        IndexedSymbol target,
        string? sourceFilePath = null) =>
        RelationEvidenceMapper.FromEdge(edge, source, target, sourceFilePath, edge.Line);
}
