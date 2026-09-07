using CodeMap.Core.Ids;
using CodeMap.Core.Models;

namespace CodeMap.Core.Tests;

public class CodeEdgeTests
{
    [Fact]
    public void CreateGraphEdge_LinksSourceAndTarget()
    {
        var idGenerator = new CodeMapIdGenerator();
        var sourceId = idGenerator.CreateSymbolId(
            "MyProject",
            "MyApp.ApprovalViewModel.ExtractFilesAsync()");
        var targetId = idGenerator.CreateSymbolId(
            "MyProject",
            "MyApp.IExtractApprovalFilesHandler.HandleAsync()");

        var edge = new CodeEdge
        {
            SourceId = sourceId,
            TargetId = targetId,
            Kind = EdgeKind.Calls,
            SourceLocation = new SourceLocation
            {
                StartLine = 42,
                StartColumn = 9,
                EndLine = 42,
                EndColumn = 55
            }
        };

        Assert.Equal(sourceId, edge.SourceId);
        Assert.Equal(targetId, edge.TargetId);
        Assert.Equal(EdgeKind.Calls, edge.Kind);
        Assert.NotEqual(edge.SourceId, edge.TargetId);
    }
}