namespace CodeMap.Engine.Indexing;

public interface IIndexStateStore
{
    Task<string?> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(string content, CancellationToken cancellationToken = default);
}

public sealed class FileIndexStateStore(string statePath) : IIndexStateStore
{
    public string StatePath { get; } = Path.GetFullPath(statePath);

    public async Task<string?> LoadAsync(CancellationToken cancellationToken = default) =>
        File.Exists(StatePath) ? await File.ReadAllTextAsync(StatePath, cancellationToken) : null;

    public async Task SaveAsync(string content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        var temporaryPath = StatePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporaryPath, content, cancellationToken);
            File.Move(temporaryPath, StatePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }
}
