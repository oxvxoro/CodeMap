using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text.Json;
using CodeMap.CSharp;
using CodeMap.Core;
using CodeMap.Core.Contracts;
using CodeMap.Core.Models;
using CodeMap.Core.Models.Investigation;
using CodeMap.Engine.Application;
using CodeMap.Engine.Application.Investigation;
using CodeMap.Storage;
using CodeMap.Storage.Queries;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace CodeMap.Mcp;

public static class CodeMapMcpServer
{
    private static string DefaultRoot { get; set; } = Directory.GetCurrentDirectory();

    public static async Task RunAsync(string defaultRoot, CancellationToken cancellationToken)
    {
        DefaultRoot = defaultRoot;
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.AddConsole(consoleLogOptions =>
            consoleLogOptions.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Services.AddSingleton(new CodeMapMcpContext(defaultRoot));
        builder.Services.AddSingleton<IIndexFreshnessService>(services =>
            services.GetRequiredService<CodeMapMcpContext>());
        builder.Services.AddSingleton<CodeMapApplication>(services =>
            CreateApplication(services.GetRequiredService<IIndexFreshnessService>()));
        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithToolsFromAssembly();
        await builder.Build().RunAsync(cancellationToken);
    }

    internal static CodeMapApplication CreateApplication(IIndexFreshnessService freshness) =>
        new(freshness, graphReaderFactory: OpenGraphReaderAsync);

    private static async Task<ICodeMapGraphReader> OpenGraphReaderAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        var connection = await new CodeMapQueryStore(databasePath)
            .OpenReadOnlyConnectionAsync(cancellationToken);
        return new SqliteCodeMapGraphReader(connection);
    }
}

















public sealed class CodeMapMcpContext(string defaultRoot) : IDisposable, IIndexFreshnessService
{
    public string DefaultRoot { get; } = defaultRoot;

    private readonly ConcurrentDictionary<string, FreshnessEntry> _freshness = new(StringComparer.OrdinalIgnoreCase);
    internal SemaphoreSlim SemanticSliceGate { get; } = new(1, 1);






    internal Func<string, FileSystemWatcher> WatcherFactory { private get; init; } = root => new FileSystemWatcher(root)
    {
        IncludeSubdirectories = true,
        NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
    };

    internal Func<string, CancellationToken, Task<bool>> FreshnessProbe { private get; init; } =
        (root, cancellationToken) => new IncrementalCodeMapIndexer().IsUpToDateAsync(root, cancellationToken);







    public void Dispose()
    {
        foreach (var entry in _freshness.Values)
            entry.Watcher?.Dispose();
        SemanticSliceGate.Dispose();
    }

    public async Task<bool> IsUpToDateAsync(string projectRoot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = Path.GetFullPath(projectRoot);
        var entry = _freshness.GetOrAdd(key, _ => new FreshnessEntry());

        Task<bool> probeTask;
        lock (entry.Gate)
        {
            if (entry.UpToDate is { } cached)
                return cached;




            // 해시 검사 중 발생한 변경도 놓치지 않도록 watcher를 먼저 시작한다.
            EnsureWatcher(key, entry);
            entry.InFlight ??= ProbeAndCacheAsync(key, entry);
            probeTask = entry.InFlight;
        }

        return await probeTask.WaitAsync(cancellationToken).ConfigureAwait(false);
    }


    public void InvalidateRoot(string projectRoot) => Invalidate(Path.GetFullPath(projectRoot));

    void IIndexFreshnessService.Invalidate(string projectRoot) => InvalidateRoot(projectRoot);

    private void Invalidate(string key)
    {
        if (_freshness.TryGetValue(key, out var entry))
            lock (entry.Gate)
            {
                entry.UpToDate = null;
                entry.Generation++;
            }
    }

    private void EnsureWatcher(string root, FreshnessEntry entry)
    {
        if (entry.Watcher is not null)
            return;
        try
        {
            var watcher = WatcherFactory(root);
            void OnEvent(object? _, FileSystemEventArgs e)
            {
                if (!IgnoreRules.IsIgnored(root, e.FullPath) && !IgnoreRules.IsOutsideRoot(root, e.FullPath))
                    Invalidate(root);
            }
            watcher.Changed += OnEvent;
            watcher.Created += OnEvent;
            watcher.Deleted += OnEvent;



            watcher.Renamed += (_, e) =>
            {
                if (!IgnoreRules.IsIgnored(root, e.OldFullPath) && !IgnoreRules.IsOutsideRoot(root, e.OldFullPath))
                    Invalidate(root);
                else
                    OnEvent(null, e);
            };
            watcher.Error += (_, _) =>
            {
                lock (entry.Gate)
                {
                    entry.UpToDate = null;
                    entry.Generation++;
                    entry.Watcher?.Dispose();
                    entry.Watcher = null;
                }
            };
            watcher.EnableRaisingEvents = true;
            entry.Watcher = watcher;
        }
        catch
        {




        }
    }

    private async Task<bool> ProbeAndCacheAsync(string root, FreshnessEntry entry)
    {
        int generation;
        lock (entry.Gate)
            generation = entry.Generation;
        try
        {
            var upToDate = await FreshnessProbe(root, CancellationToken.None).ConfigureAwait(false);
            lock (entry.Gate)
            {
                if (entry.Generation == generation && entry.Watcher is not null)
                    entry.UpToDate = upToDate;
                return entry.Generation == generation && upToDate;
            }
        }
        finally
        {
            lock (entry.Gate)
                entry.InFlight = null;
        }
    }

    private sealed class FreshnessEntry
    {
        public readonly object Gate = new();
        public bool? UpToDate;
        public int Generation;
        public Task<bool>? InFlight;
        public FileSystemWatcher? Watcher;
    }
}

[McpServerToolType]
public static class CodeMapTools
{
    [McpServerTool, Description("Find symbols in the CodeMap semantic index.")]
    public static async Task<string> FindSymbol(CodeMapMcpContext context, CodeMapApplication application, string query, string? root = null, int maxResults = 20, CancellationToken cancellationToken = default)
    {
        var response = await application.FindAsync(new FindRequest(query, root, maxResults), cancellationToken);
        if (!response.Succeeded)
            return JsonSerializer.Serialize(new { error = ErrorObject(response.Error!), stale = response.Stale });
        var matches = response.Value!.Matches;
        return JsonSerializer.Serialize(new
        {
            matches = matches.Select(symbol => new
            {
                symbol.Id,
                symbol.Project,
                kind = symbol.Kind.ToString(),
                symbol.Name,
                symbol.QualifiedName,
                file = symbol.RelativePath,
                symbol.StartLine,
                symbol.EndLine,
                symbol.Language
            }),
            stale = response.Stale
        });
    }

    [McpServerTool, Description("Build an agent-friendly context bundle for a task.")]
    public static async Task<string> GetContext(CodeMapMcpContext context, CodeMapApplication application, string task, string? root = null, int maxResults = 5, int tokens = 500, CancellationToken cancellationToken = default)
    {
        var response = await application.ContextAsync(new ContextRequest(task, root, maxResults, tokens), cancellationToken);
        if (!response.Succeeded)
            return JsonSerializer.Serialize(new { error = ErrorObject(response.Error!), stale = response.Stale });
        return JsonSerializer.Serialize(new
        {
            matches = response.Value!.Matches,
            mapLines = response.Value.Map.Lines,
            stale = response.Stale
        });
    }

    [McpServerTool, Description("Run a goal-directed investigation that internally combines find, callers, callees, impact, flow, and slice into one deterministic, budget-aware evidence bundle. Prefer this when the root symbol and goal are already known; use explain_relation separately when a specific edge evidence string is required.")]
    public static async Task<string> Investigate(
        CodeMapMcpContext context,
        CodeMapApplication application,
        string query,
        string goal,
        string? root = null,
        int tokens = 2000,
        int maxResults = 200,
        int? depth = null,
        double minConfidence = 0,
        string sourceMode = "minimal",
        CancellationToken cancellationToken = default)
    {
        if (!Enum.TryParse<InvestigationGoal>(goal, true, out var parsedGoal))
            return JsonSerializer.Serialize(new { version = 1, query, goal, error = new { code = "query_failed", message = "goal must be one of: debug, trace, impact, understand." }, reason = "query_failed" });
        var response = await application.InvestigateAsync(
            new InvestigationRequest(query, parsedGoal, ResolveRoot(context, root), tokens, maxResults, depth, minConfidence, true, sourceMode),
            cancellationToken);
        if (!response.Succeeded)
        {
            if (response.Error!.Code == "ambiguous" && response.Value is { } ambiguous)
                return JsonSerializer.Serialize(new
                {
                    version = 1,
                    query,
                    goal = parsedGoal.ToString().ToLowerInvariant(),
                    root = (object?)null,
                    isAmbiguous = true,
                    ambiguousCandidates = ambiguous.Resolution.AmbiguousCandidates.Select(ToMatch).ToArray(),
                    items = Array.Empty<object>(),
                    sourceSpans = Array.Empty<object>(),
                    budget = new { requested = ambiguous.Budget.RequestedBudget, estimated = 0, truncated = false, reason = (string?)null },
                    coverage = new { providers = Array.Empty<object>(), remaining = new Dictionary<string, int>(), negativeEvidence = Array.Empty<string>() },
                    stale = response.Stale,
                    warnings = Array.Empty<string>(),
                    reason = "ambiguous"
                });
            return JsonSerializer.Serialize(new { version = 1, query, goal = parsedGoal.ToString().ToLowerInvariant(), error = ErrorObject(response.Error!), reason = response.Error!.Code, stale = response.Stale });
        }
        var result = response.Value!;
        return JsonSerializer.Serialize(new
        {
            version = 1,
            query,
            goal = parsedGoal.ToString().ToLowerInvariant(),
            root = result.Resolution.Root is null ? null : ToMatch(result.Resolution.Root),
            isAmbiguous = result.Resolution.IsAmbiguous,
            ambiguousCandidates = result.Resolution.AmbiguousCandidates.Select(ToMatch).ToArray(),
            items = result.Items.Select(ToItem).ToArray(),
            sourceSpans = result.SourceSpans.Select(span => new
            {
                file = span.File,
                startLine = span.StartLine,
                endLine = span.EndLine,
                text = span.Text,
                candidateIds = span.CandidateIds
            }).ToArray(),
            budget = new
            {
                requested = result.Budget.RequestedBudget,
                estimated = result.Budget.EstimatedTokens,
                truncated = result.Budget.Truncated,
                reason = result.Budget.TruncationReason
            },
            coverage = new
            {
                providers = result.Coverage.Providers.Select(status => new
                {
                    provider = status.Provider.ToString().ToLowerInvariant(),
                    state = status.State.ToString().ToLowerInvariant(),
                    status.Reason,
                    foundCount = status.FoundCount
                }),
                result.Coverage.Remaining,
                negativeEvidence = result.Coverage.NegativeEvidence
            },
            stale = response.Stale,
            warnings = Array.Empty<string>(),
            reason = (string?)null
        });

        object ToMatch(IndexedSymbol symbol) => new
        {
            id = symbol.Id,
            project = symbol.Project,
            kind = symbol.Kind.ToString(),
            name = symbol.Name,
            qualifiedName = symbol.QualifiedName,
            file = symbol.RelativePath,
            startLine = symbol.StartLine,
            endLine = symbol.EndLine,
            language = symbol.Language
        };

        object ToItem(InvestigationCandidate candidate) => new
        {
            symbol = ToMatch(candidate.Symbol),
            via = candidate.Via is null ? null : new
            {
                edgeKind = candidate.Via.Kind.ToString(),
                resolutionKind = candidate.Via.ResolutionKind.ToString().ToLowerInvariant(),
                confidence = candidate.Via.Confidence,
                location = candidate.Via.SourceFileId is null && candidate.Via.Line is null
                    ? null
                    : new
                    {
                        file = candidate.EvidenceLocation?.File ?? candidate.Symbol.RelativePath,
                        startLine = candidate.EvidenceLocation?.StartLine ?? candidate.Via.Line,
                        startColumn = candidate.EvidenceLocation?.StartColumn ?? candidate.Via.StartColumn,
                        endLine = candidate.EvidenceLocation?.EndLine ?? candidate.Via.EndLine,
                        endColumn = candidate.EvidenceLocation?.EndColumn ?? candidate.Via.EndColumn
                    }
            },
            depth = candidate.Depth,
            provider = candidate.Provider,
            alsoFoundBy = candidate.AlsoFoundBy,
            localEvidence = candidate.LocalEvidence
        };
    }

    [McpServerTool, Description("Compute an intraprocedural C# semantic dependency slice for one executable symbol. Requires a fresh CodeMap index; use refresh_index first when the index is stale.")]
    public static async Task<string> GetSemanticSlice(
        CodeMapMcpContext context,
        CodeMapApplication application,
        string query,
        string direction = "backward",
        int? line = null,
        int? column = null,
        int maxResults = 80,
        bool includeSource = false,
        string? root = null,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.TryParse<SliceDirection>(direction, ignoreCase: true, out var parsedDirection))
            return JsonSerializer.Serialize(new { error = new { code = "query_failed", message = "direction must be 'backward' or 'forward'." } });

        var response = await application.SemanticSliceAsync(
            ResolveRoot(context, root),
            new SemanticSliceRequest(parsedDirection, line, column, maxResults, query, includeSource),
            cancellationToken);
        if (!response.Succeeded)
            return JsonSerializer.Serialize(new { error = ErrorObject(response.Error!), stale = response.Stale });
        var result = response.Value!;
        return JsonSerializer.Serialize(new
        {
            version = 1,
            query,
            direction = parsedDirection.ToString().ToLowerInvariant(),
            entrySymbol = result.EntrySymbol,
            scope = result.Scope,
            items = result.Items,
            dependencies = result.Dependencies,
            result.Truncated,
            source = result.Source,
            stale = response.Stale
        });
    }

    [McpServerTool, Description("Show reverse dependency impact for a symbol. profile 'code' (default) follows code references only; 'app' additionally reuses the routes/DI/UI edges flow already traverses.")]
    public static async Task<string> GetImpact(CodeMapMcpContext context, CodeMapApplication application, string query, string? root = null, int depth = 2, int maxResults = 20, string profile = "code", CancellationToken cancellationToken = default)
    {
        var response = await application.ImpactAsync(
            new ImpactRequest(query, root, depth, maxResults, profile), cancellationToken);
        if (!response.Succeeded)
            return JsonSerializer.Serialize(new { error = ErrorObject(response.Error!), query, stale = response.Stale });
        return JsonSerializer.Serialize(new
        {
            symbol = response.Value!.Symbol,
            impact = response.Value.Items,
            stale = response.Stale
        });
    }

    [McpServerTool, Description("Explain relations between two symbols with evidence metadata. Preferred follow-up after get_flow(includeEvidence=false) to prove the one selected edge, rather than requesting evidence for every relation up front.")]
    public static async Task<string> ExplainRelation(CodeMapMcpContext context, CodeMapApplication application, string source, string target, string? root = null, double minConfidence = 0, int maxResults = 20, CancellationToken cancellationToken = default)
    {
        var response = await application.RelationsAsync(
            new RelationsRequest(source, target, root, null, maxResults, minConfidence), cancellationToken);
        if (!response.Succeeded)
            return JsonSerializer.Serialize(new
            {
                error = ErrorObject(response.Error!),
                query = ErrorQuery(response.Error!, source, target),
                stale = response.Stale
            });
        var relations = response.Value!
            .Select(item => new
            {
                kind = "relation",
                source = item.Source.DisplayName,
                target = item.Target.DisplayName,
                sourceId = item.Source.Id,
                targetId = item.Target.Id,
                edgeKind = item.Edge.Kind.ToString(),
                resolutionKind = item.Edge.ResolutionKind.ToString().ToLowerInvariant(),
                confidence = item.Edge.Confidence,
                location = item.Evidence.File is null && item.Evidence.Line is null
                    ? null
                    : new { file = item.Evidence.File, line = item.Evidence.Line },
                evidence = item.Evidence.Evidence
            });
        return JsonSerializer.Serialize(new { relations, stale = response.Stale });
    }

    [McpServerTool, Description("Traverse HTTP/UI application flow edges (routes, DI, Razor/Blazor, WPF XAML) from an entry symbol or route. For first-pass discovery, prefer includeEvidence=false with the smallest useful depth/maxResults, then call explain_relation for the one edge that needs proof.")]





    public static async Task<string> GetFlow(CodeMapMcpContext context, CodeMapApplication application, string entry, string kind = "all", int depth = 4, string? root = null, int maxResults = 20, double minConfidence = 0, CancellationToken cancellationToken = default, bool includeEvidence = true)
    {
        var response = await application.FlowAsync(
            new FlowRequest(entry, kind, depth, root, maxResults, minConfidence), cancellationToken);
        if (!response.Succeeded)
            return JsonSerializer.Serialize(new { error = ErrorObject(response.Error!), query = entry, stale = response.Stale });
        var flowResult = response.Value!;
        var entrySymbol = flowResult.Entry;
        var flow = flowResult.Items;
        var sourceById = flowResult.Sources;






        object relations;
        if (includeEvidence)
        {
            relations = flow.Select(item =>
            {
                var sourceSymbol = sourceById.GetValueOrDefault(item.Via.SourceId) ?? entrySymbol;
                var evidenceValue = RelationEvidenceMapper.FromEdge(item.Via, sourceSymbol, item.Symbol,
                    item.Via.SourceFileId is not null && flowResult.Files.TryGetValue(item.Via.SourceFileId, out var file) ? file.RelativePath : null,
                    item.Via.Line);
                return new
                {
                    kind = "flow",
                    source = sourceSymbol.DisplayName,
                    target = item.Symbol.DisplayName,
                    sourceId = sourceSymbol.Id,
                    targetId = item.Symbol.Id,
                    depth = item.Depth,
                    edgeKind = item.Via.Kind.ToString(),
                    resolutionKind = item.Via.ResolutionKind.ToString().ToLowerInvariant(),
                    confidence = item.Via.Confidence,
                    location = evidenceValue.File is null && evidenceValue.Line is null
                        ? null
                        : new { file = evidenceValue.File, line = evidenceValue.Line },
                    evidence = evidenceValue.Evidence
                };
            }).ToArray();
        }
        else
        {
            relations = flow.Select(item =>
            {
                var sourceSymbol = sourceById.GetValueOrDefault(item.Via.SourceId) ?? entrySymbol;
                return new
                {
                    kind = "flow",
                    source = sourceSymbol.DisplayName,
                    target = item.Symbol.DisplayName,
                    sourceId = sourceSymbol.Id,
                    targetId = item.Symbol.Id,
                    depth = item.Depth,
                    edgeKind = item.Via.Kind.ToString(),
                    resolutionKind = item.Via.ResolutionKind.ToString().ToLowerInvariant(),
                    confidence = item.Via.Confidence
                };
            }).ToArray();
        }
        return JsonSerializer.Serialize(new
        {
            matches = new[]
            {
                new
                {
                    entrySymbol.Id,
                    entrySymbol.Project,
                    kind = entrySymbol.Kind.ToString(),
                    entrySymbol.Name,
                    entrySymbol.QualifiedName,
                    file = entrySymbol.RelativePath,
                    entrySymbol.StartLine,
                    entrySymbol.EndLine,
                    entrySymbol.Language
                }
            },
            relations,
            stale = response.Stale
        });
    }

    [McpServerTool, Description("Refresh the CodeMap index for the repository.")]
    public static async Task<string> RefreshIndex(CodeMapMcpContext context, CodeMapApplication application, string? root = null, bool force = false, CancellationToken cancellationToken = default)
    {
        var requestedRoot = ResolveRoot(context, root);






        string actualRoot;
        try
        {
            actualRoot = ResolveIndexRoot(requestedRoot);
        }
        catch (FileNotFoundException)
        {
            actualRoot = requestedRoot;
        }
        var response = await application.RefreshIndexAsync(new RefreshIndexRequest(actualRoot, force), cancellationToken);
        return response.Succeeded
            ? response.Value!.ToString()
            : JsonSerializer.Serialize(new { error = ErrorObject(response.Error!) });
    }

    [McpServerTool, Description("Read CodeMap index metadata and counts without modifying the index.")]
    public static async Task<string> GetStatus(
        CodeMapMcpContext context,
        CodeMapApplication application,
        string? root = null,
        bool checkFreshness = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await application.StatusAsync(new StatusRequest(ResolveRoot(context, root), checkFreshness), cancellationToken);
            if (!response.Succeeded)
                return JsonSerializer.Serialize(new { version = 1, error = ErrorObject(response.Error!) });
            var status = response.Value!;
            return JsonSerializer.Serialize(new
            {
                version = 1,
                indexState = status.IndexState,
                lastIndexedAtUtc = status.LastIndexedAtUtc,
                schemaVersion = status.SchemaVersion,
                schemaOutdated = status.SchemaOutdated,
                analyzerVersions = status.AnalyzerVersions,
                analyzerVersionsOutdated = status.AnalyzerVersionsOutdated,
                symbols = status.Symbols,
                edges = status.Edges,
                freshnessChecked = checkFreshness,
                stale = checkFreshness ? response.Stale : (bool?)null
            });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var (code, message) = CodeMapErrorClassifier.Classify(exception);
            return JsonSerializer.Serialize(new { version = 1, error = new { code, message } });
        }
    }

    // Source-compatible overloads for callers that invoke the tool methods
    // directly. MCP discovery uses the attributed overloads above and injects
    // the long-lived application instance from the host container.
    public static async Task<string> FindSymbol(CodeMapMcpContext context, string query, string? root = null, int maxResults = 20, CancellationToken cancellationToken = default)
    {
        var response = await FindSymbol(context, CodeMapMcpServer.CreateApplication(context), query, root, maxResults, cancellationToken);
        using var document = JsonDocument.Parse(response);
        if (document.RootElement.TryGetProperty("error", out var error)
            && error.ValueKind == JsonValueKind.Object
            && error.TryGetProperty("code", out var code)
            && code.GetString() == "schema_outdated")
            throw new InvalidOperationException(error.GetProperty("message").GetString());
        return response;
    }

    public static Task<string> GetContext(CodeMapMcpContext context, string task, string? root = null, int maxResults = 5, int tokens = 500, CancellationToken cancellationToken = default) =>
        GetContext(context, CodeMapMcpServer.CreateApplication(context), task, root, maxResults, tokens, cancellationToken);

    public static Task<string> GetSemanticSlice(CodeMapMcpContext context, string query, string direction = "backward", int? line = null, int? column = null, int maxResults = 80, bool includeSource = false, string? root = null, CancellationToken cancellationToken = default) =>
        GetSemanticSlice(context, CodeMapMcpServer.CreateApplication(context), query, direction, line, column, maxResults, includeSource, root, cancellationToken);

    public static Task<string> GetImpact(CodeMapMcpContext context, string query, string? root = null, int depth = 2, int maxResults = 20, string profile = "code", CancellationToken cancellationToken = default) =>
        LegacyErrorResponse(GetImpact(context, CodeMapMcpServer.CreateApplication(context), query, root, depth, maxResults, profile, cancellationToken));

    public static Task<string> ExplainRelation(CodeMapMcpContext context, string source, string target, string? root = null, double minConfidence = 0, int maxResults = 20, CancellationToken cancellationToken = default) =>
        LegacyErrorResponse(ExplainRelation(context, CodeMapMcpServer.CreateApplication(context), source, target, root, minConfidence, maxResults, cancellationToken));

    public static Task<string> GetFlow(CodeMapMcpContext context, string entry, string kind = "all", int depth = 4, string? root = null, int maxResults = 20, double minConfidence = 0, CancellationToken cancellationToken = default, bool includeEvidence = true) =>
        LegacyErrorResponse(GetFlow(context, CodeMapMcpServer.CreateApplication(context), entry, kind, depth, root, maxResults, minConfidence, cancellationToken, includeEvidence));

    public static Task<string> RefreshIndex(CodeMapMcpContext context, string? root = null, bool force = false, CancellationToken cancellationToken = default) =>
        RefreshIndex(context, CodeMapMcpServer.CreateApplication(context), root, force, cancellationToken);

    public static Task<string> GetStatus(CodeMapMcpContext context, string? root = null, bool checkFreshness = false, CancellationToken cancellationToken = default) =>
        GetStatus(context, CodeMapMcpServer.CreateApplication(context), root, checkFreshness, cancellationToken);

    private static async Task<string> LegacyErrorResponse(Task<string> responseTask)
    {
        var response = await responseTask;
        using var document = JsonDocument.Parse(response);
        if (!document.RootElement.TryGetProperty("error", out var error)
            || error.ValueKind != JsonValueKind.Object
            || !(error.TryGetProperty("code", out var code) || error.TryGetProperty("Code", out code)))
            return response;
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
            values[property.Name] = property.NameEquals("error") ? code.GetString() : property.Value;
        return JsonSerializer.Serialize(values);
    }

    private static object ErrorObject(QueryError error) =>
        new { code = error.Code, message = error.Message };

    private static string ErrorQuery(QueryError error, string first, string second) =>
        error.Message.StartsWith("Symbol query '", StringComparison.Ordinal)
            ? error.Message[14..].Split('\'', 2)[0]
            : error.Message.Contains(second, StringComparison.Ordinal) ? second : first;

    private static string ResolveRoot(CodeMapMcpContext context, string? root) =>
        string.IsNullOrWhiteSpace(root) ? context.DefaultRoot : Path.GetFullPath(root);


    private static string ResolveIndexRoot(string queryRoot)
    {
        var databasePath = CodeMapIndexLocator.FindDatabase(queryRoot);
        return CodeMapIndexLocator.ResolveIndexRoot(databasePath);
    }

}
