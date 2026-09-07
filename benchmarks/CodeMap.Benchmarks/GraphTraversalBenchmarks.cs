using System.Text;
using BenchmarkDotNet.Attributes;
using CodeMap.Core.Models;
using CodeMap.Storage;

namespace CodeMap.Benchmarks;









[MemoryDiagnoser]
public class GraphTraversalBenchmarks
{
    private string _fanOutDirectory = null!;
    private string _diamondDirectory = null!;
    private CodeMapQueryStore _fanOutStore = null!;
    private CodeMapQueryStore _diamondStore = null!;
    private IndexedSymbol _fanOutRoot = null!;
    private IndexedSymbol _diamondRoot = null!;
    private IndexedSymbol _diamondLeaf = null!;

    [Params(50, 400)]
    public int Width { get; set; }

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _fanOutDirectory = await IndexGeneratedSourceAsync(BuildFanOutSource(Width));
        _fanOutStore = new CodeMapQueryStore(Path.Combine(_fanOutDirectory, ".codemap", "index.db"));
        var fanOutGraph = await _fanOutStore.LoadAsync();
        _fanOutRoot = fanOutGraph.Symbols.Single(s => s.Name == "Root");

        _diamondDirectory = await IndexGeneratedSourceAsync(BuildDiamondSource(Width));
        _diamondStore = new CodeMapQueryStore(Path.Combine(_diamondDirectory, ".codemap", "index.db"));
        var diamondGraph = await _diamondStore.LoadAsync();
        _diamondRoot = diamondGraph.Symbols.Single(s => s.Name == "Root");
        _diamondLeaf = diamondGraph.Symbols.Single(s => s.Name == "Leaf0");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        FixtureIndex.Delete(_fanOutDirectory);
        FixtureIndex.Delete(_diamondDirectory);
    }


    [Benchmark]
    public async Task<int> FanOut_Callees()
    {
        await using var connection = await _fanOutStore.OpenReadOnlyConnectionAsync();
        var graph = new CodeMapSnapshot { Files = [], Symbols = [], Edges = [] };
        await using var service = new CodeMapQueryService(graph, connection);
        return service.Callees(_fanOutRoot, depth: 1, maxResults: Width + 10).Count;
    }


    [Benchmark]
    public async Task<int> Diamond_Callees()
    {
        await using var connection = await _diamondStore.OpenReadOnlyConnectionAsync();
        var graph = new CodeMapSnapshot { Files = [], Symbols = [], Edges = [] };
        await using var service = new CodeMapQueryService(graph, connection);
        return service.Callees(_diamondRoot, depth: 3, maxResults: Width + 10).Count;
    }


    [Benchmark]
    public async Task<int> Diamond_Impact()
    {
        await using var connection = await _diamondStore.OpenReadOnlyConnectionAsync();
        var graph = new CodeMapSnapshot { Files = [], Symbols = [], Edges = [] };
        await using var service = new CodeMapQueryService(graph, connection);
        return service.Impact(_diamondLeaf, depth: 3, maxResults: Width + 10).Count;
    }

    private static async Task<string> IndexGeneratedSourceAsync(string source)
    {
        var destination = Path.Combine(Path.GetTempPath(), "codemap-bench-graph-" + Guid.NewGuid());
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


    private static string BuildFanOutSource(int leafCount)
    {
        var sb = new StringBuilder();
        sb.AppendLine("namespace Bench;");
        sb.AppendLine("public class Graph");
        sb.AppendLine("{");
        for (var i = 0; i < leafCount; i++)
            sb.AppendLine($"    public void Leaf{i}() {{ }}");
        sb.AppendLine("    public void Root()");
        sb.AppendLine("    {");
        for (var i = 0; i < leafCount; i++)
            sb.AppendLine($"        Leaf{i}();");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }







    private static string BuildDiamondSource(int width)
    {
        var layer1Count = Math.Max(2, width / 10);
        var layer2Count = Math.Max(2, width / 20);
        var leafCount = width;

        var sb = new StringBuilder();
        sb.AppendLine("namespace Bench;");
        sb.AppendLine("public class Graph");
        sb.AppendLine("{");
        for (var i = 0; i < leafCount; i++)
            sb.AppendLine($"    public void Leaf{i}() {{ }}");
        for (var j = 0; j < layer2Count; j++)
        {
            sb.AppendLine($"    public void L2_{j}()");
            sb.AppendLine("    {");
            for (var i = 0; i < leafCount; i++)
            {
                if (i % layer2Count == j || (i + 1) % layer2Count == j)
                    sb.AppendLine($"        Leaf{i}();");
            }
            sb.AppendLine("    }");
        }
        for (var i = 0; i < layer1Count; i++)
        {
            sb.AppendLine($"    public void L1_{i}()");
            sb.AppendLine("    {");
            for (var j = 0; j < layer2Count; j++)
                sb.AppendLine($"        L2_{j}();");
            sb.AppendLine("    }");
        }
        sb.AppendLine("    public void Root()");
        sb.AppendLine("    {");
        for (var i = 0; i < layer1Count; i++)
            sb.AppendLine($"        L1_{i}();");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }
}
