using System.Text;
using CodeMap.Core.Ids;
using CodeMap.Core.Models;
using Microsoft.CodeAnalysis;

namespace CodeMap.CSharp;







internal static class RazorMarkupAnalyzer
{
    private static readonly HashSet<string> EventAttributes = new(StringComparer.Ordinal)
    {
        "@onclick", "@onchange", "@onsubmit", "@oninput"
    };









    internal static CodeNode PredeclareArtifact(string projectName, string relativePath, string content)
    {
        var isComponent = relativePath.EndsWith(".razor", StringComparison.OrdinalIgnoreCase);
        var tokens = RazorTokenizer.Tokenize(content);
        var hasPageDirective = tokens.Any(t => t.Kind == RazorTokenKind.Directive && t.Name == "page");
        var kind = isComponent ? NodeKind.RazorComponent : hasPageDirective ? NodeKind.RazorPage : NodeKind.RazorView;

        return new CodeNode
        {
            Id = ArtifactId(projectName, relativePath),
            Kind = kind,
            Name = System.IO.Path.GetFileName(relativePath),
            QualifiedName = relativePath.Replace('\\', '/'),
            FilePath = relativePath,
            SourceLocation = new SourceLocation { StartLine = 1, EndLine = 1 },
            Language = "razor"
        };
    }

    private static string ArtifactId(string projectName, string relativePath) =>
        $"razor://{projectName}/{relativePath.Replace('\\', '/')}";

    internal static AnalysisResult Analyze(
        string projectName,
        string relativePath,
        string content,
        DotNetSourceGraphLookup lookup,
        ICodeMapIdGenerator ids)
    {
        var nodes = new List<CodeNode>();
        var edges = new List<CodeEdge>();
        var edgeKeys = new HashSet<string>(StringComparer.Ordinal);

        void AddEdge(string sourceId, string targetId, EdgeKind kind, double confidence, int line)
        {
            var key = $"{sourceId}{targetId}{kind}{line}";
            if (!edgeKeys.Add(key) || sourceId == targetId)
                return;
            edges.Add(new CodeEdge
            {
                SourceId = sourceId,
                TargetId = targetId,
                Kind = kind,
                ResolutionKind = EdgeResolutionKind.Syntactic,
                Confidence = confidence,
                SourceLocation = new SourceLocation { StartLine = line, EndLine = line }
            });
        }

        var isComponent = relativePath.EndsWith(".razor", StringComparison.OrdinalIgnoreCase);
        var tokens = RazorTokenizer.Tokenize(content);
        var hasPageDirective = tokens.Any(t => t.Kind == RazorTokenKind.Directive && t.Name == "page");

        var kind = isComponent ? NodeKind.RazorComponent : hasPageDirective ? NodeKind.RazorPage : NodeKind.RazorView;
        var artifactId = ArtifactId(projectName, relativePath);



        var componentClass = ResolveComponentClass(tokens, relativePath, lookup);
        var signature = componentClass is not null ? $"component={componentClass.Id}" : null;

        nodes.Add(new CodeNode
        {
            Id = artifactId,
            Kind = kind,
            Name = System.IO.Path.GetFileName(relativePath),
            QualifiedName = relativePath.Replace('\\', '/'),
            FilePath = relativePath,
            SourceLocation = new SourceLocation { StartLine = 1, EndLine = 1 },
            Language = "razor",
            Signature = signature
        });

        var ownerClass = componentClass;

        foreach (var tag in tokens.Where(t => t.Kind == RazorTokenKind.StartTag))
        {
            AnalyzeChildComponent(projectName, artifactId, tag, lookup, AddEdge);
            AnalyzeParameterBindings(artifactId, tag, lookup, ownerClass, AddEdge);
            AnalyzeEventBindings(artifactId, tag, lookup, ownerClass, AddEdge);
            AnalyzeTwoWayBindings(artifactId, tag, lookup, ownerClass, AddEdge);
        }

        return new AnalysisResult { Nodes = nodes, Edges = edges };
    }

    private static CodeNode? ResolveComponentClass(IReadOnlyList<RazorToken> tokens, string relativePath, DotNetSourceGraphLookup lookup)
    {
        var inherits = tokens.FirstOrDefault(t => t.Kind == RazorTokenKind.Directive && t.Name == "inherits");
        if (inherits is not null && !string.IsNullOrWhiteSpace(inherits.Value))
        {
            var typeName = inherits.Value.Trim();
            var simpleName = typeName.Contains('.') ? typeName[(typeName.LastIndexOf('.') + 1)..] : typeName;
            var byQualified = lookup.SingleClassByQualifiedName(typeName);
            if (byQualified is not null)
                return byQualified;
            var byName = lookup.SingleClassByName(simpleName);
            if (byName is not null)
                return byName;
        }


        var fileName = System.IO.Path.GetFileNameWithoutExtension(relativePath);
        return lookup.SingleClassByName(fileName);
    }

    private static void AnalyzeChildComponent(
        string projectName,
        string artifactId,
        RazorToken tag,
        DotNetSourceGraphLookup lookup,
        Action<string, string, EdgeKind, double, int> addEdge)
    {
        if (!IsPascalCaseTag(tag.Name))
            return;
        if (tag.Name == "DynamicComponent")
            return;

        var isDotted = tag.Name.Contains('.');
        var simpleName = isDotted ? tag.Name[(tag.Name.LastIndexOf('.') + 1)..] : tag.Name;










        var byFileName = lookup.ByName(simpleName + ".razor").Where(n => n.Kind == NodeKind.RazorComponent).ToArray();
        var target = isDotted
            ? FindByNamespaceSuffix(byFileName, tag.Name) is { } namespacedMatch ? namespacedMatch : lookup.SingleClassByName(simpleName)
            : byFileName is [var razorMatch] ? razorMatch : lookup.SingleClassByName(simpleName);
        if (target is null)
            return;
        addEdge(artifactId, target.Id, EdgeKind.Renders, 0.90, tag.Line);
    }











    private static CodeNode? FindByNamespaceSuffix(IReadOnlyList<CodeNode> candidates, string tagName)
    {
        var segments = tagName.Split('.');
        for (var take = segments.Length - 1; take >= 2; take--)
        {
            var suffix = string.Join('/', segments[^take..]) + ".razor";
            var matches = candidates.Where(n =>
            {
                var path = n.QualifiedName.Replace('\\', '/');
                return path == suffix || path.EndsWith("/" + suffix, StringComparison.Ordinal);
            }).ToArray();
            if (matches.Length == 1)
                return matches[0];
        }
        return null;
    }

    private static void AnalyzeParameterBindings(
        string artifactId,
        RazorToken tag,
        DotNetSourceGraphLookup lookup,
        CodeNode? ownerClass,
        Action<string, string, EdgeKind, double, int> addEdge)
    {
        if (!IsPascalCaseTag(tag.Name))
            return;
        var simpleName = tag.Name.Contains('.') ? tag.Name[(tag.Name.LastIndexOf('.') + 1)..] : tag.Name;
        var targetClass = lookup.SingleClassByName(simpleName);
        if (targetClass is null)
            return;

        foreach (var attribute in tag.Attributes)
        {
            if (attribute.Name.StartsWith('@') || attribute.Name.Contains(':'))
                continue;


            var candidates = lookup.ByQualifiedName(targetClass.QualifiedName + "." + attribute.Name)
                .Where(n => n.Kind == NodeKind.Property)
                .ToArray();
            if (candidates.Length != 1)
                continue;
            if (!IsComponentParameter(candidates[0], lookup))
                continue;
            addEdge(artifactId, candidates[0].Id, EdgeKind.BindsTo, 0.85, attribute.Line);
        }
    }









    private static bool IsComponentParameter(CodeNode propertyNode, DotNetSourceGraphLookup lookup)
    {
        if (lookup.SymbolFor(propertyNode.Id) is not IPropertySymbol property)
            return false;
        foreach (var attributeData in property.GetAttributes())
        {
            if (attributeData.AttributeClass?.ToDisplayString() == "Microsoft.AspNetCore.Components.ParameterAttribute")
                return true;
        }
        return false;
    }

    private static void AnalyzeEventBindings(
        string artifactId,
        RazorToken tag,
        DotNetSourceGraphLookup lookup,
        CodeNode? ownerClass,
        Action<string, string, EdgeKind, double, int> addEdge)
    {
        if (ownerClass is null)
            return;
        foreach (var attribute in tag.Attributes)
        {
            if (!EventAttributes.Contains(attribute.Name))
                continue;
            var methodName = ExtractMethodName(attribute.Value);
            if (methodName is null)
                continue;
            var candidates = lookup.ByQualifiedName(ownerClass.QualifiedName + "." + methodName)
                .Where(n => n.Kind == NodeKind.Method)
                .ToArray();
            if (candidates.Length != 1)
                continue;
            addEdge(artifactId, candidates[0].Id, EdgeKind.HandlesEvent, 0.90, attribute.Line);
        }
    }

    private static void AnalyzeTwoWayBindings(
        string artifactId,
        RazorToken tag,
        DotNetSourceGraphLookup lookup,
        CodeNode? ownerClass,
        Action<string, string, EdgeKind, double, int> addEdge)
    {
        if (ownerClass is null)
            return;
        foreach (var attribute in tag.Attributes)
        {



            if (attribute.Name != "@bind" && !attribute.Name.StartsWith("@bind-", StringComparison.Ordinal))
                continue;
            if (attribute.Name.EndsWith(":event", StringComparison.Ordinal))
                continue;
            var expression = attribute.Value.Trim();
            var propertyName = ExtractSimpleMemberName(expression);
            if (propertyName is null)
                continue;
            var candidates = lookup.ByQualifiedName(ownerClass.QualifiedName + "." + propertyName)
                .Where(n => n.Kind == NodeKind.Property)
                .ToArray();
            if (candidates.Length != 1)
                continue;
            addEdge(artifactId, candidates[0].Id, EdgeKind.BindsTo, 0.85, attribute.Line);
        }
    }

    private static string? ExtractMethodName(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith('@'))
            trimmed = trimmed[1..].Trim();

        if (trimmed.StartsWith('(') && trimmed.Contains("=>"))
        {
            var arrowIndex = trimmed.IndexOf("=>", StringComparison.Ordinal);
            var body = trimmed[(arrowIndex + 2)..].Trim().TrimEnd(')');
            var parenIndex = body.IndexOf('(');
            var candidate = parenIndex >= 0 ? body[..parenIndex] : body;
            return IsSimpleIdentifier(candidate) ? candidate : null;
        }
        var callParenIndex = trimmed.IndexOf('(');
        var name = callParenIndex >= 0 ? trimmed[..callParenIndex] : trimmed;
        return IsSimpleIdentifier(name) ? name : null;
    }

    private static string? ExtractSimpleMemberName(string expression)
    {
        var trimmed = expression.TrimStart('@').Trim();


        if (trimmed.StartsWith("this.", StringComparison.Ordinal))
            trimmed = trimmed["this.".Length..];
        return IsSimpleIdentifier(trimmed) ? trimmed : null;
    }

    private static bool IsSimpleIdentifier(string value) =>
        value.Length > 0 && char.IsLetter(value[0]) || value.StartsWith('_')
            ? value.All(c => char.IsLetterOrDigit(c) || c == '_')
            : false;

    private static bool IsPascalCaseTag(string tagName)
    {
        var lastSegment = tagName.Contains('.') ? tagName[(tagName.LastIndexOf('.') + 1)..] : tagName;
        return lastSegment.Length > 0 && char.IsUpper(lastSegment[0]);
    }
}

internal enum RazorTokenKind { StartTag, Directive }

internal sealed record RazorAttribute(string Name, string Value, int Line);

internal sealed record RazorToken(RazorTokenKind Kind, string Name, string Value, int Line, IReadOnlyList<RazorAttribute> Attributes);







internal static class RazorTokenizer
{
    internal static IReadOnlyList<RazorToken> Tokenize(string content)
    {
        var tokens = new List<RazorToken>();
        var length = content.Length;
        var line = 1;
        var i = 0;

        void AdvanceLineCounter(int from, int to)
        {
            for (var j = from; j < to && j < length; j++)
                if (content[j] == '\n')
                    line++;
        }

        while (i < length)
        {
            var c = content[i];

            if (c == '@' && i + 1 < length && content[i + 1] == '*')
            {
                var end = content.IndexOf("*@", i + 2, StringComparison.Ordinal);
                var stop = end < 0 ? length : end + 2;
                AdvanceLineCounter(i, stop);
                i = stop;
                continue;
            }

            if (c == '<' && i + 3 < length && content[i + 1] == '!' && content[i + 2] == '-' && content[i + 3] == '-')
            {
                var end = content.IndexOf("-->", i + 4, StringComparison.Ordinal);
                var stop = end < 0 ? length : end + 3;
                AdvanceLineCounter(i, stop);
                i = stop;
                continue;
            }

            if (c == '@' && i + 1 < length && (char.IsLetter(content[i + 1]) || content[i + 1] == '('))
            {
                var directiveStart = i;
                var j = i + 1;
                var nameStart = j;
                while (j < length && (char.IsLetterOrDigit(content[j]) || content[j] == '_'))
                    j++;
                var name = content[nameStart..j];
                if (IsKnownDirective(name))
                {
                    var lineStart = j;
                    while (lineStart < length && (content[lineStart] == ' ' || content[lineStart] == '\t'))
                        lineStart++;
                    var lineEnd = content.IndexOf('\n', lineStart);
                    if (lineEnd < 0) lineEnd = length;
                    var value = content[lineStart..lineEnd].TrimEnd('\r', '\n', ' ', '\t');
                    tokens.Add(new RazorToken(RazorTokenKind.Directive, name, value, line, Array.Empty<RazorAttribute>()));
                    AdvanceLineCounter(directiveStart, lineEnd);
                    i = lineEnd;
                    continue;
                }
            }

            if (c == '<' && i + 1 < length && (char.IsLetter(content[i + 1])))
            {
                var tagStartLine = line;
                var j = i + 1;
                var nameStart = j;
                while (j < length && content[j] is not (' ' or '\t' or '\r' or '\n' or '>' or '/'))
                    j++;
                var tagName = content[nameStart..j];

                var attributes = new List<RazorAttribute>();
                while (j < length && content[j] != '>')
                {
                    while (j < length && char.IsWhiteSpace(content[j]))
                        j++;
                    if (j >= length || content[j] is '>' or '/')
                        break;
                    var attrNameStart = j;
                    while (j < length && content[j] is not (' ' or '\t' or '\r' or '\n' or '=' or '>' or '/'))
                        j++;
                    var attrName = content[attrNameStart..j];
                    if (attrName.Length == 0)
                    {
                        j++;
                        continue;
                    }
                    while (j < length && (content[j] == ' ' || content[j] == '\t'))
                        j++;
                    var attrValue = string.Empty;
                    if (j < length && content[j] == '=')
                    {
                        j++;
                        while (j < length && (content[j] == ' ' || content[j] == '\t'))
                            j++;
                        if (j < length && (content[j] == '"' || content[j] == '\''))
                        {
                            var quote = content[j];
                            j++;
                            var valueStart = j;
                            while (j < length && content[j] != quote)
                                j++;
                            attrValue = content[valueStart..j];
                            if (j < length) j++;
                        }
                        else
                        {
                            var valueStart = j;
                            while (j < length && content[j] is not (' ' or '\t' or '\r' or '\n' or '>' or '/'))
                                j++;
                            attrValue = content[valueStart..j];
                        }
                    }
                    var attrLine = line;
                    AdvanceLineCounter(attrNameStart, j);
                    attributes.Add(new RazorAttribute(attrName, attrValue, attrLine));
                }
                var tagEnd = j < length && content[j] == '>' ? j + 1 : j;
                tokens.Add(new RazorToken(RazorTokenKind.StartTag, tagName, string.Empty, tagStartLine, attributes));
                AdvanceLineCounter(i, tagEnd);
                i = tagEnd;
                continue;
            }

            if (c == '\n')
                line++;
            i++;
        }

        return tokens;
    }

    private static bool IsKnownDirective(string name) =>
        name is "page" or "inherits" or "inject" or "layout" or "model" or "using" or "namespace" or "implements" or "attribute";
}
