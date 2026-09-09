using CodeMap.Core.Models;
using CodeMap.Storage;
using CodeMap.Storage.Queries.Sqlite;
using Microsoft.Data.Sqlite;

namespace CodeMap.Storage.Queries;

internal sealed class SqliteQueryBackend : ICodeMapQueryBackend
{
    private readonly SqliteSymbolQueries _symbols;
    private readonly SqliteRelationQueries _relations;
    private readonly SqliteFileQueries _files;

    public SqliteQueryBackend(SqliteConnection connection)
    {
        var service = new CodeMapQueryService(connection);
        _symbols = new SqliteSymbolQueries(service);
        _relations = new SqliteRelationQueries(service);
        _files = new SqliteFileQueries(service);
    }

    public IReadOnlyList<IndexedSymbol> FindCandidates(string query) => _symbols.Find(query, int.MaxValue);
    public IndexedSymbol? FindById(string id) => _symbols.FindById(id);
    public IReadOnlyDictionary<string, IndexedSymbol> FindByIds(IEnumerable<string> ids) => _symbols.FindByIds(ids);
    public IReadOnlyList<IndexedFile> Files() => _files.All();
    public IReadOnlyDictionary<string, IndexedFile> FindFilesByIds(IEnumerable<string> ids) => _files.ByIds(ids);
    public IReadOnlyList<IndexedEdge> Incoming(string symbolId, IReadOnlySet<EdgeKind> kinds) => _relations.Incoming(symbolId, kinds);
    public IReadOnlyList<IndexedEdge> Outgoing(string symbolId, IReadOnlySet<EdgeKind> kinds) => _relations.Outgoing(symbolId, kinds);
}
