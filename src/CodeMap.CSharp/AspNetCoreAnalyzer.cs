using CodeMap.Core.Ids;
using CodeMap.Core.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CodeMap.CSharp;







internal static class AspNetCoreAnalyzer
{
    private static readonly string[] MinimalApiMethodNames = ["MapGet", "MapPost", "MapPut", "MapDelete", "MapPatch", "MapMethods"];
    private static readonly string[] HttpVerbAttributeNames =
        ["HttpGetAttribute", "HttpPostAttribute", "HttpPutAttribute", "HttpDeleteAttribute", "HttpPatchAttribute"];

    internal static AnalysisResult Analyze(
        string projectName,
        string projectDirectory,
        Compilation compilation,
        DotNetSourceGraphLookup lookup,
        ICodeMapIdGenerator ids,
        CancellationToken cancellationToken)
    {
        var nodes = new List<CodeNode>();
        var edges = new List<CodeEdge>();
        var nodeIds = new HashSet<string>(StringComparer.Ordinal);
        var edgeKeys = new HashSet<string>(StringComparer.Ordinal);

        void AddNode(CodeNode node)
        {
            if (nodeIds.Add(node.Id))
                nodes.Add(node);
        }

        void AddEdge(string sourceId, string targetId, EdgeKind kind, double confidence, EdgeResolutionKind resolution, SourceLocation? location)
        {
            var key = $"{sourceId}{targetId}{kind}{location?.StartLine}{location?.StartColumn}{location?.EndLine}{location?.EndColumn}";
            if (!edgeKeys.Add(key) || sourceId == targetId)
                return;
            edges.Add(new CodeEdge
            {
                SourceId = sourceId,
                TargetId = targetId,
                Kind = kind,
                ResolutionKind = resolution,
                Confidence = confidence,
                SourceLocation = location
            });
        }

        foreach (var tree in compilation.SyntaxTrees)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(tree.FilePath))
                continue;
            var model = compilation.GetSemanticModel(tree);
            var root = tree.GetRoot(cancellationToken);
            var relativePath = CSharpLanguageAnalyzer.GetRelativePath(projectDirectory, tree.FilePath);

            AnalyzeMinimalApi(projectName, relativePath, model, root, lookup, ids, AddNode, AddEdge);
            AnalyzeMvcClasses(projectName, relativePath, model, root, lookup, ids, AddNode, AddEdge);
            AnalyzeDiRegistrations(projectName, relativePath, model, root, ids, AddNode, AddEdge);
        }

        return new AnalysisResult { Nodes = nodes, Edges = edges };
    }



    private static void AnalyzeMinimalApi(
        string projectName,
        string relativePath,
        SemanticModel model,
        SyntaxNode root,
        DotNetSourceGraphLookup lookup,
        ICodeMapIdGenerator ids,
        Action<CodeNode> addNode,
        Action<string, string, EdgeKind, double, EdgeResolutionKind, SourceLocation?> addEdge)
    {
        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method)
                continue;
            if (!IsEndpointRouteBuilderExtension(method))
                continue;

            var methodName = method.Name;
            if (methodName == "MapMethods")
            {
                AnalyzeMapMethods(projectName, relativePath, invocation, model, method, lookup, ids, addNode, addEdge);
                continue;
            }
            if (!MinimalApiMethodNames.Contains(methodName))
                continue;

            var verb = methodName switch
            {
                "MapGet" => "GET",
                "MapPost" => "POST",
                "MapPut" => "PUT",
                "MapDelete" => "DELETE",
                "MapPatch" => "PATCH",
                _ => null
            };
            if (verb is null)
                continue;

            var arguments = invocation.ArgumentList.Arguments;
            if (arguments.Count < 2)
                continue;
            if (!TryGetConstantString(model, arguments[0].Expression, out var template))
                continue;

            CreateRouteAndHandlerEdge(projectName, relativePath, verb, template, arguments[1].Expression, invocation.GetLocation(),
                model, lookup, ids, addNode, addEdge);
        }
    }

    private static void AnalyzeMapMethods(
        string projectName,
        string relativePath,
        InvocationExpressionSyntax invocation,
        SemanticModel model,
        IMethodSymbol method,
        DotNetSourceGraphLookup lookup,
        ICodeMapIdGenerator ids,
        Action<CodeNode> addNode,
        Action<string, string, EdgeKind, double, EdgeResolutionKind, SourceLocation?> addEdge)
    {
        var arguments = invocation.ArgumentList.Arguments;
        if (arguments.Count < 3)
            return;
        if (!TryGetConstantString(model, arguments[0].Expression, out var template))
            return;
        if (!TryGetConstantStringArray(model, arguments[1].Expression, out var verbs))
            return;

        foreach (var verb in verbs)
            CreateRouteAndHandlerEdge(projectName, relativePath, verb.ToUpperInvariant(), template, arguments[2].Expression, invocation.GetLocation(),
                model, lookup, ids, addNode, addEdge);
    }

    private static void CreateRouteAndHandlerEdge(
        string projectName,
        string relativePath,
        string verb,
        string template,
        ExpressionSyntax handlerExpression,
        Location invocationLocation,
        SemanticModel model,
        DotNetSourceGraphLookup lookup,
        ICodeMapIdGenerator ids,
        Action<CodeNode> addNode,
        Action<string, string, EdgeKind, double, EdgeResolutionKind, SourceLocation?> addEdge)
    {
        var normalizedTemplate = NormalizeTemplate(template);
        var routeId = $"route://{projectName}/{verb}/{normalizedTemplate}";
        var location = CSharpLanguageAnalyzer.ToLocation(invocationLocation);
        addNode(new CodeNode
        {
            Id = routeId,
            Kind = NodeKind.Route,
            Name = $"{verb} {normalizedTemplate}",
            QualifiedName = $"{verb} {normalizedTemplate}",
            FilePath = relativePath,
            SourceLocation = location,
            Language = "csharp"
        });







        var handlerSymbolInfo = model.GetSymbolInfo(handlerExpression);
        var handlerSymbol = handlerSymbolInfo.Symbol as IMethodSymbol
            ?? (handlerSymbolInfo.CandidateSymbols.Length == 1 ? handlerSymbolInfo.CandidateSymbols[0] as IMethodSymbol : null);






        if (handlerSymbol is null && handlerExpression is AnonymousFunctionExpressionSyntax anonymousHandler
            && CSharpLanguageAnalyzer.TryGetAnonymousFunctionSymbol(model, anonymousHandler, out var anonymousSymbol))
            handlerSymbol = anonymousSymbol;

        if (handlerSymbol is null)
            return;
        var handlerNode = lookup.NodeForSymbol(handlerSymbol) ?? ResolveMethodNode(handlerSymbol, lookup);
        if (handlerNode is null)
            return;
        addEdge(routeId, handlerNode.Id, EdgeKind.RoutesTo, 1.00, EdgeResolutionKind.Semantic, location);
    }









    private static CodeNode? ResolveMethodNode(IMethodSymbol symbol, DotNetSourceGraphLookup lookup)
    {
        var candidates = lookup.ByName(symbol.Name)
            .Where(n => n.Kind is NodeKind.Method)
            .Where(n => string.Equals(n.QualifiedName, GetMethodQualifiedName(symbol), StringComparison.Ordinal))
            .Where(n => string.Equals(n.Signature, GetMethodSignature(symbol), StringComparison.Ordinal))
            .ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }

    private static string GetMethodQualifiedName(IMethodSymbol symbol)
    {
        var containingType = symbol.ContainingType?.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
        return containingType is null ? symbol.Name : containingType + "." + symbol.Name;
    }





    private static readonly SymbolDisplayFormat MethodSignatureParameterFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameOnly,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    private static string GetMethodSignature(IMethodSymbol symbol) =>
        symbol.Name
        + (symbol.TypeParameters.Length == 0 ? string.Empty : "<" + string.Join(", ", symbol.TypeParameters.Select(t => t.Name)) + ">")
        + "(" + string.Join(", ", symbol.Parameters.Select(p => p.Type.ToDisplayString(MethodSignatureParameterFormat))) + ")";

    private static bool IsEndpointRouteBuilderExtension(IMethodSymbol method)
    {
        var containingType = method.ContainingType?.ToDisplayString();
        if (containingType == "Microsoft.AspNetCore.Builder.EndpointRouteBuilderExtensions")
            return true;


        if (method.ReducedFrom?.ContainingType?.ToDisplayString() == "Microsoft.AspNetCore.Builder.EndpointRouteBuilderExtensions")
            return true;
        if (method.Parameters.Length > 0 &&
            method.Parameters[0].Type.ToDisplayString() == "Microsoft.AspNetCore.Routing.IEndpointRouteBuilder")
            return true;
        return false;
    }



    private static void AnalyzeMvcClasses(
        string projectName,
        string relativePath,
        SemanticModel model,
        SyntaxNode root,
        DotNetSourceGraphLookup lookup,
        ICodeMapIdGenerator ids,
        Action<CodeNode> addNode,
        Action<string, string, EdgeKind, double, EdgeResolutionKind, SourceLocation?> addEdge)
    {
        foreach (var classDeclaration in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            if (model.GetDeclaredSymbol(classDeclaration) is not INamedTypeSymbol classSymbol)
                continue;
            if (!IsController(classSymbol))
                continue;

            var classRouteTemplates = GetRouteTemplates(classSymbol.GetAttributes());
            foreach (var methodDeclaration in classDeclaration.Members.OfType<MethodDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(methodDeclaration) is not IMethodSymbol methodSymbol)
                    continue;
                AnalyzeAction(projectName, relativePath, classSymbol, methodSymbol, classRouteTemplates, methodDeclaration, lookup, ids, addNode, addEdge);
            }
        }
    }

    private static bool IsController(INamedTypeSymbol type)
    {
        for (var baseType = type.BaseType; baseType is not null; baseType = baseType.BaseType)
        {
            var name = baseType.ToDisplayString();
            if (name is "Microsoft.AspNetCore.Mvc.Controller" or "Microsoft.AspNetCore.Mvc.ControllerBase"
                or "Microsoft.AspNetCore.Mvc.RazorPages.PageModel")
                return true;
        }




        return type.GetAttributes().Any(attribute =>
            attribute.AttributeClass?.ToDisplayString() is "Microsoft.AspNetCore.Mvc.ControllerAttribute" or "Microsoft.AspNetCore.Mvc.ApiControllerAttribute");
    }

    private static void AnalyzeAction(
        string projectName,
        string relativePath,
        INamedTypeSymbol classSymbol,
        IMethodSymbol methodSymbol,
        IReadOnlyList<(string? Verb, string Template)> classRouteTemplates,
        MethodDeclarationSyntax methodDeclaration,
        DotNetSourceGraphLookup lookup,
        ICodeMapIdGenerator ids,
        Action<CodeNode> addNode,
        Action<string, string, EdgeKind, double, EdgeResolutionKind, SourceLocation?> addEdge)
    {
        if (methodSymbol.DeclaredAccessibility != Accessibility.Public || methodSymbol.IsStatic)
            return;

        var methodAttributes = methodSymbol.GetAttributes();
        var verbTemplates = GetHttpVerbTemplates(methodAttributes);
        var routeTemplates = GetRouteTemplates(methodAttributes);








        IReadOnlyList<(string Verb, string Template)> combinations;
        if (verbTemplates.Count > 0)
            combinations = verbTemplates.SelectMany(vt => CombineTemplates(classRouteTemplates, vt.Template).Select(template => (vt.Verb, template))).ToArray();
        else if (routeTemplates.Count > 0)
            combinations = routeTemplates.SelectMany(rt => CombineTemplates(classRouteTemplates, rt.Template).Select(template => ("ANY", template))).ToArray();
        else
            return;







        var methodNode = lookup.NodeForSymbol(methodSymbol) ?? ResolveMethodNode(methodSymbol, lookup);
        if (methodNode is null)
            return;

        foreach (var (verb, template) in combinations)
        {
            var resolved = ReplaceTokens(template, classSymbol.Name, methodSymbol.Name);
            var normalizedTemplate = NormalizeTemplate(resolved);
            var routeId = $"route://{projectName}/{verb}/{normalizedTemplate}";
            var location = CSharpLanguageAnalyzer.ToLocation(methodDeclaration.GetLocation());
            addNode(new CodeNode
            {
                Id = routeId,
                Kind = NodeKind.Route,
                Name = $"{verb} {normalizedTemplate}",
                QualifiedName = $"{verb} {normalizedTemplate}",
                FilePath = relativePath,
                SourceLocation = location,
                Language = "csharp"
            });
            addEdge(routeId, methodNode.Id, EdgeKind.RoutesTo, 1.00, EdgeResolutionKind.Semantic, location);
        }
    }











    private static IReadOnlyList<string> CombineTemplates(IReadOnlyList<(string? Verb, string Template)> classTemplates, string actionTemplate)
    {
        if (actionTemplate.StartsWith("~/", StringComparison.Ordinal))
            return [actionTemplate[1..]];
        if (actionTemplate.StartsWith('/'))
            return [actionTemplate];

        var classPrefixes = classTemplates.Select(t => t.Template).Where(template => !string.IsNullOrEmpty(template)).ToArray();
        if (classPrefixes.Length == 0)
            return ["/" + actionTemplate];
        return classPrefixes
            .Select(classTemplate => "/" + classTemplate.Trim('/') + (actionTemplate.Length == 0 ? "" : "/" + actionTemplate.TrimStart('/')))
            .ToArray();
    }

    private static string ReplaceTokens(string template, string controllerName, string actionName)
    {
        var controllerShortName = controllerName.EndsWith("Controller", StringComparison.Ordinal)
            ? controllerName[..^"Controller".Length]
            : controllerName;
        return template
            .Replace("[controller]", controllerShortName, StringComparison.OrdinalIgnoreCase)
            .Replace("[action]", actionName, StringComparison.OrdinalIgnoreCase);
    }





    private static IReadOnlyList<(string Verb, string Template)> GetHttpVerbTemplates(IEnumerable<AttributeData> attributes)
    {
        var result = new List<(string, string)>();
        foreach (var attribute in attributes)
        {
            var attributeClassName = attribute.AttributeClass?.ToDisplayString();
            var verb = attributeClassName switch
            {
                "Microsoft.AspNetCore.Mvc.HttpGetAttribute" => "GET",
                "Microsoft.AspNetCore.Mvc.HttpPostAttribute" => "POST",
                "Microsoft.AspNetCore.Mvc.HttpPutAttribute" => "PUT",
                "Microsoft.AspNetCore.Mvc.HttpDeleteAttribute" => "DELETE",
                "Microsoft.AspNetCore.Mvc.HttpPatchAttribute" => "PATCH",
                _ => null
            };
            if (verb is null)
                continue;
            var template = attribute.ConstructorArguments.Length > 0 && attribute.ConstructorArguments[0].Value is string templateValue
                ? templateValue
                : string.Empty;
            result.Add((verb, template));
        }
        return result;
    }

    private static IReadOnlyList<(string? Verb, string Template)> GetRouteTemplates(IEnumerable<AttributeData> attributes)
    {
        var result = new List<(string?, string)>();
        foreach (var attribute in attributes)
        {
            if (attribute.AttributeClass?.ToDisplayString() != "Microsoft.AspNetCore.Mvc.RouteAttribute")
                continue;
            var template = attribute.ConstructorArguments.Length > 0 && attribute.ConstructorArguments[0].Value is string templateValue
                ? templateValue
                : null;
            if (template is not null)
                result.Add((null, template));
        }
        return result;
    }



    private static void AnalyzeDiRegistrations(
        string projectName,
        string relativePath,
        SemanticModel model,
        SyntaxNode root,
        ICodeMapIdGenerator ids,
        Action<CodeNode> addNode,
        Action<string, string, EdgeKind, double, EdgeResolutionKind, SourceLocation?> addEdge)
    {
        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method)
                continue;
            if (method.Name is not ("AddSingleton" or "AddScoped" or "AddTransient"))
                continue;
            if (!method.IsGenericMethod)
                continue;
            var receiverType = method.ReceiverType ?? method.ReducedFrom?.Parameters.FirstOrDefault()?.Type;
            var isServiceCollection = method.Parameters.Length > 0
                ? method.Parameters[0].Type.ToDisplayString() == "Microsoft.Extensions.DependencyInjection.IServiceCollection"
                : receiverType?.ToDisplayString() == "Microsoft.Extensions.DependencyInjection.IServiceCollection";
            if (!isServiceCollection)
                continue;

            var typeArguments = method.TypeArguments;
            if (typeArguments.Length is not (1 or 2))
                continue;
            if (typeArguments.Any(t => t.TypeKind == TypeKind.Error))
                continue;

            var lifetime = method.Name switch
            {
                "AddSingleton" => "singleton",
                "AddScoped" => "scoped",
                "AddTransient" => "transient",
                _ => null
            };
            if (lifetime is null)
                continue;

            var serviceType = typeArguments[0];
            var implementationType = typeArguments.Length == 2 ? typeArguments[1] : typeArguments[0];
            var location = CSharpLanguageAnalyzer.ToLocation(invocation.GetLocation());
            var serviceDisplayName = serviceType.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
            var registrationId = $"di://{projectName}/{relativePath.Replace('\\', '/')}/{location.StartLine}/{serviceDisplayName}";
            addNode(new CodeNode
            {
                Id = registrationId,
                Kind = NodeKind.DependencyRegistration,
                Name = serviceType.Name,
                QualifiedName = serviceDisplayName,
                FilePath = relativePath,
                SourceLocation = location,
                Language = "csharp",
                Signature = $"lifetime={lifetime}"
            });

            var serviceTargetId = ResolveTypeNodeId(serviceType, ids, projectName);
            var implementationTargetId = ResolveTypeNodeId(implementationType, ids, projectName);
            if (serviceTargetId is not null)
                addEdge(registrationId, serviceTargetId, EdgeKind.Registers, 1.00, EdgeResolutionKind.Semantic, location);
            if (implementationTargetId is not null)
                addEdge(registrationId, implementationTargetId, EdgeKind.ResolvesTo, 1.00, EdgeResolutionKind.Semantic, location);
        }
    }

    private static string? ResolveTypeNodeId(ITypeSymbol type, ICodeMapIdGenerator ids, string projectName)
    {
        if (type is not INamedTypeSymbol named)
            return null;
        var documentationId = named.OriginalDefinition.GetDocumentationCommentId();
        return string.IsNullOrEmpty(documentationId) ? null : ids.CreateGlobalSymbolId(documentationId);
    }



    private static bool TryGetConstantString(SemanticModel model, ExpressionSyntax expression, out string value)
    {
        if (expression is LiteralExpressionSyntax literal && literal.Token.Value is string literalValue)
        {
            value = literalValue;
            return true;
        }
        var constant = model.GetConstantValue(expression);
        if (constant.HasValue && constant.Value is string constantValue)
        {
            value = constantValue;
            return true;
        }
        value = string.Empty;
        return false;
    }

    private static bool TryGetConstantStringArray(SemanticModel model, ExpressionSyntax expression, out IReadOnlyList<string> values)
    {
        if (expression is ArrayCreationExpressionSyntax { Initializer: { } initializer })
            return TryGetAllConstantStrings(model, initializer.Expressions, out values);
        if (expression is ImplicitArrayCreationExpressionSyntax { Initializer: { } implicitInitializer })
            return TryGetAllConstantStrings(model, implicitInitializer.Expressions, out values);
        if (expression is CollectionExpressionSyntax collection)
            return TryGetAllConstantStrings(model, collection.Elements.OfType<ExpressionElementSyntax>().Select(e => e.Expression), out values);
        values = Array.Empty<string>();
        return false;
    }

    private static bool TryGetAllConstantStrings(SemanticModel model, IEnumerable<ExpressionSyntax> expressions, out IReadOnlyList<string> values)
    {
        var result = new List<string>();
        foreach (var expression in expressions)
        {
            if (!TryGetConstantString(model, expression, out var value))
            {
                values = Array.Empty<string>();
                return false;
            }
            result.Add(value);
        }
        values = result;
        return result.Count > 0;
    }

    private static string NormalizeTemplate(string template)
    {
        var trimmed = template.Trim('/');
        return trimmed.Length == 0 ? "/" : "/" + trimmed;
    }
}
