using BenchmarkDotNet.Attributes;
using CodeMap.Core.Models;
using CodeMap.Storage;

namespace CodeMap.Benchmarks;


[MemoryDiagnoser]
public class IncrementalUpdateBenchmarks
{
    private string _workingDirectory = null!;

    [GlobalSetup]
    public async Task SetupAsync() => _workingDirectory = await FixtureIndex.CreateAsync();

    [GlobalCleanup]
    public void Cleanup() => FixtureIndex.Delete(_workingDirectory);

    [Benchmark]
    public async Task<IndexSummary> NoOpUpdate() =>
        await new IncrementalCodeMapIndexer().UpdateAsync(_workingDirectory, CancellationToken.None);
}


[MemoryDiagnoser]
public class ChangedProjectUpdateBenchmarks
{
    private string _workingDirectory = null!;
    private string _sourcePath = null!;
    private string _projectPath = null!;
    private string _originalSource = null!;
    private string _originalProject = null!;

    [Params(DirtyUpdateKind.SourceFile, DirtyUpdateKind.ProjectFile)]
    public DirtyUpdateKind DirtyUpdate { get; set; }

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _workingDirectory = await FixtureIndex.CreateAsync();
        _sourcePath = Path.Combine(_workingDirectory, "ProjA", "Caller.cs");
        _projectPath = Path.Combine(_workingDirectory, "ProjA", "ProjA.csproj");
        _originalSource = await File.ReadAllTextAsync(_sourcePath);
        _originalProject = await File.ReadAllTextAsync(_projectPath);
    }

    [GlobalCleanup]
    public void Cleanup() => FixtureIndex.Delete(_workingDirectory);






    [IterationSetup(Target = nameof(ChangedProjectUpdate))]
    public async Task PrepareChangedProjectUpdateAsync()
    {
        await File.WriteAllTextAsync(_sourcePath, _originalSource);
        await File.WriteAllTextAsync(_projectPath, _originalProject);
        await new IncrementalCodeMapIndexer().UpdateAsync(_workingDirectory, CancellationToken.None);

        if (DirtyUpdate == DirtyUpdateKind.SourceFile)
            await File.AppendAllTextAsync(_sourcePath, Environment.NewLine + "// benchmark change");
        else
            await File.AppendAllTextAsync(_projectPath, Environment.NewLine + "<!-- 벤치마크 변경 -->");
    }

    [Benchmark]
    public async Task<IndexSummary> ChangedProjectUpdate() =>
        await new IncrementalCodeMapIndexer().UpdateAsync(_workingDirectory, CancellationToken.None);

    public enum DirtyUpdateKind
    {
        SourceFile,
        ProjectFile
    }
}
