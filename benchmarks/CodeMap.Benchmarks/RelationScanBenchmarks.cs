using System.Text;
using BenchmarkDotNet.Attributes;
using CodeMap.Core.Analysis;
using CodeMap.CSharp;

namespace CodeMap.Benchmarks;







[MemoryDiagnoser]
public class RelationScanBenchmarks
{
    private string _source = null!;

    [Params(200, 2000)]
    public int Count { get; set; }

    [GlobalSetup]
    public void Setup() => _source = BuildSource(Count);

    [Benchmark]
    public async Task<int> AnalyzeAsync_InvocationsObjectCreationsAndTypeReferences()
    {
        var analyzer = new CSharpLanguageAnalyzer();
        var result = await analyzer.AnalyzeAsync(new AnalysisContext
        {
            ProjectName = "Fixture",
            FilePath = "Generated.cs",
            Content = _source,
            RootDirectory = Directory.GetCurrentDirectory()
        }, CancellationToken.None);
        return result.Edges.Count;
    }






    private static string BuildSource(int count)
    {
        var sb = new StringBuilder();
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine("namespace Bench;");
        sb.AppendLine("public class Target { public void Method() { } }");
        sb.AppendLine("public class Graph");
        sb.AppendLine("{");
        sb.AppendLine("    public void Run()");
        sb.AppendLine("    {");
        for (var i = 0; i < count; i++)
        {
            sb.AppendLine($"        var t{i} = new Target();");
            sb.AppendLine($"        t{i}.Method();");
            sb.AppendLine($"        List<Target> l{i} = new();");
        }
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }
}
