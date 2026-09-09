using CodeMap.CSharp;
using CodeMap.Core.Models;

namespace CodeMap.Storage;

public sealed record SliceScope(string SymbolId, string DisplayName, string File, int? StartLine, int? EndLine);

public sealed record SemanticSliceResult(
    IndexedSymbol EntrySymbol,
    SliceScope Scope,
    IReadOnlyList<SliceItem> Items,
    IReadOnlyList<SliceDependency> Dependencies,
    bool Truncated,
    string? Source = null);

public sealed class SemanticSliceException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

public sealed class CodeMapSemanticSliceService
{
    private static readonly SemaphoreSlim SliceConcurrencyGate = new(2, 2);
    private readonly IncrementalCodeMapIndexer _indexer;
    private readonly ProjectStateLocator _projectStateLocator;
    private readonly CSharpSymbolLocator _symbolLocator = new();
    private readonly CSharpSemanticSliceAnalyzer _analyzer = new();

    public CodeMapSemanticSliceService(IncrementalCodeMapIndexer? indexer = null, ProjectStateLocator? projectStateLocator = null)
    {
        _indexer = indexer ?? new IncrementalCodeMapIndexer();
        _projectStateLocator = projectStateLocator ?? new ProjectStateLocator();
    }

    public async Task<SemanticSliceResult> SliceAsync(string projectRoot, SemanticSliceRequest request, CancellationToken cancellationToken = default)
    {
        await SliceConcurrencyGate.WaitAsync(cancellationToken);
        try { return await SliceCoreAsync(projectRoot, request, cancellationToken); }
        finally { SliceConcurrencyGate.Release(); }
    }

    private async Task<SemanticSliceResult> SliceCoreAsync(string projectRoot, SemanticSliceRequest request, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Query))
            throw new SemanticSliceException("query_failed", "A symbol query is required.");
        var databasePath = CodeMapIndexLocator.FindDatabase(projectRoot);
        var root = CodeMapIndexLocator.ResolveIndexRoot(databasePath);
        if (!await _indexer.IsUpToDateAsync(root, cancellationToken))
            throw new SemanticSliceException("semantic_slice_stale_index", "Semantic slice requires a fresh index. Run: codemap update");
        var graph = await new CodeMapQueryStore(databasePath).LoadAsync(cancellationToken)
            ?? throw new SemanticSliceException("query_failed", "The index could not be opened.");
        var resolved = new CodeMapQueryService(graph).ResolveSymbol(request.Query, callableOnly: false, maxResults: 20);
        if (resolved.Matches.Count == 0)
            throw new SemanticSliceException("no_matches", $"No symbol matches '{request.Query}'.");
        if (resolved.IsAmbiguous)
            throw new SemanticSliceException("ambiguous", $"Symbol query '{request.Query}' is ambiguous.");
        var entry = resolved.Matches[0];
        if (!string.Equals(entry.Language, "csharp", StringComparison.OrdinalIgnoreCase))
            throw new SemanticSliceException("unsupported_language", "Semantic slice currently supports C# symbols only.");
        if (entry.Kind is not (NodeKind.Method or NodeKind.Constructor or NodeKind.Function or NodeKind.Property))
            throw new SemanticSliceException("unsupported_symbol_kind", $"'{entry.Kind}' is not an executable C# scope.");

        var projectPath = await _projectStateLocator.FindProjectPathAsync(root, entry.Project, cancellationToken)
            ?? throw new SemanticSliceException("source_not_found", $"Could not resolve project '{entry.Project}' from index state.");
        using var workspace = await CSharpWorkspaceIndexer.OpenProjectWorkspaceAsync(projectPath, cancellationToken);
        var symbol = await _symbolLocator.ResolveAsync(workspace.Project, entry.Id, entry.RelativePath, entry.StartLine, entry.Name, cancellationToken)
            ?? throw new SemanticSliceException("roslyn_symbol_not_found", $"Could not resolve '{entry.DisplayName}' in current C# source.");
        var sourceLocation = symbol.Locations.FirstOrDefault(location => location.IsInSource);
        var sourcePath = sourceLocation?.SourceTree?.FilePath;
        var document = sourcePath is null ? null : workspace.Project.Documents.FirstOrDefault(document =>
            string.Equals(document.FilePath, sourcePath, StringComparison.OrdinalIgnoreCase));
        var model = document is null ? null : await document.GetSemanticModelAsync(cancellationToken);
        if (model is null)
            throw new SemanticSliceException("source_not_found", "Could not load the symbol source document.");
        var analysis = _analyzer.Analyze(model, symbol, request, cancellationToken);
        if (!await _indexer.IsUpToDateAsync(root, cancellationToken))
            throw new SemanticSliceException("semantic_slice_stale_index", "Source changed while semantic slice was being computed. Run: codemap update");
        string? source = null;
        if (request.IncludeSource)
        {
            var sourceDocument = document ?? throw new SemanticSliceException("source_not_found", "Could not load the symbol source document.");
            var text = await sourceDocument.GetTextAsync(cancellationToken);
            var start = Math.Max(0, (entry.StartLine ?? 1) - 1);
            var end = Math.Min(text.Lines.Count - 1, (entry.EndLine ?? text.Lines.Count) - 1);
            if (end >= start)
                source = string.Join(Environment.NewLine, Enumerable.Range(start, end - start + 1).Select(line => text.Lines[line].ToString()));
        }
        return new SemanticSliceResult(entry,
            new SliceScope(entry.Id, entry.DisplayName, entry.RelativePath, entry.StartLine, entry.EndLine),
            analysis.Items, analysis.Dependencies, analysis.Truncated, source);
    }
}
