using CodeMap.Storage;

namespace CodeMap.Core.Tests;

public sealed class CodeMapIndexLocatorTests
{
    [Fact]
    public void FindDatabase_FromFilePath_FindsNearestIndex()
    {
        var root = Path.Combine(Path.GetTempPath(), "codemap-index-locator-" + Guid.NewGuid());
        var project = Path.Combine(root, "src", "Project");
        var sourceFile = Path.Combine(project, "Widget.cs");
        var databasePath = Path.Combine(root, ".codemap", "index.db");
        Directory.CreateDirectory(project);
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        File.WriteAllText(sourceFile, "// fixture");
        File.WriteAllText(databasePath, string.Empty);

        try
        {
            Assert.Equal(databasePath, CodeMapIndexLocator.FindDatabase(sourceFile));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task IsStaleAsync_IOException_IsConservativelyStale()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), "codemap-missing", ".codemap", "index.db");

        var stale = await CodeMapIndexLocator.IsStaleAsync(
            databasePath,
            CancellationToken.None,
            (_, _) => Task.FromException<bool>(new IOException("simulated I/O failure")));

        Assert.True(stale);
    }

    [Fact]
    public void Classify_MapsContractExceptions()
    {
        Assert.Equal(("index_not_found", "No CodeMap index found.\nRun: codemap index"),
            CodeMapErrorClassifier.Classify(new FileNotFoundException("No CodeMap index found.\nRun: codemap index")));
        Assert.Equal(("index_building", "CodeMap index is being rebuilt. Try again shortly."),
            CodeMapErrorClassifier.Classify(new IndexBuildingException()));
        Assert.Equal(("git_unavailable", "Not a git repository."),
            CodeMapErrorClassifier.Classify(new GitUnavailableException("Not a git repository.")));
        Assert.Equal(("query_failed", "codemap query failed: boom"),
            CodeMapErrorClassifier.Classify(new InvalidOperationException("boom")));
    }
}
