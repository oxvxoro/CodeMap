using System.Text.Json;
using CodeMap.Core;
using CodeMap.Core.Models;
using CodeMap.CSharp;
using CodeMap.Web;
using Microsoft.Data.Sqlite;

namespace CodeMap.Storage;

public sealed partial class IncrementalCodeMapIndexer
{
    private const int IndexFormatVersion = 4;

    private static readonly JsonSerializerOptions StateJsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    private sealed record IndexStateFileEntry(string RelativePath, string Hash);









    private sealed record IndexStateExternalAssembly(string ExternalProjectName, string AssemblyPath, string AssemblyHash);

    private sealed record IndexStateProject(
        string ProjectName,
        string ProjectPath,
        string? ProjectFileHash,
        IReadOnlyList<IndexStateFileEntry> Files,
        IReadOnlyList<string>? ReferencedProjects = null,








        string? PublicSurfaceFingerprint = null,




        IReadOnlyList<IndexStateExternalAssembly>? ExternalAssemblies = null,
        string? ProviderKind = null,
        string? ProviderVersion = null,
        string? ProviderInputPath = null,
        string? ProviderInputHash = null);

    private sealed record IndexStateFile(
        string? SchemaVersion,
        int IndexFormatVersion,
        IReadOnlyDictionary<string, string>? AnalyzerVersions,
        string ConfigHash,
        string ToolVersion,
        DateTimeOffset IndexedAtUtc,
        string? ResolvedInputPath,
        string? ResolvedInputHash,
        IReadOnlyList<IndexStateProject> Projects);

    private sealed record ProjectChangeSummary(
        int Added,
        int Updated,
        int Removed,
        int Skipped,
        HashSet<string> DirtyProjects,
        HashSet<string> RemovedProjects);

    private static async Task WriteStateAsync(
        string root,
        string? resolved,
        IReadOnlyList<AnalyzedProject> projects,
        IndexStateFile? previousState,
        IReadOnlyCollection<string>? removedProjects,
        IReadOnlyDictionary<string, string[]> projectReferences,
        IReadOnlyDictionary<string, string?> publicSurfaceFingerprints,
        IReadOnlyDictionary<string, IReadOnlyList<ExternalAssemblyReference>> externalAssembliesByOwningProject,
        CancellationToken cancellationToken)
    {
        var statePath = Path.Combine(root, ".codemap", "state.json");
        Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
        string? resolvedInputHash = null;
        if (resolved is not null && File.Exists(resolved))
            resolvedInputHash = SqliteCodeMapStore.ComputeContentHash(resolved, await File.ReadAllTextAsync(resolved, cancellationToken));

        var previousProjectsByName = previousState?.Projects.ToDictionary(project => project.ProjectName, StringComparer.Ordinal)
            ?? new Dictionary<string, IndexStateProject>(StringComparer.Ordinal);
        var stateProjectsByName = new Dictionary<string, IndexStateProject>(previousProjectsByName, StringComparer.Ordinal);
        if (removedProjects is not null)
        {
            foreach (var removedProject in removedProjects)
                stateProjectsByName.Remove(removedProject);
        }







        var externalProjectNames = new HashSet<string>(
            projects.Select(project => project.ProjectName).Where(SqliteCodeMapStore.IsExternalProject),
            StringComparer.Ordinal);

        foreach (var project in projects)
        {
            if (externalProjectNames.Contains(project.ProjectName))
                continue;
            string? projectFileHash = null;
            if (File.Exists(project.ProjectPath))
                projectFileHash = SqliteCodeMapStore.ComputeContentHash(project.ProjectPath, await File.ReadAllTextAsync(project.ProjectPath, cancellationToken));
            var files = project.Files.Select(file => new IndexStateFileEntry(file.RelativePath, SqliteCodeMapStore.ComputeContentHash(file.RelativePath, file.Content))).ToArray();
            IReadOnlyList<string>? referencedProjects = null;
            var previousProject = previousProjectsByName.GetValueOrDefault(project.ProjectName);
            if (projectReferences.TryGetValue(project.ProjectName, out var references))
                referencedProjects = references;
            else if (previousProject is not null)
                referencedProjects = previousProject.ReferencedProjects;





            var fingerprint = publicSurfaceFingerprints.GetValueOrDefault(project.ProjectName);





            var externalAssemblies = externalAssembliesByOwningProject.TryGetValue(project.ProjectName, out var owned)
                ? owned.Select(reference => new IndexStateExternalAssembly(reference.ExternalProjectName, reference.AssemblyPath, reference.AssemblyHash)).ToArray()
                : previousProject?.ExternalAssemblies;
            var providerKind = ProviderKindOf(project.ProjectName);
            var providerVersion = providerKind == "web" ? WebLanguageAnalyzer.AnalyzerVersion : CSharpLanguageAnalyzer.AnalyzerVersion;
            stateProjectsByName[project.ProjectName] = new IndexStateProject(
                project.ProjectName, project.ProjectPath, projectFileHash, files, referencedProjects, fingerprint, externalAssemblies,
                providerKind, providerVersion, Path.GetFullPath(project.ProjectPath), projectFileHash);
        }

        var mergedProjects = stateProjectsByName.Values.ToList();
        var analyzerVersions = mergedProjects
            .SelectMany(project => project.Files.Select(file => GuessAnalyzerLanguage(file.RelativePath)))
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .Where(SqliteCodeMapStore.CurrentAnalyzerVersions.ContainsKey)
            .ToDictionary(language => language, language => SqliteCodeMapStore.CurrentAnalyzerVersions[language], StringComparer.Ordinal);
        var state = new IndexStateFile(SqliteCodeMapStore.SchemaVersion, IndexFormatVersion, analyzerVersions, SqliteCodeMapStore.ConfigHash, SqliteCodeMapStore.ToolVersion,
            DateTimeOffset.UtcNow, resolved is null ? null : Path.GetFullPath(resolved), resolvedInputHash, mergedProjects);
        await WriteStateFileAtomicallyAsync(statePath, JsonSerializer.Serialize(state, StateJsonOptions), cancellationToken);
    }

    private static string ProviderKindOf(string projectName) =>
        projectName.StartsWith("web:", StringComparison.Ordinal) ? "web" : "csharp";

    internal static async Task WriteScipProviderStateAsync(
        string root,
        AnalyzedProject project,
        string artifactPath,
        string artifactHash,
        CancellationToken cancellationToken)
    {
        var state = await TryLoadStateAsync(root, cancellationToken)
            ?? throw new InvalidOperationException("A CodeMap index must exist before importing SCIP data.");
        var projects = state.Projects.ToDictionary(item => item.ProjectName, StringComparer.Ordinal);
        projects[project.ProjectName] = new IndexStateProject(
            project.ProjectName,
            Path.GetFullPath(root),
            null,
            project.Files.Select(file => new IndexStateFileEntry(file.RelativePath, SqliteCodeMapStore.ComputeContentHash(file.RelativePath, file.Content))).ToArray(),
            ProviderKind: "scip",
            ProviderVersion: Scip.ScipSymbolMapper.AnalyzerVersion,
            ProviderInputPath: Path.GetFullPath(artifactPath),
            ProviderInputHash: artifactHash);
        var updated = state with { IndexedAtUtc = DateTimeOffset.UtcNow, Projects = projects.Values.OrderBy(item => item.ProjectName, StringComparer.Ordinal).ToArray() };
        var statePath = Path.Combine(root, ".codemap", "state.json");
        await WriteStateFileAtomicallyAsync(statePath, JsonSerializer.Serialize(updated, StateJsonOptions), cancellationToken);
    }

    internal static async Task RemoveScipProviderStateAsync(string root, string projectName, CancellationToken cancellationToken)
    {
        var state = await TryLoadStateAsync(root, cancellationToken)
            ?? throw new InvalidOperationException("A CodeMap index state must exist before removing SCIP data.");
        var project = state.Projects.FirstOrDefault(item => string.Equals(item.ProjectName, projectName, StringComparison.Ordinal));
        if (project is null || !string.Equals(project.ProviderKind, "scip", StringComparison.Ordinal))
            throw new InvalidOperationException($"SCIP provider '{projectName}' was not found.");
        var updated = state with
        {
            IndexedAtUtc = DateTimeOffset.UtcNow,
            Projects = state.Projects.Where(item => !string.Equals(item.ProjectName, projectName, StringComparison.Ordinal)).ToArray()
        };
        var statePath = Path.Combine(root, ".codemap", "state.json");
        await WriteStateFileAtomicallyAsync(statePath, JsonSerializer.Serialize(updated, StateJsonOptions), cancellationToken);
    }

    internal static async Task<bool> IsScipProviderRecordedAsync(string root, string projectName, CancellationToken cancellationToken)
    {
        var state = await TryLoadStateAsync(root, cancellationToken);
        return state?.Projects.Any(item => string.Equals(item.ProjectName, projectName, StringComparison.Ordinal)
            && string.Equals(item.ProviderKind, "scip", StringComparison.Ordinal)) == true;
    }

    internal static async Task<IReadOnlyList<ScipProviderInfo>> ListScipProvidersAsync(string root, CancellationToken cancellationToken)
    {
        var state = await TryLoadStateAsync(root, cancellationToken);
        if (state is null)
            return Array.Empty<ScipProviderInfo>();
        var result = new List<ScipProviderInfo>();
        foreach (var project in state.Projects.Where(item => string.Equals(item.ProviderKind, "scip", StringComparison.Ordinal)).OrderBy(item => item.ProjectName, StringComparer.Ordinal))
        {
            var name = project.ProjectName.StartsWith("scip:", StringComparison.Ordinal) ? project.ProjectName[5..] : project.ProjectName;
            result.Add(new ScipProviderInfo(name, project.ProjectName, project.ProviderInputPath ?? string.Empty,
                project.ProviderInputHash ?? string.Empty, project.ProviderVersion ?? string.Empty, project.Files.Count,
                await IsScipProjectUpToDateAsync(project, cancellationToken)));
        }
        return result;
    }











    private static IReadOnlyList<string> ComputeOrphanedExternalProjects(
        IndexStateFile previousState,
        IReadOnlyCollection<string> removedProjects,
        IReadOnlyList<AnalyzedProject> analyzedProjects,
        IReadOnlyDictionary<string, IReadOnlyList<ExternalAssemblyReference>> externalAssembliesByOwningProject)
    {
        var previouslyReferenced = previousState.Projects
            .SelectMany(project => project.ExternalAssemblies ?? Array.Empty<IndexStateExternalAssembly>())
            .Select(external => external.ExternalProjectName)
            .ToHashSet(StringComparer.Ordinal);
        if (previouslyReferenced.Count == 0)
            return Array.Empty<string>();

        var previousProjectNames = previousState.Projects
            .Select(project => project.ProjectName)
            .ToHashSet(StringComparer.Ordinal);
        var removedSet = new HashSet<string>(removedProjects, StringComparer.Ordinal);
        var analyzedExternalNames = new HashSet<string>(
            analyzedProjects.Select(project => project.ProjectName).Where(SqliteCodeMapStore.IsExternalProject),
            StringComparer.Ordinal);

        var stillReferenced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var project in previousState.Projects)
        {
            if (removedSet.Contains(project.ProjectName))
                continue;
            var externalAssemblies = externalAssembliesByOwningProject.TryGetValue(project.ProjectName, out var owned)
                ? owned.Select(reference => reference.ExternalProjectName)
                : (project.ExternalAssemblies ?? Array.Empty<IndexStateExternalAssembly>()).Select(external => external.ExternalProjectName);
            stillReferenced.UnionWith(externalAssemblies);
        }


        foreach (var (owningProject, references) in externalAssembliesByOwningProject)
        {
            if (previousProjectNames.Contains(owningProject))
                continue;
            foreach (var reference in references)
                stillReferenced.Add(reference.ExternalProjectName);
        }

        return previouslyReferenced
            .Except(stillReferenced, StringComparer.Ordinal)


            .Except(analyzedExternalNames, StringComparer.Ordinal)
            .ToArray();
    }









    private static async Task WriteStateFileAtomicallyAsync(string statePath, string content, CancellationToken cancellationToken)
    {
        var tempPath = statePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(tempPath, content, cancellationToken);
            if (File.Exists(statePath))
                File.Replace(tempPath, statePath, null);
            else
                File.Move(tempPath, statePath);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static string? GuessAnalyzerLanguage(string relativePath)
    {
        var extension = Path.GetExtension(relativePath);
        return extension switch
        {
            ".cs" or ".razor" or ".cshtml" or ".xaml" => "csharp",
            ".js" or ".jsx" or ".ts" or ".tsx" or ".html" or ".css" => "web",
            _ => null
        };
    }

    private static async Task<IndexStateFile?> TryLoadStateAsync(string root, CancellationToken cancellationToken)
    {
        var statePath = Path.Combine(root, ".codemap", "state.json");
        if (!File.Exists(statePath))
            return null;

        try
        {
            await using var stream = File.OpenRead(statePath);
            var state = await JsonSerializer.DeserializeAsync<IndexStateFile>(stream, StateJsonOptions, cancellationToken);
            if (state is null || state.IndexFormatVersion != IndexFormatVersion)
                return null;
            if (!string.Equals(state.SchemaVersion, SqliteCodeMapStore.SchemaVersion, StringComparison.Ordinal))
                throw CreateVersionMismatch("schema", state.SchemaVersion, SqliteCodeMapStore.SchemaVersion);
            if (!string.Equals(state.ConfigHash, SqliteCodeMapStore.ConfigHash, StringComparison.Ordinal))
                return null;
            return state;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<ProjectChangeSummary> DetectProjectChangesAsync(
        IndexStateFile state,
        string root,
        string? resolved,
        CancellationToken cancellationToken)
    {
        var dirtyProjects = new HashSet<string>(StringComparer.Ordinal);
        var removedProjects = new HashSet<string>(StringComparer.Ordinal);
        var added = 0;
        var updated = 0;
        var removed = 0;
        var skipped = 0;

        foreach (var project in state.Projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ProjectStillExists(project))
            {
                removedProjects.Add(project.ProjectName);
                dirtyProjects.Add(project.ProjectName);
                removed += project.Files.Count;
                continue;
            }

            var fileChanges = await CountProjectFileChangesAsync(project, cancellationToken);
            var isDirty = fileChanges.Added > 0 || fileChanges.Updated > 0 || fileChanges.Removed > 0
                || fileChanges.ProjectFileChanged || fileChanges.ExternalAssembliesChanged;
            if (!isDirty)
            {
                skipped += project.Files.Count;
                continue;
            }

            dirtyProjects.Add(project.ProjectName);
            added += fileChanges.Added;
            updated += fileChanges.Updated;
            removed += fileChanges.Removed;
            skipped += fileChanges.Unchanged;
        }

        foreach (var discoveredProject in DiscoverNewProjectNames(root, resolved, state))
        {
            dirtyProjects.Add(discoveredProject);
            added += await CountProjectFilesOnDiskAsync(root, resolved, discoveredProject, cancellationToken);
        }

        return new ProjectChangeSummary(added, updated, removed, skipped, dirtyProjects, removedProjects);
    }

    private static bool ProjectStillExists(IndexStateProject project)
    {
        if (Directory.Exists(project.ProjectPath) && !File.Exists(project.ProjectPath))
            return Directory.Exists(project.ProjectPath);
        return File.Exists(project.ProjectPath) || Directory.Exists(project.ProjectPath);
    }












    private static async Task<(int Added, int Updated, int Removed, int Unchanged, bool ProjectFileChanged, bool ExternalAssembliesChanged)> CountProjectFileChangesAsync(
        IndexStateProject project,
        CancellationToken cancellationToken)
    {
        var isWebProject = Directory.Exists(project.ProjectPath) && !File.Exists(project.ProjectPath);
        string projectDirectory;
        var projectFileChanged = false;
        if (isWebProject)
        {
            projectDirectory = project.ProjectPath;
        }
        else
        {
            if (!File.Exists(project.ProjectPath))
                return (0, 0, project.Files.Count, 0, ProjectFileChanged: true, ExternalAssembliesChanged: false);
            var projectFileHash = SqliteCodeMapStore.ComputeContentHash(project.ProjectPath, await File.ReadAllTextAsync(project.ProjectPath, cancellationToken));
            projectFileChanged = !string.Equals(projectFileHash, project.ProjectFileHash, StringComparison.Ordinal);
            projectDirectory = Path.GetDirectoryName(project.ProjectPath)!;
        }

        var recorded = project.Files.ToDictionary(file => file.RelativePath, file => file.Hash, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var added = 0;
        var updated = 0;
        var unchanged = 0;

        foreach (var file in EnumerateProjectCandidateFiles(projectDirectory, isWebProject))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = GetRelativePath(projectDirectory, file);
            seen.Add(relative);
            var content = await File.ReadAllTextAsync(file, cancellationToken);
            var hash = SqliteCodeMapStore.ComputeContentHash(relative, content);
            if (!recorded.TryGetValue(relative, out var recordedHash))
            {
                added++;
                continue;
            }

            if (string.Equals(recordedHash, hash, StringComparison.Ordinal))
                unchanged++;
            else
                updated++;
        }

        var removed = recorded.Keys.Count(relative => !seen.Contains(relative));
        var externalAssembliesChanged = !IsEveryExternalAssemblyUpToDate(project);
        return (added, updated, removed, unchanged, projectFileChanged, externalAssembliesChanged);
    }

    private static async Task<int> CountProjectFilesOnDiskAsync(
        string root,
        string? resolved,
        string projectName,
        CancellationToken cancellationToken)
    {
        var projectPath = TryFindProjectPath(root, resolved, projectName);
        if (projectPath is null)
            return 0;

        var isWebProject = Directory.Exists(projectPath) && !File.Exists(projectPath);
        var projectDirectory = isWebProject ? projectPath : Path.GetDirectoryName(projectPath)!;
        var count = 0;
        foreach (var file in EnumerateProjectCandidateFiles(projectDirectory, isWebProject))
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = file;
            count++;
        }
        return count;
    }










    private static bool IsSolutionFile(string resolved) =>
        resolved.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || resolved.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> DiscoverNewProjectNames(string root, string? resolved, IndexStateFile state)
    {
        var knownProjects = state.Projects.Select(project => project.ProjectName).ToHashSet(StringComparer.Ordinal);
        if (resolved is not null && File.Exists(resolved) && !IsSolutionFile(resolved))
        {
            var resolvedDirectory = Path.GetDirectoryName(resolved)!;
            foreach (var csproj in Directory.EnumerateFiles(resolvedDirectory, "*.csproj", SearchOption.AllDirectories)
                         .Where(file => !IgnoreRules.IsIgnored(resolvedDirectory, file)))
            {
                var projectName = Path.GetFileNameWithoutExtension(csproj);
                if (!knownProjects.Contains(projectName))
                    yield return projectName;
            }
        }

        var webName = WebProjectName(root);
        if (!knownProjects.Contains(webName) && Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
                .Any(file => !IgnoreRules.IsIgnored(root, file) && WebFileFilter.CanAnalyze(file)))
            yield return webName;
    }

    private static bool HasStoredProjectGraph(IndexStateFile state) =>
        state.Projects.All(project =>
            project.ProjectName.StartsWith("web:", StringComparison.Ordinal)
            || project.ReferencedProjects is not null);

    private static bool ShouldRefreshProjectGraph(IndexStateFile state, ProjectChangeSummary changeSummary)
    {
        var projectsByName = state.Projects.ToDictionary(project => project.ProjectName, StringComparer.Ordinal);
        foreach (var dirtyProject in changeSummary.DirtyProjects)
        {
            var project = projectsByName.GetValueOrDefault(dirtyProject);
            if (project is null || project.ReferencedProjects is null)
                return true;
            if (project.ProjectFileHash is null || !File.Exists(project.ProjectPath))
                continue;
            var currentHash = SqliteCodeMapStore.ComputeContentHash(project.ProjectPath, File.ReadAllText(project.ProjectPath));
            if (!string.Equals(currentHash, project.ProjectFileHash, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static IReadOnlyDictionary<string, HashSet<string>> BuildReverseReferencing(IReadOnlyList<IndexStateProject> projects)
    {
        var reverse = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var project in projects)
        {
            if (project.ReferencedProjects is null)
                continue;
            foreach (var referenced in project.ReferencedProjects)
            {
                if (!reverse.TryGetValue(referenced, out var dependents))
                {
                    dependents = new HashSet<string>(StringComparer.Ordinal);
                    reverse[referenced] = dependents;
                }
                dependents.Add(project.ProjectName);
            }
        }
        return reverse;
    }

    private static string? TryFindProjectPath(string root, string? resolved, string projectName)
    {
        if (projectName.StartsWith("web:", StringComparison.Ordinal))
            return root;

        if (resolved is not null && File.Exists(resolved))
        {
            var resolvedDirectory = Path.GetDirectoryName(resolved)!;
            var matches = Directory.EnumerateFiles(resolvedDirectory, "*.csproj", SearchOption.AllDirectories)
                .Where(file => !IgnoreRules.IsIgnored(resolvedDirectory, file))
                .Where(file => string.Equals(Path.GetFileNameWithoutExtension(file), projectName, StringComparison.Ordinal))
                .ToArray();
            if (matches.Length == 1)
                return matches[0];
        }

        return null;
    }

    private static void WipeIndexArtifacts(string root, string databasePath)
    {
        SqliteConnection.ClearAllPools();
        foreach (var path in new[] { databasePath, databasePath + "-wal", databasePath + "-shm" })
        {
            if (File.Exists(path))
                File.Delete(path);
        }

        var statePath = Path.Combine(root, ".codemap", "state.json");
        if (File.Exists(statePath))
            File.Delete(statePath);
    }

    private static IEnumerable<string> EnumerateProjectCandidateFiles(string projectDirectory, bool isWebProject) =>
        isWebProject
            ? Directory.EnumerateFiles(projectDirectory, "*.*", SearchOption.AllDirectories)
                .Where(file => !IgnoreRules.IsIgnored(projectDirectory, file) && WebFileFilter.CanAnalyze(file))
            : Directory.EnumerateFiles(projectDirectory, "*.*", SearchOption.AllDirectories)
                .Where(file => IsCSharpProjectFile(file))
                .Where(file => !IgnoreRules.IsIgnored(projectDirectory, file) && !IgnoreRules.IsOutsideRoot(projectDirectory, file));







    private static bool IsCSharpProjectFile(string file) =>
        file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || CSharp.DotNetMarkupFiles.IsMarkupFile(file);









    private static async Task<IndexStateFile?> TryLoadUpToDateStateAsync(string root, string? resolved, CancellationToken cancellationToken)
    {
        var statePath = Path.Combine(root, ".codemap", "state.json");
        if (!File.Exists(statePath))
            return null;

        IndexStateFile? state;
        try
        {
            await using var stream = File.OpenRead(statePath);
            state = await JsonSerializer.DeserializeAsync<IndexStateFile>(stream, StateJsonOptions, cancellationToken);
        }
        catch (JsonException) { return null; }
        if (state is null || state.IndexFormatVersion != IndexFormatVersion)
            return null;
        if (!string.Equals(state.SchemaVersion, SqliteCodeMapStore.SchemaVersion, StringComparison.Ordinal))
            throw CreateVersionMismatch("schema", state.SchemaVersion, SqliteCodeMapStore.SchemaVersion);
        if (state.AnalyzerVersions is not null)
            foreach (var (language, expected) in state.AnalyzerVersions)
                if (SqliteCodeMapStore.CurrentAnalyzerVersions.TryGetValue(language, out var current)
                    && !string.Equals(expected, current, StringComparison.Ordinal))
                    throw CreateVersionMismatch($"analyzer ({language})", expected, current);
        if (!string.Equals(state.ConfigHash, SqliteCodeMapStore.ConfigHash, StringComparison.Ordinal))
            return null;

        var resolvedPath = resolved is null ? null : Path.GetFullPath(resolved);
        if (!string.Equals(state.ResolvedInputPath, resolvedPath, StringComparison.OrdinalIgnoreCase))
            return null;
        if (resolvedPath is not null)
        {
            if (!File.Exists(resolvedPath))
                return null;
            var inputHash = SqliteCodeMapStore.ComputeContentHash(resolvedPath, await File.ReadAllTextAsync(resolvedPath, cancellationToken));
            if (!string.Equals(inputHash, state.ResolvedInputHash, StringComparison.Ordinal))
                return null;
        }

        foreach (var project in state.Projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await IsProjectUpToDateAsync(project, cancellationToken))
                return null;
        }
        return state;
    }

    private static InvalidOperationException CreateVersionMismatch(string kind, string? found, string expected) =>
        new($"CodeMap index {kind} version is outdated (found '{found ?? "none"}', expected '{expected}').\nRun: codemap index --force");

    private static async Task EnsureAnalyzerVersionsAsync(string databasePath, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        try
        {
            var storedVersions = new Dictionary<string, string>(StringComparer.Ordinal);
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT key, value FROM metadata WHERE key LIKE 'analyzer_version_%'";
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var key = reader.GetString(0);
                    var language = key["analyzer_version_".Length..];
                    var storedVersion = reader.GetString(1);
                    storedVersions[language] = storedVersion;
                    if (SqliteCodeMapStore.CurrentAnalyzerVersions.TryGetValue(language, out var expected)
                        && !string.Equals(storedVersion, expected, StringComparison.Ordinal))
                        throw CreateVersionMismatch($"analyzer ({language})", storedVersion, expected);
                }
            }

            await using (var languageCommand = connection.CreateCommand())
            {
                languageCommand.CommandText = "SELECT DISTINCT language FROM files";
                await using var languageReader = await languageCommand.ExecuteReaderAsync(cancellationToken);
                while (await languageReader.ReadAsync(cancellationToken))
                {
                    var language = SqliteCodeMapStore.AnalyzerLanguageForFile(languageReader.GetString(0));
                    if (language is not null && !storedVersions.ContainsKey(language))
                        throw CreateVersionMismatch($"analyzer ({language})", null, "current per-language metadata");
                }
            }
        }
        catch (SqliteException)
        {
            throw CreateVersionMismatch("analyzer", null, "current per-language metadata");
        }
    }

    private static readonly WebLanguageAnalyzer WebFileFilter = new();

    private static async Task<bool> IsProjectUpToDateAsync(IndexStateProject project, CancellationToken cancellationToken)
    {
        if (string.Equals(project.ProviderKind, "scip", StringComparison.Ordinal))
            return await IsScipProjectUpToDateAsync(project, cancellationToken);

        var isWebProject = Directory.Exists(project.ProjectPath) && !File.Exists(project.ProjectPath);
        string projectDirectory;
        if (isWebProject)
        {
            projectDirectory = project.ProjectPath;
        }
        else
        {
            if (!File.Exists(project.ProjectPath))
                return false;
            var projectFileHash = SqliteCodeMapStore.ComputeContentHash(project.ProjectPath, await File.ReadAllTextAsync(project.ProjectPath, cancellationToken));
            if (!string.Equals(projectFileHash, project.ProjectFileHash, StringComparison.Ordinal))
                return false;
            projectDirectory = Path.GetDirectoryName(project.ProjectPath)!;
        }
        if (!Directory.Exists(projectDirectory))
            return false;







        var candidateFiles = EnumerateProjectCandidateFiles(projectDirectory, isWebProject);

        var recorded = project.Files.ToDictionary(f => f.RelativePath, f => f.Hash, StringComparer.Ordinal);
        var seenCount = 0;
        foreach (var file in candidateFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            seenCount++;
            var relative = GetRelativePath(projectDirectory, file);
            if (!recorded.TryGetValue(relative, out var recordedHash))
                return false;
            var content = await File.ReadAllTextAsync(file, cancellationToken);
            if (!string.Equals(SqliteCodeMapStore.ComputeContentHash(relative, content), recordedHash, StringComparison.Ordinal))
                return false;
        }
        if (seenCount != recorded.Count)
            return false;

        return IsEveryExternalAssemblyUpToDate(project);
    }

    private static async Task<bool> IsScipProjectUpToDateAsync(IndexStateProject project, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(project.ProviderInputPath) || string.IsNullOrWhiteSpace(project.ProviderInputHash)
            || !File.Exists(project.ProviderInputPath))
            return false;
        await using var stream = File.OpenRead(project.ProviderInputPath);
        var currentHash = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
        if (!string.Equals(currentHash, project.ProviderInputHash, StringComparison.Ordinal))
            return false;
        foreach (var file in project.Files)
        {
            var path = Path.Combine(project.ProjectPath, file.RelativePath);
            if (!File.Exists(path))
                return false;
            var content = await File.ReadAllTextAsync(path, cancellationToken);
            if (!string.Equals(SqliteCodeMapStore.ComputeContentHash(file.RelativePath, content), file.Hash, StringComparison.Ordinal))
                return false;
        }
        return true;
    }










    private static bool IsEveryExternalAssemblyUpToDate(IndexStateProject project)
    {
        if (project.ExternalAssemblies is null || project.ExternalAssemblies.Count == 0)
            return true;
        foreach (var external in project.ExternalAssemblies)
        {
            if (!File.Exists(external.AssemblyPath))
                return false;
            var currentHash = CSharpWorkspaceIndexer.ComputeAssemblyFileHash(external.AssemblyPath);
            if (!string.Equals(currentHash, external.AssemblyHash, StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    private static string GetRelativePath(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
    }
}
