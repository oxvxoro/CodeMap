using System.Collections.Immutable;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using CodeMap.Core;
using CodeMap.Core.Analysis;
using CodeMap.Core.Ids;
using CodeMap.Core.Models;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;

namespace CodeMap.CSharp;

public sealed record CSharpSourceFile(string FilePath, string RelativePath, string Content);

public sealed record CSharpProjectAnalysis(
    string ProjectName,
    string ProjectPath,
    IReadOnlyList<CSharpSourceFile> Files,
    AnalysisResult Result,
    string? PublicSurfaceFingerprint = null,
    IReadOnlyDictionary<string, HashSet<string>>? ExternalRoots = null)
{
    public AnalyzedProject ToAnalyzedProject() =>
        new(ProjectName, ProjectPath,
            Files.Select(file => new AnalyzedSourceFile(file.FilePath, file.RelativePath, file.Content, LanguageForFile(file.RelativePath))).ToArray(),
            Result,
            AnalyzerCapabilityLevel.Semantic);



    private static string LanguageForFile(string relativePath)
    {
        if (DotNetMarkupFiles.IsXamlFile(relativePath))
            return "xaml";
        if (DotNetMarkupFiles.IsRazorFile(relativePath))
            return "razor";
        return "csharp";
    }
}














internal sealed record CompilationAnalysis(
    AnalysisResult Result,
    IReadOnlyDictionary<string, ISymbol> SymbolsById,
    string? PublicSurfaceFingerprint,
    IReadOnlyDictionary<string, HashSet<string>> ExternalRoots);





public sealed class CSharpLanguageAnalyzer : ILanguageAnalyzer
{









    // 저장된 분석 결과의 의미가 바뀌면 이전 인덱스를 강제로 다시 만든다.
    public const string AnalyzerVersion = "7";

    private readonly ICodeMapIdGenerator _ids;

    public CSharpLanguageAnalyzer(ICodeMapIdGenerator? ids = null) => _ids = ids ?? new CodeMapIdGenerator();

    public string Language => "csharp";

    public bool CanAnalyze(string filePath) => filePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

    public async Task<AnalysisResult> AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        var root = Path.GetFullPath(context.RootDirectory ?? Path.GetDirectoryName(Path.GetFullPath(context.FilePath))!);
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (context.SourceFiles is not null)
            foreach (var source in context.SourceFiles)
            {
                var sourcePath = Path.IsPathRooted(source.Key) ? source.Key : Path.Combine(root, source.Key);
                files[Path.GetFullPath(sourcePath)] = source.Value;
            }
        var currentPath = Path.GetFullPath(context.FilePath);
        files[currentPath] = context.Content ?? await File.ReadAllTextAsync(currentPath, cancellationToken);

        var trees = files.Select(pair => CSharpSyntaxTree.ParseText(
                pair.Value,
                CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview),
                pair.Key))
            .ToImmutableArray();
        var compilation = CSharpCompilation.Create(
            context.ProjectName,
            trees,
            CreateDefaultReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var sourceFiles = files.Select(pair => new CSharpSourceFile(
                pair.Key,
                GetRelativePath(root, pair.Key),
                pair.Value))
            .ToArray();
        var analysis = await AnalyzeCompilationAsync(context.ProjectName, root, sourceFiles, compilation,
            EmptyAssemblyToProjectMap, cancellationToken);
        return analysis.Result;
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyAssemblyToProjectMap =
        new Dictionary<string, string>(StringComparer.Ordinal);









    private static IReadOnlyDictionary<string, string> BuildAssemblyToProjectMap(Project project)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var ambiguous = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<ProjectId>();
        var queue = new Queue<Project>();
        queue.Enqueue(project);
        visited.Add(project.Id);






        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var referenced in current.ProjectReferences
                .Select(reference => project.Solution.GetProject(reference.ProjectId))
                .Where(referenced => referenced is not null && referenced.Language == LanguageNames.CSharp))
            {
                if (!visited.Add(referenced!.Id))
                    continue;
                queue.Enqueue(referenced);

                var assemblyName = referenced.AssemblyName;
                if (ambiguous.Contains(assemblyName))
                    continue;
                if (map.TryGetValue(assemblyName, out var existing) && !string.Equals(existing, referenced.Name, StringComparison.Ordinal))
                {
                    map.Remove(assemblyName);
                    ambiguous.Add(assemblyName);
                    continue;
                }
                map[assemblyName] = referenced.Name;
            }
        }
        return map;
    }

    internal async Task<CSharpProjectAnalysis> AnalyzeProjectAsync(Project project, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var compilation = await project.GetCompilationAsync(cancellationToken)
            ?? throw new InvalidOperationException($"Roslyn could not create a compilation for '{project.Name}'.");
        var projectPath = project.FilePath ?? throw new InvalidOperationException($"Project '{project.Name}' has no path.");
        var root = Path.GetDirectoryName(projectPath)!;
        var files = new List<CSharpSourceFile>();
        foreach (var document in project.Documents.Where(d => d.FilePath is not null && CanAnalyze(d.FilePath)
            && !IgnoreRules.IsIgnored(root, d.FilePath) && !IgnoreRules.IsOutsideRoot(root, d.FilePath)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = await document.GetTextAsync(cancellationToken);
            files.Add(new CSharpSourceFile(document.FilePath!, GetRelativePath(root, document.FilePath!), text.ToString()));
        }

        var assemblyToProject = BuildAssemblyToProjectMap(project);
        var analysis = await AnalyzeCompilationAsync(project.Name, root, files, compilation, assemblyToProject, cancellationToken);






        var enrichment = await DotNetApplicationAnalyzer.EnrichProjectAsync(project, compilation, analysis.Result, analysis.SymbolsById, root, _ids, cancellationToken);
        var mergedFiles = MergeFiles(files, enrichment.Files);
        var mergedResult = MergeResult(analysis.Result, enrichment.Result);
        return new CSharpProjectAnalysis(project.Name, projectPath, mergedFiles, mergedResult, analysis.PublicSurfaceFingerprint, analysis.ExternalRoots);
    }

    private static IReadOnlyList<CSharpSourceFile> MergeFiles(IReadOnlyList<CSharpSourceFile> files, IReadOnlyList<CSharpSourceFile> markupFiles)
    {
        if (markupFiles.Count == 0)
            return files;
        var seen = new HashSet<string>(files.Select(f => f.FilePath), StringComparer.OrdinalIgnoreCase);
        var merged = new List<CSharpSourceFile>(files);
        foreach (var file in markupFiles)
            if (seen.Add(file.FilePath))
                merged.Add(file);
        return merged;
    }

    private static AnalysisResult MergeResult(AnalysisResult baseResult, AnalysisResult enrichmentResult)
    {
        if (enrichmentResult.Nodes.Count == 0 && enrichmentResult.Edges.Count == 0)
            return baseResult;

        var nodes = new List<CodeNode>(baseResult.Nodes);
        var nodeIds = new HashSet<string>(baseResult.Nodes.Select(n => n.Id), StringComparer.Ordinal);
        foreach (var node in enrichmentResult.Nodes)
            if (nodeIds.Add(node.Id))
                nodes.Add(node);

        var edges = new List<CodeEdge>(baseResult.Edges);
        var edgeKeys = new HashSet<string>(
            baseResult.Edges.Select(EdgeKey),
            StringComparer.Ordinal);
        foreach (var edge in enrichmentResult.Edges)
            if (edgeKeys.Add(EdgeKey(edge)))
                edges.Add(edge);

        return new AnalysisResult { Nodes = nodes, Edges = edges };
    }

    private static string EdgeKey(CodeEdge edge) =>
        $"{edge.SourceId}{edge.TargetId}{edge.Kind}{edge.SourceLocation?.StartLine}{edge.SourceLocation?.StartColumn}{edge.SourceLocation?.EndLine}{edge.SourceLocation?.EndColumn}";

    internal static ImmutableArray<MetadataReference> CreateDefaultReferences()
    {
        var paths = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        return paths.Select(path => (MetadataReference)MetadataReference.CreateFromFile(path)).ToImmutableArray();
    }

    private async Task<CompilationAnalysis> AnalyzeCompilationAsync(
        string projectName,
        string root,
        IReadOnlyList<CSharpSourceFile> files,
        Compilation compilation,
        IReadOnlyDictionary<string, string> assemblyToProject,
        CancellationToken cancellationToken)
    {
        var nodes = new Dictionary<ISymbol, CodeNode>(SymbolEqualityComparer.Default);
        var symbolFiles = new Dictionary<ISymbol, string>(SymbolEqualityComparer.Default);
        var externalRoots = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var trees = compilation.SyntaxTrees.ToDictionary(tree => Path.GetFullPath(tree.FilePath ?? string.Empty), StringComparer.OrdinalIgnoreCase);
        var fileNodes = new Dictionary<string, CodeNode>(StringComparer.OrdinalIgnoreCase);
        var edges = new List<CodeEdge>();
        var edgeKeys = new HashSet<string>(StringComparer.Ordinal);





        var rootsAndModels = new Dictionary<string, (SyntaxNode Root, SemanticModel Model)>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileId = _ids.CreateFileId(projectName, file.RelativePath);
            fileNodes[file.FilePath] = new CodeNode
            {
                Id = fileId, Kind = NodeKind.File, Name = Path.GetFileName(file.FilePath),
                QualifiedName = file.RelativePath, FilePath = file.RelativePath, Language = Language
            };
            if (!trees.TryGetValue(Path.GetFullPath(file.FilePath), out var tree))
                continue;
            var model = compilation.GetSemanticModel(tree);
            var syntaxRoot = tree.GetRoot(cancellationToken);
            rootsAndModels[file.FilePath] = (syntaxRoot, model);
            RegisterDeclarations(syntaxRoot, model, file.RelativePath, projectName, nodes, symbolFiles);
        }

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!rootsAndModels.TryGetValue(file.FilePath, out var rootAndModel))
                continue;
            RegisterAnonymousFunctions(rootAndModel.Root, rootAndModel.Model, file.RelativePath, projectName, nodes, symbolFiles);
        }


        var symbolsByFile = new Dictionary<string, List<ISymbol>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (symbol, filePath) in symbolFiles)
        {
            if (!symbolsByFile.TryGetValue(filePath, out var list))
                symbolsByFile[filePath] = list = [];
            list.Add(symbol);
        }
        foreach (var pair in fileNodes)
        {
            if (!symbolsByFile.TryGetValue(pair.Value.FilePath!, out var definedSymbols))
                continue;
            foreach (var symbol in definedSymbols)
                AddEdge(pair.Value.Id, nodes[symbol].Id, EdgeKind.Defines, null, null, edges, edgeKeys);
        }

        foreach (var pair in nodes.ToArray())
        {
            var symbol = pair.Key;
            var node = pair.Value;
            if (symbol.ContainingSymbol is not null && nodes.TryGetValue(symbol.ContainingSymbol, out var container))
                AddEdge(container.Id, node.Id, EdgeKind.Contains, null, null, edges, edgeKeys);

            switch (symbol)
            {
                case INamedTypeSymbol type:
                    AddTypeRelations(type, nodes, assemblyToProject, edges, edgeKeys, compilation, externalRoots);
                    break;
                case IMethodSymbol method:
                    AddMethodRelations(method, nodes, assemblyToProject, edges, edgeKeys, compilation, externalRoots);
                    foreach (var parameter in method.Parameters)
                        AddTypeEdge(method, parameter.Type, EdgeKind.UsesType, nodes, assemblyToProject, edges, edgeKeys);
                    if (method.MethodKind != MethodKind.Constructor && !method.ReturnsVoid)
                        AddTypeEdge(method, method.ReturnType, EdgeKind.UsesType, nodes, assemblyToProject, edges, edgeKeys);
                    break;
                case IPropertySymbol property:
                    AddTypeEdge(property, property.Type, EdgeKind.UsesType, nodes, assemblyToProject, edges, edgeKeys);
                    if (property.OverriddenProperty is not null)
                    {
                        var overriddenTarget = ResolveTargetId(property.OverriddenProperty, nodes, assemblyToProject, compilation, externalRoots);
                        if (overriddenTarget is not null)
                            AddEdge(node.Id, overriddenTarget.Value.Id, EdgeKind.Overrides, null, overriddenTarget.Value.Project, edges, edgeKeys);
                    }
                    break;
                case IFieldSymbol field:
                    AddTypeEdge(field, field.Type, EdgeKind.UsesType, nodes, assemblyToProject, edges, edgeKeys);
                    break;
                case IEventSymbol @event:
                    AddTypeEdge(@event, @event.Type, EdgeKind.UsesType, nodes, assemblyToProject, edges, edgeKeys);
                    if (@event.OverriddenEvent is not null)
                    {
                        var overriddenTarget = ResolveTargetId(@event.OverriddenEvent, nodes, assemblyToProject, compilation, externalRoots);
                        if (overriddenTarget is not null)
                            AddEdge(node.Id, overriddenTarget.Value.Id, EdgeKind.Overrides, null, overriddenTarget.Value.Project, edges, edgeKeys);
                    }
                    break;
            }
        }

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!rootsAndModels.TryGetValue(file.FilePath, out var rootAndModel))
                continue;
            var model = rootAndModel.Model;
            var rootNode = rootAndModel.Root;









            foreach (var syntax in rootNode.DescendantNodes())
            {
                switch (syntax)
                {
                    case InvocationExpressionSyntax invocation:
                    {
                        if (model.GetSymbolInfo(invocation, cancellationToken).Symbol is not IMethodSymbol target)
                            continue;
                        var source = GetSourceSymbol(model, invocation.SpanStart, nodes);
                        var targetResolution = source is null ? null : ResolveTargetId(target, nodes, assemblyToProject, compilation, externalRoots);
                        if (source is null || targetResolution is null)
                            continue;
                        AddEdge(source.Id, targetResolution.Value.Id, EdgeKind.Calls, ToLocation(invocation.GetLocation()), targetResolution.Value.Project, edges, edgeKeys);
                        break;
                    }
                    case ObjectCreationExpressionSyntax creation:
                    {
                        if (model.GetSymbolInfo(creation, cancellationToken).Symbol is not IMethodSymbol constructor)
                            continue;
                        var source = GetSourceSymbol(model, creation.SpanStart, nodes);
                        var targetResolution = source is null ? null : ResolveTargetId(constructor, nodes, assemblyToProject, compilation, externalRoots);
                        if (source is null || targetResolution is null)
                            continue;
                        AddEdge(source.Id, targetResolution.Value.Id, EdgeKind.Constructs, ToLocation(creation.GetLocation()), targetResolution.Value.Project, edges, edgeKeys);
                        break;
                    }






                    case SimpleNameSyntax name:
                    {
                        if (model.GetSymbolInfo(name, cancellationToken).Symbol is not INamedTypeSymbol typeSymbol)
                            continue;
                        var source = GetSourceSymbol(model, name.SpanStart, nodes);
                        var targetResolution = source is null ? null : ResolveTargetId(typeSymbol, nodes, assemblyToProject);
                        if (source is null || targetResolution is null)
                            continue;
                        AddEdge(source.Id, targetResolution.Value.Id, EdgeKind.References, ToLocation(name.GetLocation()), targetResolution.Value.Project, edges, edgeKeys);
                        break;
                    }
                }
            }
        }

        var symbolsById = nodes.ToDictionary(pair => pair.Value.Id, pair => pair.Key, StringComparer.Ordinal);
        var result = new AnalysisResult { Nodes = fileNodes.Values.Concat(nodes.Values).ToArray(), Edges = edges };
        var fingerprint = ComputePublicSurfaceFingerprint(compilation, nodes.Keys);
        return new CompilationAnalysis(result, symbolsById, fingerprint, externalRoots);
    }





























    private static string? ComputePublicSurfaceFingerprint(Compilation compilation, IEnumerable<ISymbol> declaredSymbols)
    {
        if (compilation.Assembly.GetAttributes().Any(attribute =>
                string.Equals(attribute.AttributeClass?.Name, "InternalsVisibleToAttribute", StringComparison.Ordinal)))
            return null;

        var entries = new List<string>();
        foreach (var symbol in declaredSymbols)
        {
            if (!IsEffectivelyExternallyVisible(symbol))
                continue;
            var qualifiedName = GetQualifiedName(symbol);
            var signature = GetFingerprintSignature(symbol);
            var kind = symbol.Kind.ToString();
            var accessibility = symbol.DeclaredAccessibility.ToString();
            var extra = symbol is INamedTypeSymbol namedType
                ? "|base=" + (namedType.BaseType?.ToDisplayString() ?? "")
                  + "|impl=" + string.Join(",", namedType.AllInterfaces.Select(i => i.ToDisplayString()).OrderBy(name => name, StringComparer.Ordinal))
                  + "|constraints=" + string.Join(",", namedType.TypeParameters.Select(FormatTypeParameterWithVariance))
                  + "|enumtype=" + (namedType.EnumUnderlyingType?.ToDisplayString() ?? "")
                  + "|invoke=" + (namedType.DelegateInvokeMethod is { } invoke
                      ? invoke.ReturnType.ToDisplayString(FingerprintTypeFormat)
                        + "(" + string.Join(",", invoke.Parameters.Select(FormatParameterModifiers)) + ")"
                      : "")
                : "";
            var attributes = FormatAttributesForFingerprint(symbol);
            entries.Add($"{qualifiedName}|{kind}|{accessibility}|{signature}{extra}|attrs={attributes}");
        }
        entries.Sort(StringComparer.Ordinal);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', entries)))).ToLowerInvariant();
    }













    private static string FormatAttributesForFingerprint(ISymbol symbol)
    {
        var parts = new List<string>();
        AppendAttributes(parts, "self", symbol.GetAttributes());
        if (symbol is IMethodSymbol method)
        {
            AppendAttributes(parts, "return", method.GetReturnTypeAttributes());
            foreach (var parameter in method.Parameters)
                AppendAttributes(parts, "param:" + parameter.Name, parameter.GetAttributes());
            foreach (var typeParameter in method.TypeParameters)
                AppendAttributes(parts, "typeparam:" + typeParameter.Name, typeParameter.GetAttributes());
        }
        else if (symbol is INamedTypeSymbol type)
        {
            foreach (var typeParameter in type.TypeParameters)
                AppendAttributes(parts, "typeparam:" + typeParameter.Name, typeParameter.GetAttributes());
        }
        parts.Sort(StringComparer.Ordinal);
        return string.Join(",", parts);
    }

    private static void AppendAttributes(List<string> parts, string slot, ImmutableArray<AttributeData> attributes)
    {
        foreach (var attribute in attributes)
        {
            var typeName = attribute.AttributeClass?.ToDisplayString(FingerprintTypeFormat) ?? "?";
            var constructorArgs = string.Join(",", attribute.ConstructorArguments.Select(FormatTypedConstant));
            var namedArgs = string.Join(",", attribute.NamedArguments
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => pair.Key + "=" + FormatTypedConstant(pair.Value)));
            parts.Add($"{slot}:{typeName}({constructorArgs})[{namedArgs}]");
        }
    }








    private static string FormatTypedConstant(TypedConstant constant) => constant.Kind switch
    {
        TypedConstantKind.Error => "?",
        TypedConstantKind.Array => "[" + string.Join(",", constant.Values.Select(FormatTypedConstant)) + "]",
        _ => constant.Value switch
        {
            null => "null",
            IFormattable formattable => formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
            var value => value.ToString() ?? "null"
        }
    };

    private static readonly SymbolDisplayFormat FingerprintTypeFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameOnly,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

























    private static string? GetFingerprintSignature(ISymbol symbol)
    {
        var baseSignature = GetSignature(symbol);
        return symbol switch
        {
            IMethodSymbol method => baseSignature
                + "->" + (method.MethodKind == MethodKind.Constructor ? "void" : method.ReturnType.ToDisplayString(FingerprintTypeFormat))
                + "|params=" + string.Join(",", method.Parameters.Select(FormatParameterModifiers))
                + "|constraints=" + string.Join(",", method.TypeParameters.Select(FormatTypeParameterConstraints))
                + "|mods=" + FormatMemberModifiers(method),
            IPropertySymbol property => baseSignature + ":" + property.Type.ToDisplayString(FingerprintTypeFormat)
                + "|params=" + string.Join(",", property.Parameters.Select(FormatParameterModifiers))
                + "|get=" + (property.GetMethod?.DeclaredAccessibility.ToString() ?? "")
                + "|set=" + (property.SetMethod?.DeclaredAccessibility.ToString() ?? "")
                + "|mods=" + FormatMemberModifiers(property),
            IFieldSymbol field => baseSignature + ":" + field.Type.ToDisplayString(FingerprintTypeFormat)
                + "|mods=" + FormatMemberModifiers(field),
            IEventSymbol @event => baseSignature + ":" + @event.Type.ToDisplayString(FingerprintTypeFormat)
                + "|mods=" + FormatMemberModifiers(@event),
            _ => baseSignature
        };
    }

    private static string FormatParameterModifiers(IParameterSymbol parameter) =>
        parameter.RefKind + (parameter.IsParams ? ",params" : "") + ":" + parameter.Type.ToDisplayString(FingerprintTypeFormat);






    private static string FormatMemberModifiers(ISymbol symbol) =>
        (symbol.IsStatic ? "static," : "")
        + (symbol.IsVirtual ? "virtual," : "")
        + (symbol.IsAbstract ? "abstract," : "")
        + (symbol.IsSealed ? "sealed," : "")
        + (symbol.IsOverride ? "override," : "");









    private static string FormatTypeParameterWithVariance(ITypeParameterSymbol typeParameter) =>
        "variance=" + typeParameter.Variance + "|constraints=" + FormatTypeParameterConstraints(typeParameter);

    private static string FormatTypeParameterConstraints(ITypeParameterSymbol typeParameter)
    {
        var parts = new List<string>();
        if (typeParameter.HasReferenceTypeConstraint) parts.Add("class");
        if (typeParameter.HasValueTypeConstraint) parts.Add("struct");
        if (typeParameter.HasUnmanagedTypeConstraint) parts.Add("unmanaged");
        if (typeParameter.HasNotNullConstraint) parts.Add("notnull");
        parts.AddRange(typeParameter.ConstraintTypes
            .Select(type => type.ToDisplayString(FingerprintTypeFormat))
            .OrderBy(name => name, StringComparer.Ordinal));
        if (typeParameter.HasConstructorConstraint) parts.Add("new()");
        return typeParameter.Name + "(" + string.Join(",", parts) + ")";
    }







    private static bool IsEffectivelyExternallyVisible(ISymbol? symbol)
    {
        for (var current = symbol; current is not null; current = current.ContainingSymbol)
        {
            if (current is INamespaceSymbol)
                break;
            if (current.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal))
                return false;
        }
        return true;
    }

    private void RegisterDeclarations(
        SyntaxNode root,
        SemanticModel model,
        string relativePath,
        string projectName,
        Dictionary<ISymbol, CodeNode> nodes,
        Dictionary<ISymbol, string> symbolFiles)
    {
        foreach (var declaration in root.DescendantNodesAndSelf().Where(IsSupportedDeclaration))
        {
            var symbol = model.GetDeclaredSymbol(declaration);
            if (symbol is null || !symbol.Locations.Any(l => l.IsInSource))
                continue;
            symbol = symbol.OriginalDefinition;
            if (nodes.ContainsKey(symbol))
            {
                var location = ToLocation(declaration.GetLocation());
                var existingNode = nodes[symbol];
                if (!existingNode.AdditionalLocations.Any(existing =>
                        string.Equals(existing.FilePath, relativePath, StringComparison.OrdinalIgnoreCase) &&
                        existing.Location.StartLine == location.StartLine &&
                        existing.Location.StartColumn == location.StartColumn))
                    existingNode.AdditionalLocations.Add(new AdditionalLocation(relativePath, location));
                continue;
            }
            var kind = GetNodeKind(declaration, symbol);
            var qualifiedName = GetQualifiedName(symbol);
            var signature = GetSignature(symbol);





            var documentationId = symbol is IMethodSymbol { MethodKind: MethodKind.LocalFunction }
                ? null
                : symbol.GetDocumentationCommentId();
            var node = new CodeNode
            {
                Id = string.IsNullOrEmpty(documentationId)
                    ? _ids.CreateSymbolId(projectName, GetSymbolIdKey(symbol, qualifiedName, signature))
                    : _ids.CreateGlobalSymbolId(documentationId),
                Kind = kind,
                Name = symbol.Name,
                QualifiedName = qualifiedName,
                FilePath = relativePath,
                SourceLocation = ToLocation(declaration.GetLocation()),
                Language = Language,
                Signature = signature,
                Visibility = GetVisibility(symbol)
            };
            nodes.Add(symbol, node);
            symbolFiles.Add(symbol, relativePath);
        }
    }








    private void RegisterAnonymousFunctions(
        SyntaxNode root,
        SemanticModel model,
        string relativePath,
        string projectName,
        Dictionary<ISymbol, CodeNode> nodes,
        Dictionary<ISymbol, string> symbolFiles)
    {
        foreach (var anonymousFunction in root.DescendantNodes().OfType<AnonymousFunctionExpressionSyntax>())
        {
            if (!TryGetAnonymousFunctionSymbol(model, anonymousFunction, out var symbol) || symbol is null)
                continue;

            symbol = (IMethodSymbol)symbol.OriginalDefinition;
            if (nodes.ContainsKey(symbol))
                continue;

            var location = ToLocation(anonymousFunction.GetLocation());
            var qualifiedName = GetQualifiedName(symbol, location);
            var signature = GetSignature(symbol);
            var node = new CodeNode
            {
                Id = _ids.CreateSymbolId(projectName, qualifiedName),
                Kind = NodeKind.Function,
                Name = "<lambda>",
                QualifiedName = qualifiedName,
                FilePath = relativePath,
                SourceLocation = location,
                Language = Language,
                Signature = signature,
                Visibility = null
            };
            nodes.Add(symbol, node);
            symbolFiles.Add(symbol, relativePath);
        }
    }







    internal static bool TryGetAnonymousFunctionSymbol(SemanticModel model, SyntaxNode anonymousFunction, out IMethodSymbol? symbol)
    {
        symbol = model.GetSymbolInfo(anonymousFunction).Symbol as IMethodSymbol
            ?? model.GetDeclaredSymbol(anonymousFunction) as IMethodSymbol;
        return symbol is not null;
    }

    private static bool IsSupportedDeclaration(SyntaxNode node) => node switch
    {
        BaseNamespaceDeclarationSyntax or BaseTypeDeclarationSyntax or DelegateDeclarationSyntax => true,
        BaseMethodDeclarationSyntax or PropertyDeclarationSyntax or IndexerDeclarationSyntax => true,
        LocalFunctionStatementSyntax => true,
        VariableDeclaratorSyntax variable when variable.Parent?.Parent is FieldDeclarationSyntax or EventFieldDeclarationSyntax => true,
        EventDeclarationSyntax or EventFieldDeclarationSyntax => true,
        _ => false
    };

    private static NodeKind GetNodeKind(SyntaxNode declaration, ISymbol symbol) => symbol switch
    {
        INamespaceSymbol => NodeKind.Namespace,
        IMethodSymbol method when method.MethodKind == MethodKind.Constructor => NodeKind.Constructor,
        IMethodSymbol method when method.MethodKind == MethodKind.LocalFunction => NodeKind.Function,
        IMethodSymbol => NodeKind.Method,
        IPropertySymbol => NodeKind.Property,
        IFieldSymbol => NodeKind.Field,
        IEventSymbol => NodeKind.Event,
        INamedTypeSymbol named when named.TypeKind == TypeKind.Interface => NodeKind.Interface,
        INamedTypeSymbol named when named.TypeKind == TypeKind.Struct => NodeKind.Struct,
        INamedTypeSymbol named when named.TypeKind == TypeKind.Enum => NodeKind.Enum,
        INamedTypeSymbol named when named.TypeKind == TypeKind.Delegate => NodeKind.Delegate,
        INamedTypeSymbol named when named.IsRecord => NodeKind.Record,
        INamedTypeSymbol => NodeKind.Class,
        _ => NodeKind.Function
    };

    private static string GetQualifiedName(ISymbol symbol)
    {
        if (symbol is INamespaceSymbol ns)
            return ns.IsGlobalNamespace ? string.Empty : ns.ToDisplayString();
        if (symbol is INamedTypeSymbol)
            return symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);









        if (symbol is IMethodSymbol { MethodKind: MethodKind.LocalFunction } local && local.ContainingSymbol is not null)
            return GetContainerQualifiedName(local.ContainingSymbol) + "::" + local.Name;
        if (symbol.ContainingType is not null)
        {
            var containingType = symbol.ContainingType.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
            return symbol is IMethodSymbol { MethodKind: MethodKind.Constructor }
                ? containingType + "::.ctor"
                : containingType + "." + symbol.Name;
        }
        return symbol.ContainingNamespace is { IsGlobalNamespace: false } containingNs
            ? containingNs.ToDisplayString() + "." + symbol.Name
            : symbol.Name;
    }








    private static string GetQualifiedName(IMethodSymbol anonymousFunction, SourceLocation location)
    {
        var containerName = anonymousFunction.ContainingSymbol is { } container
            ? GetContainerQualifiedName(container)
            : string.Empty;
        return containerName + "::<lambda>@" + location.StartLine + ":" + location.StartColumn;
    }








    private static string GetContainerQualifiedName(ISymbol container)
    {
        var qualifiedName = GetQualifiedName(container);
        return GetSymbolIdKey(container, qualifiedName, GetSignature(container));
    }

    private static string? GetSignature(ISymbol symbol)
    {
        if (symbol is not IMethodSymbol and not IPropertySymbol and not IFieldSymbol and not IEventSymbol)
            return null;
        var format = new SymbolDisplayFormat(
            globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
            typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameOnly,
            genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
            parameterOptions: SymbolDisplayParameterOptions.IncludeType,
            miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);
        return symbol switch
        {
            IMethodSymbol method => (method.MethodKind == MethodKind.Constructor ? ".ctor" : method.Name)
                + (method.TypeParameters.Length == 0 ? string.Empty : "<" + string.Join(", ", method.TypeParameters.Select(t => t.Name)) + ">")
                + "(" + string.Join(", ", method.Parameters.Select(p => p.Type.ToDisplayString(format))) + ")",
            IPropertySymbol property => property.Name,
            IFieldSymbol field => field.Name,
            IEventSymbol @event => @event.Name,
            _ => null
        };
    }

    private static string GetSymbolIdKey(ISymbol symbol, string qualifiedName, string? signature)
    {
        if (symbol is not IMethodSymbol || string.IsNullOrEmpty(signature))
            return qualifiedName;
        var memberName = symbol is IMethodSymbol { MethodKind: MethodKind.Constructor } ? ".ctor" : symbol.Name;
        return qualifiedName + signature[memberName.Length..];
    }

    private static string? GetVisibility(ISymbol symbol) => symbol.DeclaredAccessibility switch
    {
        Accessibility.NotApplicable => null,
        var value => value.ToString().ToLowerInvariant()
    };













    private (string Id, string? Project)? ResolveTargetId(
        ISymbol symbol, Dictionary<ISymbol, CodeNode> nodes, IReadOnlyDictionary<string, string> assemblyToProject,
        Compilation? compilation = null, Dictionary<string, HashSet<string>>? externalRoots = null)
    {
        var original = symbol.OriginalDefinition;
        if (nodes.TryGetValue(original, out var local))
            return (local.Id, null);
        var documentationId = original.GetDocumentationCommentId();
        if (string.IsNullOrEmpty(documentationId))
            return null;
        var assemblyName = original.ContainingAssembly?.Name ?? string.Empty;
        var targetProject = assemblyToProject.GetValueOrDefault(assemblyName);
        if (targetProject is null && compilation is not null && externalRoots is not null
            && original.ContainingAssembly is { } containingAssembly
            && compilation.GetMetadataReference(containingAssembly) is PortableExecutableReference { FilePath: { } pePath })
        {
            if (!externalRoots.TryGetValue(pePath, out var docIds))
                externalRoots[pePath] = docIds = new HashSet<string>(StringComparer.Ordinal);
            docIds.Add(documentationId);
        }
        return (_ids.CreateGlobalSymbolId(documentationId), targetProject);
    }

    private void AddTypeRelations(INamedTypeSymbol type, Dictionary<ISymbol, CodeNode> nodes,
        IReadOnlyDictionary<string, string> assemblyToProject, List<CodeEdge> edges, HashSet<string> keys,
        Compilation compilation, Dictionary<string, HashSet<string>> externalRoots)
    {
        if (!nodes.TryGetValue(type, out var typeNode))
            return;
        if (type.BaseType is not null)
        {
            var baseTarget = ResolveTargetId(type.BaseType, nodes, assemblyToProject, compilation, externalRoots);
            if (baseTarget is not null)
                AddEdge(typeNode.Id, baseTarget.Value.Id, EdgeKind.Inherits, null, baseTarget.Value.Project, edges, keys);
        }
        foreach (var iface in type.AllInterfaces)
        {
            var interfaceTarget = ResolveTargetId(iface, nodes, assemblyToProject, compilation, externalRoots);
            if (interfaceTarget is not null)
                AddEdge(typeNode.Id, interfaceTarget.Value.Id, EdgeKind.Implements, null, interfaceTarget.Value.Project, edges, keys);
            if (!nodes.TryGetValue(iface.OriginalDefinition, out var interfaceNode))
                continue;
            AddEdge(interfaceNode.Id, typeNode.Id, EdgeKind.ImplementedBy, null, null, edges, keys);
            foreach (var interfaceMember in iface.GetMembers())
            {
                if (!nodes.TryGetValue(interfaceMember.OriginalDefinition, out var interfaceMemberNode))
                    continue;
                var implementation = type.FindImplementationForInterfaceMember(interfaceMember);
                if (implementation is not null && nodes.TryGetValue(implementation.OriginalDefinition, out var implementationNode))
                    AddEdge(interfaceMemberNode.Id, implementationNode.Id, EdgeKind.ImplementedBy, null, null, edges, keys);
            }
        }
    }

    private void AddMethodRelations(IMethodSymbol method, Dictionary<ISymbol, CodeNode> nodes,
        IReadOnlyDictionary<string, string> assemblyToProject, List<CodeEdge> edges, HashSet<string> keys,
        Compilation compilation, Dictionary<string, HashSet<string>> externalRoots)
    {
        if (!nodes.TryGetValue(method, out var source))
            return;
        if (method.OverriddenMethod is not null)
        {
            var overriddenTarget = ResolveTargetId(method.OverriddenMethod, nodes, assemblyToProject, compilation, externalRoots);
            if (overriddenTarget is not null)
                AddEdge(source.Id, overriddenTarget.Value.Id, EdgeKind.Overrides, null, overriddenTarget.Value.Project, edges, keys);
        }
        foreach (var ifaceMethod in method.ExplicitInterfaceImplementations)
            if (nodes.TryGetValue(ifaceMethod.OriginalDefinition, out var ifaceNode))
                AddEdge(ifaceNode.Id, source.Id, EdgeKind.ImplementedBy, null, null, edges, keys);
    }









    private void AddTypeEdge(ISymbol source, ITypeSymbol type, EdgeKind kind, Dictionary<ISymbol, CodeNode> nodes,
        IReadOnlyDictionary<string, string> assemblyToProject, List<CodeEdge> edges, HashSet<string> keys)
    {
        var named = type as INamedTypeSymbol ?? type.OriginalDefinition as INamedTypeSymbol;
        if (named is null || !nodes.TryGetValue(source, out var sourceNode))
            return;
        var target = ResolveTargetId(named, nodes, assemblyToProject);
        if (target is not null)
            AddEdge(sourceNode.Id, target.Value.Id, kind, null, target.Value.Project, edges, keys);
    }

    private static CodeNode? GetSourceSymbol(SemanticModel model, int position, Dictionary<ISymbol, CodeNode> nodes)
    {
        for (var symbol = model.GetEnclosingSymbol(position); symbol is not null; symbol = symbol.ContainingSymbol)
            if (nodes.TryGetValue(symbol.OriginalDefinition, out var node))
                return node;
        return null;
    }

    private static void AddEdge(string sourceId, string targetId, EdgeKind kind, SourceLocation? location,
        string? targetProject, List<CodeEdge> edges, HashSet<string> keys)
    {
        var key = $"{sourceId}\u001f{targetId}\u001f{kind}\u001f{location?.StartLine}\u001f{location?.StartColumn}\u001f{location?.EndLine}\u001f{location?.EndColumn}";
        if (sourceId == targetId || !keys.Add(key))
            return;
        edges.Add(new CodeEdge
        {
            SourceId = sourceId,
            TargetId = targetId,
            Kind = kind,
            ResolutionKind = EdgeResolutionKind.Semantic,
            Confidence = 1.0,
            SourceLocation = location,
            TargetProject = targetProject
        });
    }

    internal static SourceLocation ToLocation(Location location)
    {
        var span = location.GetLineSpan();
        return new SourceLocation
        {
            StartLine = span.StartLinePosition.Line + 1, StartColumn = span.StartLinePosition.Character + 1,
            EndLine = span.EndLinePosition.Line + 1, EndColumn = span.EndLinePosition.Character + 1
        };
    }

    internal static string GetRelativePath(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative == "." ? Path.GetFileName(path) : relative.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
    }
}


public sealed class CSharpWorkspaceIndexer
{
    private readonly CSharpLanguageAnalyzer _analyzer;

    public CSharpWorkspaceIndexer(CSharpLanguageAnalyzer? analyzer = null) => _analyzer = analyzer ?? new CSharpLanguageAnalyzer();

    public static async Task<CSharpProjectWorkspace> OpenProjectWorkspaceAsync(string projectPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        EnsureMsBuildRegistered();
        var workspace = MSBuildWorkspace.Create();
        try
        {
            var project = await workspace.OpenProjectAsync(projectPath, cancellationToken: cancellationToken);
            return new CSharpProjectWorkspace(workspace, project);
        }
        catch
        {
            workspace.Dispose();
            throw;
        }
    }

    public IReadOnlyList<string> LastAnalyzedProjectNames { get; private set; } = Array.Empty<string>();

    public async Task<IReadOnlyList<CSharpProjectAnalysis>> AnalyzeAsync(
        string inputPath,
        IReadOnlySet<string>? projectNames = null,
        CancellationToken cancellationToken = default) =>
        (await AnalyzeWithReferencingGraphAsync(inputPath, projectNames, cancellationToken)).Projects;

    public async Task<CSharpWorkspaceAnalysisResult> AnalyzeWithReferencingGraphAsync(
        string inputPath,
        IReadOnlySet<string>? projectNames = null,
        CancellationToken cancellationToken = default)
    {
        var projectPath = ResolveInput(inputPath);
        EnsureMsBuildRegistered();
        using var workspace = MSBuildWorkspace.Create();
        if (Path.GetExtension(projectPath).Equals(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            var single = await _analyzer.AnalyzeProjectAsync(
                await workspace.OpenProjectAsync(projectPath, cancellationToken: cancellationToken),
                cancellationToken);
            var included = projectNames is null || projectNames.Contains(single.ProjectName)
                ? new[] { single }
                : Array.Empty<CSharpProjectAnalysis>();
            var (externalProjects, owningMapSingle) = await AnalyzeExternalAssembliesAsync(included, cancellationToken);
            var allProjects = included.Concat(externalProjects).ToArray();
            LastAnalyzedProjectNames = allProjects.Select(project => project.ProjectName).ToArray();
            var fingerprintsSingle = allProjects.ToDictionary(project => project.ProjectName, project => project.PublicSurfaceFingerprint, StringComparer.Ordinal);
            var referencesSingle = new Dictionary<string, string[]>(StringComparer.Ordinal);
            foreach (var project in included)
                referencesSingle[project.ProjectName] = Array.Empty<string>();
            return new CSharpWorkspaceAnalysisResult(
                allProjects,
                new Dictionary<string, HashSet<string>>(StringComparer.Ordinal),
                referencesSingle,
                fingerprintsSingle,
                owningMapSingle);
        }

        var solution = await workspace.OpenSolutionAsync(projectPath, cancellationToken: cancellationToken);
        var referencing = BuildReferencingProjects(solution);
        var projectReferences = BuildProjectReferences(solution);
        var analyzed = new List<CSharpProjectAnalysis>();
        foreach (var project in solution.Projects.Where(p => p.Language == LanguageNames.CSharp))
        {
            if (projectNames is not null && !projectNames.Contains(project.Name))
                continue;
            analyzed.Add(await _analyzer.AnalyzeProjectAsync(project, cancellationToken));
        }
        var (externalAnalyzed, owningMap) = await AnalyzeExternalAssembliesAsync(analyzed, cancellationToken);
        var allAnalyzed = analyzed.Concat(externalAnalyzed).ToArray();
        LastAnalyzedProjectNames = allAnalyzed.Select(project => project.ProjectName).ToArray();
        var fingerprints = allAnalyzed.ToDictionary(project => project.ProjectName, project => project.PublicSurfaceFingerprint, StringComparer.Ordinal);
        return new CSharpWorkspaceAnalysisResult(allAnalyzed, referencing, projectReferences, fingerprints, owningMap);
    }
















    private static async Task<(IReadOnlyList<CSharpProjectAnalysis> ExternalProjects, IReadOnlyDictionary<string, IReadOnlyList<ExternalAssemblyReference>> OwningMap)> AnalyzeExternalAssembliesAsync(
        IReadOnlyList<CSharpProjectAnalysis> analyzed, CancellationToken cancellationToken)
    {
        var rootsByAssemblyPath = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in analyzed)
        {
            if (project.ExternalRoots is null)
                continue;
            foreach (var (assemblyPath, docIds) in project.ExternalRoots)
            {
                if (!rootsByAssemblyPath.TryGetValue(assemblyPath, out var merged))
                    rootsByAssemblyPath[assemblyPath] = merged = new HashSet<string>(StringComparer.Ordinal);
                merged.UnionWith(docIds);
            }
        }

        var emptyOwningMap = new Dictionary<string, IReadOnlyList<ExternalAssemblyReference>>(StringComparer.Ordinal);
        if (rootsByAssemblyPath.Count == 0)
            return (Array.Empty<CSharpProjectAnalysis>(), emptyOwningMap);

        var ids = new CodeMapIdGenerator();
        var externalProjects = new List<CSharpProjectAnalysis>();
        var projectNameByAssemblyPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var hashByAssemblyPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (assemblyPath, docIds) in rootsByAssemblyPath)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CSharpProjectAnalysis? external;
            try
            {
                external = ExternalAssemblyAnalyzer.Analyze(assemblyPath, docIds, ids, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                external = null;
            }
            if (external is null)
                continue;
            externalProjects.Add(external);
            projectNameByAssemblyPath[assemblyPath] = external.ProjectName;
            hashByAssemblyPath[assemblyPath] = ComputeAssemblyFileHash(assemblyPath);
        }

        var owningMap = new Dictionary<string, IReadOnlyList<ExternalAssemblyReference>>(StringComparer.Ordinal);
        foreach (var project in analyzed)
        {
            if (project.ExternalRoots is null)
                continue;
            var references = new List<ExternalAssemblyReference>();
            foreach (var assemblyPath in project.ExternalRoots.Keys)
            {
                if (projectNameByAssemblyPath.TryGetValue(assemblyPath, out var externalProjectName))
                    references.Add(new ExternalAssemblyReference(externalProjectName, assemblyPath, hashByAssemblyPath[assemblyPath]));
            }
            if (references.Count > 0)
                owningMap[project.ProjectName] = references;
        }
        return (externalProjects, owningMap);
    }









    public static string ComputeAssemblyFileHash(string assemblyPath) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(assemblyPath))).ToLowerInvariant();





    public async Task<IReadOnlyDictionary<string, HashSet<string>>> GetReferencingProjectsAsync(
        string inputPath,
        CancellationToken cancellationToken = default) =>
        (await AnalyzeWithReferencingGraphAsync(inputPath, projectNames: new HashSet<string>(StringComparer.Ordinal), cancellationToken)).ReferencingProjects;

    private static IReadOnlyDictionary<string, HashSet<string>> BuildReferencingProjects(Microsoft.CodeAnalysis.Solution solution)
    {
        var referencing = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var project in solution.Projects.Where(p => p.Language == LanguageNames.CSharp))
        {
            foreach (var reference in project.ProjectReferences)
            {
                var referenced = solution.GetProject(reference.ProjectId);
                if (referenced is null || referenced.Language != LanguageNames.CSharp)
                    continue;
                if (!referencing.TryGetValue(referenced.Name, out var dependents))
                {
                    dependents = new HashSet<string>(StringComparer.Ordinal);
                    referencing[referenced.Name] = dependents;
                }
                dependents.Add(project.Name);
            }
        }
        return referencing;
    }

    private static IReadOnlyDictionary<string, string[]> BuildProjectReferences(Microsoft.CodeAnalysis.Solution solution)
    {
        var references = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var project in solution.Projects.Where(p => p.Language == LanguageNames.CSharp))
        {
            references[project.Name] = project.ProjectReferences
                .Select(reference => solution.GetProject(reference.ProjectId))
                .Where(referenced => referenced is not null && referenced.Language == LanguageNames.CSharp)
                .Select(referenced => referenced!.Name)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
        }
        return references;
    }

















    public static IReadOnlySet<string> GetDeclaredProjectNames(string solutionPath)
    {
        EnsureMsBuildRegistered();
        var solutionFile = Microsoft.Build.Construction.SolutionFile.Parse(solutionPath);
        return solutionFile.ProjectsInOrder
            .Where(project => project.ProjectType != Microsoft.Build.Construction.SolutionProjectType.SolutionFolder)
            .Select(project => Path.GetFileNameWithoutExtension(project.RelativePath))
            .ToHashSet(StringComparer.Ordinal);
    }

    public static string ResolveInput(string inputPath)
    {
        if (string.IsNullOrWhiteSpace(inputPath))
            inputPath = Directory.GetCurrentDirectory();
        var path = Path.GetFullPath(inputPath);
        if (File.Exists(path))
        {
            var extension = Path.GetExtension(path);
            if (extension.Equals(".sln", StringComparison.OrdinalIgnoreCase) || extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase) || extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase))
                return path;
            throw new ArgumentException("Input must be a directory, .sln, .slnx, or .csproj.", nameof(inputPath));
        }
        if (!Directory.Exists(path))
            throw new DirectoryNotFoundException(path);
        var candidates = Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories)
            .Where(file => !IgnoreRules.IsIgnored(path, file))
            .Where(file => Path.GetExtension(file).Equals(".sln", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(file).Equals(".slnx", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (candidates.Length == 1)
            return candidates[0];
        if (candidates.Length > 1)
            throw new InvalidOperationException($"Multiple solutions found in '{path}'. Specify one explicitly.");
        var projects = Directory.EnumerateFiles(path, "*.csproj", SearchOption.AllDirectories)
            .Where(file => !IgnoreRules.IsIgnored(path, file))
            .ToArray();
        if (projects.Length == 1)
            return projects[0];
        if (projects.Length > 1)
            throw new InvalidOperationException($"Multiple projects found in '{path}'. Specify a .sln/.slnx/.csproj explicitly.");
        throw new FileNotFoundException($"No solution or project found in '{path}'.");
    }

    private static readonly Lock MsBuildRegistrationLock = new();




    private static void EnsureMsBuildRegistered()
    {
        if (MSBuildLocator.IsRegistered)
            return;
        lock (MsBuildRegistrationLock)
        {
            if (!MSBuildLocator.IsRegistered)
                MSBuildLocator.RegisterDefaults();
        }
    }
}












public sealed record CSharpWorkspaceAnalysisResult(
    IReadOnlyList<CSharpProjectAnalysis> Projects,
    IReadOnlyDictionary<string, HashSet<string>> ReferencingProjects,
    IReadOnlyDictionary<string, string[]> ProjectReferences,
    IReadOnlyDictionary<string, string?> PublicSurfaceFingerprints,
    IReadOnlyDictionary<string, IReadOnlyList<ExternalAssemblyReference>>? ExternalAssembliesByOwningProject = null);

public sealed class CSharpProjectWorkspace(MSBuildWorkspace workspace, Project project) : IDisposable
{
    public Project Project { get; } = project;

    public void Dispose() => workspace.Dispose();
}


public sealed record ExternalAssemblyReference(string ExternalProjectName, string AssemblyPath, string AssemblyHash);
