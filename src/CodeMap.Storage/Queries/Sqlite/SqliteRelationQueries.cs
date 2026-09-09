using CodeMap.Core.Models;

namespace CodeMap.Storage.Queries.Sqlite;

/// <summary>Relation-oriented SQLite adapter.</summary>
internal sealed class SqliteRelationQueries(CodeMapQueryService service)
{
    public IReadOnlyList<IndexedEdge> Incoming(string symbolId, IReadOnlySet<EdgeKind> kinds) => service.Incoming(symbolId, kinds.ToArray());
    public IReadOnlyList<IndexedEdge> Outgoing(string symbolId, IReadOnlySet<EdgeKind> kinds) => service.Outgoing(symbolId, kinds.ToArray());
    public IReadOnlyList<IndexedRelation> Callers(IndexedSymbol symbol, int maxResults) => service.CallerRelations(symbol, maxResults);
    public IReadOnlyList<IndexedRelation> Callees(IndexedSymbol symbol, int depth, int maxResults) => service.CalleeRelations(symbol, depth, maxResults);
    public IReadOnlyList<IndexedRelation> Implementations(IndexedSymbol symbol, int maxResults) => service.ImplementationRelations(symbol, maxResults);
    public IReadOnlyList<RelationQueryResult> Between(string sourceId, string targetId, int maxResults, double minConfidence) => service.Relations(sourceId, targetId, null, maxResults, minConfidence);
}
