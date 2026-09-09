using CodeMap.Storage;
using CodeMap.Core.Models;
using CodeMap.CSharp;
using CodeMap.Engine.Concurrency;

namespace CodeMap.Engine.Application;

/// <summary>
/// Transport-independent CodeMap use cases. JSON, exit codes, and protocol
/// envelopes intentionally remain in CLI/MCP/LSP adapters.
/// </summary>
public sealed class CodeMapApplication
{
    private readonly IIndexFreshnessService? _freshness;
    private readonly Func<IncrementalCodeMapIndexer> _indexerFactory;
    private readonly KeyedAsyncLock<string> _sliceLocks = new();

    public CodeMapApplication(
        IIndexFreshnessService? freshness = null,
        Func<IncrementalCodeMapIndexer>? indexerFactory = null)
    {
        _freshness = freshness;
        _indexerFactory = indexerFactory ?? (() => new IncrementalCodeMapIndexer());
    }

    public async Task<ApplicationResponse<ApplicationFindResult>> FindAsync(
        FindRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Query))
            return ApplicationResponse<ApplicationFindResult>.Failure(new("query_failed", "A symbol query is required."));

        return await WithServiceAsync(request.Root, cancellationToken, (service, stale) =>
        {
            var resolution = service.ResolveSymbol(request.Query, callableOnly: false, request.MaxResults);
            var members = resolution.Matches.ToDictionary(
                symbol => symbol.Id,
                symbol => service.Members(symbol),
                StringComparer.Ordinal);
            return ApplicationResponse<ApplicationFindResult>.Success(new ApplicationFindResult(resolution, members), stale);
        });
    }

    public async Task<ApplicationResponse<ApplicationContextResult>> ContextAsync(
        ContextRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Task))
            return ApplicationResponse<ApplicationContextResult>.Failure(
                new("query_failed", "A context task is required."));

        return await WithServiceAsync(request.Root, cancellationToken, (service, stale) =>
        {
            var matches = service.Find(request.Task, request.MaxResults);
            var map = service.BuildMap(matches.FirstOrDefault()?.Name ?? request.Task, null, request.TokenBudget);
            return ApplicationResponse<ApplicationContextResult>.Success(
                new ApplicationContextResult(matches, map), stale);
        });
    }

    public async Task<ApplicationResponse<IReadOnlyList<RelationQueryResult>>> RelationsAsync(
        RelationsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!RelationConfidence.IsValid(request.MinConfidence))
            return ApplicationResponse<IReadOnlyList<RelationQueryResult>>.Failure(
                new("query_failed", RelationConfidence.InvalidMessage));

        return await WithServiceAsync(request.Root, cancellationToken, (service, stale) =>
        {
            var source = ResolveUnique(service, request.Source, request.MaxResults, out var sourceError);
            if (sourceError is not null)
                return ApplicationResponse<IReadOnlyList<RelationQueryResult>>.Failure(sourceError, stale);
            var target = ResolveUnique(service, request.Target, request.MaxResults, out var targetError);
            if (targetError is not null)
                return ApplicationResponse<IReadOnlyList<RelationQueryResult>>.Failure(targetError, stale);

            var edgeKind = request.EdgeKind is null || !Enum.TryParse<EdgeKind>(request.EdgeKind, true, out var parsed)
                ? (EdgeKind?)null
                : parsed;
            return ApplicationResponse<IReadOnlyList<RelationQueryResult>>.Success(
                service.Relations(source!.Id, target!.Id, edgeKind, request.MaxResults, request.MinConfidence), stale);
        });
    }

    public async Task<ApplicationResponse<ApplicationImpactResult>> ImpactAsync(
        ImpactRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!CodeMapQueryService.IsValidImpactProfile(request.Profile))
            return ApplicationResponse<ApplicationImpactResult>.Failure(
                new("query_failed", "profile must be one of: code, app."));
        if (!RelationConfidence.IsValid(request.MinConfidence))
            return ApplicationResponse<ApplicationImpactResult>.Failure(
                new("query_failed", RelationConfidence.InvalidMessage));

        return await WithServiceAsync(request.Root, cancellationToken, (service, stale) =>
        {
            var symbol = ResolveUnique(service, request.Query, request.MaxResults, out var error);
            if (error is not null)
                return ApplicationResponse<ApplicationImpactResult>.Failure(error, stale);
            var items = service.Impact(symbol!, request.Depth, request.MaxResults, request.Profile)
                .Where(item => (item.Via.Confidence ?? 1) >= request.MinConfidence)
                .ToArray();
            var files = service.FindFilesByIds(items.Select(item => item.Via.SourceFileId).OfType<string>());
            return ApplicationResponse<ApplicationImpactResult>.Success(
                new ApplicationImpactResult(symbol!, items, files), stale);
        });
    }

    public async Task<ApplicationResponse<ApplicationFlowResult>> FlowAsync(
        FlowRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!CodeMapQueryService.IsValidFlowKind(request.Kind))
            return ApplicationResponse<ApplicationFlowResult>.Failure(
                new("query_failed", "kind must be one of: http, ui, all."));
        if (!CodeMapQueryService.IsValidFlowDepth(request.Depth))
            return ApplicationResponse<ApplicationFlowResult>.Failure(
                new("query_failed", $"depth must be between {CodeMapQueryService.FlowMinDepth} and {CodeMapQueryService.FlowMaxDepth}."));
        if (!RelationConfidence.IsValid(request.MinConfidence))
            return ApplicationResponse<ApplicationFlowResult>.Failure(
                new("query_failed", RelationConfidence.InvalidMessage));

        return await WithServiceAsync(request.Root, cancellationToken, (service, stale) =>
        {
            var entry = ResolveUnique(service, request.Entry, request.MaxResults, out var error);
            if (error is not null)
                return ApplicationResponse<ApplicationFlowResult>.Failure(error, stale);
            var items = service.Flow(entry!, request.Kind, request.Depth, request.MaxResults, request.MinConfidence);
            var sources = service.FindByIds(items.Select(item => item.Via.SourceId));
            var files = service.FindFilesByIds(items.Select(item => item.Via.SourceFileId).OfType<string>());
            return ApplicationResponse<ApplicationFlowResult>.Success(
                new ApplicationFlowResult(entry!, items, sources, files), stale);
        });
    }

    public async Task<ApplicationResponse<CodeMapIndexStatus>> StatusAsync(
        StatusRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var database = CodeMapIndexLocator.FindDatabase(Path.GetFullPath(request.Root));
            var status = await CodeMapIndexStatusReader.ReadAsync(database, cancellationToken);
            var stale = request.CheckFreshness && _freshness is not null
                ? await CodeMapIndexLocator.IsStaleAsync(database, cancellationToken, _freshness.IsUpToDateAsync)
                : false;
            return ApplicationResponse<CodeMapIndexStatus>.Success(status, stale);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return ApplicationResponse<CodeMapIndexStatus>.Failure(Classify(exception));
        }
    }

    public async Task<ApplicationResponse<SemanticSliceResult>> SemanticSliceAsync(
        string root,
        SemanticSliceRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(request);
        var key = Path.GetFullPath(root);
        await using var lease = await _sliceLocks.AcquireAsync(key, cancellationToken);
        try
        {
            var result = await new CodeMapSemanticSliceService(_indexerFactory()).SliceAsync(key, request, cancellationToken);
            return ApplicationResponse<SemanticSliceResult>.Success(result);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return ApplicationResponse<SemanticSliceResult>.Failure(Classify(exception));
        }
    }

    public async Task<ApplicationResponse<IndexSummary>> RefreshIndexAsync(
        RefreshIndexRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var indexer = _indexerFactory();
            var summary = request.Force
                ? await indexer.IndexAsync(request.Root, force: true, cancellationToken)
                : await indexer.UpdateAsync(request.Root, cancellationToken);
            _freshness?.Invalidate(Path.GetFullPath(request.Root));
            return ApplicationResponse<IndexSummary>.Success(summary);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return ApplicationResponse<IndexSummary>.Failure(Classify(exception));
        }
    }

    private async Task<ApplicationResponse<T>> WithServiceAsync<T>(
        string? requestedRoot,
        CancellationToken cancellationToken,
        Func<CodeMapQueryService, bool, ApplicationResponse<T>> action)
    {
        try
        {
            var queryRoot = Path.GetFullPath(string.IsNullOrWhiteSpace(requestedRoot)
                ? Directory.GetCurrentDirectory()
                : requestedRoot);
            var database = CodeMapIndexLocator.FindDatabase(queryRoot);
            var stale = _freshness is not null
                && await CodeMapIndexLocator.IsStaleAsync(
                    database,
                    cancellationToken,
                    _freshness.IsUpToDateAsync);
            await using var connection = await new CodeMapQueryStore(database).OpenReadOnlyConnectionAsync(cancellationToken);
            await using var service = new CodeMapQueryService(connection);
            return action(service, stale);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return ApplicationResponse<T>.Failure(Classify(exception));
        }
    }

    private static IndexedSymbol? ResolveUnique(CodeMapQueryService service, string query, int maxResults, out QueryError? error)
    {
        var resolution = service.ResolveSymbol(query, callableOnly: false, maxResults);
        if (resolution.Matches.Count == 0)
        {
            error = new("no_matches", $"No symbol matches '{query}'.");
            return null;
        }
        if (resolution.IsAmbiguous || resolution.Matches.Count > 1)
        {
            error = new("ambiguous", $"Symbol query '{query}' is ambiguous.");
            return null;
        }
        error = null;
        return resolution.Matches[0];
    }

    private static QueryError Classify(Exception exception)
    {
        var (code, message) = CodeMapErrorClassifier.Classify(exception);
        return new QueryError(code, message);
    }
}
