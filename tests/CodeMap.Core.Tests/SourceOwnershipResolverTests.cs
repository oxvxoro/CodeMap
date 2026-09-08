using CodeMap.Storage;

namespace CodeMap.Core.Tests;

public sealed class SourceOwnershipResolverTests
{
    [Theory]
    [InlineData("Program.cs", "CSharp")]
    [InlineData("Views/Home.razor", "CSharp")]
    [InlineData("client.ts", "Web")]
    [InlineData("index.html", "Web")]
    [InlineData("app.py", "Unowned")]
    [InlineData("bin/generated.py", "Ignored")]
    public void ClassifiesFilesByExistingAnalyzerOwnership(string relativePath, string expected)
    {
        var root = Path.Combine(Path.GetTempPath(), "codemap-owner-" + Guid.NewGuid().ToString("N"));
        var resolver = new SourceOwnershipResolver(root);
        Assert.Equal(expected, resolver.GetOwner(Path.Combine(root, relativePath)).ToString());
    }

    [Fact]
    public void ClassifiesOutsideRootBeforeExtensionOwnership()
    {
        var root = Path.Combine(Path.GetTempPath(), "codemap-owner-" + Guid.NewGuid().ToString("N"));
        var outside = Path.Combine(Path.GetDirectoryName(root)!, "outside.cs");
        Assert.Equal(SourceOwner.OutsideRoot, new SourceOwnershipResolver(root).GetOwner(outside));
    }
}
