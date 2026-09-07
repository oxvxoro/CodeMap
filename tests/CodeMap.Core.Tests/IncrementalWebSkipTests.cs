using CodeMap.Core.Models;
using CodeMap.Storage;
using CodeMap.Web;

namespace CodeMap.Core.Tests;

[Collection("MsBuild")]
public sealed class IncrementalWebSkipTests
{
    [Fact]
    public async Task UpdateAsync_CSharpOnlyChange_DoesNotInvokeWebAnalyzer()
    {
        var workingDirectory = CopyFixtureToTempDirectory();
        var webCalls = 0;
        var countingWeb = new CountingWebWorkspaceIndexer(() => Interlocked.Increment(ref webCalls));
        try
        {
            var indexer = new IncrementalCodeMapIndexer(webWorkspaceIndexer: countingWeb);
            await indexer.IndexAsync(workingDirectory, force: true, CancellationToken.None);
            var callsAfterIndex = webCalls;

            var greeterPath = Path.Combine(workingDirectory, "ProjB", "Greeter.cs");
            await File.WriteAllTextAsync(greeterPath, """
                namespace Fixture.ProjB;

                public class Greeter
                {
                    public string Greet() => "updated";
                }
                """);
            await indexer.UpdateAsync(workingDirectory, CancellationToken.None);

            Assert.Equal(callsAfterIndex, webCalls);
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }

    private sealed class CountingWebWorkspaceIndexer : WebWorkspaceIndexer
    {
        private readonly Action _onAnalyze;

        public CountingWebWorkspaceIndexer(Action onAnalyze) => _onAnalyze = onAnalyze;

        public override async Task<AnalyzedProject?> AnalyzeAsync(string root, CancellationToken cancellationToken = default)
        {
            _onAnalyze();
            return await base.AnalyzeAsync(root, cancellationToken);
        }
    }

    private static string CopyFixtureToTempDirectory()
    {
        var testDir = AppContext.BaseDirectory;
        var solutionRoot = Path.GetFullPath(Path.Combine(testDir, "..", "..", "..", "..", ".."));
        var source = Path.Combine(solutionRoot, "tests", "Fixtures", "MultiProject");
        var destination = Path.Combine(Path.GetTempPath(), "codemap-web-skip-" + Guid.NewGuid());
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*.*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var targetPath = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.Copy(file, targetPath, overwrite: true);
        }
        return destination;
    }

    private static void CleanUp(string workingDirectory)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(workingDirectory))
            Directory.Delete(workingDirectory, recursive: true);
    }
}