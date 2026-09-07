using System.Text.Json;
using CodeMap.Core.Models;
using CodeMap.Storage;

namespace CodeMap.Core.Tests;











public sealed class RelationEvidenceMapperTests
{
    private static IndexedSymbol Symbol(string id, NodeKind kind = NodeKind.Class) =>
        new(id, "Proj", "file1", "File.cs", kind, "Name", "Ns.Name", null, 1, 1, null, "csharp");

    private static string Evidence(EdgeKind kind, EdgeResolutionKind resolution, double? confidence)
    {
        var edge = new IndexedEdge("source", "target", kind, "file1", 1, resolution, confidence);
        return RelationEvidenceMapper.FromEdge(edge, Symbol("source"), Symbol("target"), "File.cs", 1).Evidence;
    }

    [Fact]
    public void SemanticEdge_IsAlwaysLabeledSemanticRegardlessOfConfidence() =>
        Assert.Equal("semantic", Evidence(EdgeKind.Calls, EdgeResolutionKind.Semantic, 1.0));

    [Fact]
    public void UsesCss_IsLabeledCssSelector() =>
        Assert.Equal("css-selector", Evidence(EdgeKind.UsesCss, EdgeResolutionKind.Syntactic, 0.85));

    [Fact]
    public void UsesElement_IsLabeledDomSelector() =>
        Assert.Equal("dom-selector", Evidence(EdgeKind.UsesElement, EdgeResolutionKind.Heuristic, 0.60));





    [Fact]
    public void Renders_IsLabeledRazorComponentTag_NotStaticImport() =>
        Assert.Equal("razor-component-tag", Evidence(EdgeKind.Renders, EdgeResolutionKind.Syntactic, 0.90));

    [Fact]
    public void HandlesEvent_IsLabeledMarkupEventBinding_NotStaticImport() =>
        Assert.Equal("markup-event-binding", Evidence(EdgeKind.HandlesEvent, EdgeResolutionKind.Syntactic, 0.90));

    [Fact]
    public void UsesViewModel_IsLabeledXamlViewModelReference_NotStaticImport() =>
        Assert.Equal("xaml-viewmodel-reference", Evidence(EdgeKind.UsesViewModel, EdgeResolutionKind.Syntactic, 0.90));




    [Fact]
    public void BindsTo_IsLabeledMarkupDataBinding_NotStaticImport() =>
        Assert.Equal("markup-data-binding", Evidence(EdgeKind.BindsTo, EdgeResolutionKind.Syntactic, 0.85));

    [Theory]
    [InlineData(0.90)]
    [InlineData(0.85)]
    public void Imports_DirectOrReexport_IsLabeledStaticImport(double confidence) =>
        Assert.Equal("static-import", Evidence(EdgeKind.Imports, EdgeResolutionKind.Heuristic, confidence));

    [Fact]
    public void References_SameFileFallback_IsLabeledSameFileFallback() =>
        Assert.Equal("same-file-fallback", Evidence(EdgeKind.References, EdgeResolutionKind.Heuristic, 0.70));

    [Fact]
    public void Imports_NameFallback_IsLabeledNameFallback() =>
        Assert.Equal("name-fallback", Evidence(EdgeKind.Imports, EdgeResolutionKind.Heuristic, 0.60));

    [Fact]
    public void RoutesTo_Registers_ResolvesTo_AreSemanticNotConfidenceMapped()
    {
        Assert.Equal("semantic", Evidence(EdgeKind.RoutesTo, EdgeResolutionKind.Semantic, 1.0));
        Assert.Equal("semantic", Evidence(EdgeKind.Registers, EdgeResolutionKind.Semantic, 1.0));
        Assert.Equal("semantic", Evidence(EdgeKind.ResolvesTo, EdgeResolutionKind.Semantic, 1.0));
    }







    [Fact]
    public void AllMapperOutputs_AreAllowedBySchemaEvidenceEnum()
    {
        var mapperOutputs = new[]
        {
            Evidence(EdgeKind.Calls, EdgeResolutionKind.Semantic, 1.0),
            Evidence(EdgeKind.UsesCss, EdgeResolutionKind.Syntactic, 0.85),
            Evidence(EdgeKind.UsesElement, EdgeResolutionKind.Heuristic, 0.60),
            Evidence(EdgeKind.Renders, EdgeResolutionKind.Syntactic, 0.90),
            Evidence(EdgeKind.HandlesEvent, EdgeResolutionKind.Syntactic, 0.90),
            Evidence(EdgeKind.UsesViewModel, EdgeResolutionKind.Syntactic, 0.90),
            Evidence(EdgeKind.BindsTo, EdgeResolutionKind.Syntactic, 0.85),
            Evidence(EdgeKind.Imports, EdgeResolutionKind.Heuristic, 0.90),
            Evidence(EdgeKind.References, EdgeResolutionKind.Heuristic, 0.70),
            Evidence(EdgeKind.Imports, EdgeResolutionKind.Heuristic, 0.60),
            Evidence(EdgeKind.Contains, EdgeResolutionKind.Heuristic, 0.99),
        };

        var schemaPath = FindSchemaPath();
        using var schemaDocument = JsonDocument.Parse(File.ReadAllText(schemaPath));
        var evidenceEnum = schemaDocument.RootElement
            .GetProperty("$defs").GetProperty("relation").GetProperty("properties").GetProperty("evidence").GetProperty("enum")
            .EnumerateArray()
            .Select(element => element.ValueKind == JsonValueKind.Null ? null : element.GetString())
            .ToHashSet(StringComparer.Ordinal);

        foreach (var output in mapperOutputs)
            Assert.Contains(output, evidenceEnum);
    }

    private static string FindSchemaPath()
    {
        var testDir = AppContext.BaseDirectory;
        var solutionRoot = Path.GetFullPath(Path.Combine(testDir, "..", "..", "..", "..", ".."));
        return Path.Combine(solutionRoot, "docs", "cli-json-v3.schema.json");
    }
}