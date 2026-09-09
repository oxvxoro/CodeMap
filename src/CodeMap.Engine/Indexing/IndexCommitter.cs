using CodeMap.Core.Contracts;
using CodeMap.Core.Models;

namespace CodeMap.Engine.Indexing;

public sealed record IndexCommitRequest(IReadOnlyCollection<AnalyzedProject> Projects, IReadOnlyCollection<string> RemovedProjects, IReadOnlyDictionary<string, string> Metadata);
public delegate Task CommitIndexAsync(IndexCommitRequest request, CancellationToken cancellationToken);

/// <summary>Persistence port for the final index stage.</summary>
public sealed class IndexCommitter
{
    private readonly CommitIndexAsync _commit;

    public IndexCommitter(CommitIndexAsync commit) => _commit = commit;

    public IndexCommitter(ICodeMapIndexWriter writer)
        : this((request, cancellationToken) => writer.CommitAsync(
            new IndexCommitBatch(request.Projects, request.RemovedProjects, request.Metadata), cancellationToken))
    {
    }

    public Task CommitAsync(IndexCommitRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _commit(request, cancellationToken);
    }
}
