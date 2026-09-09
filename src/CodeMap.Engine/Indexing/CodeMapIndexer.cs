using CodeMap.Storage;
using CodeMap.Core.Models;

namespace CodeMap.Engine.Indexing;

/// <summary>
/// Engine-facing indexing facade. The storage-namespaced indexer remains
/// available for source compatibility until the orchestration move is complete.
/// </summary>
public sealed class CodeMapIndexer(IncrementalCodeMapIndexer? implementation = null)
{
    private readonly IncrementalCodeMapIndexer _implementation = implementation ?? new IncrementalCodeMapIndexer();

    public Task<IndexSummary> IndexAsync(string inputPath, bool force = false, CancellationToken cancellationToken = default) =>
        _implementation.IndexAsync(inputPath, force, cancellationToken);

    public Task<IndexSummary> UpdateAsync(string inputPath, CancellationToken cancellationToken = default) =>
        _implementation.UpdateAsync(inputPath, cancellationToken);

    public Task<bool> IsUpToDateAsync(string inputPath, CancellationToken cancellationToken = default) =>
        _implementation.IsUpToDateAsync(inputPath, cancellationToken);
}
