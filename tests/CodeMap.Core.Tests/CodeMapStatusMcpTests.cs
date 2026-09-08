using System.Text.Json;
using CodeMap.Mcp;
using CodeMap.Storage;
using Microsoft.Data.Sqlite;

namespace CodeMap.Core.Tests;

[Collection("MsBuild")]
public sealed class CodeMapStatusMcpTests
{
    [Fact(Timeout = 90_000)]
    public async Task GetStatus_ReturnsMetadataAndFreshnessWithoutChangingIndex()
    {
        var workingDirectory = CopyFixture();
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);
            using var context = new CodeMapMcpContext(workingDirectory);

            var response = await CodeMapTools.GetStatus(context, workingDirectory, checkFreshness: true, CancellationToken.None);

            using var document = JsonDocument.Parse(response);
            Assert.Equal(1, document.RootElement.GetProperty("version").GetInt32());
            Assert.Equal("ready", document.RootElement.GetProperty("indexState").GetString());
            Assert.False(document.RootElement.GetProperty("schemaOutdated").GetBoolean());
            Assert.False(document.RootElement.GetProperty("analyzerVersionsOutdated").GetBoolean());
            Assert.True(document.RootElement.GetProperty("symbols").GetInt32() > 0);
            Assert.True(document.RootElement.GetProperty("edges").GetInt32() > 0);
            Assert.True(document.RootElement.GetProperty("freshnessChecked").GetBoolean());
            Assert.False(document.RootElement.GetProperty("stale").GetBoolean());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(workingDirectory))
                Directory.Delete(workingDirectory, recursive: true);
        }
    }

    private static string CopyFixture()
    {
        var solutionRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var source = Path.Combine(solutionRoot, "tests", "Fixtures", "MultiProject");
        var destination = Path.Combine(Path.GetTempPath(), "codemap-status-mcp-" + Guid.NewGuid());
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*.*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
        return destination;
    }
}
