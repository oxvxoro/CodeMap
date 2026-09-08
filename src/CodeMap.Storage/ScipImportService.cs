using System.Security.Cryptography;
using CodeMap.Scip;

namespace CodeMap.Storage;

public sealed record ScipImportOptions(string Name, bool Replace = true, bool RequireAllDocumentsInsideRoot = true);

public sealed record ScipImportSummary(
    string Provider,
    string Project,
    string Artifact,
    int Files,
    int Symbols,
    int Edges,
    int SkippedSymbols,
    string ArtifactHash);

public sealed record ScipProviderInfo(string Name, string Project, string Artifact, string ArtifactHash, string ProviderVersion, int Files, bool Fresh);

public sealed class ScipImportService
{
    private readonly ScipIndexReader _reader;
    private readonly ScipGraphMapper _mapper;

    public ScipImportService(ScipIndexReader? reader = null, ScipGraphMapper? mapper = null)
    {
        _reader = reader ?? new ScipIndexReader();
        _mapper = mapper ?? new ScipGraphMapper();
    }

    public async Task<ScipImportSummary> ImportAsync(
        string repositoryRoot,
        string scipPath,
        ScipImportOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(scipPath);
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Name))
            throw new ArgumentException("A stable SCIP import name is required.", nameof(options));

        var root = Path.GetFullPath(repositoryRoot);
        var artifact = Path.GetFullPath(scipPath);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException(root);
        if (!File.Exists(artifact))
            throw new FileNotFoundException("SCIP artifact was not found.", artifact);
        var index = await _reader.ReadAsync(artifact, cancellationToken);
        ValidateSourceOwnership(root, index, options.RequireAllDocumentsInsideRoot);
        var project = _mapper.Map(options.Name, root, index);
        var projectName = project.ProjectName;
        var store = new SqliteCodeMapStore(Path.Combine(root, ".codemap", "index.db"));
        var existingProjects = File.Exists(store.DatabasePath)
            ? (await store.GetFilesAsync(cancellationToken)).Select(file => file.Project).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        if (!options.Replace && existingProjects.Contains(projectName))
            throw new InvalidOperationException($"SCIP project '{projectName}' already exists. Use --replace to re-import it.");

        await using var artifactStream = File.OpenRead(artifact);
        var artifactHash = Convert.ToHexString(await SHA256.HashDataAsync(artifactStream, cancellationToken)).ToLowerInvariant();
        await store.ReplaceProjectsAsync([project], Array.Empty<string>(), cancellationToken);
        await IncrementalCodeMapIndexer.WriteScipProviderStateAsync(root, project, artifact, artifactHash, cancellationToken);
        var importedSymbols = project.Result.Nodes.Count(node => node.Kind != CodeMap.Core.Models.NodeKind.File);
        var sourceSymbols = index.Documents.Sum(document => document.Symbols.Count);
        return new ScipImportSummary("scip", projectName, artifact, project.Files.Count, importedSymbols, project.Result.Edges.Count,
            Math.Max(0, sourceSymbols - importedSymbols), artifactHash);
    }

    public async Task RemoveAsync(string repositoryRoot, string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var root = Path.GetFullPath(repositoryRoot);
        var projectName = "scip:" + name;
        var store = new SqliteCodeMapStore(Path.Combine(root, ".codemap", "index.db"));
        if (!File.Exists(store.DatabasePath))
            throw new InvalidOperationException("A CodeMap index must exist before removing SCIP data.");
        if (!await IncrementalCodeMapIndexer.IsScipProviderRecordedAsync(root, projectName, cancellationToken))
            throw new InvalidOperationException($"SCIP provider '{projectName}' was not found.");
        await store.ReplaceProjectsAsync(Array.Empty<CodeMap.Core.Models.AnalyzedProject>(), [projectName], cancellationToken);
        await IncrementalCodeMapIndexer.RemoveScipProviderStateAsync(root, projectName, cancellationToken);
    }

    public Task<IReadOnlyList<ScipProviderInfo>> ListAsync(string repositoryRoot, CancellationToken cancellationToken = default) =>
        IncrementalCodeMapIndexer.ListScipProvidersAsync(Path.GetFullPath(repositoryRoot), cancellationToken);

    private static void ValidateSourceOwnership(string root, ScipIndex index, bool requireAllDocumentsInsideRoot)
    {
        var ownership = new SourceOwnershipResolver(root);
        foreach (var document in index.Documents)
        {
            var path = Path.GetFullPath(Path.Combine(root, document.RelativePath));
            var owner = ownership.GetOwner(path);
            if (owner is SourceOwner.CSharp or SourceOwner.Web)
                throw new InvalidOperationException($"scip_source_conflict: '{document.RelativePath}' is already owned by a built-in analyzer.");
            if (requireAllDocumentsInsideRoot && owner == SourceOwner.OutsideRoot)
                throw new InvalidOperationException($"SCIP document '{document.RelativePath}' is outside the repository root.");
        }
    }
}
