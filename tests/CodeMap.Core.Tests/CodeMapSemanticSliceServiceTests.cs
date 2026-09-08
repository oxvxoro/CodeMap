using CodeMap.CSharp;
using CodeMap.Storage;
using System.Text.Json;

namespace CodeMap.Core.Tests;

[Collection("MsBuild")]
public sealed class CodeMapSemanticSliceServiceTests
{
    [Fact]
    public async Task SliceAsync_RejectsMissingOrStaleIndexBeforeOpeningSource()
    {
        var root = Path.Combine(Path.GetTempPath(), "codemap-semantic-slice-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var exception = await Assert.ThrowsAsync<SemanticSliceException>(() =>
                new CodeMapSemanticSliceService().SliceAsync(root,
                    new SemanticSliceRequest(Query: "Fixture.Sample.Calculate"), CancellationToken.None));

            Assert.Equal("semantic_slice_stale_index", exception.Code);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact(Timeout = 90_000)]
    public async Task SliceAsync_UsesFreshIndexToResolveAndAnalyzeMethod()
    {
        var source = Path.Combine(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..")), "tests", "Fixtures", "MultiProject");
        var root = Path.Combine(Path.GetTempPath(), "codemap-semantic-slice-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        foreach (var file in Directory.EnumerateFiles(source, "*.*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(root, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(root, force: true, CancellationToken.None);

            using (var state = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, ".codemap", "state.json"))))
            {
                Assert.Equal(4, state.RootElement.GetProperty("indexFormatVersion").GetInt32());
                Assert.All(state.RootElement.GetProperty("projects").EnumerateArray(), project =>
                    Assert.Equal("csharp", project.GetProperty("providerKind").GetString()));
            }

            var result = await new CodeMapSemanticSliceService().SliceAsync(root,
                new SemanticSliceRequest(Query: "Fixture.ProjB.Greeter.Greet"), CancellationToken.None);

            Assert.Equal("Greet", result.EntrySymbol.Name);
            Assert.NotEmpty(result.Items);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }
}
