using CodeMap.Storage;
using CodeMap.Core.Contracts;
using CodeMap.Core.Models;
using CodeMap.CSharp;
using CodeMap.Engine.Concurrency;
using CodeMap.Engine.Application.Investigation;
using CodeMap.Core.Models.Investigation;

namespace CodeMap.Engine.Application;

/// <summary>
/// Transport-independent CodeMap use cases. JSON, exit codes, and protocol
/// envelopes intentionally remain in CLI/MCP/LSP adapters.
/// </summary>
public sealed class CodeMapApplication
{
    private readonly IIndexFreshnessService? _freshness;
    private readonly Func<IncrementalCodeMapIndexer> _indexerFactory;
    private readonly Func<string, CancellationToken, Task<ICodeMapGraphReader>> _graphReaderFactory;
    private readonly KeyedAsyncLock<string> _sliceLocks = new();

    public CodeMapApplication(
        IIndexFreshnessService? freshness = null,
        Func<IncrementalCodeMapIndexer>? indexerFactory = null,
        Func<string, CancellationToken, Task<ICodeMapGraphReader>>? graphReaderFactory = null)
    {
        _freshness = freshness;
        _indexerFactory = indexerFactory ?? (() => new IncrementalCodeMapIndexer());
        _graphReaderFactory = graphReaderFactory
            ?? throw new ArgumentNullException(nameof(graphReaderFactory),
                "A graph reader factory must be supplied by the storage adapter.");
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
        if (!CodeMapQueryValidation.IsValidImpactProfile(request.Profile))
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
        if (!CodeMapQueryValidation.IsValidFlowKind(request.Kind))
            return ApplicationResponse<ApplicationFlowResult>.Failure(
                new("query_failed", "kind must be one of: http, ui, all."));
        if (!CodeMapQueryValidation.IsValidFlowDepth(request.Depth))
            return ApplicationResponse<ApplicationFlowResult>.Failure(
                new("query_failed", $"depth must be between 1 and 8."));
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

    public async Task<ApplicationResponse<InvestigationResult>> InvestigateAsync(
        InvestigationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Query))
            return ApplicationResponse<InvestigationResult>.Failure(
                new("query_failed", "A symbol query is required."));
        if (request.TokenBudget <= 0 || request.MaxResults <= 0)
            return ApplicationResponse<InvestigationResult>.Failure(
                new("query_failed", "tokens and max-results must be greater than zero."));
        if (!RelationConfidence.IsValid(request.MinConfidence))
            return ApplicationResponse<InvestigationResult>.Failure(
                new("query_failed", RelationConfidence.InvalidMessage));
        if (request.Depth is <= 0 or > 8)
            return ApplicationResponse<InvestigationResult>.Failure(
                new("query_failed", "depth must be between 1 and 8."));
        if (request.SourceMode is not ("none" or "minimal" or "scope"))
            return ApplicationResponse<InvestigationResult>.Failure(
                new("query_failed", "source-mode must be one of: none, minimal, scope."));

        return await WithServiceAsync(request.Root, cancellationToken, async (service, stale) =>
        {
            var resolution = service.ResolveSymbol(request.Query, callableOnly: false, request.MaxResults);
            if (resolution.Matches.Count == 0)
                return ApplicationResponse<InvestigationResult>.Failure(
                    new("no_matches", $"No symbol matches '{request.Query}'."), stale);
            if (resolution.IsAmbiguous || resolution.Matches.Count > 1)
                return ApplicationResponse<InvestigationResult>.Failure(
                    new("ambiguous", $"Symbol query '{request.Query}' is ambiguous."),
                    stale,
                    new InvestigationResult(
                        new InvestigationResponse(1, request.Query, request.Goal, null, true, resolution.Matches),
                        Array.Empty<InvestigationCandidate>(),
                        new InvestigationBudgetAllocator().Allocate(Array.Empty<InvestigationCandidate>(), request.TokenBudget),
                        InvestigationCoverage.Empty,
                        Array.Empty<InvestigationSourceSpan>()));

            var root = resolution.Matches[0];
            var queryRoot = Path.GetFullPath(string.IsNullOrWhiteSpace(request.Root)
                ? Directory.GetCurrentDirectory()
                : request.Root);
            var overrides = new InvestigationOverrides(
                request.MaxResults,
                request.Depth ?? (request.Goal == InvestigationGoal.Trace ? 4 : request.Goal == InvestigationGoal.Impact ? 2 : 1),
                request.MinConfidence,
                request.IncludeHeuristic,
                request.TokenBudget,
                queryRoot);
            var orchestrator = new InvestigationOrchestrator(
                new InvestigationRankingPolicy(),
                new InvestigationBudgetAllocator(),
                goal => InvestigationProfiles.Create(goal, SliceForInvestigationAsync));
            var result = await orchestrator.RunAsync(request.Goal, service, root, overrides, cancellationToken);
            var coverage = new CoverageAggregator().Aggregate(
                result.ProviderStatuses,
                result.Selection.Selected.Concat(result.Selection.Excluded).ToArray(),
                result.Selection.Selected,
                service,
                root,
                request);
            var sourceMode = Enum.Parse<SourceEvidenceMode>(request.SourceMode, ignoreCase: true);
            var sourceSpans = new SourceEvidenceBuilder().BuildSpans(
                result.Candidates,
                sourceMode,
                Math.Max(0, request.TokenBudget - result.Selection.EstimatedTokens),
                new FileTextAccessor(queryRoot));
            var response = new InvestigationResponse(1, request.Query, request.Goal, root, false, Array.Empty<IndexedSymbol>());
            return ApplicationResponse<InvestigationResult>.Success(
                new InvestigationResult(response, result.Candidates, result.Selection, coverage, sourceSpans), stale);
        });

        async Task<SemanticSliceResult> SliceForInvestigationAsync(
            IndexedSymbol symbol, string projectRoot, CancellationToken token)
        {
            return await new CodeMapSemanticSliceService(_indexerFactory()).SliceAsync(
                projectRoot,
                new SemanticSliceRequest(Query: symbol.QualifiedName, MaxResults: request.MaxResults),
                token);
        }
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
        Func<ICodeMapGraphReader, bool, ApplicationResponse<T>> action)
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
            await using var reader = await _graphReaderFactory(database, cancellationToken);
            return action(reader, stale);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return ApplicationResponse<T>.Failure(Classify(exception));
        }
    }

    private async Task<ApplicationResponse<T>> WithServiceAsync<T>(
        string? requestedRoot,
        CancellationToken cancellationToken,
        Func<ICodeMapGraphReader, bool, Task<ApplicationResponse<T>>> action)
    {
        try
        {
            var queryRoot = Path.GetFullPath(string.IsNullOrWhiteSpace(requestedRoot)
                ? Directory.GetCurrentDirectory()
                : requestedRoot);
            var database = CodeMapIndexLocator.FindDatabase(queryRoot);
            var stale = _freshness is not null
                && await CodeMapIndexLocator.IsStaleAsync(database, cancellationToken, _freshness.IsUpToDateAsync);
            await using var reader = await _graphReaderFactory(database, cancellationToken);
            return await action(reader, stale);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return ApplicationResponse<T>.Failure(Classify(exception));
        }
    }

    private static IndexedSymbol? ResolveUnique(ICodeMapGraphReader service, string query, int maxResults, out QueryError? error)
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
