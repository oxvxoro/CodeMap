using BenchmarkDotNet.Attributes;
using CodeMap.CSharp;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CodeMap.Benchmarks;

[MemoryDiagnoser]
public class SemanticSliceBenchmarks
{
    private readonly CSharpSemanticSliceAnalyzer _analyzer = new();
    private SemanticModel _model = null!;
    private IMethodSymbol _method = null!;

    [Params(50, 500)]
    public int OperationCount { get; set; }

    [Params("linear", "branch", "loop")]
    public string Workload { get; set; } = "linear";

    [GlobalSetup]
    public void Setup()
    {
        var assignments = Workload switch
        {
            "branch" => string.Join(Environment.NewLine, Enumerable.Range(1, OperationCount)
                .Select(index => $"if (value >= {index}) value = value + {index};")),
            "loop" => $"for (var i = 0; i < {OperationCount}; i++) value = value + i;",
            _ => string.Join(Environment.NewLine, Enumerable.Range(1, OperationCount)
                .Select(index => $"value = value + {index};"))
        };
        var tree = CSharpSyntaxTree.ParseText($"class Fixture {{ int Compute(int value) {{ {assignments} return value; }} }}");
        var compilation = CSharpCompilation.Create("SemanticSliceBenchmark", [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)], new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        _model = compilation.GetSemanticModel(tree);
        _method = _model.GetDeclaredSymbol(tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single())!;
    }

    [Benchmark]
    public SemanticSliceAnalysis BackwardSlice() =>
        _analyzer.Analyze(_model, _method, new SemanticSliceRequest(SliceDirection.Backward), CancellationToken.None);
}
