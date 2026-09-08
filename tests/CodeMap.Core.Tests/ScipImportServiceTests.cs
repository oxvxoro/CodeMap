using CodeMap.Scip.Protocol;
using CodeMap.Storage;
using Google.Protobuf;
using Microsoft.Data.Sqlite;
using ProtoIndex = CodeMap.Scip.Protocol.Index;

namespace CodeMap.Core.Tests;

public sealed class ScipImportServiceTests
{
    [Fact]
    public async Task Import_Remove_AndStaleDetection_UseOneProviderLifecycle()
    {
        var root = Path.Combine(Path.GetTempPath(), "codemap-scip-import-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "app.py");
        var artifact = Path.Combine(root, "app.scip");
        const string symbol = "scip-python python pkg 1.0 run().";
        await File.WriteAllTextAsync(source, "def run():\n    return 1\n");
        var index = new ProtoIndex
        {
            Documents =
            {
                new Document
                {
                    RelativePath = "app.py",
                    Language = "python",
                    Symbols = { new SymbolInformation { Symbol = symbol, DisplayName = "run", Kind = 17 } },
                    Occurrences = { new Occurrence { Symbol = symbol, SymbolRoles = 1, SingleLineRange = new SingleLineRange { Line = 0, StartCharacter = 4, EndCharacter = 7 } } }
                }
            }
        };
        await File.WriteAllBytesAsync(artifact, index.ToByteArray());
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(root, force: true);
            var imports = new ScipImportService();
            var summary = await imports.ImportAsync(root, artifact, new ScipImportOptions("python"));
            Assert.Equal("scip:python", summary.Project);

            var database = Path.Combine(root, ".codemap", "index.db");
            var graph = await new CodeMapQueryStore(database).LoadAsync();
            Assert.Contains(new CodeMapQueryService(graph).Find("run", 10), item => item.Id.StartsWith("scip://python/", StringComparison.Ordinal));

            const string nextSymbol = "scip-python python pkg 1.0 next().";
            await File.WriteAllTextAsync(source, "def next():\n    return 2\n");
            var replacement = new ProtoIndex
            {
                Documents =
                {
                    new Document
                    {
                        RelativePath = "app.py",
                        Language = "python",
                        Symbols = { new SymbolInformation { Symbol = nextSymbol, DisplayName = "next", Kind = 17 } },
                        Occurrences = { new Occurrence { Symbol = nextSymbol, SymbolRoles = 1, SingleLineRange = new SingleLineRange { Line = 0, StartCharacter = 4, EndCharacter = 8 } } }
                    }
                }
            };
            await File.WriteAllBytesAsync(artifact, replacement.ToByteArray());
            await imports.ImportAsync(root, artifact, new ScipImportOptions("python"));
            graph = await new CodeMapQueryStore(database).LoadAsync();
            Assert.DoesNotContain(new CodeMapQueryService(graph).Find("run", 10), item => item.Id.StartsWith("scip://python/", StringComparison.Ordinal));
            Assert.Contains(new CodeMapQueryService(graph).Find("next", 10), item => item.Id.StartsWith("scip://python/", StringComparison.Ordinal));

            await new IncrementalCodeMapIndexer().IndexAsync(root, force: true);
            graph = await new CodeMapQueryStore(database).LoadAsync();
            Assert.Contains(new CodeMapQueryService(graph).Find("next", 10), item => item.Id.StartsWith("scip://python/", StringComparison.Ordinal));

            await File.AppendAllTextAsync(source, "# changed\n");
            await Assert.ThrowsAsync<InvalidOperationException>(() => new IncrementalCodeMapIndexer().UpdateAsync(root));

            await imports.RemoveAsync(root, "python");
            graph = await new CodeMapQueryStore(database).LoadAsync();
            Assert.DoesNotContain(new CodeMapQueryService(graph).Find("run", 10), item => item.Id.StartsWith("scip://python/", StringComparison.Ordinal));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }
}
