namespace CodeMap.Storage;

public static class CodeMapIndexLocator
{
    public static string FindDatabase(string? root)
    {
        var start = string.IsNullOrWhiteSpace(root)
            ? Directory.GetCurrentDirectory()
            : Path.GetFullPath(root);
        if (File.Exists(start))
            start = Path.GetDirectoryName(start)!;

        var directory = new DirectoryInfo(start);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, ".codemap", "index.db");
            if (File.Exists(candidate))
                return candidate;
            directory = directory.Parent!;
        }

        throw new FileNotFoundException("No CodeMap index found.\nRun: codemap index");
    }

    public static string ResolveIndexRoot(string databasePath) =>
        Path.GetDirectoryName(Path.GetDirectoryName(databasePath))!;

    public static async Task<bool> IsStaleAsync(
        string databasePath,
        CancellationToken cancellationToken,
        Func<string, CancellationToken, Task<bool>>? freshnessProbe = null)
    {
        try
        {
            var projectRoot = ResolveIndexRoot(databasePath);
            var upToDate = freshnessProbe is null
                ? await new IncrementalCodeMapIndexer().IsUpToDateAsync(projectRoot, cancellationToken)
                : await freshnessProbe(projectRoot, cancellationToken);
            return !upToDate;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return true;
        }
        catch (InvalidOperationException exception) when (exception.Message.StartsWith("Multiple solutions found", StringComparison.Ordinal))
        {
            return true;
        }
    }
}
