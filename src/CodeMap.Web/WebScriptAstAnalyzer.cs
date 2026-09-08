using CodeMap.Core.Models;
using TreeSitter;

namespace CodeMap.Web;

internal static class WebScriptAstAnalyzer
{
    internal enum ScriptImportBindingKind
    {
        Named,
        Default,
        Namespace,
        SideEffect
    }

    internal enum ScriptExportKind
    {
        Declaration,
        Named,
        DefaultFrom,
        StarFrom
    }

    internal sealed record ScriptSymbol(
        string Name,
        NodeKind Kind,
        string? Signature,
        int StartIndex,
        int EndIndex,
        bool IsDefaultExport);

    internal sealed record ScriptImportBinding(string LocalName, string ImportedName, ScriptImportBindingKind Kind);

    internal sealed record ScriptImport(string Specifier, int StartIndex, int EndIndex, IReadOnlyList<ScriptImportBinding> Bindings);

    internal sealed record ScriptExport(
        string ExportedName,
        string? LocalName,
        ScriptExportKind Kind,
        string? ReexportSpecifier,
        int StartIndex,
        int EndIndex);

    internal sealed record ScriptCall(
        string Name,
        string? Qualifier,
        int StartIndex,
        int EndIndex,
        int EnclosingStartIndex,
        int EnclosingEndIndex);

    internal sealed record ScriptJsx(string Name, int StartIndex, int EndIndex, int EnclosingStartIndex, int EnclosingEndIndex);

    internal sealed record ScriptHttpLiteral(string Route, int StartIndex, int EndIndex);

    internal sealed record ScriptSelectorQuery(string Selector, int StartIndex, int EndIndex, int EnclosingStartIndex, int EnclosingEndIndex);

    internal sealed record ScriptAnalysis(
        IReadOnlyList<ScriptSymbol> Symbols,
        IReadOnlyList<ScriptImport> Imports,
        IReadOnlyList<ScriptExport> Exports,
        IReadOnlyList<ScriptCall> Calls,
        IReadOnlyList<ScriptSelectorQuery> SelectorQueries,
        IReadOnlyList<ScriptJsx> JsxElements,
        IReadOnlyList<ScriptHttpLiteral> HttpLiterals);

    internal static ScriptAnalysis Analyze(string content, string language, string? filePath = null)
    {
        try
        {
            using var treeLanguage = CreateLanguage(language, filePath);
            using var parser = new Parser(treeLanguage);
            using var tree = parser.Parse(content);
            if (tree?.RootNode is null)
                return Empty;

            var symbols = new List<ScriptSymbol>();
            var imports = new List<ScriptImport>();
            var exports = new List<ScriptExport>();
            var calls = new List<ScriptCall>();
            var selectors = new List<ScriptSelectorQuery>();
            var jsx = new List<ScriptJsx>();
            var httpLiterals = new List<ScriptHttpLiteral>();
            Walk(tree.RootNode, content, symbols, imports, exports, calls, selectors, jsx, httpLiterals, enclosing: null);
            return new ScriptAnalysis(symbols, imports, exports, calls, selectors, jsx, httpLiterals);
        }
        catch
        {
            return Empty;
        }
    }

    private static readonly ScriptAnalysis Empty = new([], [], [], [], [], [], []);

    private static Language CreateLanguage(string language, string? filePath)
    {
        var extension = string.IsNullOrWhiteSpace(filePath) ? string.Empty : Path.GetExtension(filePath).ToLowerInvariant();
        return (language, extension) switch
        {
            ("typescript", ".tsx") => new Language("TSX"),
            ("javascript", ".jsx") => new Language("JSX"),
            ("typescript", _) => new Language("TypeScript"),
            _ => new Language("JavaScript")
        };
    }

    private static void Walk(
        Node node,
        string content,
        List<ScriptSymbol> symbols,
        List<ScriptImport> imports,
        List<ScriptExport> exports,
        List<ScriptCall> calls,
        List<ScriptSelectorQuery> selectors,
        List<ScriptJsx> jsx,
        List<ScriptHttpLiteral> httpLiterals,
        (int Start, int End)? enclosing)
    {
        var currentEnclosing = enclosing;
        switch (node.Type)
        {
            case "function_declaration":
            case "generator_function_declaration":
            case "method_definition":
            {
                var nameNode = node.GetChildForField("name");
                if (nameNode is not null)
                {
                    var name = Slice(content, nameNode.StartIndex, nameNode.EndIndex);
                    var kind = node.Type == "method_definition" ? NodeKind.Method : NodeKind.Function;
                    symbols.Add(new ScriptSymbol(name, kind, BuildSignature(name, node, content), node.StartIndex, node.EndIndex, false));
                    currentEnclosing = (node.StartIndex, node.EndIndex);
                }
                break;
            }
            case "arrow_function":
            case "function":
            case "function_expression":
            case "generator_function":
                currentEnclosing = (node.StartIndex, node.EndIndex);
                break;
            case "class_declaration":
            {
                var nameNode = node.GetChildForField("name");
                if (nameNode is not null)
                {
                    var name = Slice(content, nameNode.StartIndex, nameNode.EndIndex);
                    symbols.Add(new ScriptSymbol(name, NodeKind.Class, null, node.StartIndex, node.EndIndex, false));
                    currentEnclosing = (node.StartIndex, node.EndIndex);
                }
                break;
            }
            case "interface_declaration":
            case "type_alias_declaration":
            {
                var nameNode = node.GetChildForField("name");
                if (nameNode is not null)
                {
                    var name = Slice(content, nameNode.StartIndex, nameNode.EndIndex);
                    symbols.Add(new ScriptSymbol(name, NodeKind.Interface, null, node.StartIndex, node.EndIndex, false));
                }
                break;
            }
            case "lexical_declaration":
            case "variable_declaration":
            {
                foreach (var child in node.NamedChildren)
                {
                    if (child.Type != "variable_declarator") continue;
                    var nameNode = child.GetChildForField("name");
                    var valueNode = child.GetChildForField("value");
                    if (nameNode is null || valueNode is null) continue;
                    if (valueNode.Type is "arrow_function" or "function" or "function_expression" or "generator_function")
                    {
                        var name = Slice(content, nameNode.StartIndex, nameNode.EndIndex);
                        symbols.Add(new ScriptSymbol(name, NodeKind.Function, BuildSignature(name, valueNode, content), valueNode.StartIndex, valueNode.EndIndex, false));
                    }
                }
                break;
            }
            case "import_statement":
            {
                var source = node.GetChildForField("source");
                if (source is not null)
                {
                    var specifier = Unquote(Slice(content, source.StartIndex, source.EndIndex));
                    if (!string.IsNullOrWhiteSpace(specifier))
                    {
                        var bindings = CollectImportBindings(node, content);
                        imports.Add(new ScriptImport(specifier, node.StartIndex, node.EndIndex, bindings));
                    }
                }
                break;
            }
            case "export_statement":
                ExtractExport(node, content, symbols, exports);
                break;
            case "jsx_element":
            {
                var opening = node.NamedChildren.FirstOrDefault(child => child.Type is "jsx_opening_element" or "jsx_self_closing_element");
                if (opening is not null)
                    Walk(opening, content, symbols, imports, exports, calls, selectors, jsx, httpLiterals, currentEnclosing);
                break;
            }
            case "jsx_opening_element":
            case "jsx_self_closing_element":
            {
                var nameNode = node.GetChildForField("name");
                if (nameNode is not null)
                {
                    var name = Slice(content, nameNode.StartIndex, nameNode.EndIndex);
                    if (name.Length > 0 && char.IsUpper(name[0]) && !name.Contains('.'))
                    {
                        var enc = currentEnclosing ?? (node.StartIndex, node.EndIndex);
                        jsx.Add(new ScriptJsx(name, node.StartIndex, node.EndIndex, enc.Start, enc.End));
                    }
                }
                break;
            }
            case "call_expression":
            {
                TryExtractHttpLiteral(node, content, httpLiterals);
                var functionNode = node.GetChildForField("function");
                if (functionNode is not null)
                {
                    if (functionNode.Type == "identifier")
                    {
                        var name = Slice(content, functionNode.StartIndex, functionNode.EndIndex);
                        if (name == "require")
                            TryExtractRequireImport(node, content, imports);
                        else if (!IsIgnoredCall(name))
                        {
                            var enc = currentEnclosing ?? (node.StartIndex, node.EndIndex);
                            calls.Add(new ScriptCall(name, null, node.StartIndex, node.EndIndex, enc.Start, enc.End));
                        }
                    }
                    else if (functionNode.Type == "member_expression")
                    {
                        var property = functionNode.GetChildForField("property");
                        var objectNode = functionNode.GetChildForField("object");
                        if (property is not null)
                        {
                            var method = Slice(content, property.StartIndex, property.EndIndex);
                            if (method is "querySelector" or "querySelectorAll" or "getElementById" or "getElementsByClassName")
                            {
                                var argumentsNode = node.GetChildForField("arguments");
                                var argument = argumentsNode?.NamedChildren.FirstOrDefault();
                                if (argument is not null && argument.Type is "string" or "template_string")
                                {
                                    var selector = Unquote(Slice(content, argument.StartIndex, argument.EndIndex));
                                    if (!string.IsNullOrWhiteSpace(selector))
                                    {
                                        var enc = currentEnclosing ?? (node.StartIndex, node.EndIndex);
                                        selectors.Add(new ScriptSelectorQuery(selector, node.StartIndex, node.EndIndex, enc.Start, enc.End));
                                    }
                                }
                            }
                            else if (objectNode is not null && TryGetMemberQualifier(objectNode, content, out var qualifier))
                            {
                                var enc = currentEnclosing ?? (node.StartIndex, node.EndIndex);
                                calls.Add(new ScriptCall(method, qualifier, node.StartIndex, node.EndIndex, enc.Start, enc.End));
                            }
                        }
                    }
                }
                break;
            }
        }

        foreach (var child in node.NamedChildren)
            Walk(child, content, symbols, imports, exports, calls, selectors, jsx, httpLiterals, currentEnclosing);
    }

    private static bool IsDefaultExport(Node exportStatement)
    {
        foreach (var child in exportStatement.Children)
            if (child.Type == "default")
                return true;
        return false;
    }

    private static void ExtractExport(Node exportStatement, string content, List<ScriptSymbol> symbols, List<ScriptExport> exports)
    {
        var source = exportStatement.GetChildForField("source");
        if (source is not null)
        {
            var specifier = Unquote(Slice(content, source.StartIndex, source.EndIndex));
            if (string.IsNullOrWhiteSpace(specifier))
                return;
            var clause = exportStatement.NamedChildren.FirstOrDefault(child => child.Type == "export_clause");
            if (clause is not null)
            {
                foreach (var exportSpecifier in clause.NamedChildren.Where(child => child.Type == "export_specifier"))
                {
                    var local = exportSpecifier.GetChildForField("name");
                    var exported = exportSpecifier.GetChildForField("alias") ?? local;
                    if (local is null || exported is null) continue;
                    var localName = Slice(content, local.StartIndex, local.EndIndex);
                    var exportedName = Slice(content, exported.StartIndex, exported.EndIndex);
                    var kind = localName == "default" && exportedName == "default"
                        ? ScriptExportKind.DefaultFrom
                        : ScriptExportKind.Named;
                    exports.Add(new ScriptExport(exportedName, localName, kind, specifier, exportStatement.StartIndex, exportStatement.EndIndex));
                }
                return;
            }
            exports.Add(new ScriptExport("*", null, ScriptExportKind.StarFrom, specifier, exportStatement.StartIndex, exportStatement.EndIndex));
            return;
        }

        var declaration = exportStatement.NamedChildren.FirstOrDefault();
        if (declaration is null) return;
        switch (declaration.Type)
        {
            case "function_declaration":
            case "generator_function_declaration":
            case "class_declaration":
            {
                var nameNode = declaration.GetChildForField("name");
                var isDefault = IsDefaultExport(exportStatement);
                if (isDefault)
                {
                    exports.Add(new ScriptExport("default", nameNode is null ? null : Slice(content, nameNode.StartIndex, nameNode.EndIndex),
                        ScriptExportKind.Declaration, null, exportStatement.StartIndex, exportStatement.EndIndex));
                }
                if (nameNode is not null)
                {
                    var name = Slice(content, nameNode.StartIndex, nameNode.EndIndex);
                    var kind = declaration.Type == "class_declaration" ? NodeKind.Class : NodeKind.Function;
                    var signature = kind == NodeKind.Function ? BuildSignature(name, declaration, content) : null;
                    symbols.Add(new ScriptSymbol(name, kind, signature, declaration.StartIndex, declaration.EndIndex, isDefault));
                    if (!isDefault)
                        exports.Add(new ScriptExport(name, name, ScriptExportKind.Declaration, null, exportStatement.StartIndex, exportStatement.EndIndex));
                }
                break;
            }
            case "lexical_declaration":
            case "variable_declaration":
            {
                var isDefault = IsDefaultExport(exportStatement);
                foreach (var child in declaration.NamedChildren.Where(child => child.Type == "variable_declarator"))
                {
                    var nameNode = child.GetChildForField("name");
                    var valueNode = child.GetChildForField("value");
                    if (nameNode is null) continue;
                    var name = Slice(content, nameNode.StartIndex, nameNode.EndIndex);
                    if (isDefault)
                        exports.Add(new ScriptExport("default", name, ScriptExportKind.Declaration, null, exportStatement.StartIndex, exportStatement.EndIndex));
                    else
                        exports.Add(new ScriptExport(name, name, ScriptExportKind.Declaration, null, exportStatement.StartIndex, exportStatement.EndIndex));
                    if (valueNode is not null && valueNode.Type is "arrow_function" or "function" or "function_expression" or "generator_function")
                        symbols.Add(new ScriptSymbol(name, NodeKind.Function, BuildSignature(name, valueNode, content), valueNode.StartIndex, valueNode.EndIndex, isDefault));
                }
                break;
            }
            case "export_clause":
                foreach (var exportSpecifier in declaration.NamedChildren.Where(child => child.Type == "export_specifier"))
                {
                    var local = exportSpecifier.GetChildForField("name");
                    var exported = exportSpecifier.GetChildForField("alias") ?? local;
                    if (local is null || exported is null) continue;
                    var localName = Slice(content, local.StartIndex, local.EndIndex);
                    var exportedName = Slice(content, exported.StartIndex, exported.EndIndex);
                    exports.Add(new ScriptExport(exportedName, localName, ScriptExportKind.Named, null, exportStatement.StartIndex, exportStatement.EndIndex));
                }
                break;
        }
    }

    private static IReadOnlyList<ScriptImportBinding> CollectImportBindings(Node importStatement, string content)
    {
        var bindings = new List<ScriptImportBinding>();
        foreach (var child in importStatement.NamedChildren)
        {
            switch (child.Type)
            {
                case "import_clause":
                    foreach (var clauseChild in child.NamedChildren)
                    {
                        switch (clauseChild.Type)
                        {
                            case "identifier":
                            case "import_default":
                            {
                                var nameNode = clauseChild.Type == "import_default"
                                    ? clauseChild.GetChildForField("name") ?? clauseChild.NamedChildren.FirstOrDefault(node => node.Type == "identifier")
                                    : clauseChild;
                                if (nameNode is null) break;
                                bindings.Add(new ScriptImportBinding(
                                    Slice(content, nameNode.StartIndex, nameNode.EndIndex),
                                    "default",
                                    ScriptImportBindingKind.Default));
                                break;
                            }
                            case "namespace_import":
                            {
                                var nameNode = clauseChild.NamedChildren.FirstOrDefault(node => node.Type == "identifier");
                                if (nameNode is not null)
                                {
                                    bindings.Add(new ScriptImportBinding(
                                        Slice(content, nameNode.StartIndex, nameNode.EndIndex),
                                        "*",
                                        ScriptImportBindingKind.Namespace));
                                }
                                break;
                            }
                            case "named_imports":
                                foreach (var specifier in clauseChild.NamedChildren.Where(node => node.Type == "import_specifier"))
                                {
                                    var imported = specifier.GetChildForField("name");
                                    var alias = specifier.GetChildForField("alias") ?? imported;
                                    if (imported is null || alias is null) continue;
                                    bindings.Add(new ScriptImportBinding(
                                        Slice(content, alias.StartIndex, alias.EndIndex),
                                        Slice(content, imported.StartIndex, imported.EndIndex),
                                        ScriptImportBindingKind.Named));
                                }
                                break;
                        }
                    }
                    break;
            }
        }
        if (bindings.Count == 0)
            bindings.Add(new ScriptImportBinding(string.Empty, string.Empty, ScriptImportBindingKind.SideEffect));
        return bindings;
    }

    private static string BuildSignature(string name, Node node, string content)
    {
        var parameters = node.GetChildForField("parameters");
        if (parameters is null) return $"{name}()";
        return $"{name}{Slice(content, parameters.StartIndex, parameters.EndIndex)}";
    }

    private static void TryExtractRequireImport(Node callExpression, string content, List<ScriptImport> imports)
    {
        var argumentsNode = callExpression.GetChildForField("arguments");
        var argument = argumentsNode?.NamedChildren.FirstOrDefault();
        if (argument is null || argument.Type is not ("string" or "template_string"))
            return;
        var specifier = Unquote(Slice(content, argument.StartIndex, argument.EndIndex));
        if (string.IsNullOrWhiteSpace(specifier) || specifier.Contains("${", StringComparison.Ordinal))
            return;
        imports.Add(new ScriptImport(specifier, callExpression.StartIndex, callExpression.EndIndex,
            [new ScriptImportBinding("default", "default", ScriptImportBindingKind.Default)]));
    }

    private static bool IsIgnoredCall(string name) =>
        name is "if" or "for" or "while" or "switch" or "catch" or "function" or "require";

    private static void TryExtractHttpLiteral(Node callExpression, string content, List<ScriptHttpLiteral> literals)
    {
        var functionNode = callExpression.GetChildForField("function");
        var propertyNode = functionNode?.Type == "member_expression" ? functionNode.GetChildForField("property") : null;
        var method = functionNode?.Type == "identifier"
            ? Slice(content, functionNode.StartIndex, functionNode.EndIndex)
            : propertyNode is null ? string.Empty : Slice(content, propertyNode.StartIndex, propertyNode.EndIndex);
        if (method is not ("fetch" or "get" or "post" or "put" or "patch" or "delete" or "request"))
            return;
        var argumentsNode = callExpression.GetChildForField("arguments");
        var argument = argumentsNode?.NamedChildren.FirstOrDefault();
        if (argument is null || argument.Type is not ("string" or "template_string"))
            return;
        var route = Unquote(Slice(content, argument.StartIndex, argument.EndIndex));
        if (string.IsNullOrWhiteSpace(route) || route.Contains("${", StringComparison.Ordinal))
            return;
        if (!route.StartsWith("/", StringComparison.Ordinal) && !Uri.TryCreate(route, UriKind.Absolute, out _))
            return;
        literals.Add(new ScriptHttpLiteral(route, callExpression.StartIndex, callExpression.EndIndex));
    }

    private static bool TryGetMemberQualifier(Node node, string content, out string qualifier)
    {
        qualifier = string.Empty;
        if (node.Type == "identifier")
        {
            qualifier = Slice(content, node.StartIndex, node.EndIndex);
            return true;
        }
        if (node.Type != "member_expression")
            return false;
        var objectNode = node.GetChildForField("object");
        var propertyNode = node.GetChildForField("property");
        if (objectNode is null || propertyNode is null || !TryGetMemberQualifier(objectNode, content, out var parent))
            return false;
        qualifier = $"{parent}.{Slice(content, propertyNode.StartIndex, propertyNode.EndIndex)}";
        return true;
    }

    private static string Slice(string content, int start, int end) =>
        content[Math.Clamp(start, 0, content.Length)..Math.Clamp(end, 0, content.Length)];

    private static string Unquote(string value)
    {
        value = value.Trim();
        if (value.Length >= 2 && (value[0] is '"' or '\'' || value.StartsWith('`')))
            return value[1..^1];
        return value;
    }
}
