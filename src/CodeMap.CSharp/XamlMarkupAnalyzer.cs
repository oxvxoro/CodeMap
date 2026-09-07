using System.Xml;
using System.Xml.Linq;
using CodeMap.Core.Ids;
using CodeMap.Core.Models;

namespace CodeMap.CSharp;








internal static class XamlMarkupAnalyzer
{
    private const string XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly HashSet<string> WpfRootElementNames = new(StringComparer.Ordinal)
    {
        "Window", "UserControl", "Page", "ResourceDictionary"
    };

    internal static bool IsWpfProject(string projectDirectory, IReadOnlyList<string> xamlFiles)
    {
        if (File.Exists(Path.Combine(projectDirectory, "App.xaml")))
            return true;
        foreach (var file in xamlFiles)
        {
            var document = TryLoad(file);
            if (document?.Root is null)
                continue;
            if (WpfRootElementNames.Contains(document.Root.Name.LocalName))
                return true;
        }
        return false;
    }

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

        var artifactId = $"xaml://{projectName}/{relativePath.Replace('\\', '/')}";
        XDocument? document;
        try
        {
            document = TryParse(content);
        }
        catch
        {
            document = null;
        }


        if (document?.Root is null)
            return new AnalysisResult { Nodes = [], Edges = [] };

        var root = document.Root;
        var xClassAttribute = root.Attribute(XName.Get("Class", XamlNamespace));
        CodeNode? codeBehindClass = null;
        string? signature = null;
        if (xClassAttribute is not null)
        {
            codeBehindClass = lookup.SingleClassByQualifiedName(xClassAttribute.Value);
            if (codeBehindClass is not null)
                signature = $"class={codeBehindClass.Id}";
        }

        nodes.Add(new CodeNode
        {
            Id = artifactId,
            Kind = NodeKind.XamlView,
            Name = Path.GetFileName(relativePath),
            QualifiedName = relativePath.Replace('\\', '/'),
            FilePath = relativePath,
            SourceLocation = new SourceLocation { StartLine = 1, EndLine = 1 },
            Language = "xaml",
            Signature = signature
        });

        var viewModel = ResolveViewModel(root, lookup);
        if (viewModel is not null)
            AddEdge(artifactId, viewModel.Id, EdgeKind.UsesViewModel, 0.90, GetLine(root));

        foreach (var element in root.DescendantsAndSelf())
        {
            foreach (var attribute in element.Attributes())
            {
                AnalyzeBindingAttribute(artifactId, attribute, viewModel, lookup, AddEdge);
                AnalyzeClickAttribute(artifactId, attribute, codeBehindClass, lookup, AddEdge);
            }
        }

        return new AnalysisResult { Nodes = nodes, Edges = edges };
    }

    private static CodeNode? ResolveViewModel(XElement root, DotNetSourceGraphLookup lookup)
    {
        var dataTypeAttribute = root.Attribute(XName.Get("DataType", XamlNamespace));
        if (dataTypeAttribute is not null)
        {
            var typeReference = ExtractTypeExtensionValue(dataTypeAttribute.Value);
            if (typeReference is not null)
            {
                var (prefix, localName) = SplitPrefixed(typeReference);
                var clrNamespace = ResolveClrNamespace(root, prefix);
                var qualifiedName = clrNamespace is null ? localName : clrNamespace + "." + localName;
                var match = lookup.SingleClassByQualifiedName(qualifiedName) ?? lookup.SingleClassByName(localName);
                if (match is not null)
                    return match;
            }
        }

        var dataContextAttribute = root.Attribute("DataContext");
        if (dataContextAttribute is not null)
        {
            var resourceKey = ExtractStaticResourceKey(dataContextAttribute.Value);
            if (resourceKey is not null)
            {



                var declaration = root.DescendantsAndSelf()
                    .FirstOrDefault(e => (string?)e.Attribute(XName.Get("Key", XamlNamespace)) == resourceKey);
                if (declaration is not null)
                {
                    var localName = declaration.Name.LocalName;
                    var match = lookup.SingleClassByName(localName);
                    if (match is not null)
                        return match;
                }
            }
        }
        return null;
    }

    private static void AnalyzeBindingAttribute(
        string artifactId,
        XAttribute attribute,
        CodeNode? viewModel,
        DotNetSourceGraphLookup lookup,
        Action<string, string, EdgeKind, double, int> addEdge)
    {
        if (viewModel is null)
            return;

        if (attribute.Name.NamespaceName == XamlNamespace || attribute.IsNamespaceDeclaration)
            return;
        var value = attribute.Value.Trim();
        if (!value.StartsWith("{Binding", StringComparison.Ordinal))
            return;
        var inner = value[1..^(value.EndsWith('}') ? 1 : 0)].Trim();

        inner = inner.Length >= "Binding".Length ? inner["Binding".Length..].Trim() : inner;

        if (ContainsExcludedBindingFeature(inner))
            return;

        var line = GetLine(attribute.Parent!);


        var commandValue = ExtractNamedBindingArgument(inner, "Command");
        if (commandValue is not null)
        {
            TryBindSingleMember(artifactId, viewModel, commandValue, lookup, line, addEdge);
            return;
        }


        var pathValue = ExtractNamedBindingArgument(inner, "Path") ?? ExtractPositionalPath(inner);
        if (pathValue is not null)
        {


            var firstSegment = pathValue.Split('.')[0].Trim();
            TryBindSingleMember(artifactId, viewModel, firstSegment, lookup, line, addEdge);
        }
    }

    private static bool ContainsExcludedBindingFeature(string inner) =>
        inner.Contains("RelativeSource", StringComparison.OrdinalIgnoreCase)
        || inner.Contains("ElementName", StringComparison.OrdinalIgnoreCase);

    private static void TryBindSingleMember(
        string artifactId,
        CodeNode viewModel,
        string memberName,
        DotNetSourceGraphLookup lookup,
        int line,
        Action<string, string, EdgeKind, double, int> addEdge)
    {
        var trimmed = memberName.Trim();
        if (!IsSimpleIdentifier(trimmed))
            return;


        var candidates = lookup.ByQualifiedName(viewModel.QualifiedName + "." + trimmed)
            .Where(n => n.Kind is NodeKind.Property or NodeKind.Method)
            .ToArray();
        if (candidates.Length != 1)
            return;
        addEdge(artifactId, candidates[0].Id, EdgeKind.BindsTo, 0.85, line);
    }

    private static void AnalyzeClickAttribute(
        string artifactId,
        XAttribute attribute,
        CodeNode? codeBehindClass,
        DotNetSourceGraphLookup lookup,
        Action<string, string, EdgeKind, double, int> addEdge)
    {
        if (codeBehindClass is null)
            return;
        if (attribute.Name.LocalName != "Click")
            return;
        var methodName = attribute.Value.Trim();
        if (!IsSimpleIdentifier(methodName))
            return;
        var candidates = lookup.ByQualifiedName(codeBehindClass.QualifiedName + "." + methodName)
            .Where(n => n.Kind == NodeKind.Method)
            .ToArray();
        if (candidates.Length != 1)
            return;
        addEdge(artifactId, candidates[0].Id, EdgeKind.HandlesEvent, 0.90, GetLine(attribute.Parent!));
    }

    private static string? ExtractTypeExtensionValue(string value)
    {
        var trimmed = value.Trim();
        if (!trimmed.StartsWith('{'))
            return null;
        var inner = trimmed.Trim('{', '}').Trim();

        var parts = inner.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
            return null;
        var markupName = parts[0].Contains(':') ? parts[0][(parts[0].IndexOf(':') + 1)..] : parts[0];
        return markupName != "Type" ? null : parts[1];
    }

    private static string? ExtractStaticResourceKey(string value)
    {
        var trimmed = value.Trim();
        if (!trimmed.StartsWith("{StaticResource", StringComparison.Ordinal))
            return null;
        var inner = trimmed.Trim('{', '}')["StaticResource".Length..].Trim();
        return inner.Length == 0 ? null : inner;
    }

    private static string? ExtractNamedBindingArgument(string inner, string name)
    {
        var parts = SplitBindingArguments(inner);
        foreach (var part in parts)
        {
            var equalsIndex = part.IndexOf('=');
            if (equalsIndex < 0)
                continue;
            var key = part[..equalsIndex].Trim();
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
                return part[(equalsIndex + 1)..].Trim();
        }
        return null;
    }

    private static string? ExtractPositionalPath(string inner)
    {
        var parts = SplitBindingArguments(inner);
        foreach (var part in parts)
        {
            if (!part.Contains('=') && part.Trim().Length > 0)
                return part.Trim();
        }
        return null;
    }

    private static IReadOnlyList<string> SplitBindingArguments(string inner) =>
        inner.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static (string? Prefix, string LocalName) SplitPrefixed(string value)
    {
        var colonIndex = value.IndexOf(':');
        return colonIndex < 0 ? (null, value) : (value[..colonIndex], value[(colonIndex + 1)..]);
    }

    private static string? ResolveClrNamespace(XElement contextElement, string? prefix)
    {
        if (prefix is null)
            return null;
        for (var element = contextElement; element is not null; element = element.Parent)
        {
            var xmlnsAttribute = element.Attribute(XNamespace.Xmlns + prefix);
            if (xmlnsAttribute is null)
                continue;
            var value = xmlnsAttribute.Value;

            if (!value.StartsWith("clr-namespace:", StringComparison.Ordinal))
                return null;
            var withoutPrefix = value["clr-namespace:".Length..];
            var semicolonIndex = withoutPrefix.IndexOf(';');
            return semicolonIndex < 0 ? withoutPrefix : withoutPrefix[..semicolonIndex];
        }
        return null;
    }

    private static bool IsSimpleIdentifier(string value) =>
        value.Length > 0
        && (char.IsLetter(value[0]) || value[0] == '_')
        && value.All(c => char.IsLetterOrDigit(c) || c == '_');

    private static int GetLine(XElement element) =>
        element is IXmlLineInfo info && info.HasLineInfo() ? info.LineNumber : 1;

    private static XDocument? TryLoad(string filePath)
    {
        try
        {
            return TryParse(File.ReadAllText(filePath));
        }
        catch
        {
            // 구문을 해석할 수 없는 XAML은 신뢰할 수 없으므로 그래프에 추가하지 않는다.
            return null;
        }
    }

    private static XDocument TryParse(string content)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null
        };
        using var stringReader = new StringReader(content);
        using var xmlReader = XmlReader.Create(stringReader, settings);
        return XDocument.Load(xmlReader, LoadOptions.SetLineInfo);
    }
}
