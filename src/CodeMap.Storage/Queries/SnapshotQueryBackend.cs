using CodeMap.Core.Models;
using CodeMap.Storage;

namespace CodeMap.Storage.Queries;

internal sealed class SnapshotQueryBackend : ICodeMapQueryBackend
{
    private readonly CodeMapQueryService _service;

    public SnapshotQueryBackend(CodeMapSnapshot snapshot) => _service = new CodeMapQueryService(snapshot);

    public IReadOnlyList<IndexedSymbol> FindCandidates(string query) => _service.Find(query, int.MaxValue);
    public IndexedSymbol? FindById(string id) => _service.FindById(id);
    public IReadOnlyDictionary<string, IndexedSymbol> FindByIds(IEnumerable<string> ids) => _service.FindByIds(ids);
    public IReadOnlyList<IndexedFile> Files() => _service.Files();
    public IReadOnlyDictionary<string, IndexedFile> FindFilesByIds(IEnumerable<string> ids) => _service.FindFilesByIds(ids);
    public IReadOnlyList<IndexedEdge> Incoming(string symbolId, IReadOnlySet<EdgeKind> kinds) => _service.Incoming(symbolId, kinds.ToArray());
    public IReadOnlyList<IndexedEdge> Outgoing(string symbolId, IReadOnlySet<EdgeKind> kinds) => _service.Outgoing(symbolId, kinds.ToArray());
}
