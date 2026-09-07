using System.Text.Json;
using CodeMap.Core.Models;

namespace CodeMap.Core.Tests;

public class SerializationTests
{
    [Fact]
    public void CodeNode_RoundTripsThroughJson()
    {
        var node = new CodeNode
        {
            Id = "file://MyProject/Program.cs",
            Kind = NodeKind.File,
            Name = "Program.cs",
            QualifiedName = "Program.cs",
            FilePath = "Program.cs",
            Language = "csharp"
        };

        var json = JsonSerializer.Serialize(node);
        var restored = JsonSerializer.Deserialize<CodeNode>(json);

        Assert.NotNull(restored);
        Assert.Equal(node.Id, restored.Id);
        Assert.Equal(node.Kind, restored.Kind);
        Assert.Equal(node.Name, restored.Name);
        Assert.Equal(node.Language, restored.Language);
    }

    [Fact]
    public void AnalysisResult_RoundTripsThroughJson()
    {
        var result = new AnalysisResult
        {
            Nodes =
            [
                new CodeNode
                {
                    Id = "file://MyProject/Program.cs",
                    Kind = NodeKind.File,
                    Name = "Program.cs",
                    QualifiedName = "Program.cs",
                    Language = "csharp"
                }
            ],
            Edges =
            [
                new CodeEdge
                {
                    SourceId = "file://MyProject/Program.cs",
                    TargetId = "csharp://MyProject/MyApp.Program.Main()",
                    Kind = EdgeKind.Defines
                }
            ]
        };

        var json = JsonSerializer.Serialize(result);
        var restored = JsonSerializer.Deserialize<AnalysisResult>(json);

        Assert.NotNull(restored);
        Assert.Single(restored.Nodes);
        Assert.Single(restored.Edges);
        Assert.Equal(EdgeKind.Defines, restored.Edges[0].Kind);
    }
}