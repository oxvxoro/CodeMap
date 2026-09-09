namespace CodeMap.Engine.Indexing;

public sealed record RepositoryChangeSet(
    IReadOnlySet<string> DirtyProjects,
    IReadOnlySet<string> RemovedProjects,
    int AddedFiles,
    int UpdatedFiles,
    int RemovedFiles)
{
    public static RepositoryChangeSet Empty { get; } = new(
        new HashSet<string>(StringComparer.Ordinal),
        new HashSet<string>(StringComparer.Ordinal), 0, 0, 0);
}

/// <summary>Pure file/project change calculation; it never invokes an analyzer.</summary>
public static class ChangeDetector
{
    public static RepositoryChangeSet Detect(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> previous,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> current)
    {
        var dirty = new HashSet<string>(StringComparer.Ordinal);
        var removed = previous.Keys.Except(current.Keys, StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        var addedFiles = 0;
        var updatedFiles = 0;
        var removedFiles = 0;

        foreach (var project in current)
        {
            if (!previous.TryGetValue(project.Key, out var oldFiles))
            {
                dirty.Add(project.Key);
                addedFiles += project.Value.Count;
                continue;
            }
            foreach (var file in project.Value)
            {
                if (!oldFiles.TryGetValue(file.Key, out var oldHash))
                {
                    addedFiles++;
                    dirty.Add(project.Key);
                }
                else if (!string.Equals(oldHash, file.Value, StringComparison.Ordinal))
                {
                    updatedFiles++;
                    dirty.Add(project.Key);
                }
            }
            foreach (var file in oldFiles.Keys)
                if (!project.Value.ContainsKey(file))
                {
                    removedFiles++;
                    dirty.Add(project.Key);
                }
        }

        return new RepositoryChangeSet(dirty, removed, addedFiles, updatedFiles, removedFiles);
    }
}
