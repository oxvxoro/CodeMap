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
    IReadOnlyList<RelationQueryResult> Relations(string sourceId, string targetId, EdgeKind? edgeKind, int maxResults, double minConfidence);
    IReadOnlyList<ImpactItem> Impact(IndexedSymbol root, int depth, int maxResults, string profile);
    IReadOnlyList<ImpactItem> Flow(IndexedSymbol entry, string kind, int depth, int maxResults, double minConfidence);
    RepoMap BuildMap(string? focus, string? project, int tokenBudget);
}
