using BenchmarkDotNet.Attributes;
using CodeMap.Mcp;
using CodeMap.Storage;

namespace CodeMap.Benchmarks;







[MemoryDiagnoser]
public class McpFreshnessBenchmarks
{
    private string _workingDirectory = null!;
    private CodeMapMcpContext _warmContext = null!;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _workingDirectory = await FixtureIndex.CreateAsync();
        _warmContext = new CodeMapMcpContext(_workingDirectory);

        await _warmContext.IsUpToDateAsync(_workingDirectory, CancellationToken.None);
    }

    [GlobalCleanup]
    public void Cleanup() => FixtureIndex.Delete(_workingDirectory);

    [Benchmark(Baseline = true)]
    public Task<bool> UncachedRepeatedCheck() =>
        new IncrementalCodeMapIndexer().IsUpToDateAsync(_workingDirectory, CancellationToken.None);

    [Benchmark]
    public Task<bool> CachedRepeatedCheck() =>
        _warmContext.IsUpToDateAsync(_workingDirectory, CancellationToken.None);
}
