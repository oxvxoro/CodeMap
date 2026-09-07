using CodeMap.Core.Ids;
using CodeMap.Core.Models;

namespace CodeMap.Core.Tests;

public class CodeNodeTests
{
    [Fact]
    public void CreateCodeNode_SetsRequiredProperties()
    {
        var node = new CodeNode
        {
            Id = "csharp://MyProject/MyApp.Services.UserService.GetUser(System.Int32)",
            Kind = NodeKind.Method,
            Name = "GetUser",
            QualifiedName = "MyApp.Services.UserService.GetUser",
            FilePath = "Services/UserService.cs",
            Language = "csharp",
            Signature = "GetUser(int id)",
            Visibility = "public",
            SourceLocation = new SourceLocation
            {
                StartLine = 10,
                StartColumn = 5,
                EndLine = 15,
                EndColumn = 6
            }
        };

        Assert.Equal(NodeKind.Method, node.Kind);
        Assert.Equal("GetUser", node.Name);
        Assert.Equal("csharp", node.Language);
        Assert.NotNull(node.SourceLocation);
        Assert.Equal(10, node.SourceLocation.StartLine);
    }
}