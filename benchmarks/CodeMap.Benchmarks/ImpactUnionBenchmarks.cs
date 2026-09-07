using System.Text;
using BenchmarkDotNet.Attributes;
using CodeMap.Core.Models;
using CodeMap.Storage;

namespace CodeMap.Benchmarks;











[MemoryDiagnoser]
public class ImpactUnionBenchmarks
{
    private string _directory = null!;
    private CodeMapQueryStore _store = null!;
    private IndexedSymbol[] _roots = null!;

    [Params(1, 10, 50, 200)]
    public int RootCount { get; set; }

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _directory = await IndexGeneratedSourceAsync(BuildSource(RootCount));
        _store = new CodeMapQueryStore(Path.Combine(_directory, ".codemap", "index.db"));
        var graph = await _store.LoadAsync();
        _roots = Enumerable.Range(0, RootCount)
            .Select(i => graph.Symbols.Single(s => s.Name == $"Root{i}"))
            .ToArray();
    }

    [GlobalCleanup]
    public void Cleanup() => FixtureIndex.Delete(_directory);

    [Benchmark]
    public async Task<int> ImpactUnion_AcrossRoots()
    {
        await using var connection = await _store.OpenReadOnlyConnectionAsync();
        var graph = new CodeMapSnapshot { Files = [], Symbols = [], Edges = [] };
        await using var service = new CodeMapQueryService(graph, connection);
        return service.ImpactUnion(_roots, depth: 3, maxResults: RootCount + 50).Count;
    }

    private static async Task<string> IndexGeneratedSourceAsync(string source)
    {
        var destination = Path.Combine(Path.GetTempPath(), "codemap-bench-impactunion-" + Guid.NewGuid());
        Directory.CreateDirectory(destination);
        await File.WriteAllTextAsync(Path.Combine(destination, "Bench.csproj"), BenchProjectFile);
        await File.WriteAllTextAsync(Path.Combine(destination, "Graph.cs"), source);
        await new IncrementalCodeMapIndexer().IndexAsync(destination, force: true, CancellationToken.None);
        return destination;
    }

    private const string BenchProjectFile = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <Nullable>enable</Nullable>
          </PropertyGroup>
        </Project>
        """;








    private static string BuildSource(int rootCount)
    {
        const int commonCount = 5;
        var sb = new StringBuilder();
        sb.AppendLine("namespace Bench;");
        sb.AppendLine("public class Graph");
        sb.AppendLine("{");
        for (var i = 0; i < rootCount; i++)
            sb.AppendLine($"    public void Root{i}() {{ }}");
        for (var c = 0; c < commonCount; c++)
        {
            sb.AppendLine($"    public void Common{c}()");
            sb.AppendLine("    {");
            for (var i = 0; i < rootCount; i++)
                sb.AppendLine($"        Root{i}();");
            sb.AppendLine("    }");
        }
        sb.AppendLine("    public void Entry()");
        sb.AppendLine("    {");
        for (var c = 0; c < commonCount; c++)
            sb.AppendLine($"        Common{c}();");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }
}
