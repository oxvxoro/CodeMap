namespace CodeMap.Engine.Indexing;

public enum RepositoryInputKind
{
    Directory,
    Solution,
    Project
}

public sealed record ResolvedRepositoryInput(string Root, string? SolutionOrProjectPath, RepositoryInputKind Kind);

public sealed class RepositoryInputResolver
{
    public ResolvedRepositoryInput Resolve(string? inputPath)
    {
        var path = Path.GetFullPath(string.IsNullOrWhiteSpace(inputPath) ? Directory.GetCurrentDirectory() : inputPath);
        if (Directory.Exists(path))
        {
            var input = Directory.EnumerateFiles(path, "*.*", SearchOption.TopDirectoryOnly)
                .Where(file => Path.GetExtension(file) is ".sln" or ".slnx" or ".csproj")
                .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            return new ResolvedRepositoryInput(path, input, input is null
                ? RepositoryInputKind.Directory
                : Path.GetExtension(input).Equals(".csproj", StringComparison.OrdinalIgnoreCase)
                    ? RepositoryInputKind.Project
                    : RepositoryInputKind.Solution);
        }
        if (!File.Exists(path))
            throw new DirectoryNotFoundException(path);
        var extension = Path.GetExtension(path);
        var kind = extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase)
            ? RepositoryInputKind.Project
            : extension is ".sln" or ".slnx"
                ? RepositoryInputKind.Solution
                : throw new ArgumentException("The input must be a repository directory, solution, or project file.", nameof(inputPath));
        return new ResolvedRepositoryInput(Path.GetDirectoryName(path)!, path, kind);
    }
}
