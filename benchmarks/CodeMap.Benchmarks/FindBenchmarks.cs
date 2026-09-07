using BenchmarkDotNet.Attributes;
using CodeMap.Storage;

namespace CodeMap.Benchmarks;






[MemoryDiagnoser]
public class FindBenchmarks
{
    private string _workingDirectory = null!;
    private CodeMapQueryStore _store = null!;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _workingDirectory = await FixtureIndex.CreateAsync();
        _store = new CodeMapQueryStore(Path.Combine(_workingDirectory, ".codemap", "index.db"));
    }

    [GlobalCleanup]
    public void Cleanup() => FixtureIndex.Delete(_workingDirectory);

    [Benchmark]
    public async Task<int> FindExact()
    {
        await using var connection = await _store.OpenReadOnlyConnectionAsync();
        await using var service = new CodeMapQueryService(connection);
        return service.Find("Greeter", 20).Count;
    }

    [Benchmark]
    public async Task<int> FindPrefix()
    {
        await using var connection = await _store.OpenReadOnlyConnectionAsync();
        await using var service = new CodeMapQueryService(connection);
        return service.Find("Fixture.ProjA", 20).Count;
    }

    [Benchmark]
    public async Task<int> FindContains()
    {
        await using var connection = await _store.OpenReadOnlyConnectionAsync();
        await using var service = new CodeMapQueryService(connection);
        return service.Find("eeter", 20).Count;
    }
}
