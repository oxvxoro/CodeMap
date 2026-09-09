using CodeMap.Core.Models;

namespace CodeMap.Core.Contracts;

/// <summary>Read-only graph port consumed by Engine application use cases.</summary>
public interface ICodeMapGraphReader : IAsyncDisposable
{
    IReadOnlyList<IndexedSymbol> Find(string query, int maxResults = 20);
    SymbolSearchResult ResolveSymbol(string query, bool callableOnly, int maxResults);
    IndexedSymbol? FindById(string id);
    IReadOnlyDictionary<string, IndexedSymbol> FindByIds(IEnumerable<string> ids);
    IReadOnlyList<IndexedFile> Files();
    IReadOnlyDictionary<string, IndexedFile> FindFilesByIds(IEnumerable<string> ids);
    IReadOnlyList<IndexedSymbol> Members(IndexedSymbol symbol, int maxResults = 200);
    IReadOnlyList<IndexedRelation> CallerRelations(IndexedSymbol symbol, int maxResults = 200);
    IReadOnlyList<IndexedRelation> CalleeRelations(IndexedSymbol symbol, int depth = 1, int maxResults = 200);
    IReadOnlyList<IndexedRelation> ImplementationRelations(IndexedSymbol symbol, int maxResults = 200);
    RelationPage<IndexedRelation> CallerRelationsPaged(IndexedSymbol symbol, int limit, int offset, double minConfidence = 0);
    RelationPage<IndexedRelation> CalleeRelationsPaged(IndexedSymbol symbol, int depth, int limit, int offset, double minConfidence = 0);
    RelationPage<IndexedRelation> ImplementationRelationsPaged(IndexedSymbol symbol, int limit, int offset, double minConfidence = 0);
    RelationPage<ImpactItem> ImpactPaged(IndexedSymbol root, int depth, int limit, int offset, string profile);
    RelationPage<ImpactItem> FlowPaged(IndexedSymbol entry, string kind, int depth, int limit, int offset, double minConfidence);
    RelationPage<IndexedSymbol> MembersPaged(IndexedSymbol symbol, int limit, int offset);
    IReadOnlyList<RelationQueryResult> Relations(string sourceId, string targetId, EdgeKind? edgeKind, int maxResults, double minConfidence);
    IReadOnlyList<ImpactItem> Impact(IndexedSymbol root, int depth, int maxResults, string profile);
    IReadOnlyList<ImpactItem> Flow(IndexedSymbol entry, string kind, int depth, int maxResults, double minConfidence);
    RepoMap BuildMap(string? focus, string? project, int tokenBudget);
}
