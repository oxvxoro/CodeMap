using CodeMap.Core.Models;

namespace CodeMap.Storage.Queries.Sqlite;

/// <summary>Impact optimized-path adapter.</summary>
internal sealed class SqliteImpactQueries(CodeMapQueryService service)
{
    public IReadOnlyList<ImpactItem> Query(IndexedSymbol symbol, int depth, int maxResults, string profile) => service.Impact(symbol, depth, maxResults, profile);
    public IReadOnlyList<ImpactItem> QueryUnion(IEnumerable<IndexedSymbol> roots, int depth, int maxResults, string profile) => service.ImpactUnion(roots, depth, maxResults, profile);
}
