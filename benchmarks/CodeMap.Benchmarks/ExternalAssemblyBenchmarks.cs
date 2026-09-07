using System.Text;
using BenchmarkDotNet.Attributes;
using CodeMap.CSharp;

namespace CodeMap.Benchmarks;












[MemoryDiagnoser]
public class ExternalAssemblyBenchmarks
{
    private string _directory = null!;

    [Params(1, 20, 100)]
    public int ReachedMemberCount { get; set; }

    [GlobalSetup]
    public void Setup() => _directory = IndexGeneratedSource(BuildSource(ReachedMemberCount));

    [GlobalCleanup]
    public void Cleanup() => FixtureIndex.Delete(_directory);

    [Benchmark]
    public async Task<int> AnalyzeAsync_ExternalAssemblyGraph()
    {
        var indexer = new CSharpWorkspaceIndexer();
        var projects = await indexer.AnalyzeAsync(_directory, null, CancellationToken.None);
        return projects.Where(p => p.ProjectName.StartsWith("external:", StringComparison.Ordinal))
            .Sum(p => p.Result.Nodes.Count);
    }

    private static string IndexGeneratedSource(string source)
    {
        var destination = Path.Combine(Path.GetTempPath(), "codemap-bench-external-" + Guid.NewGuid());
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(destination, "Bench.csproj"), BenchProjectFile);
        File.WriteAllText(Path.Combine(destination, "Graph.cs"), source);
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







    private static string BuildSource(int reachedMemberCount)
    {
        string[] members = ["Add(1)", "Contains(1)", "IndexOf(1)", "Insert(0, 1)", "Remove(1)", "Clear()", "ToArray()", "Sort()", "Reverse()", "TrimExcess()"];
        var sb = new StringBuilder();
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine("namespace Bench;");
        sb.AppendLine("public class Graph");
        sb.AppendLine("{");
        sb.AppendLine("    public void Run()");
        sb.AppendLine("    {");
        for (var i = 0; i < reachedMemberCount; i++)
        {
            sb.AppendLine($"        var list{i} = new List<int>();");
            sb.AppendLine($"        list{i}.{members[i % members.Length]};");
        }
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }
}
