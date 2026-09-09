using CodeMap.Core.Contracts;
using CodeMap.Core.Models;
using Microsoft.Data.Sqlite;

namespace CodeMap.Storage.Queries;

/// <summary>SQLite-backed implementation of the Engine graph read port.</summary>
public sealed class SqliteCodeMapGraphReader : ICodeMapGraphReader
{
    private readonly CodeMapQueryService _service;

    public SqliteCodeMapGraphReader(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _service = new CodeMapQueryService(connection);
    }

    public IReadOnlyList<IndexedSymbol> Find(string query, int maxResults = 20) => _service.Find(query, maxResults);
    public SymbolSearchResult ResolveSymbol(string query, bool callableOnly, int maxResults) => _service.ResolveSymbol(query, callableOnly, maxResults);
    public IndexedSymbol? FindById(string id) => _service.FindById(id);
    public IReadOnlyDictionary<string, IndexedSymbol> FindByIds(IEnumerable<string> ids) => _service.FindByIds(ids);
    public IReadOnlyList<IndexedFile> Files() => _service.Files();
    public IReadOnlyDictionary<string, IndexedFile> FindFilesByIds(IEnumerable<string> ids) => _service.FindFilesByIds(ids);
    public IReadOnlyList<IndexedSymbol> Members(IndexedSymbol symbol, int maxResults = 200) => _service.Members(symbol, maxResults);
    public IReadOnlyList<RelationQueryResult> Relations(string sourceId, string targetId, EdgeKind? edgeKind, int maxResults, double minConfidence) => _service.Relations(sourceId, targetId, edgeKind, maxResults, minConfidence);
    public IReadOnlyList<ImpactItem> Impact(IndexedSymbol root, int depth, int maxResults, string profile) => _service.Impact(root, depth, maxResults, profile);
    public IReadOnlyList<ImpactItem> Flow(IndexedSymbol entry, string kind, int depth, int maxResults, double minConfidence) => _service.Flow(entry, kind, depth, maxResults, minConfidence);
    public RepoMap BuildMap(string? focus, string? project, int tokenBudget) => _service.BuildMap(focus, project, tokenBudget);

    public ValueTask DisposeAsync() => _service.DisposeAsync();
}
