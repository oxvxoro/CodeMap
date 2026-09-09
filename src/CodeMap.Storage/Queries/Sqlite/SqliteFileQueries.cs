using CodeMap.Core.Models;

namespace CodeMap.Storage.Queries.Sqlite;

/// <summary>File lookup adapter used by evidence and map shaping.</summary>
internal sealed class SqliteFileQueries(CodeMapQueryService service)
{
    public IReadOnlyList<IndexedFile> All() => service.Files();
    public IReadOnlyDictionary<string, IndexedFile> ByIds(IEnumerable<string> ids) => service.FindFilesByIds(ids);
}
