namespace CodeMap.Core.Models;

public sealed record SymbolSearchResult(IReadOnlyList<IndexedSymbol> Matches, bool IsAmbiguous = false);

public sealed record ImpactItem(IndexedSymbol Symbol, IndexedEdge Via, int Depth, string? RootId = null);

public sealed record RepoMap(IReadOnlyList<string> Lines, int TokenBudget, int EstimatedTokens)
{
    public string Text => string.Join(Environment.NewLine, Lines);
}

public sealed record IndexSummary(
    int IndexedFiles, int Added, int Updated, int Removed, int Skipped,
    int Symbols, int Edges, TimeSpan Elapsed,
    IReadOnlyList<string> AnalyzedProjects = null!)
{
    public IReadOnlyList<string> AnalyzedProjects { get; init; } = AnalyzedProjects ?? Array.Empty<string>();
    public IReadOnlyList<string> SkippedPropagationNotes { get; init; } = Array.Empty<string>();

    public override string ToString() =>
        $"Indexed {IndexedFiles} files\nAdded: {Added}\nUpdated: {Updated}\nRemoved: {Removed}\nSkipped: {Skipped}\nSymbols: {Symbols}\nEdges: {Edges}\nElapsed: {Elapsed.TotalMilliseconds:0} ms"
        + (SkippedPropagationNotes.Count == 0
            ? string.Empty
            : $"\nFingerprint propagation skips:\n{string.Join('\n', SkippedPropagationNotes.Select(note => $"- {note}"))}");
}
