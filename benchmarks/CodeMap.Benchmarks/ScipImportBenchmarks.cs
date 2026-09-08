using BenchmarkDotNet.Attributes;
using CodeMap.Scip;
using CodeMap.Scip.Protocol;
using Google.Protobuf;

namespace CodeMap.Benchmarks;

[MemoryDiagnoser]
public class ScipImportBenchmarks
{
    private readonly ScipIndexReader _reader = new();
    private readonly ScipGraphMapper _mapper = new();
    private byte[] _artifact = null!;
    private ScipIndex _index = null!;
    private string _repositoryRoot = null!;

    [Params(1_000, 10_000, 100_000)]
    public int SymbolCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _repositoryRoot = Path.Combine(Path.GetTempPath(), "codemap-scip-benchmark-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_repositoryRoot);
        File.WriteAllText(Path.Combine(_repositoryRoot, "fixture.cs"), "class Fixture { }\n");
        var index = new CodeMap.Scip.Protocol.Index();
        var document = new Document { RelativePath = "fixture.cs", Language = "C#" };
        for (var symbolIndex = 0; symbolIndex < SymbolCount; symbolIndex++)
        {
            var symbol = $"scip . Fixture#{symbolIndex}().";
            document.Occurrences.Add(new Occurrence { Symbol = symbol, SymbolRoles = 1, SingleLineRange = new SingleLineRange { Line = 0, StartCharacter = 0, EndCharacter = 1 } });
            document.Symbols.Add(new SymbolInformation { Symbol = symbol, DisplayName = $"M{symbolIndex}", Kind = 26 });
        }
        index.Documents.Add(document);
        _artifact = index.ToByteArray();
        _index = _reader.Read(_artifact);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_repositoryRoot))
            Directory.Delete(_repositoryRoot, recursive: true);
    }

    [Benchmark]
    public ScipIndex Parse() => _reader.Read(_artifact);

    [Benchmark]
    public int Map() => _mapper.Map("benchmark", _repositoryRoot, _index).Result.Nodes.Count;
}
