using CodeMap.Core.Analysis;
using CodeMap.Core.Models;

namespace CodeMap.Scip;

public sealed class ScipGraphMapper
{
    private readonly ScipSymbolMapper _symbols;

    public ScipGraphMapper(ScipSymbolMapper? symbols = null) => _symbols = symbols ?? new ScipSymbolMapper();

    public AnalyzedProject Map(string importName, string repositoryRoot, ScipIndex index)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(importName);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentNullException.ThrowIfNull(index);
        var projectName = "scip:" + importName;
        var root = Path.GetFullPath(repositoryRoot);
        var nodes = new List<CodeNode>();
        var edges = new List<CodeEdge>();
        var nodeIds = new Dictionary<string, string>(StringComparer.Ordinal);
        var definitions = new Dictionary<string, (ScipDocument Document, ScipOccurrence Occurrence)>(StringComparer.Ordinal);
        var files = new List<AnalyzedSourceFile>();

        foreach (var document in index.Documents.OrderBy(document => document.RelativePath, StringComparer.Ordinal))
        {
            var relativePath = NormalizeRelativePath(document.RelativePath);
            var path = Path.GetFullPath(Path.Combine(root, relativePath));
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && !string.Equals(path, root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"SCIP document '{document.RelativePath}' is outside the repository root.");
            if (!File.Exists(path))
                throw new FileNotFoundException($"SCIP document source file was not found: {relativePath}", path);
            files.Add(new AnalyzedSourceFile(path, relativePath, File.ReadAllText(path), document.Language));
            nodes.Add(new CodeNode { Id = $"file://{projectName}/{relativePath}", Kind = NodeKind.File, Name = Path.GetFileName(relativePath), QualifiedName = relativePath, FilePath = relativePath, Language = document.Language });

            foreach (var occurrence in document.Occurrences.Where(occurrence => occurrence.IsDefinition).OrderBy(occurrence => occurrence.Range.StartLine).ThenBy(occurrence => occurrence.Range.StartColumn))
                definitions.TryAdd(occurrence.Symbol, (document, occurrence));
        }

        foreach (var document in index.Documents.OrderBy(document => document.RelativePath, StringComparer.Ordinal))
        {
            var relativePath = NormalizeRelativePath(document.RelativePath);
            foreach (var information in document.Symbols.OrderBy(symbol => symbol.Symbol, StringComparer.Ordinal))
            {
                var kind = _symbols.MapKind(information.Kind);
                if (kind is null || !definitions.TryGetValue(information.Symbol, out var definition))
                    continue;
                var id = _symbols.CreateId(importName, information.Symbol);
                nodeIds[information.Symbol] = id;
                nodes.Add(new CodeNode { Id = id, Kind = kind.Value, Name = _symbols.DisplayName(information), QualifiedName = information.Symbol,
                    FilePath = relativePath, SourceLocation = ToLocation(definition.Occurrence.Range), Language = document.Language });
                edges.Add(new CodeEdge { SourceId = $"file://{projectName}/{relativePath}", TargetId = id, Kind = EdgeKind.Defines });
            }
        }

        foreach (var document in index.Documents)
        foreach (var occurrence in document.Occurrences.Where(occurrence => !occurrence.IsDefinition).OrderBy(occurrence => occurrence.Range.StartLine).ThenBy(occurrence => occurrence.Range.StartColumn))
        {
            if (!nodeIds.TryGetValue(occurrence.Symbol, out var target) || !TryFindEnclosingDefinition(document, occurrence, nodeIds, out var source))
                continue;
            edges.Add(new CodeEdge { SourceId = source, TargetId = target, Kind = EdgeKind.References, ResolutionKind = EdgeResolutionKind.Semantic, Confidence = 1.0, SourceLocation = ToLocation(occurrence.Range) });
        }
        foreach (var information in index.Documents.SelectMany(document => document.Symbols).OrderBy(symbol => symbol.Symbol, StringComparer.Ordinal))
        {
            if (!nodeIds.TryGetValue(information.Symbol, out var implementation))
                continue;
            foreach (var relationship in information.Relationships?.Where(item => item.IsImplementation).OrderBy(item => item.Symbol, StringComparer.Ordinal)
                ?? Enumerable.Empty<ScipRelationship>())
            {
                if (!nodeIds.TryGetValue(relationship.Symbol, out var contract))
                    continue;
                edges.Add(new CodeEdge { SourceId = implementation, TargetId = contract, Kind = EdgeKind.Implements, ResolutionKind = EdgeResolutionKind.Semantic, Confidence = 1.0 });
                edges.Add(new CodeEdge { SourceId = contract, TargetId = implementation, Kind = EdgeKind.ImplementedBy, ResolutionKind = EdgeResolutionKind.Semantic, Confidence = 1.0 });
            }
        }
        return new AnalyzedProject(projectName, root, files, new AnalysisResult { Nodes = nodes, Edges = edges }, AnalyzerCapabilityLevel.Semantic);
    }

    private static bool TryFindEnclosingDefinition(ScipDocument document, ScipOccurrence occurrence, IReadOnlyDictionary<string, string> ids, out string source)
    {
        if (occurrence.EnclosingSymbol is { } enclosingSymbol && ids.TryGetValue(enclosingSymbol, out var enclosingId))
        {
            source = enclosingId;
            return true;
        }

        var enclosing = document.Occurrences.Where(candidate => candidate.IsDefinition && ids.ContainsKey(candidate.Symbol) && Contains(candidate.EnclosingRange ?? candidate.Range, occurrence.Range))
            .OrderBy(candidate => Span(candidate.Range)).ThenBy(candidate => candidate.Range.StartLine).FirstOrDefault();
        if (enclosing is null)
        {
            source = string.Empty;
            return false;
        }
        source = ids[enclosing.Symbol];
        return true;
    }

    private static bool Contains(ScipRange outer, ScipRange inner) => (outer.StartLine < inner.StartLine || outer.StartLine == inner.StartLine && outer.StartColumn <= inner.StartColumn)
        && (outer.EndLine > inner.EndLine || outer.EndLine == inner.EndLine && outer.EndColumn >= inner.EndColumn);
    private static int Span(ScipRange range) => (range.EndLine - range.StartLine) * 1_000_000 + range.EndColumn - range.StartColumn;
    private static SourceLocation ToLocation(ScipRange range) => new() { StartLine = range.StartLine + 1, StartColumn = range.StartColumn + 1, EndLine = range.EndLine + 1, EndColumn = range.EndColumn + 1 };
    private static string NormalizeRelativePath(string path) => path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
}
