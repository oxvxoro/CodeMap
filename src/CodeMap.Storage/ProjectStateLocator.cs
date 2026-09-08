using System.Text.Json;

namespace CodeMap.Storage;

public sealed class ProjectStateLocator
{
    public async Task<string?> FindProjectPathAsync(string projectRoot, string projectName, CancellationToken cancellationToken)
    {
        var statePath = Path.Combine(projectRoot, ".codemap", "state.json");
        if (!File.Exists(statePath))
            return null;
        await using var stream = File.OpenRead(statePath);
        var state = await JsonSerializer.DeserializeAsync<StateFile>(stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, cancellationToken);
        var path = state?.Projects?.SingleOrDefault(project => string.Equals(project.ProjectName, projectName, StringComparison.Ordinal))?.ProjectPath;
        if (string.IsNullOrWhiteSpace(path))
            return null;
        var fullPath = Path.IsPathRooted(path) ? Path.GetFullPath(path) : Path.GetFullPath(Path.Combine(projectRoot, path));
        return File.Exists(fullPath) ? fullPath : null;
    }

    private sealed record StateFile(IReadOnlyList<StateProject>? Projects);
    private sealed record StateProject(string ProjectName, string ProjectPath);
}
