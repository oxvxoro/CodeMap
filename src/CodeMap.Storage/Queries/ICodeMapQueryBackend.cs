using CodeMap.Core.Models;
using CodeMap.Storage;

namespace CodeMap.Storage.Queries;

internal interface ICodeMapQueryBackend
{
    IReadOnlyList<IndexedSymbol> FindCandidates(string query);
    IndexedSymbol? FindById(string id);
    IReadOnlyDictionary<string, IndexedSymbol> FindByIds(IEnumerable<string> ids);
    IReadOnlyList<IndexedFile> Files();
    IReadOnlyDictionary<string, IndexedFile> FindFilesByIds(IEnumerable<string> ids);
    IReadOnlyList<IndexedEdge> Incoming(string symbolId, IReadOnlySet<EdgeKind> kinds);
    IReadOnlyList<IndexedEdge> Outgoing(string symbolId, IReadOnlySet<EdgeKind> kinds);
}
