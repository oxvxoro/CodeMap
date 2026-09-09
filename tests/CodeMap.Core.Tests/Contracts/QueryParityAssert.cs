using CodeMap.Storage;

namespace CodeMap.Core.Tests.Contracts;

internal static class QueryParityAssert
{
    public static void SameSymbols(IReadOnlyList<IndexedSymbol> expected, IReadOnlyList<IndexedSymbol> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (var index = 0; index < expected.Count; index++)
        {
            var left = expected[index];
            var right = actual[index];
            Assert.Equal(left.Id, right.Id);
            Assert.Equal(left.Project, right.Project);
            Assert.Equal(left.FileId, right.FileId);
            Assert.Equal(left.RelativePath, right.RelativePath);
            Assert.Equal(left.Kind, right.Kind);
            Assert.Equal(left.Name, right.Name);
            Assert.Equal(left.QualifiedName, right.QualifiedName);
            Assert.Equal(left.Signature, right.Signature);
            Assert.Equal(left.StartLine, right.StartLine);
            Assert.Equal(left.EndLine, right.EndLine);
            Assert.Equal(left.Visibility, right.Visibility);
            Assert.Equal(left.Language, right.Language);
        }
    }

    public static void SameRelations(IReadOnlyList<IndexedRelation> expected, IReadOnlyList<IndexedRelation> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (var index = 0; index < expected.Count; index++)
        {
            Assert.Equal(expected[index].Symbol.Id, actual[index].Symbol.Id);
            SameEdges(expected[index].Edge, actual[index].Edge);
        }
    }

    public static void SameRelations(IReadOnlyList<RelationQueryResult> expected, IReadOnlyList<RelationQueryResult> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (var index = 0; index < expected.Count; index++)
        {
            Assert.Equal(expected[index].Source.Id, actual[index].Source.Id);
            Assert.Equal(expected[index].Target.Id, actual[index].Target.Id);
            SameEdges(expected[index].Edge, actual[index].Edge);
            Assert.Equal(expected[index].Evidence, actual[index].Evidence);
        }
    }

    public static void SameImpact(
        IReadOnlyList<ImpactItem> expected,
        IReadOnlyList<ImpactItem> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (var index = 0; index < expected.Count; index++)
        {
            Assert.Equal(expected[index].Symbol.Id, actual[index].Symbol.Id);
            Assert.Equal(expected[index].Depth, actual[index].Depth);
            Assert.Equal(expected[index].RootId, actual[index].RootId);
            SameEdges(expected[index].Via, actual[index].Via);
        }
    }

    private static void SameEdges(IndexedEdge expected, IndexedEdge actual)
    {
        Assert.Equal(expected.SourceId, actual.SourceId);
        Assert.Equal(expected.TargetId, actual.TargetId);
        Assert.Equal(expected.Kind, actual.Kind);
        Assert.Equal(expected.SourceFileId, actual.SourceFileId);
        Assert.Equal(expected.Line, actual.Line);
        Assert.Equal(expected.ResolutionKind, actual.ResolutionKind);
        Assert.Equal(expected.Confidence, actual.Confidence);
        Assert.Equal(expected.StartColumn, actual.StartColumn);
        Assert.Equal(expected.EndLine, actual.EndLine);
        Assert.Equal(expected.EndColumn, actual.EndColumn);
    }
}
