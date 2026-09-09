using CodeMap.Core.Models;

namespace CodeMap.Core.Contracts;

public sealed record IndexCommitBatch(
    IReadOnlyCollection<AnalyzedProject> Projects,
    IReadOnlyCollection<string> RemovedProjects,
    IReadOnlyDictionary<string, string> Metadata);

/// <summary>Persistence port used by indexing orchestration.</summary>
public interface ICodeMapIndexWriter
{
    Task CommitAsync(IndexCommitBatch batch, CancellationToken cancellationToken = default);
}
