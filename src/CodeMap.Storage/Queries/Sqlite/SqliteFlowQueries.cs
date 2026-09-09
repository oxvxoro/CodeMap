using CodeMap.Core.Models;

namespace CodeMap.Storage.Queries.Sqlite;

/// <summary>Flow-specific adapter for the SQLite backend.</summary>
internal sealed class SqliteFlowQueries(CodeMapQueryService service)
{
    public IReadOnlyList<ImpactItem> Query(IndexedSymbol entry, string kind, int depth, int maxResults, double minConfidence) => service.Flow(entry, kind, depth, maxResults, minConfidence);
}
