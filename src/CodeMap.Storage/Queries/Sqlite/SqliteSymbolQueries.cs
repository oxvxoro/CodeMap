using CodeMap.Core.Models;

namespace CodeMap.Storage.Queries.Sqlite;

/// <summary>Symbol-oriented SQLite adapter kept separate from graph traversal.</summary>
internal sealed class SqliteSymbolQueries(CodeMapQueryService service)
{
    public IReadOnlyList<IndexedSymbol> Find(string query, int maxResults) => service.Find(query, maxResults);
    public SymbolSearchResult Resolve(string query, bool callableOnly, int maxResults) => service.ResolveSymbol(query, callableOnly, maxResults);
    public IndexedSymbol? FindById(string id) => service.FindById(id);
    public IReadOnlyDictionary<string, IndexedSymbol> FindByIds(IEnumerable<string> ids) => service.FindByIds(ids);
    public IReadOnlyList<IndexedSymbol> Members(IndexedSymbol symbol, int maxResults) => service.Members(symbol, maxResults);
}
