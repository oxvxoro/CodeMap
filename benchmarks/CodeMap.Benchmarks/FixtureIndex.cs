using CodeMap.Storage;

namespace CodeMap.Benchmarks;


internal static class FixtureIndex
{
    internal static async Task<string> CreateAsync()
    {
        var source = FindFixtureDirectory();
        var destination = Path.Combine(Path.GetTempPath(), "codemap-bench-" + Guid.NewGuid());
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*.*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var targetPath = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.Copy(file, targetPath, overwrite: true);
        }
        await new IncrementalCodeMapIndexer().IndexAsync(destination, force: true, CancellationToken.None);
        return destination;
    }

    internal static void Delete(string workingDirectory)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(workingDirectory))
            Directory.Delete(workingDirectory, recursive: true);
    }

    private static string FindFixtureDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "tests", "Fixtures", "MultiProject");
            if (Directory.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate tests/Fixtures/MultiProject from " + AppContext.BaseDirectory);
    }
}
