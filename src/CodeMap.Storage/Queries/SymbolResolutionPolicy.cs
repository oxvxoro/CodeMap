using CodeMap.Storage;
using CodeMap.Core.Models;

namespace CodeMap.Storage.Queries;

internal static class SymbolResolutionPolicy
{
    public static IReadOnlyList<IndexedSymbol> PreferSourceOverExternal(
        IReadOnlyList<IndexedSymbol> matches,
        string query)
    {
        if (matches.Count <= 1 || matches.Any(symbol => string.Equals(symbol.Id, query, StringComparison.OrdinalIgnoreCase)))
            return matches;
        var source = matches.Where(symbol => !symbol.Project.StartsWith("external:", StringComparison.Ordinal)).ToArray();
        return source.Length > 0 ? source : matches;
    }
}
