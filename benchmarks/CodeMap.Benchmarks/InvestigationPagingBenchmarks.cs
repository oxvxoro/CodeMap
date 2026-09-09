using System.Text;
using BenchmarkDotNet.Attributes;
using CodeMap.Core.Models;
using CodeMap.Storage;

namespace CodeMap.Benchmarks;

[MemoryDiagnoser]
public sealed class InvestigationPagingBenchmarks
{
    private string _directory = null!;
    private CodeMapQueryStore _store = null!;
    private IndexedSymbol _root = null!;

    [Params(10, 100, 1000)]
    public int FanIn { get; set; }

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _directory = await CreateIndexAsync(BuildSource(FanIn));
        _store = new CodeMapQueryStore(Path.Combine(_directory, ".codemap", "index.db"));
        var graph = await _store.LoadAsync();
        _root = graph.Symbols.Single(symbol => symbol.Name == "Target");
    }

    [GlobalCleanup]
    public void Cleanup() => FixtureIndex.Delete(_directory);

    [Benchmark(Baseline = true)]
    public async Task<int> CollectAllCallers()
    {
        await using var connection = await _store.OpenReadOnlyConnectionAsync();
        await using var service = new CodeMapQueryService(
            new CodeMapSnapshot { Files = [], Symbols = [], Edges = [] }, connection);
        return service.Callers(_root, FanIn + 1).Count;
    }

    [Benchmark]
    public async Task<int> BudgetBoundedCallerPage()
    {
        await using var connection = await _store.OpenReadOnlyConnectionAsync();
        await using var service = new CodeMapQueryService(
            new CodeMapSnapshot { Files = [], Symbols = [], Edges = [] }, connection);
        return service.CallerRelationsPaged(_root, limit: 8, offset: 0).Items.Count;
    }

    private static async Task<string> CreateIndexAsync(string source)
    {
        var directory = Path.Combine(Path.GetTempPath(), "codemap-bench-investigation-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "Bench.csproj"), ProjectFile);
        await File.WriteAllTextAsync(Path.Combine(directory, "Graph.cs"), source);
        await new IncrementalCodeMapIndexer().IndexAsync(directory, force: true, CancellationToken.None);
        return directory;
    }

    private static string BuildSource(int fanIn)
    {
        var builder = new StringBuilder();
        builder.AppendLine("namespace Bench;");
        builder.AppendLine("public class Graph");
        builder.AppendLine("{");
        builder.AppendLine("    public void Target() { }");
        for (var i = 0; i < fanIn; i++)
            builder.AppendLine($"    public void Caller{i}() => Target();");
        builder.AppendLine("}");
        return builder.ToString();
    }

    private const string ProjectFile = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <Nullable>enable</Nullable>
          </PropertyGroup>
        </Project>
        """;
}
