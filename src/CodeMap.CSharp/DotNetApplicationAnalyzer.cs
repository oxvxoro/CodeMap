using CodeMap.Core;
using CodeMap.Core.Ids;
using CodeMap.Core.Models;
using Microsoft.CodeAnalysis;

namespace CodeMap.CSharp;


internal sealed record DotNetEnrichmentResult(IReadOnlyList<CSharpSourceFile> Files, AnalysisResult Result)
{
    internal static readonly DotNetEnrichmentResult Empty = new(Array.Empty<CSharpSourceFile>(), new AnalysisResult { Nodes = [], Edges = [] });
}







public static class DotNetMarkupFiles
{
    public static bool IsMarkupFile(string filePath) =>
        HasExtension(filePath, ".razor") || HasExtension(filePath, ".cshtml") || HasExtension(filePath, ".xaml");

    public static bool IsRazorFile(string filePath) =>
        HasExtension(filePath, ".razor") || HasExtension(filePath, ".cshtml");

    public static bool IsXamlFile(string filePath) => HasExtension(filePath, ".xaml");

    private static bool HasExtension(string filePath, string extension) =>
        filePath.EndsWith(extension, StringComparison.OrdinalIgnoreCase);
}








internal static class DotNetApplicationAnalyzer
{
    internal static async Task<DotNetEnrichmentResult> EnrichProjectAsync(
        Project project,
        Compilation compilation,
        AnalysisResult csharpResult,
        IReadOnlyDictionary<string, ISymbol> csharpSymbolsById,
        string projectDirectory,
        ICodeMapIdGenerator ids,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var nodes = new List<CodeNode>();
        var edges = new List<CodeEdge>();
        var files = new List<CSharpSourceFile>();
        var nodeIds = new HashSet<string>(StringComparer.Ordinal);
        var edgeKeys = new HashSet<string>(StringComparer.Ordinal);

        void AddNode(CodeNode node)
        {
            if (nodeIds.Add(node.Id))
                nodes.Add(node);
        }

        void AddEdge(CodeEdge edge)
        {
            var key = $"{edge.SourceId}{edge.TargetId}{edge.Kind}{edge.SourceLocation?.StartLine}{edge.SourceLocation?.StartColumn}{edge.SourceLocation?.EndLine}{edge.SourceLocation?.EndColumn}";
            if (edgeKeys.Add(key))
                edges.Add(edge);
        }





        var lookup = new DotNetSourceGraphLookup(csharpResult, csharpSymbolsById);



        var aspNetResult = AspNetCoreAnalyzer.Analyze(project.Name, projectDirectory, compilation, lookup, ids, cancellationToken);
        foreach (var node in aspNetResult.Nodes) AddNode(node);
        foreach (var edge in aspNetResult.Edges) AddEdge(edge);







        var razorFiles = new List<(string Path, string RelativePath, string Content)>();
        foreach (var razorPath in EnumerateMarkupFiles(projectDirectory, DotNetMarkupFiles.IsRazorFile))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = CSharpLanguageAnalyzer.GetRelativePath(projectDirectory, razorPath);
            var content = await File.ReadAllTextAsync(razorPath, cancellationToken);
            files.Add(new CSharpSourceFile(razorPath, relativePath, content));
            razorFiles.Add((razorPath, relativePath, content));
        }

        if (razorFiles.Count > 0)
        {
            var razorArtifactNodes = razorFiles
                .Select(file => RazorMarkupAnalyzer.PredeclareArtifact(project.Name, file.RelativePath, file.Content))
                .ToArray();
            var augmentedLookup = new DotNetSourceGraphLookup(csharpResult, csharpSymbolsById, razorArtifactNodes);

            foreach (var file in razorFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var razorResult = RazorMarkupAnalyzer.Analyze(project.Name, file.RelativePath, file.Content, augmentedLookup, ids);
                foreach (var node in razorResult.Nodes) AddNode(node);
                foreach (var edge in razorResult.Edges) AddEdge(edge);
            }
        }





        var xamlFiles = EnumerateMarkupFiles(projectDirectory, DotNetMarkupFiles.IsXamlFile).ToArray();
        if (xamlFiles.Length > 0 && XamlMarkupAnalyzer.IsWpfProject(projectDirectory, xamlFiles))
        {
            foreach (var xamlPath in xamlFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relativePath = CSharpLanguageAnalyzer.GetRelativePath(projectDirectory, xamlPath);
                var content = await File.ReadAllTextAsync(xamlPath, cancellationToken);
                files.Add(new CSharpSourceFile(xamlPath, relativePath, content));
                var xamlResult = XamlMarkupAnalyzer.Analyze(project.Name, relativePath, content, lookup, ids);
                foreach (var node in xamlResult.Nodes) AddNode(node);
                foreach (var edge in xamlResult.Edges) AddEdge(edge);
            }
        }

        if (nodes.Count == 0 && edges.Count == 0 && files.Count == 0)
            return DotNetEnrichmentResult.Empty;
        return new DotNetEnrichmentResult(files, new AnalysisResult { Nodes = nodes, Edges = edges });
    }

    private static IEnumerable<string> EnumerateMarkupFiles(string projectDirectory, Func<string, bool> predicate)
    {
        if (!Directory.Exists(projectDirectory))
            yield break;
        foreach (var file in Directory.EnumerateFiles(projectDirectory, "*.*", SearchOption.AllDirectories))
        {
            if (!predicate(file))
                continue;
            if (IgnoreRules.IsIgnored(projectDirectory, file) || IgnoreRules.IsOutsideRoot(projectDirectory, file))
                continue;
            yield return file;
        }
    }
}







internal sealed class DotNetSourceGraphLookup
{
    private readonly ILookup<string, CodeNode> _byQualifiedName;
    private readonly ILookup<string, CodeNode> _byName;
    private readonly Dictionary<string, CodeNode> _byId;
    private readonly IReadOnlyDictionary<string, ISymbol> _symbolsById;
    private readonly Dictionary<ISymbol, CodeNode> _nodeBySymbol;

    internal DotNetSourceGraphLookup(AnalysisResult csharpResult, IReadOnlyDictionary<string, ISymbol> csharpSymbolsById)
        : this(csharpResult, csharpSymbolsById, Array.Empty<CodeNode>())
    {
    }



    internal DotNetSourceGraphLookup(AnalysisResult csharpResult, IReadOnlyDictionary<string, ISymbol> csharpSymbolsById, IReadOnlyList<CodeNode> extraNodes)
    {
        var allNodes = extraNodes.Count == 0 ? csharpResult.Nodes : csharpResult.Nodes.Concat(extraNodes).ToArray();
        _byQualifiedName = allNodes.ToLookup(n => n.QualifiedName, StringComparer.Ordinal);
        _byName = allNodes.ToLookup(n => n.Name, StringComparer.Ordinal);
        _byId = allNodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        _symbolsById = csharpSymbolsById;
        Nodes = allNodes;







        _nodeBySymbol = new Dictionary<ISymbol, CodeNode>(SymbolEqualityComparer.Default);
        foreach (var (nodeId, symbol) in _symbolsById)
            if (_byId.TryGetValue(nodeId, out var node))
                _nodeBySymbol[symbol] = node;
    }

    internal IReadOnlyList<CodeNode> Nodes { get; }



    internal ISymbol? SymbolFor(string nodeId) => _symbolsById.GetValueOrDefault(nodeId);





    internal CodeNode? NodeForSymbol(ISymbol symbol) => _nodeBySymbol.GetValueOrDefault(symbol.OriginalDefinition);

    internal CodeNode? ById(string id) => _byId.GetValueOrDefault(id);

    internal IReadOnlyList<CodeNode> ByQualifiedName(string qualifiedName) => _byQualifiedName[qualifiedName].ToArray();

    internal IReadOnlyList<CodeNode> ByName(string name) => _byName[name].ToArray();


    internal CodeNode? SingleClassByName(string name)
    {
        var matches = _byName[name].Where(n => n.Kind is NodeKind.Class or NodeKind.Record).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    internal CodeNode? SingleClassByQualifiedName(string qualifiedName)
    {
        var matches = _byQualifiedName[qualifiedName].Where(n => n.Kind is NodeKind.Class or NodeKind.Record).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }
}
