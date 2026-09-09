namespace CodeMap.Engine.Application;

/// <summary>
/// Shared freshness contract for short-lived CLI and long-lived MCP/LSP hosts.
/// Implementations own their caching policy; callers only observe a conservative
/// boolean and explicitly invalidate a repository after an update.
/// </summary>
public interface IIndexFreshnessService
{
    Task<bool> IsUpToDateAsync(string root, CancellationToken cancellationToken = default);

    void Invalidate(string root);
}
