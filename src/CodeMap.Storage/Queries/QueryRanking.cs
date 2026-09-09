using CodeMap.Core.Models;
using CodeMap.Storage;

namespace CodeMap.Storage.Queries;

internal static class QueryRanking
{
    public static IReadOnlyList<IndexedSymbol> Rank(
        IEnumerable<IndexedSymbol> candidates,
        string query,
        int maxResults)
    {
        var ranked = candidates
            .Select(symbol => (Symbol: symbol, Score: Score(symbol, query)))
            .Where(item => item.Score >= 0)
            .GroupBy(item => item.Score / 100)
            .OrderByDescending(group => group.Key)
            .FirstOrDefault()?
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Symbol.Project.StartsWith("external:", StringComparison.Ordinal))
            .ThenBy(item => item.Symbol.QualifiedName, StringComparer.Ordinal)
            .ThenBy(item => item.Symbol.Signature ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(item => item.Symbol.Id, StringComparer.Ordinal)
            .Take(Math.Max(1, maxResults))
            .Select(item => item.Symbol)
            .ToArray();
        return ranked ?? Array.Empty<IndexedSymbol>();
    }

    private static int Score(IndexedSymbol symbol, string query)
    {
        if (string.Equals(symbol.QualifiedName, query, StringComparison.OrdinalIgnoreCase)
            || string.Equals(symbol.DisplayName, query, StringComparison.OrdinalIgnoreCase)
            || string.Equals(symbol.Id, query, StringComparison.OrdinalIgnoreCase))
            return 1000;
        if (string.Equals(symbol.Name, query, StringComparison.OrdinalIgnoreCase))
            return 900;
        if (symbol.QualifiedName.StartsWith(query, StringComparison.OrdinalIgnoreCase)
            || symbol.DisplayName.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            return 700;
        return symbol.QualifiedName.Contains(query, StringComparison.OrdinalIgnoreCase)
            || symbol.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ? 500 : -1;
    }
}
