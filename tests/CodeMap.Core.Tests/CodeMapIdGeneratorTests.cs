using CodeMap.Core.Ids;

namespace CodeMap.Core.Tests;

public class CodeMapIdGeneratorTests
{
    private readonly CodeMapIdGenerator _generator = new();

    [Fact]
    public void CreateFileId_IsDeterministic()
    {
        const string project = "FileExtract.Wpf";
        const string path = "Features\\Approval\\ApprovalViewModel.cs";

        var first = _generator.CreateFileId(project, path);
        var second = _generator.CreateFileId(project, path);

        Assert.Equal(first, second);
        Assert.Equal("file://FileExtract.Wpf/Features/Approval/ApprovalViewModel.cs", first);
    }

    [Fact]
    public void CreateSymbolId_IsDeterministic()
    {
        const string project = "FileExtract.Wpf";
        const string signature = "FileExtract.App.Features.Approval.ApprovalViewModel.ExtractFilesAsync()";

        var first = _generator.CreateSymbolId(project, signature);
        var second = _generator.CreateSymbolId(project, signature);

        Assert.Equal(first, second);
        Assert.Equal(
            "csharp://FileExtract.Wpf/FileExtract.App.Features.Approval.ApprovalViewModel.ExtractFilesAsync()",
            first);
    }

    [Fact]
    public void CreateFileId_NormalizesBackslashes()
    {
        var id = _generator.CreateFileId("MyProject", "src\\Models\\User.cs");

        Assert.Equal("file://MyProject/src/Models/User.cs", id);
    }
}