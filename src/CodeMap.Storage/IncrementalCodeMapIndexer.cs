using CodeMap.Core.Models;
using CodeMap.CSharp;
using CodeMap.Web;

namespace CodeMap.Storage;


public sealed partial class IncrementalCodeMapIndexer
{
    private readonly CSharpWorkspaceIndexer _workspaceIndexer;
    private readonly WebWorkspaceIndexer _webWorkspaceIndexer;

    public IncrementalCodeMapIndexer(CSharpWorkspaceIndexer? workspaceIndexer = null, WebWorkspaceIndexer? webWorkspaceIndexer = null)
    {
        _workspaceIndexer = workspaceIndexer ?? new CSharpWorkspaceIndexer();
        _webWorkspaceIndexer = webWorkspaceIndexer ?? new WebWorkspaceIndexer();
    }

    private IReadOnlyDictionary<string, string[]> _lastProjectReferences = new Dictionary<string, string[]>(StringComparer.Ordinal);







    private IReadOnlyDictionary<string, string?> _lastPublicSurfaceFingerprints = new Dictionary<string, string?>(StringComparer.Ordinal);









    private IReadOnlyDictionary<string, IReadOnlyList<ExternalAssemblyReference>> _lastExternalAssembliesByOwningProject =
        new Dictionary<string, IReadOnlyList<ExternalAssemblyReference>>(StringComparer.Ordinal);

    public async Task<IndexSummary> IndexAsync(string inputPath, bool force = false, CancellationToken cancellationToken = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var root = ResolveRoot(inputPath, out var resolved);
        var store = new SqliteCodeMapStore(Path.Combine(root, ".codemap", "index.db"));
        if (force)
            WipeIndexArtifacts(root, store.DatabasePath);

        var projects = await AnalyzeAllAsync(root, resolved, csharpProjectNames: null, dirtyProjectNames: null, cancellationToken);
        var previousProjects = force || !File.Exists(store.DatabasePath)
            ? new HashSet<string>(StringComparer.Ordinal)
            : (await store.GetFilesAsync(cancellationToken))
                .Select(file => file.Project)
                .ToHashSet(StringComparer.Ordinal);
        var currentProjects = projects.Select(project => project.ProjectName).ToHashSet(StringComparer.Ordinal);
        await store.ReplaceProjectsAsync(projects, previousProjects.Except(currentProjects, StringComparer.Ordinal).ToArray(), cancellationToken);
        var counts = await store.GetCountsAsync(cancellationToken);





        var files = projects.Where(p => !SqliteCodeMapStore.IsExternalProject(p.ProjectName)).Sum(p => p.Files.Count);
        await WriteStateAsync(root, resolved, projects, previousState: null, removedProjects: null, _lastProjectReferences, _lastPublicSurfaceFingerprints, _lastExternalAssembliesByOwningProject, cancellationToken);
        stopwatch.Stop();
        return new IndexSummary(files, files, 0, 0, 0, counts.Symbols, counts.Edges, stopwatch.Elapsed,
            projects.Select(project => project.ProjectName).ToArray());
    }

    public async Task<IndexSummary> UpdateAsync(string inputPath, CancellationToken cancellationToken = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var root = ResolveRoot(inputPath, out var resolved);
        var store = new SqliteCodeMapStore(Path.Combine(root, ".codemap", "index.db"));
        var hadDatabase = File.Exists(store.DatabasePath);





        if (hadDatabase)
            await EnsureAnalyzerVersionsAsync(store.DatabasePath, cancellationToken);
        var cachedState = hadDatabase
            ? await TryLoadUpToDateStateAsync(root, resolved, cancellationToken)
            : null;
        if (cachedState is not null)
        {
            var cachedCounts = await store.GetCountsAsync(cancellationToken);
            var cachedFileCount = cachedState.Projects.Sum(p => p.Files.Count);
            stopwatch.Stop();
            return new IndexSummary(cachedFileCount, 0, 0, 0, cachedFileCount, cachedCounts.Symbols, cachedCounts.Edges, stopwatch.Elapsed);
        }

        var previousState = await TryLoadStateAsync(root, cancellationToken);
        if (previousState is null || !hadDatabase)
        {
            var fullProjects = await AnalyzeAllAsync(root, resolved, csharpProjectNames: null, dirtyProjectNames: null, cancellationToken);
            var previousProjects = (await store.GetFilesAsync(cancellationToken))
                .Select(file => file.Project)
                .ToHashSet(StringComparer.Ordinal);
            var currentProjects = fullProjects.Select(project => project.ProjectName).ToHashSet(StringComparer.Ordinal);
            await store.ReplaceProjectsAsync(fullProjects, previousProjects.Except(currentProjects, StringComparer.Ordinal).ToArray(), cancellationToken);
            var fullCounts = await store.GetCountsAsync(cancellationToken);
            var fullFiles = fullProjects.Where(p => !SqliteCodeMapStore.IsExternalProject(p.ProjectName)).Sum(project => project.Files.Count);
            await WriteStateAsync(root, resolved, fullProjects, previousState: null, removedProjects: null, _lastProjectReferences, _lastPublicSurfaceFingerprints, _lastExternalAssembliesByOwningProject, cancellationToken);
            stopwatch.Stop();
            return new IndexSummary(fullFiles, fullFiles, 0, 0, 0, fullCounts.Symbols, fullCounts.Edges, stopwatch.Elapsed,
                fullProjects.Select(project => project.ProjectName).ToArray());
        }

        if (resolved is not null && IsSolutionFile(resolved)
            && await IsSolutionInputChangedAsync(previousState, resolved, cancellationToken))
        {
            return await ReconcileSolutionMembershipAsync(root, resolved, store, previousState, stopwatch, cancellationToken);
        }

        var changeSummary = await DetectProjectChangesAsync(previousState, root, resolved, cancellationToken);
        var dirtyProjects = new HashSet<string>(changeSummary.DirtyProjects, StringComparer.Ordinal);
        IReadOnlyList<AnalyzedProject> analyzedProjects;
        if (resolved is null)
        {
            analyzedProjects = await AnalyzeAllAsync(root, resolved, csharpProjectNames: null, dirtyProjectNames: dirtyProjects, cancellationToken);
        }
        else
        {
            IReadOnlyDictionary<string, HashSet<string>> referencing;
            CSharpWorkspaceAnalysisResult? firstWaveSeed = null;
            if (HasStoredProjectGraph(previousState) && !ShouldRefreshProjectGraph(previousState, changeSummary))
                referencing = BuildReverseReferencing(previousState.Projects);
            else
            {






                firstWaveSeed = await _workspaceIndexer.AnalyzeWithReferencingGraphAsync(
                    resolved,
                    dirtyProjects,
                    cancellationToken);
                referencing = firstWaveSeed.ReferencingProjects;
                _lastProjectReferences = firstWaveSeed.ProjectReferences;
            }
            analyzedProjects = await ExpandAndAnalyzeUsingFingerprintsAsync(
                root, resolved, dirtyProjects, referencing, previousState, cancellationToken, firstWaveSeed);
        }

        var analyzedNames = analyzedProjects.Select(project => project.ProjectName).ToHashSet(StringComparer.Ordinal);
        var orphanedExternalProjects = ComputeOrphanedExternalProjects(
            previousState, changeSummary.RemovedProjects, analyzedProjects, _lastExternalAssembliesByOwningProject);
        var deletedProjects = changeSummary.RemovedProjects
            .Where(project => !analyzedNames.Contains(project))
            .Concat(orphanedExternalProjects)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (analyzedProjects.Count > 0 || deletedProjects.Length > 0)
            await store.ReplaceProjectsAsync(analyzedProjects, deletedProjects, cancellationToken);

        var counts = await store.GetCountsAsync(cancellationToken);
        var indexedFiles = previousState.Projects.Sum(project => project.Files.Count)
            + changeSummary.Added
            - changeSummary.Removed;
        await WriteStateAsync(root, resolved, analyzedProjects, previousState, changeSummary.RemovedProjects, _lastProjectReferences, _lastPublicSurfaceFingerprints, _lastExternalAssembliesByOwningProject, cancellationToken);
        stopwatch.Stop();
        return new IndexSummary(
            Math.Max(0, indexedFiles),
            changeSummary.Added,
            changeSummary.Updated,
            changeSummary.Removed,
            changeSummary.Skipped,
            counts.Symbols,
            counts.Edges,
            stopwatch.Elapsed,
            analyzedProjects.Select(project => project.ProjectName).ToArray());
    }








    private static async Task<bool> IsSolutionInputChangedAsync(IndexStateFile previousState, string resolved, CancellationToken cancellationToken)
    {
        var currentHash = SqliteCodeMapStore.ComputeContentHash(resolved, await File.ReadAllTextAsync(resolved, cancellationToken));
        return !string.Equals(currentHash, previousState.ResolvedInputHash, StringComparison.Ordinal);
    }




















    private async Task<IndexSummary> ReconcileSolutionMembershipAsync(
        string root,
        string resolved,
        SqliteCodeMapStore store,
        IndexStateFile previousState,
        System.Diagnostics.Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        var analyzedProjects = await AnalyzeAllAsync(root, resolved, csharpProjectNames: null, dirtyProjectNames: null, cancellationToken);
        var declaredNames = CSharpWorkspaceIndexer.GetDeclaredProjectNames(resolved);
        var removedProjects = previousState.Projects
            .Select(project => project.ProjectName)
            .Where(name => !name.StartsWith("web:", StringComparison.Ordinal) && !SqliteCodeMapStore.IsExternalProject(name))
            .Where(name => !declaredNames.Contains(name))
            .ToHashSet(StringComparer.Ordinal);
        analyzedProjects = analyzedProjects
            .Where(project => project.ProjectName.StartsWith("web:", StringComparison.Ordinal)
                || SqliteCodeMapStore.IsExternalProject(project.ProjectName)
                || declaredNames.Contains(project.ProjectName))
            .ToArray();

        var orphanedExternalProjects = ComputeOrphanedExternalProjects(
            previousState, removedProjects, analyzedProjects, _lastExternalAssembliesByOwningProject);
        var deletedProjects = removedProjects
            .Concat(orphanedExternalProjects)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (analyzedProjects.Count > 0 || deletedProjects.Length > 0)
            await store.ReplaceProjectsAsync(analyzedProjects, deletedProjects, cancellationToken);

        var counts = await store.GetCountsAsync(cancellationToken);
        var previousFileCountByProject = previousState.Projects.ToDictionary(project => project.ProjectName, project => project.Files.Count, StringComparer.Ordinal);
        var added = analyzedProjects
            .Where(project => !previousFileCountByProject.ContainsKey(project.ProjectName))
            .Sum(project => project.Files.Count);
        var removedFileCount = removedProjects.Sum(name => previousFileCountByProject.GetValueOrDefault(name));
        var indexedFiles = analyzedProjects.Where(p => !SqliteCodeMapStore.IsExternalProject(p.ProjectName)).Sum(p => p.Files.Count);
        await WriteStateAsync(root, resolved, analyzedProjects, previousState, removedProjects, _lastProjectReferences, _lastPublicSurfaceFingerprints, _lastExternalAssembliesByOwningProject, cancellationToken);
        stopwatch.Stop();
        return new IndexSummary(
            Math.Max(0, indexedFiles),
            added,
            0,
            removedFileCount,
            0,
            counts.Symbols,
            counts.Edges,
            stopwatch.Elapsed,
            analyzedProjects.Select(project => project.ProjectName).ToArray());
    }






    public async Task<bool> IsUpToDateAsync(string inputPath, CancellationToken cancellationToken = default)
    {
        var root = ResolveRoot(inputPath, out var resolved);
        if (!File.Exists(Path.Combine(root, ".codemap", "index.db")))
            return false;
        return await TryLoadUpToDateStateAsync(root, resolved, cancellationToken) is not null;
    }

    private static string Key(string project, string path) => project + "" + path.Replace('\\', '/');
















    private async Task<IReadOnlyList<AnalyzedProject>> ExpandAndAnalyzeUsingFingerprintsAsync(
        string root,
        string? resolved,
        HashSet<string> initiallyDirtyProjects,
        IReadOnlyDictionary<string, HashSet<string>> referencing,
        IndexStateFile previousState,
        CancellationToken cancellationToken,
        CSharpWorkspaceAnalysisResult? firstWaveSeed = null)
    {
        var previousFingerprints = previousState.Projects
            .ToDictionary(project => project.ProjectName, project => project.PublicSurfaceFingerprint, StringComparer.Ordinal);

        var analyzed = new Dictionary<string, AnalyzedProject>(StringComparer.Ordinal);
        var accumulatedProjectReferences = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var accumulatedFingerprints = new Dictionary<string, string?>(StringComparer.Ordinal);
        var accumulatedExternalAssemblies = new Dictionary<string, IReadOnlyList<ExternalAssemblyReference>>(StringComparer.Ordinal);
        var processed = new HashSet<string>(StringComparer.Ordinal);
        var wave = new HashSet<string>(initiallyDirtyProjects, StringComparer.Ordinal);
        var isFirstWave = true;

        while (wave.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var waveNames = wave.ToArray();
            IReadOnlyList<AnalyzedProject> waveResult;
            if (isFirstWave && firstWaveSeed is not null)
                waveResult = await AnalyzeSeededFirstWaveAsync(root, firstWaveSeed, wave, cancellationToken);
            else
                waveResult = await AnalyzeAllAsync(root, resolved, csharpProjectNames: waveNames, dirtyProjectNames: wave, cancellationToken);
            isFirstWave = false;
            foreach (var project in waveResult)
                analyzed[project.ProjectName] = project;
            foreach (var (name, value) in _lastProjectReferences)
                accumulatedProjectReferences[name] = value;
            foreach (var (name, value) in _lastPublicSurfaceFingerprints)
                accumulatedFingerprints[name] = value;
            foreach (var (name, value) in _lastExternalAssembliesByOwningProject)
                accumulatedExternalAssemblies[name] = value;

            var nextWave = new HashSet<string>(StringComparer.Ordinal);
            foreach (var projectName in waveNames)
            {
                processed.Add(projectName);
                if (!referencing.TryGetValue(projectName, out var dependents))
                    continue;

                var previousFingerprint = previousFingerprints.GetValueOrDefault(projectName);
                var newFingerprintKnown = _lastPublicSurfaceFingerprints.TryGetValue(projectName, out var newFingerprint);





                var surfaceProvablyUnchanged = newFingerprintKnown
                    && newFingerprint is not null
                    && previousFingerprint is not null
                    && string.Equals(newFingerprint, previousFingerprint, StringComparison.Ordinal);
                if (surfaceProvablyUnchanged)
                    continue;

                foreach (var dependent in dependents)
                    if (!processed.Contains(dependent))
                        nextWave.Add(dependent);
            }
            wave = nextWave;
        }

        _lastProjectReferences = accumulatedProjectReferences;
        _lastPublicSurfaceFingerprints = accumulatedFingerprints;
        _lastExternalAssembliesByOwningProject = accumulatedExternalAssemblies;
        return analyzed.Values.ToArray();
    }










    private async Task<IReadOnlyList<AnalyzedProject>> AnalyzeSeededFirstWaveAsync(
        string root,
        CSharpWorkspaceAnalysisResult seed,
        IReadOnlyCollection<string> dirtyProjectNames,
        CancellationToken cancellationToken)
    {
        _lastProjectReferences = seed.ProjectReferences;
        _lastPublicSurfaceFingerprints = seed.PublicSurfaceFingerprints;
        _lastExternalAssembliesByOwningProject = seed.ExternalAssembliesByOwningProject
            ?? new Dictionary<string, IReadOnlyList<ExternalAssemblyReference>>(StringComparer.Ordinal);
        var projects = new List<AnalyzedProject>(seed.Projects.Select(project => project.ToAnalyzedProject()));

        if (ShouldAnalyzeWeb(root, dirtyProjectNames))
        {
            var web = await _webWorkspaceIndexer.AnalyzeAsync(root, cancellationToken);
            if (web is not null)
                projects.Add(web);
        }

        return projects;
    }

    private async Task<IReadOnlyList<AnalyzedProject>> AnalyzeAllAsync(
        string root,
        string? resolved,
        IReadOnlyCollection<string>? csharpProjectNames,
        IReadOnlyCollection<string>? dirtyProjectNames,
        CancellationToken cancellationToken)
    {
        _lastProjectReferences = new Dictionary<string, string[]>(StringComparer.Ordinal);
        _lastPublicSurfaceFingerprints = new Dictionary<string, string?>(StringComparer.Ordinal);
        _lastExternalAssembliesByOwningProject = new Dictionary<string, IReadOnlyList<ExternalAssemblyReference>>(StringComparer.Ordinal);
        var projects = new List<AnalyzedProject>();
        if (resolved is not null)
        {
            var filter = csharpProjectNames is null ? null : csharpProjectNames.ToHashSet(StringComparer.Ordinal);
            var csharp = await _workspaceIndexer.AnalyzeWithReferencingGraphAsync(resolved, filter, cancellationToken);
            _lastProjectReferences = csharp.ProjectReferences;
            _lastPublicSurfaceFingerprints = csharp.PublicSurfaceFingerprints;
            _lastExternalAssembliesByOwningProject = csharp.ExternalAssembliesByOwningProject
                ?? new Dictionary<string, IReadOnlyList<ExternalAssemblyReference>>(StringComparer.Ordinal);
            projects.AddRange(csharp.Projects.Select(project => project.ToAnalyzedProject()));
        }

        if (ShouldAnalyzeWeb(root, dirtyProjectNames))
        {
            var web = await _webWorkspaceIndexer.AnalyzeAsync(root, cancellationToken);
            if (web is not null)
                projects.Add(web);
        }

        return projects;
    }

    private static string WebProjectName(string root) => "web:" + new DirectoryInfo(root).Name;

    private static bool ShouldAnalyzeWeb(string root, IReadOnlyCollection<string>? dirtyProjectNames)
    {
        if (dirtyProjectNames is null)
            return true;
        if (dirtyProjectNames.Count == 0)
            return false;
        return dirtyProjectNames.Contains(WebProjectName(root), StringComparer.Ordinal);
    }

    private static string ResolveRoot(string inputPath, out string? resolved)
    {
        var path = Path.GetFullPath(string.IsNullOrWhiteSpace(inputPath) ? Directory.GetCurrentDirectory() : inputPath);
        if (Directory.Exists(path))
        {
            resolved = TryResolveCSharpInput(path);
            return path;
        }
        if (File.Exists(path))
        {
            resolved = Path.GetExtension(path) is ".sln" or ".slnx" or ".csproj" ? path : null;
            return Path.GetDirectoryName(path)!;
        }
        throw new DirectoryNotFoundException(path);
    }

    private static string? TryResolveCSharpInput(string directory)
    {
        try { return CSharpWorkspaceIndexer.ResolveInput(directory); }
        catch (FileNotFoundException) { return null; }
    }
}
