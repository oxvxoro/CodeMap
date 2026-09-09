using CodeMap.Storage;
using Microsoft.Data.Sqlite;

namespace CodeMap.Core.Tests.Contracts;

internal sealed class QueryParityFixture : IAsyncDisposable
{
    private QueryParityFixture(
        string workingDirectory,
        CodeMapSnapshot snapshot,
        CodeMapQueryService snapshotService,
        CodeMapQueryService sqlService)
    {
        WorkingDirectory = workingDirectory;
        Snapshot = snapshot;
        SnapshotService = snapshotService;
        SqlService = sqlService;
    }

    public string WorkingDirectory { get; }

    public CodeMapSnapshot Snapshot { get; }

    public CodeMapQueryService SnapshotService { get; }

    public CodeMapQueryService SqlService { get; }

    public static async Task<QueryParityFixture> CreateAsync(string fixtureName)
    {
        var solutionRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var sourceDirectory = Path.Combine(solutionRoot, "tests", "Fixtures", fixtureName);
        if (!Directory.Exists(sourceDirectory))
            throw new DirectoryNotFoundException($"Fixture '{fixtureName}' was not found at '{sourceDirectory}'.");

        var workingDirectory = Path.Combine(Path.GetTempPath(), "codemap-query-parity-" + Guid.NewGuid());
        CopyDirectory(sourceDirectory, workingDirectory);
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);
            var store = new CodeMapQueryStore(Path.Combine(workingDirectory, ".codemap", "index.db"));
            var snapshot = await store.LoadAsync();
            var sqlService = new CodeMapQueryService(await store.OpenReadOnlyConnectionAsync());
            return new QueryParityFixture(workingDirectory, snapshot, new CodeMapQueryService(snapshot), sqlService);
        }
        catch
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(workingDirectory))
                Directory.Delete(workingDirectory, recursive: true);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await SnapshotService.DisposeAsync();
        await SqlService.DisposeAsync();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(WorkingDirectory))
            Directory.Delete(WorkingDirectory, recursive: true);
    }

    private static void CopyDirectory(string sourceDirectory, string destinationDirectory)
    {
        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, file);
            var destinationPath = Path.Combine(destinationDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(file, destinationPath, overwrite: true);
        }
    }
}
