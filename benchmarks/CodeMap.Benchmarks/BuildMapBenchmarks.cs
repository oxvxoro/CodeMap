using System.Text;
using BenchmarkDotNet.Attributes;
using CodeMap.Storage;

namespace CodeMap.Benchmarks;









[MemoryDiagnoser]
public class BuildMapBenchmarks
{
    private string _directory = null!;
    private CodeMapQueryStore _store = null!;

    [Params(50, 500)]
    public int SymbolCount { get; set; }

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _directory = await IndexGeneratedSourceAsync(BuildSource(SymbolCount));
        _store = new CodeMapQueryStore(Path.Combine(_directory, ".codemap", "index.db"));
    }

    [GlobalCleanup]
    public void Cleanup() => FixtureIndex.Delete(_directory);

    [Benchmark]
    public async Task<int> BuildMap_Unscoped_LargeBudget()
    {
        await using var connection = await _store.OpenReadOnlyConnectionAsync();
        await using var service = new CodeMapQueryService(connection);
        return service.BuildMap(focus: null, project: null, tokenBudget: 20_000).Lines.Count;
    }

    [Benchmark]
    public async Task<int> BuildMap_Unscoped_SmallBudget()
    {
        await using var connection = await _store.OpenReadOnlyConnectionAsync();
        await using var service = new CodeMapQueryService(connection);
        return service.BuildMap(focus: null, project: null, tokenBudget: 200).Lines.Count;
    }

    [Benchmark]
    public async Task<int> BuildMap_ProjectScoped()
    {
        await using var connection = await _store.OpenReadOnlyConnectionAsync();
        await using var service = new CodeMapQueryService(connection);
        return service.BuildMap(focus: null, project: "Bench", tokenBudget: 20_000).Lines.Count;
    }

    [Benchmark]
    public async Task<int> BuildMap_Focused()
    {
        await using var connection = await _store.OpenReadOnlyConnectionAsync();
        await using var service = new CodeMapQueryService(connection);
        return service.BuildMap(focus: "Node0", project: null, tokenBudget: 20_000).Lines.Count;
    }

    private static async Task<string> IndexGeneratedSourceAsync(string source)
    {
        var destination = Path.Combine(Path.GetTempPath(), "codemap-bench-buildmap-" + Guid.NewGuid());
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






    private static string BuildSource(int symbolCount)
    {
        var sb = new StringBuilder();
        sb.AppendLine("namespace Bench;");
        for (var i = 0; i < symbolCount; i++)
        {
            sb.AppendLine($"public class Node{i}");
            sb.AppendLine("{");
            sb.AppendLine($"    public void Run() {{ {(i + 1 < symbolCount ? $"new Node{i + 1}().Run();" : "")} }}");
            sb.AppendLine("}");
        }
        return sb.ToString();
    }
}
