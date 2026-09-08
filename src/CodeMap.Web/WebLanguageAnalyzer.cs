using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using CodeMap.Core;
using CodeMap.Core.Analysis;
using CodeMap.Core.Models;
using ExCSS;

namespace CodeMap.Web;

public sealed class WebLanguageAnalyzer : ILanguageAnalyzer
{



    public const string AnalyzerVersion = "7";

    private static readonly HtmlParser HtmlDocumentParser = new();
    private static readonly StylesheetParser CssStylesheetParser = new();

    private static readonly Regex FunctionDeclaration = new(@"\b(?:async\s+)?function\s+(?<name>[A-Za-z_$][\w$]*)\s*\((?<args>[^)]*)\)", RegexOptions.Compiled);
    private static readonly Regex ArrowDeclaration = new(@"\b(?:const|let|var)\s+(?<name>[A-Za-z_$][\w$]*)\s*=\s*(?:async\s*)?\((?<args>[^)]*)\)\s*=>", RegexOptions.Compiled);
    private static readonly Regex ClassDeclaration = new(@"\bclass\s+(?<name>[A-Za-z_$][\w$]*)", RegexOptions.Compiled);
    private static readonly Regex InterfaceDeclaration = new(@"\binterface\s+(?<name>[A-Za-z_$][\w$]*)", RegexOptions.Compiled);
    private static readonly Regex TypeDeclaration = new(@"\btype\s+(?<name>[A-Za-z_$][\w$]*)\s*=", RegexOptions.Compiled);
    private static readonly Regex MethodDeclaration = new(@"(?m)^\s*(?:public|private|protected|static|async|export|readonly|get|set|abstract|override|\s)*\s*(?<name>[A-Za-z_$][\w$]*)\s*\((?<args>[^)]*)\)\s*\{", RegexOptions.Compiled);
    private static readonly Regex ImportDeclaration = new(@"\bimport(?:[\s\S]*?\bfrom\s*)?[""'](?<path>[^""']+)[""']", RegexOptions.Compiled);
    private static readonly Regex RequireDeclaration = new(@"\brequire\s*\(\s*[""'](?<path>[^""']+)[""']\s*\)", RegexOptions.Compiled);
    private static readonly Regex CallExpression = new(@"(?<![\w$])(?<name>[A-Za-z_$][\w$]*)\s*\(", RegexOptions.Compiled);
    private static readonly Regex SelectorQuery = new(@"(?:querySelector(?:All)?|getElementById|getElementsByClassName)\s*\(\s*[""'](?<selector>[^""']+)[""']", RegexOptions.Compiled);
    private static readonly Regex HtmlAsset = new(@"<(?:script[^>]+src|link[^>]+href)\s*=\s*[""'](?<path>[^""']+)[""']", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private const double DirectImportConfidence = 0.90;
    private const double ReexportConfidence = 0.85;
    private const double SameFileConfidence = 0.70;
    private const double NameFallbackConfidence = 0.60;
    private const double CssSelectorConfidence = 0.85;
    private const double DomSelectorConfidence = 0.60;

    private sealed record ExportValue(CodeNode Node, bool ViaReexport);
    private sealed record ResolvedBinding(CodeNode? Target, string? ModuleRelative, double Confidence, bool ViaReexport);
    private readonly record struct ExportKey(string RelativePath, string ExportedName);

    public string Language => "web";
    public bool CanAnalyze(string filePath) => GetLanguage(filePath) is not null;

    public Task<AnalysisResult> AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        var root = Path.GetFullPath(context.RootDirectory ?? Path.GetDirectoryName(Path.GetFullPath(context.FilePath))!);
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (context.SourceFiles is not null)
            foreach (var source in context.SourceFiles)
                files[Path.GetFullPath(Path.IsPathRooted(source.Key) ? source.Key : Path.Combine(root, source.Key))] = source.Value;
        var currentPath = Path.GetFullPath(context.FilePath);
        files[currentPath] = context.Content ?? File.ReadAllText(currentPath);
        return Task.FromResult(AnalyzeFiles(context.ProjectName, root, files, cancellationToken));
    }

    internal static AnalyzedProject AnalyzeProject(string projectName, string root, IReadOnlyList<AnalyzedSourceFile> files, CancellationToken cancellationToken)
    {
        var content = files.ToDictionary(file => Path.GetFullPath(file.FilePath), file => file.Content, StringComparer.OrdinalIgnoreCase);
        return new AnalyzedProject(projectName, root, files,
            new WebLanguageAnalyzer().AnalyzeFiles(projectName, root, content, cancellationToken),
            AnalyzerCapabilityLevel.Heuristic);
    }

    private AnalysisResult AnalyzeFiles(string projectName, string root, IReadOnlyDictionary<string, string> files, CancellationToken cancellationToken)
    {
        var nodes = new List<CodeNode>();
        var edges = new List<CodeEdge>();
        var edgeKeys = new HashSet<string>(StringComparer.Ordinal);
        var nodeIds = new HashSet<string>(StringComparer.Ordinal);
        var fileNodes = new Dictionary<string, CodeNode>(StringComparer.OrdinalIgnoreCase);
        var functionRanges = new Dictionary<string, List<(CodeNode Node, int Start, int End)>>(StringComparer.OrdinalIgnoreCase);
        var maskedScripts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lineStartsByPath = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);
        var astByPath = new Dictionary<string, WebScriptAstAnalyzer.ScriptAnalysis>(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in files.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = RelativePath(root, pair.Key);
            var language = GetLanguage(pair.Key)!;
            var fileNode = MakeNode(FileId(projectName, relative), NodeKind.File, Path.GetFileName(pair.Key), relative, relative, language, null, null);
            AddNode(fileNode, nodes, nodeIds);
            fileNodes[pair.Key] = fileNode;
            if (language == "html") ParseHtml(projectName, relative, pair.Value, nodes, nodeIds);
            else if (language == "css") ParseCss(projectName, relative, pair.Value, nodes, nodeIds);
            else
            {
                var lineStarts = ComputeLineStarts(pair.Value);
                lineStartsByPath[pair.Key] = lineStarts;
                var ast = WebScriptAstAnalyzer.Analyze(pair.Value, language, pair.Key);
                astByPath[pair.Key] = ast;
                if (ast.Symbols.Count > 0 || ast.Imports.Count > 0 || ast.HttpLiterals.Count > 0)
                    ParseScriptFromAst(projectName, relative, pair.Value, lineStarts, language, ast, nodes, nodeIds, functionRanges);
                else
                {
                    var masked = StripComments(pair.Value);
                    maskedScripts[pair.Key] = masked;
                    ParseScript(projectName, relative, masked, lineStarts, language, nodes, nodeIds, functionRanges);
                }
            }
        }

        var htmlBySelector = nodes.Where(node => node.Kind == NodeKind.HtmlElement)
            .SelectMany(node =>
            {
                var selector = node.QualifiedName.Contains("/#", StringComparison.Ordinal) ? "#" : ".";
                return new[] { (Selector: selector + node.Name, Node: node) };
            })
            .GroupBy(item => item.Selector, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(item => item.Node).ToArray(), StringComparer.Ordinal);

        foreach (var selector in nodes.Where(node => node.Kind == NodeKind.CssSelector))
            foreach (var target in htmlBySelector.GetValueOrDefault(selector.Name, Array.Empty<CodeNode>()))
                AddEdge(target, selector, EdgeKind.UsesCss, selector.SourceLocation, EdgeResolutionKind.Syntactic, CssSelectorConfidence, edges, edgeKeys);

        ResolveScriptRelations(root, files, maskedScripts, lineStartsByPath, astByPath, nodes, fileNodes, functionRanges, htmlBySelector, WebModuleResolver.Load(root), edges, edgeKeys, cancellationToken);
        return new AnalysisResult { Nodes = nodes, Edges = edges };
    }

    private static void ParseHtml(string project, string relative, string content, List<CodeNode> nodes, HashSet<string> ids)
    {
        using var document = HtmlDocumentParser.ParseDocument(content);
        foreach (var element in document.QuerySelectorAll("*"))
        {
            var location = ElementLocation(element);
            if (!string.IsNullOrWhiteSpace(element.Id))
                AddHtmlElementNode(project, relative, element.Id, "#", location, nodes, ids);
            foreach (var className in element.ClassList)
                if (!string.IsNullOrWhiteSpace(className))
                    AddHtmlElementNode(project, relative, className, ".", location, nodes, ids);
        }
    }

    private static void AddHtmlElementNode(string project, string relative, string value, string prefix, SourceLocation? location, List<CodeNode> nodes, HashSet<string> ids)
    {
        var id = $"html://{Normalize(project)}/{relative}/{prefix}{value}";
        AddNode(MakeNode(id, NodeKind.HtmlElement, value, id, relative, "html", location), nodes, ids);
    }

    private static SourceLocation? ElementLocation(IElement element)
    {
        var reference = element.SourceReference;
        if (reference is null) return null;
        var position = reference.Position;
        return new SourceLocation { StartLine = position.Line, StartColumn = position.Column, EndLine = position.Line, EndColumn = position.Column };
    }

    private static void ParseCss(string project, string relative, string content, List<CodeNode> nodes, HashSet<string> ids)
    {
        var stylesheet = CssStylesheetParser.Parse(content);
        foreach (var rule in EnumerateStyleRules(stylesheet.Children))
            foreach (var selector in rule.SelectorText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (selector.Length == 0) continue;
                var id = $"css://{Normalize(project)}/{relative}/{selector}";
                AddNode(MakeNode(id, NodeKind.CssSelector, selector, id, relative, "css", null), nodes, ids);
            }
    }

    private static IEnumerable<IStyleRule> EnumerateStyleRules(IEnumerable<IStylesheetNode> nodes)
    {
        foreach (var node in nodes)
        {
            if (node is IStyleRule styleRule)
                yield return styleRule;
            foreach (var child in EnumerateStyleRules(node.Children))
                yield return child;
        }
    }

    private static void ParseScript(string project, string relative, string maskedContent, int[] lineStarts, string language, List<CodeNode> nodes, HashSet<string> ids, Dictionary<string, List<(CodeNode Node, int Start, int End)>> functionRanges)
    {
        var declared = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match declaration in ClassDeclaration.Matches(maskedContent))
            AddScriptNode(project, relative, maskedContent, lineStarts, language, declaration, declaration.Groups["name"].Value, NodeKind.Class, null, nodes, ids, declared);
        foreach (Match declaration in InterfaceDeclaration.Matches(maskedContent))
            AddScriptNode(project, relative, maskedContent, lineStarts, language, declaration, declaration.Groups["name"].Value, NodeKind.Interface, null, nodes, ids, declared);
        foreach (Match declaration in TypeDeclaration.Matches(maskedContent))
            AddScriptNode(project, relative, maskedContent, lineStarts, language, declaration, declaration.Groups["name"].Value, NodeKind.Interface, null, nodes, ids, declared);

        var ranges = new List<(CodeNode Node, int Start, int End)>();
        foreach (var declaration in FunctionDeclaration.Matches(maskedContent).Concat(ArrowDeclaration.Matches(maskedContent)).OrderBy(match => match.Index))
        {
            var node = AddScriptNode(project, relative, maskedContent, lineStarts, language, declaration, declaration.Groups["name"].Value, NodeKind.Function, declaration.Groups["args"].Value.Trim(), nodes, ids, declared);
            if (node is not null) ranges.Add((node, declaration.Index, FindBlockEnd(maskedContent, declaration.Index + declaration.Length)));
        }
        foreach (Match method in MethodDeclaration.Matches(maskedContent))
        {
            var name = method.Groups["name"].Value;
            if (name is "if" or "for" or "while" or "switch" or "catch" or "function" || declared.Contains(name)) continue;
            var node = AddScriptNode(project, relative, maskedContent, lineStarts, language, method, name, NodeKind.Method, method.Groups["args"].Value.Trim(), nodes, ids, declared);
            if (node is not null) ranges.Add((node, method.Index, FindBlockEnd(maskedContent, method.Index + method.Length)));
        }
        functionRanges[relative] = ranges;
    }

    private static CodeNode? AddScriptNode(string project, string relative, string content, int[] lineStarts, string language, Match match, string name, NodeKind kind, string? args, List<CodeNode> nodes, HashSet<string> ids, HashSet<string> declared)
    {
        if (!declared.Add(name)) return nodes.FirstOrDefault(node => node.FilePath == relative && node.Name == name);
        var qualified = $"{relative}::{name}";
        var id = $"{(language == "typescript" ? "ts" : "js")}://{Normalize(project)}/{qualified}";
        var signature = kind is NodeKind.Function or NodeKind.Method ? $"{name}({args})" : null;
        var node = MakeNode(id, kind, name, qualified, relative, language, Location(lineStarts, content, match.Index, match.Length), signature);
        AddNode(node, nodes, ids);
        return node;
    }

    private static void ParseScriptFromAst(
        string project,
        string relative,
        string content,
        int[] lineStarts,
        string language,
        WebScriptAstAnalyzer.ScriptAnalysis ast,
        List<CodeNode> nodes,
        HashSet<string> ids,
        Dictionary<string, List<(CodeNode Node, int Start, int End)>> functionRanges)
    {
        var declared = new HashSet<string>(StringComparer.Ordinal);
        var ranges = new List<(CodeNode Node, int Start, int End)>();
        foreach (var symbol in ast.Symbols)
        {
            if (!declared.Add(symbol.Name))
                continue;
            var qualified = $"{relative}::{symbol.Name}";
            var id = $"{(language == "typescript" ? "ts" : "js")}://{Normalize(project)}/{qualified}";
            var location = Location(lineStarts, content, symbol.StartIndex, symbol.EndIndex - symbol.StartIndex);
            var node = MakeNode(id, symbol.Kind, symbol.Name, qualified, relative, language, location, symbol.Signature);
            AddNode(node, nodes, ids);
            if (symbol.Kind is NodeKind.Function or NodeKind.Method)
                ranges.Add((node, symbol.StartIndex, symbol.EndIndex));
        }
        foreach (var literal in ast.HttpLiterals)
        {
            var qualified = $"{relative}::http:{literal.Route}";
            var id = $"route://{Normalize(project)}/{qualified}";
            AddNode(MakeNode(id, NodeKind.Route, literal.Route, qualified, relative, language,
                Location(lineStarts, content, literal.StartIndex, literal.EndIndex - literal.StartIndex)), nodes, ids);
        }
        functionRanges[relative] = ranges;
    }

    private static void ResolveScriptRelations(
        string root,
        IReadOnlyDictionary<string, string> files,
        IReadOnlyDictionary<string, string> maskedScripts,
        IReadOnlyDictionary<string, int[]> lineStartsByPath,
        IReadOnlyDictionary<string, WebScriptAstAnalyzer.ScriptAnalysis> astByPath,
        List<CodeNode> nodes,
        Dictionary<string, CodeNode> fileNodes,
        Dictionary<string, List<(CodeNode Node, int Start, int End)>> functions,
        IReadOnlyDictionary<string, CodeNode[]> htmlBySelector,
        WebModuleResolver.AliasMap aliases,
        List<CodeEdge> edges,
        HashSet<string> edgeKeys,
        CancellationToken cancellationToken)
    {
        var useBindings = !string.Equals(Environment.GetEnvironmentVariable("CODEMAP_WEB_BINDINGS"), "0", StringComparison.Ordinal);
        var scriptNodes = nodes.Where(node => node.Language is "javascript" or "typescript").ToArray();
        var nodeByQualified = scriptNodes.ToDictionary(node => node.QualifiedName, StringComparer.Ordinal);
        var byName = scriptNodes.GroupBy(node => node.Name, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

        if (useBindings)
        {
            var exportTable = BuildExportTable(root, astByPath, nodeByQualified);
            PropagateReexports(root, files, astByPath, exportTable, aliases, maxHops: 8);
            foreach (var pair in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = RelativePath(root, pair.Key);
                if (!fileNodes.TryGetValue(pair.Key, out var fileNode) || !lineStartsByPath.TryGetValue(pair.Key, out var lineStarts))
                    continue;
                if (!astByPath.TryGetValue(pair.Key, out var ast))
                    continue;

                var bindingTargets = ResolveImportBindings(root, pair.Key, ast, files, aliases, exportTable);
                var importedLocalNames = ast.Imports
                    .SelectMany(import => import.Bindings)
                    .Where(binding => binding.Kind != WebScriptAstAnalyzer.ScriptImportBindingKind.SideEffect)
                    .Select(binding => binding.LocalName)
                    .ToHashSet(StringComparer.Ordinal);
                foreach (var import in ast.Imports)
                {
                    var module = WebModuleResolver.Resolve(pair.Key, import.Specifier, files.Keys, aliases);
                    if (module is null || !fileNodes.TryGetValue(module, out var targetFile)) continue;
                    var resolvedKind = import.Specifier.StartsWith('.') ? EdgeResolutionKind.Heuristic : EdgeResolutionKind.Syntactic;
                    var confidence = import.Specifier.StartsWith('.') ? NameFallbackConfidence : DirectImportConfidence;
                    AddEdge(fileNode, targetFile, EdgeKind.Imports, Location(lineStarts, pair.Value, import.StartIndex, import.EndIndex - import.StartIndex), resolvedKind, confidence, edges, edgeKeys);
                }

                foreach (var call in ast.Calls)
                {
                    ResolvedBinding? resolved = null;
                    EdgeResolutionKind resolution = EdgeResolutionKind.Heuristic;
                    var qualifierRoot = call.Qualifier?.Split('.', 2)[0];
                    if (!string.IsNullOrWhiteSpace(qualifierRoot)
                        && bindingTargets.TryGetValue(qualifierRoot, out var namespaceBinding)
                        && namespaceBinding.ModuleRelative is not null
                        && TryResolveExport(exportTable, namespaceBinding.ModuleRelative, call.Name, out var exportNode, out var viaReexport))
                    {
                        resolved = new ResolvedBinding(exportNode, null, viaReexport ? ReexportConfidence : DirectImportConfidence, viaReexport);
                        resolution = EdgeResolutionKind.Syntactic;
                    }
                    else if (bindingTargets.TryGetValue(call.Name, out var bound))
                    {
                        resolved = bound;
                        resolution = EdgeResolutionKind.Syntactic;
                    }
                    else if (!importedLocalNames.Contains(call.Name))
                    {
                        var fallback = ResolveFallbackCallTarget(byName, call.Name, relative);
                        if (fallback is not null)
                        {
                            resolved = fallback;
                            resolution = EdgeResolutionKind.Heuristic;
                        }
                    }
                    if (resolved is null) continue;
                    var source = GetRanges(functions, relative).FirstOrDefault(item => call.EnclosingStartIndex >= item.Start && call.EnclosingEndIndex <= item.End).Node;
                    if (source is not null && resolved.Target is not null)
                        AddEdge(source, resolved.Target, EdgeKind.Calls, Location(lineStarts, pair.Value, call.StartIndex, call.EndIndex - call.StartIndex), resolution, resolved.Confidence, edges, edgeKeys);
                }

                foreach (var jsx in ast.JsxElements)
                {
                    CodeNode? target = null;
                    double confidence = SameFileConfidence;
                    var resolution = EdgeResolutionKind.Heuristic;
                    if (bindingTargets.TryGetValue(jsx.Name, out var bound) && bound.Target is not null)
                    {
                        target = bound.Target;
                        confidence = bound.Confidence;
                        resolution = EdgeResolutionKind.Syntactic;
                    }
                    else if (!importedLocalNames.Contains(jsx.Name))
                    {
                        var fallback = ResolveFallbackCallTarget(byName, jsx.Name, relative);
                        if (fallback is not null)
                        {
                            target = fallback.Target;
                            confidence = fallback.Confidence;
                        }
                    }
                    if (target is null) continue;
                    var source = GetRanges(functions, relative).FirstOrDefault(item => jsx.EnclosingStartIndex >= item.Start && jsx.EnclosingEndIndex <= item.End).Node ?? fileNode;
                    AddEdge(source, target, EdgeKind.Calls, Location(lineStarts, pair.Value, jsx.StartIndex, jsx.EndIndex - jsx.StartIndex), resolution, confidence, edges, edgeKeys);
                }

                foreach (var query in ast.SelectorQueries)
                {
                    var normalized = query.Selector.StartsWith('#') || query.Selector.StartsWith('.') ? query.Selector : "#" + query.Selector;
                    foreach (var target in htmlBySelector.GetValueOrDefault(normalized, Array.Empty<CodeNode>()))
                    {
                        var source = GetRanges(functions, relative).FirstOrDefault(item => query.EnclosingStartIndex >= item.Start && query.EnclosingEndIndex <= item.End).Node ?? fileNode;
                        AddEdge(source, target, EdgeKind.UsesElement, Location(lineStarts, pair.Value, query.StartIndex, query.EndIndex - query.StartIndex), EdgeResolutionKind.Heuristic, DomSelectorConfidence, edges, edgeKeys);
                    }
                }
            }
        }
        else
        {
            foreach (var pair in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = RelativePath(root, pair.Key);
                if (!fileNodes.TryGetValue(pair.Key, out var fileNode) || !lineStartsByPath.TryGetValue(pair.Key, out var lineStarts))
                    continue;
                if (!astByPath.TryGetValue(pair.Key, out var ast)) continue;
                foreach (var import in ast.Imports)
                {
                    var module = WebModuleResolver.Resolve(pair.Key, import.Specifier, files.Keys, aliases);
                    if (module is null || !fileNodes.TryGetValue(module, out var targetFile)) continue;
                    AddEdge(fileNode, targetFile, EdgeKind.Imports, Location(lineStarts, pair.Value, import.StartIndex, import.EndIndex - import.StartIndex), EdgeResolutionKind.Heuristic, NameFallbackConfidence, edges, edgeKeys);
                }
            }
        }

        foreach (var pair in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = RelativePath(root, pair.Key);
            if (!fileNodes.TryGetValue(pair.Key, out var fileNode)) continue;
            if (!useBindings && maskedScripts.TryGetValue(pair.Key, out var masked) && lineStartsByPath.TryGetValue(pair.Key, out var maskedLineStarts))
            {
                foreach (var importMatch in ImportDeclaration.Matches(masked).Concat(RequireDeclaration.Matches(masked)))
                {
                    var module = WebModuleResolver.Resolve(pair.Key, importMatch.Groups["path"].Value, files.Keys, aliases);
                    if (module is null || !fileNodes.TryGetValue(module, out var targetFile)) continue;
                    AddEdge(fileNode, targetFile, EdgeKind.Imports, Location(maskedLineStarts, masked, importMatch.Index, importMatch.Length), EdgeResolutionKind.Heuristic, NameFallbackConfidence, edges, edgeKeys);
                }
                foreach (Match call in CallExpression.Matches(masked))
                {
                    var name = call.Groups["name"].Value;
                    if (name is "if" or "for" or "while" or "switch" or "catch" or "function" or "querySelector" or "querySelectorAll" or "getElementById" or "getElementsByClassName") continue;
                    if (!byName.TryGetValue(name, out var candidates) || candidates.Length == 0) continue;
                    var resolved = ResolveFallbackCallTarget(byName, name, relative);
                    if (resolved is null) continue;
                    var source = GetRanges(functions, relative).FirstOrDefault(item => call.Index >= item.Start && call.Index <= item.End).Node;
                    if (source is not null && resolved.Target is not null)
                        AddEdge(source, resolved.Target, EdgeKind.Calls, Location(maskedLineStarts, masked, call.Index, call.Length), EdgeResolutionKind.Heuristic, resolved.Confidence, edges, edgeKeys);
                }
                foreach (Match query in SelectorQuery.Matches(masked))
                {
                    var selector = query.Groups["selector"].Value;
                    var normalized = selector.StartsWith('#') || selector.StartsWith('.') ? selector : "#" + selector;
                    foreach (var target in htmlBySelector.GetValueOrDefault(normalized, Array.Empty<CodeNode>()))
                    {
                        var source = GetRanges(functions, relative).FirstOrDefault(item => query.Index >= item.Start && query.Index <= item.End).Node ?? fileNode;
                        AddEdge(source, target, EdgeKind.UsesElement, Location(maskedLineStarts, masked, query.Index, query.Length), EdgeResolutionKind.Heuristic, DomSelectorConfidence, edges, edgeKeys);
                    }
                }
            }

            if (GetLanguage(pair.Key) != "html") continue;
            foreach (Match asset in HtmlAsset.Matches(pair.Value))
            {
                var targetPath = WebModuleResolver.Resolve(pair.Key, asset.Groups["path"].Value, files.Keys, aliases);
                if (targetPath is not null && fileNodes.TryGetValue(targetPath, out var target))
                    AddEdge(fileNode, target, EdgeKind.Imports, null, EdgeResolutionKind.Heuristic, NameFallbackConfidence, edges, edgeKeys);
            }
            foreach (Match href in Regex.Matches(pair.Value, @"<a\b[^>]*href\s*=\s*[""'](?<path>[^""'#?]+)[""']", RegexOptions.IgnoreCase))
            {
                var targetPath = WebModuleResolver.Resolve(pair.Key, href.Groups["path"].Value, files.Keys, aliases);
                if (targetPath is not null && fileNodes.TryGetValue(targetPath, out var target))
                    AddEdge(fileNode, target, EdgeKind.References, null, EdgeResolutionKind.Heuristic, NameFallbackConfidence, edges, edgeKeys);
            }
        }
    }

    private static Dictionary<ExportKey, ExportValue?> BuildExportTable(
        string root,
        IReadOnlyDictionary<string, WebScriptAstAnalyzer.ScriptAnalysis> astByPath,
        Dictionary<string, CodeNode> nodeByQualified)
    {
        var table = new Dictionary<ExportKey, ExportValue?>();
        foreach (var (path, ast) in astByPath)
        {
            var relative = RelativePath(root, path);
            foreach (var export in ast.Exports.Where(item => item.Kind is WebScriptAstAnalyzer.ScriptExportKind.Declaration or WebScriptAstAnalyzer.ScriptExportKind.Named))
            {
                var localName = export.LocalName ?? export.ExportedName;
                SetExport(table, relative, export.ExportedName, FindNode(nodeByQualified, relative, localName), viaReexport: false);
            }
        }
        return table;
    }

    private static void PropagateReexports(
        string root,
        IReadOnlyDictionary<string, string> files,
        IReadOnlyDictionary<string, WebScriptAstAnalyzer.ScriptAnalysis> astByPath,
        Dictionary<ExportKey, ExportValue?> exportTable,
        WebModuleResolver.AliasMap aliases,
        int maxHops)
    {
        for (var hop = 0; hop < maxHops; hop++)
        {
            var changed = false;
            foreach (var (path, ast) in astByPath)
            {
                var relative = RelativePath(root, path);
                foreach (var export in ast.Exports.Where(item => item.ReexportSpecifier is not null))
                {
                    var module = WebModuleResolver.Resolve(path, export.ReexportSpecifier!, files.Keys, aliases);
                    if (module is null) continue;
                    var sourceRelative = RelativePath(root, module);
                    if (PropagateSingleReexport(exportTable, relative, sourceRelative, export))
                        changed = true;
                }
            }
            if (!changed)
                break;
        }
    }

    private static bool PropagateSingleReexport(
        Dictionary<ExportKey, ExportValue?> exportTable,
        string targetRelative,
        string sourceRelative,
        WebScriptAstAnalyzer.ScriptExport export)
    {
        var changed = false;
        switch (export.Kind)
        {
            case WebScriptAstAnalyzer.ScriptExportKind.Named:
            case WebScriptAstAnalyzer.ScriptExportKind.DefaultFrom:
            {
                var sourceName = export.LocalName ?? export.ExportedName;
                if (!TryResolveExport(exportTable, sourceRelative, sourceName, out var node, out _))
                    return false;
                changed = SetExport(exportTable, targetRelative, export.ExportedName, node, viaReexport: true);
                break;
            }
            case WebScriptAstAnalyzer.ScriptExportKind.StarFrom:
            {
                foreach (var entry in exportTable.Where(item => item.Key.RelativePath == sourceRelative && item.Value is not null).ToArray())
                {
                    if (SetExport(exportTable, targetRelative, entry.Key.ExportedName, entry.Value!.Node, viaReexport: true))
                        changed = true;
                }
                break;
            }
        }
        return changed;
    }

    private static Dictionary<string, ResolvedBinding> ResolveImportBindings(
        string root,
        string currentFile,
        WebScriptAstAnalyzer.ScriptAnalysis ast,
        IReadOnlyDictionary<string, string> files,
        WebModuleResolver.AliasMap aliases,
        Dictionary<ExportKey, ExportValue?> exportTable)
    {
        var bindings = new Dictionary<string, ResolvedBinding>(StringComparer.Ordinal);
        foreach (var import in ast.Imports)
        {
            var module = WebModuleResolver.Resolve(currentFile, import.Specifier, files.Keys, aliases);
            if (module is null) continue;
            var moduleRelative = RelativePath(root, module);
            foreach (var binding in import.Bindings)
            {
                if (binding.Kind == WebScriptAstAnalyzer.ScriptImportBindingKind.SideEffect)
                    continue;
                if (binding.Kind == WebScriptAstAnalyzer.ScriptImportBindingKind.Namespace)
                {
                    bindings[binding.LocalName] = new ResolvedBinding(null, moduleRelative, DirectImportConfidence, false);
                    continue;
                }
                var importedName = binding.Kind == WebScriptAstAnalyzer.ScriptImportBindingKind.Default ? "default" : binding.ImportedName;
                if (!TryResolveExport(exportTable, moduleRelative, importedName, out var node, out var viaReexport))
                    continue;
                bindings[binding.LocalName] = new ResolvedBinding(node, null, viaReexport ? ReexportConfidence : DirectImportConfidence, viaReexport);
            }
        }
        return bindings;
    }

    private static bool TryResolveExport(Dictionary<ExportKey, ExportValue?> exportTable, string moduleRelative, string exportedName, out CodeNode node, out bool viaReexport)
    {
        node = null!;
        viaReexport = false;
        if (!exportTable.TryGetValue(new ExportKey(moduleRelative, exportedName), out var value) || value is null)
            return false;
        node = value.Node;
        viaReexport = value.ViaReexport;
        return true;
    }

    private static bool SetExport(Dictionary<ExportKey, ExportValue?> exportTable, string relativePath, string exportedName, CodeNode? node, bool viaReexport)
    {
        if (node is null) return false;
        var key = new ExportKey(relativePath, exportedName);
        if (!exportTable.TryGetValue(key, out var existing))
        {
            exportTable[key] = new ExportValue(node, viaReexport);
            return true;
        }
        if (existing is null) return false;
        if (existing.Node.Id != node.Id)
        {
            exportTable[key] = null;
            return false;
        }
        exportTable[key] = new ExportValue(node, existing.ViaReexport || viaReexport);
        return true;
    }

    private static CodeNode? FindNode(Dictionary<string, CodeNode> nodeByQualified, string relative, string name) =>
        nodeByQualified.GetValueOrDefault($"{relative}::{name}");

    private static ResolvedBinding? ResolveFallbackCallTarget(Dictionary<string, CodeNode[]> byName, string name, string callerRelativePath)
    {
        if (!byName.TryGetValue(name, out var candidates) || candidates.Length == 0)
            return null;
        if (candidates.Length == 1)
        {
            var confidence = string.Equals(candidates[0].FilePath, callerRelativePath, StringComparison.OrdinalIgnoreCase)
                ? SameFileConfidence
                : NameFallbackConfidence;
            return new ResolvedBinding(candidates[0], null, confidence, false);
        }
        var sameFile = candidates.Where(candidate => string.Equals(candidate.FilePath, callerRelativePath, StringComparison.OrdinalIgnoreCase)).ToArray();
        return sameFile.Length == 1 ? new ResolvedBinding(sameFile[0], null, SameFileConfidence, false) : null;
    }

    private static IReadOnlyList<(CodeNode Node, int Start, int End)> GetRanges(Dictionary<string, List<(CodeNode Node, int Start, int End)>> functions, string relative) =>
        functions.TryGetValue(relative, out var ranges) ? ranges : Array.Empty<(CodeNode Node, int Start, int End)>();

    private static int FindBlockEnd(string content, int start)
    {
        var open = content.IndexOf('{', start);
        if (open < 0) return content.Length;
        var depth = 0;
        for (var index = open; index < content.Length; index++)
        {
            if (content[index] == '{') depth++;
            else if (content[index] == '}' && --depth == 0) return index;
        }
        return content.Length;
    }

    private static string StripComments(string content)
    {
        var buffer = content.ToCharArray();
        var index = 0;
        while (index < content.Length)
        {
            var current = content[index];
            if (current == '/' && index + 1 < content.Length && content[index + 1] == '/')
            {
                var start = index;
                while (index < content.Length && content[index] != '\n') index++;
                Blank(buffer, start, index);
            }
            else if (current == '/' && index + 1 < content.Length && content[index + 1] == '*')
            {
                var start = index;
                index += 2;
                while (index + 1 < content.Length && !(content[index] == '*' && content[index + 1] == '/')) index++;
                index = Math.Min(index + 2, content.Length);
                Blank(buffer, start, index);
            }
            else if (current is '"' or '\'' or '`')
            {
                index++;
                while (index < content.Length && content[index] != current)
                    index += content[index] == '\\' && index + 1 < content.Length ? 2 : 1;
                index = Math.Min(index + 1, content.Length);
            }
            else
            {
                index++;
            }
        }
        return new string(buffer);
    }

    private static void Blank(char[] buffer, int start, int end)
    {
        for (var i = start; i < end; i++)
            if (buffer[i] != '\n') buffer[i] = ' ';
    }

    private static CodeNode MakeNode(string id, NodeKind kind, string name, string qualified, string relative, string language, SourceLocation? location, string? signature = null) => new()
    {
        Id = id, Kind = kind, Name = name, QualifiedName = qualified, FilePath = relative,
        Language = language, SourceLocation = location, Signature = signature
    };

    private static void AddNode(CodeNode node, List<CodeNode> nodes, HashSet<string> ids) { if (ids.Add(node.Id)) nodes.Add(node); }

    private static void AddEdge(
        CodeNode source,
        CodeNode target,
        EdgeKind kind,
        SourceLocation? location,
        EdgeResolutionKind resolutionKind,
        double confidence,
        List<CodeEdge> edges,
        HashSet<string> edgeKeys)
    {
        var key = $"{source.Id}\u001f{target.Id}\u001f{kind}\u001f{location?.StartLine}{location?.StartColumn}{location?.EndLine}{location?.EndColumn}";
        if (source.Id == target.Id || !edgeKeys.Add(key)) return;
        edges.Add(new CodeEdge
        {
            SourceId = source.Id,
            TargetId = target.Id,
            Kind = kind,
            ResolutionKind = resolutionKind,
            Confidence = confidence,
            SourceLocation = location
        });
    }

    private static string FileId(string project, string relative) => $"file://{Normalize(project)}/{relative}";
    private static string Normalize(string value) => value.Replace('\\', '/').Trim('/');
    private static string RelativePath(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

    private static int[] ComputeLineStarts(string content)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < content.Length; i++)
            if (content[i] == '\n') starts.Add(i + 1);
        return starts.ToArray();
    }

    private static SourceLocation Location(int[] lineStarts, string content, int index, int length)
    {
        var actualLength = Math.Min(length, content.Length - index);
        var startLineIndex = FindLine(lineStarts, index);
        var startColumn = index - lineStarts[startLineIndex] + 1;
        var endLineIndex = FindLine(lineStarts, index + actualLength);
        return new SourceLocation
        {
            StartLine = startLineIndex + 1, StartColumn = startColumn,
            EndLine = endLineIndex + 1, EndColumn = startColumn + actualLength
        };
    }

    private static int FindLine(int[] lineStarts, int position)
    {
        var low = 0;
        var high = lineStarts.Length - 1;
        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            if (lineStarts[mid] <= position) low = mid;
            else high = mid - 1;
        }
        return low;
    }

    internal static string? GetLanguage(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".js" or ".mjs" or ".cjs" or ".jsx" => "javascript",
        ".ts" or ".tsx" => "typescript",
        ".html" or ".htm" => "html",
        ".css" => "css",
        _ => null
    };
}

public class WebWorkspaceIndexer
{
    private readonly WebLanguageAnalyzer _analyzer;
    public WebWorkspaceIndexer(WebLanguageAnalyzer? analyzer = null) => _analyzer = analyzer ?? new WebLanguageAnalyzer();

    public virtual async Task<AnalyzedProject?> AnalyzeAsync(string root, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var paths = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(file => !IgnoreRules.IsIgnored(root, file) && _analyzer.CanAnalyze(file))
            .OrderBy(file => file, StringComparer.OrdinalIgnoreCase);

        var files = new List<AnalyzedSourceFile>();
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var content = await File.ReadAllTextAsync(path, cancellationToken);
            files.Add(new AnalyzedSourceFile(path, Path.GetRelativePath(root, path).Replace('\\', '/'), content, WebLanguageAnalyzer.GetLanguage(path)!));
        }
        if (files.Count == 0) return null;
        cancellationToken.ThrowIfCancellationRequested();
        return WebLanguageAnalyzer.AnalyzeProject("web:" + new DirectoryInfo(root).Name, root, files, cancellationToken);
    }
}
