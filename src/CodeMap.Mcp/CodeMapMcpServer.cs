using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text.Json;
using CodeMap.Core;
using CodeMap.Storage;
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
        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithToolsFromAssembly();
        await builder.Build().RunAsync(cancellationToken);
    }
}

















public sealed class CodeMapMcpContext(string defaultRoot) : IDisposable
{
    public string DefaultRoot { get; } = defaultRoot;

    private readonly ConcurrentDictionary<string, FreshnessEntry> _freshness = new(StringComparer.OrdinalIgnoreCase);






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
    }

    public async Task<bool> IsUpToDateAsync(string projectRoot, CancellationToken cancellationToken)
    {
        var key = Path.GetFullPath(projectRoot);
        var entry = _freshness.GetOrAdd(key, _ => new FreshnessEntry());

        int generation;
        lock (entry.Gate)
        {
            if (entry.UpToDate is { } cached)
                return cached;




            // 해시 검사 중 발생한 변경도 놓치지 않도록 watcher를 먼저 시작한다.
            EnsureWatcher(key, entry);
            generation = entry.Generation;
        }

        var upToDate = await FreshnessProbe(key, cancellationToken);

        lock (entry.Gate)
        {







            if (entry.Generation != generation)
                return false;
            if (entry.Watcher is not null)
                entry.UpToDate = upToDate;
        }
        return upToDate;
    }


    public void InvalidateRoot(string projectRoot) => Invalidate(Path.GetFullPath(projectRoot));

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
            watcher.Error += (_, _) => Invalidate(root);
            watcher.EnableRaisingEvents = true;
            entry.Watcher = watcher;
        }
        catch
        {




        }
    }

    private sealed class FreshnessEntry
    {
        public readonly object Gate = new();
        public bool? UpToDate;
        public int Generation;
        public FileSystemWatcher? Watcher;
    }
}

[McpServerToolType]
public static class CodeMapTools
{
    [McpServerTool, Description("Find symbols in the CodeMap semantic index.")]
    public static async Task<string> FindSymbol(CodeMapMcpContext context, string query, string? root = null, int maxResults = 20, CancellationToken cancellationToken = default)
    {
        var loaded = await OpenServiceAsync(context, root, cancellationToken);
        await using var service = loaded.Service;
        var matches = service.Find(query, maxResults);
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
            stale = loaded.Stale
        });
    }

    [McpServerTool, Description("Build an agent-friendly context bundle for a task.")]
    public static async Task<string> GetContext(CodeMapMcpContext context, string task, string? root = null, int maxResults = 5, int tokens = 500, CancellationToken cancellationToken = default)
    {



        var loaded = await OpenServiceAsync(context, root, cancellationToken);
        await using var service = loaded.Service;
        var matches = service.Find(task, maxResults);
        var map = service.BuildMap(matches.FirstOrDefault()?.Name ?? task, null, tokens);
        return JsonSerializer.Serialize(new { matches, mapLines = map.Lines, stale = loaded.Stale });
    }

    [McpServerTool, Description("Show reverse dependency impact for a symbol. profile 'code' (default) follows code references only; 'app' additionally reuses the routes/DI/UI edges flow already traverses.")]
    public static async Task<string> GetImpact(CodeMapMcpContext context, string query, string? root = null, int depth = 2, int maxResults = 20, string profile = "code", CancellationToken cancellationToken = default)
    {
        if (!CodeMapQueryService.IsValidImpactProfile(profile))
            return JsonSerializer.Serialize(new { error = new { code = "query_failed", message = "profile must be one of: code, app." } });

        var loaded = await OpenServiceAsync(context, root, cancellationToken);
        await using var service = loaded.Service;
        var resolution = service.ResolveSymbol(query, callableOnly: false, maxResults);
        if (resolution.Matches.Count == 0)
            return JsonSerializer.Serialize(new { error = "no_matches", stale = loaded.Stale });
        if (resolution.Matches.Count > 1)
            return JsonSerializer.Serialize(new { error = "ambiguous", query, stale = loaded.Stale });
        var symbol = resolution.Matches[0];
        var impact = service.Impact(symbol, depth, maxResults, profile);
        return JsonSerializer.Serialize(new { symbol, impact, stale = loaded.Stale });
    }

    [McpServerTool, Description("Explain relations between two symbols with evidence metadata. Preferred follow-up after get_flow(includeEvidence=false) to prove the one selected edge, rather than requesting evidence for every relation up front.")]
    public static async Task<string> ExplainRelation(CodeMapMcpContext context, string source, string target, string? root = null, double minConfidence = 0, int maxResults = 20, CancellationToken cancellationToken = default)
    {
        if (!RelationConfidence.IsValid(minConfidence))
            return JsonSerializer.Serialize(new
            {
                error = new
                {
                    code = "query_failed",
                    message = RelationConfidence.InvalidMessage
                }
            });

        var loaded = await OpenServiceAsync(context, root, cancellationToken);
        await using var service = loaded.Service;
        var sourceResolution = service.ResolveSymbol(source, callableOnly: false, maxResults);
        if (sourceResolution.Matches.Count == 0)
            return JsonSerializer.Serialize(new { error = "no_matches", stale = loaded.Stale });
        if (sourceResolution.Matches.Count > 1)
            return JsonSerializer.Serialize(new { error = "ambiguous", query = source, stale = loaded.Stale });
        var sourceSymbol = sourceResolution.Matches[0];

        var targetResolution = service.ResolveSymbol(target, callableOnly: false, maxResults);
        if (targetResolution.Matches.Count == 0)
            return JsonSerializer.Serialize(new { error = "no_matches", stale = loaded.Stale });
        if (targetResolution.Matches.Count > 1)
            return JsonSerializer.Serialize(new { error = "ambiguous", query = target, stale = loaded.Stale });
        var targetSymbol = targetResolution.Matches[0];
        var relations = service.Relations(sourceSymbol.Id, targetSymbol.Id, edgeKind: null, maxResults, minConfidence)
            .Select(item => new
            {
                kind = "relation",
                source = sourceSymbol.DisplayName,
                target = targetSymbol.DisplayName,
                sourceId = sourceSymbol.Id,
                targetId = targetSymbol.Id,
                edgeKind = item.Edge.Kind.ToString(),
                resolutionKind = item.Edge.ResolutionKind.ToString().ToLowerInvariant(),
                confidence = item.Edge.Confidence,
                location = item.Evidence.File is null && item.Evidence.Line is null
                    ? null
                    : new { file = item.Evidence.File, line = item.Evidence.Line },
                evidence = item.Evidence.Evidence
            });
        return JsonSerializer.Serialize(new { relations, stale = loaded.Stale });
    }

    [McpServerTool, Description("Traverse HTTP/UI application flow edges (routes, DI, Razor/Blazor, WPF XAML) from an entry symbol or route. For first-pass discovery, prefer includeEvidence=false with the smallest useful depth/maxResults, then call explain_relation for the one edge that needs proof.")]





    public static async Task<string> GetFlow(CodeMapMcpContext context, string entry, string kind = "all", int depth = 4, string? root = null, int maxResults = 20, double minConfidence = 0, CancellationToken cancellationToken = default, bool includeEvidence = true)
    {
        if (!RelationConfidence.IsValid(minConfidence))
            return JsonSerializer.Serialize(new { error = new { code = "query_failed", message = RelationConfidence.InvalidMessage } });
        if (!CodeMapQueryService.IsValidFlowKind(kind))
            return JsonSerializer.Serialize(new { error = new { code = "query_failed", message = "kind must be one of: http, ui, all." } });
        if (!CodeMapQueryService.IsValidFlowDepth(depth))
            return JsonSerializer.Serialize(new { error = new { code = "query_failed", message = $"depth must be between {CodeMapQueryService.FlowMinDepth} and {CodeMapQueryService.FlowMaxDepth}." } });

        var loaded = await OpenServiceAsync(context, root, cancellationToken);
        await using var service = loaded.Service;
        var resolution = service.ResolveSymbol(entry, callableOnly: false, maxResults);
        if (resolution.Matches.Count == 0)
            return JsonSerializer.Serialize(new { error = "no_matches", stale = loaded.Stale });
        if (resolution.Matches.Count > 1)
            return JsonSerializer.Serialize(new { error = "ambiguous", query = entry, stale = loaded.Stale });
        var entrySymbol = resolution.Matches[0];
        var flow = service.Flow(entrySymbol, kind, depth, maxResults, minConfidence);
        var sourceById = service.FindByIds(flow.Select(item => item.Via.SourceId));






        object relations;
        if (includeEvidence)
        {
            var fileById = service.FindFilesByIds(flow.Select(item => item.Via.SourceFileId).OfType<string>());
            relations = flow.Select(item =>
            {
                var sourceSymbol = sourceById.GetValueOrDefault(item.Via.SourceId) ?? entrySymbol;
                var evidenceValue = RelationEvidenceMapper.FromEdge(item.Via, sourceSymbol, item.Symbol,
                    item.Via.SourceFileId is not null && fileById.TryGetValue(item.Via.SourceFileId, out var file) ? file.RelativePath : null,
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
            stale = loaded.Stale
        });
    }

    [McpServerTool, Description("Refresh the CodeMap index for the repository.")]
    public static async Task<string> RefreshIndex(CodeMapMcpContext context, string? root = null, bool force = false, CancellationToken cancellationToken = default)
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
        var summary = force
            ? await new IncrementalCodeMapIndexer().IndexAsync(actualRoot, force: true, cancellationToken)
            : await new IncrementalCodeMapIndexer().UpdateAsync(actualRoot, cancellationToken);




        context.InvalidateRoot(ResolveIndexRoot(actualRoot));
        return summary.ToString();
    }

    private static string ResolveRoot(CodeMapMcpContext context, string? root) =>
        string.IsNullOrWhiteSpace(root) ? context.DefaultRoot : Path.GetFullPath(root);


    private static string ResolveIndexRoot(string queryRoot)
    {
        var databasePath = FindDatabase(queryRoot);
        return Path.GetDirectoryName(Path.GetDirectoryName(databasePath))!;
    }

    private static async Task<(CodeMapQueryService Service, bool Stale)> OpenServiceAsync(CodeMapMcpContext context, string? root, CancellationToken cancellationToken)
    {
        var databasePath = FindDatabase(ResolveRoot(context, root));
        var store = new CodeMapQueryStore(databasePath);
        var connectionTask = store.OpenReadOnlyConnectionAsync(cancellationToken);
        var staleTask = context.IsUpToDateAsync(Path.GetDirectoryName(Path.GetDirectoryName(databasePath))!, cancellationToken);
        var connection = await connectionTask;
        try
        {
            var upToDate = await staleTask;
            return (new CodeMapQueryService(connection), !upToDate);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await connection.DisposeAsync();
            throw;
        }
        catch
        {




            return (new CodeMapQueryService(connection), Stale: true);
        }
    }

    private static string FindDatabase(string root)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(root));
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, ".codemap", "index.db");
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent!;
        }
        throw new FileNotFoundException("No CodeMap index found. Run: codemap index");
    }
}
