using System.CommandLine;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeMap.Core.Models;
using CodeMap.Mcp;
using CodeMap.Storage;

namespace CodeMap.Cli;

public static partial class Program
{
    private const int JsonSchemaVersion = 4;
    private const int JsonSchemaVersionWithEvidence = 5;
    private const int DefaultMaxResults = 20;
    private const int DefaultDepth = 1;
    private const int DefaultMapTokens = 500;
    private static CancellationToken ShutdownToken { get; set; }

    public static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        ShutdownToken = cancellation.Token;
        var rootCommand = new RootCommand("CodeMap — local semantic code indexer and query CLI.");

        var versionCommand = new Command("version", "Show version information.");
        versionCommand.SetHandler(PrintVersion);
        rootCommand.AddCommand(versionCommand);

        var indexPath = new Argument<string>("path", () => Directory.GetCurrentDirectory(), "Repository, solution, or project path.") { Arity = ArgumentArity.ZeroOrOne };
        var force = new Option<bool>("--force", "Rebuild the index even when an index already exists.");
        var indexCommand = new Command("index", "Build a complete semantic index.");
        indexCommand.AddArgument(indexPath);
        indexCommand.AddOption(force);
        indexCommand.SetHandler(async (string path, bool rebuild) => await RunIndexAsync(path, rebuild), indexPath, force);
        rootCommand.AddCommand(indexCommand);

        var updatePath = new Argument<string>("path", () => Directory.GetCurrentDirectory(), "Repository, solution, or project path.") { Arity = ArgumentArity.ZeroOrOne };
        var updateCommand = new Command("update", "Update the index using content hashes.");
        updateCommand.AddArgument(updatePath);
        updateCommand.SetHandler(async (string path) => await RunUpdateAsync(path), updatePath);
        rootCommand.AddCommand(updateCommand);

        AddFindCommand(rootCommand);
        AddPairRelationCommand(rootCommand);
        AddRelationCommand(rootCommand, "refs", "Show references to a symbol.", RunRefsAsync);
        AddRelationCommand(rootCommand, "callers", "Show callers of a method.", RunCallersAsync);
        AddRelationCommand(rootCommand, "callees", "Show methods called by a method.", RunCalleesAsync, withDepth: true);
        AddRelationCommand(rootCommand, "impl", "Show implementations of a type or method.", RunImplAsync);
        AddImpactCommand(rootCommand);
        AddDiffCommand(rootCommand);
        AddCheckCommand(rootCommand);
        AddWatchCommand(rootCommand);
        AddReportCommand(rootCommand);
        AddMcpCommand(rootCommand);
        AddLspCommand(rootCommand);
        AddMapCommand(rootCommand);
        AddContextCommand(rootCommand);
        AddFlowCommand(rootCommand);

        var invocationCode = await rootCommand.InvokeAsync(args);


        return Environment.ExitCode != 0 ? Environment.ExitCode : invocationCode;
    }

    private static void AddFindCommand(RootCommand root)
    {
        var query = new Argument<string>("query", "Symbol name, qualified name, or partial query.");
        var options = CreateQueryOptions();
        var command = new Command("find", "Find symbols in the semantic index.");
        command.AddArgument(query);
        AddOptions(command, options, includeDepth: false);
        command.SetHandler(async (string value, string? rootPath, int maxResults, bool json) =>
            await RunFindAsync(value, rootPath, maxResults, json), query, options.Root, options.MaxResults, options.Json);
        root.AddCommand(command);
    }

    private static void AddPairRelationCommand(RootCommand root)
    {
        var source = new Argument<string>("source", "Source symbol name or qualified name.");
        var target = new Argument<string>("target", "Target symbol name or qualified name.");
        var options = CreateQueryOptions();
        var evidence = new Option<bool>("--evidence", "Emit JSON schema v5 with relation evidence.");
        var minConfidence = new Option<double>("--min-confidence", () => 0, "Minimum confidence for relation edges (0..1).");
        var command = new Command("relation", "Show edges between two symbols with optional evidence.");
        command.AddArgument(source);
        command.AddArgument(target);
        command.AddOption(options.Root);
        command.AddOption(options.MaxResults);
        command.AddOption(evidence);
        command.AddOption(minConfidence);
        command.AddOption(options.Json);
        command.SetHandler(async (string sourceValue, string targetValue, string? rootPath, int maxResults, bool evidenceValue, double minConfidenceValue, bool json) =>
        {
            if (!ValidateMinConfidence(minConfidenceValue, json, evidenceValue))
            {
                Exit(1);
                return;
            }
            await RunPairRelationAsync(sourceValue, targetValue, rootPath, maxResults, evidenceValue, minConfidenceValue, json);
        },
            source, target, options.Root, options.MaxResults, evidence, minConfidence, options.Json);
        root.AddCommand(command);
    }

    private static void AddRelationCommand(
        RootCommand root,
        string name,
        string description,
        Func<string, string?, int, int, bool, bool, double, Task<int>> handler,
        bool withDepth = false)
    {
        var query = new Argument<string>("query", "Symbol name or qualified name.");
        var options = CreateQueryOptions();
        var evidence = new Option<bool>("--evidence", "Emit JSON schema v5 with relation evidence. Use for verification after a specific relation is selected, not as the default for discovery.");
        var minConfidence = new Option<double>("--min-confidence", () => 0, "Minimum confidence for relation edges (0..1).");
        var command = new Command(name, description);
        command.AddArgument(query);
        AddOptions(command, options, withDepth);
        command.AddOption(evidence);
        command.AddOption(minConfidence);
        if (withDepth)
            command.SetHandler(async (string value, string? rootPath, int maxResults, int depth, bool json, bool evidenceValue, double minConfidenceValue) =>
            {
                if (!ValidateMinConfidence(minConfidenceValue, json, evidenceValue))
                {
                    Exit(1);
                    return;
                }
                await handler(value, rootPath, maxResults, depth, json, evidenceValue, minConfidenceValue);
            }, query, options.Root, options.MaxResults, options.Depth, options.Json, evidence, minConfidence);
        else
            command.SetHandler(async (string value, string? rootPath, int maxResults, bool json, bool evidenceValue, double minConfidenceValue) =>
            {
                if (!ValidateMinConfidence(minConfidenceValue, json, evidenceValue))
                {
                    Exit(1);
                    return;
                }
                await handler(value, rootPath, maxResults, DefaultDepth, json, evidenceValue, minConfidenceValue);
            }, query, options.Root, options.MaxResults, options.Json, evidence, minConfidence);
        root.AddCommand(command);
    }

    private static void AddContextCommand(RootCommand root)
    {
        var task = new Argument<string>("task", "Symbol name, file hint, or short task description.");
        var options = CreateQueryOptions();
        var tokens = new Option<int>("--tokens", () => DefaultMapTokens, "Approximate output token budget for the map section.");
        var evidence = new Option<bool>("--evidence", "Emit JSON schema v5 with relation evidence. Use for verification after a specific relation is selected, not as the default for discovery.");
        var minConfidence = new Option<double>("--min-confidence", () => 0, "Minimum confidence for relation edges (0..1).");
        var command = new Command("context", "Build a single agent-friendly context bundle for a task.");
        command.AddArgument(task);
        command.AddOption(options.Root);
        command.AddOption(options.MaxResults);
        command.AddOption(tokens);
        command.AddOption(evidence);
        command.AddOption(minConfidence);
        command.AddOption(options.Json);
        command.SetHandler(async (string taskValue, string? rootPath, int maxResults, int tokenBudget, bool evidenceValue, double minConfidenceValue, bool json) =>
        {
            if (!ValidateMinConfidence(minConfidenceValue, json, evidenceValue))
            {
                Exit(1);
                return;
            }
            await RunContextAsync(taskValue, rootPath, maxResults, tokenBudget, evidenceValue, minConfidenceValue, json);
        },
            task, options.Root, options.MaxResults, tokens, evidence, minConfidence, options.Json);
        root.AddCommand(command);
    }

    private static void AddMapCommand(RootCommand root)
    {
        var focus = new Option<string?>("--focus", "Prefer symbols close to this name or graph neighborhood.");
        var project = new Option<string?>("--project", "Restrict the map to a project name.");
        var tokens = new Option<int>("--tokens", () => DefaultMapTokens, "Approximate output token budget.");
        var format = new Option<string>("--format", () => "text", "Output format: text, mermaid, or json.");
        var rootPath = new Option<string?>("--root", "Project root or directory containing .codemap/index.db.");
        var json = new Option<bool>("--json", "Emit stable JSON instead of compact text.");
        var command = new Command("map", "Generate a compact agent-friendly repository map.");
        command.AddOption(focus);
        command.AddOption(project);
        command.AddOption(tokens);
        command.AddOption(format);
        command.AddOption(rootPath);
        command.AddOption(json);
        command.SetHandler(async (string? focusValue, string? projectValue, int tokenBudget, string formatValue, string? rootValue, bool jsonValue) =>
            await RunMapAsync(focusValue, projectValue, tokenBudget, formatValue, rootValue, jsonValue), focus, project, tokens, format, rootPath, json);
        root.AddCommand(command);
    }

    private sealed record QueryOptions(Option<string?> Root, Option<int> MaxResults, Option<int> Depth, Option<bool> Json);

    private static QueryOptions CreateQueryOptions() => new(
        new Option<string?>("--root", "Project root or directory containing .codemap/index.db."),
        new Option<int>("--max-results", () => DefaultMaxResults, "Maximum number of returned symbols."),
        new Option<int>("--depth", () => DefaultDepth, "Traversal depth for graph queries. Focused agent queries should start at the default (1) and expand only if the answer requires transitive results."),
        new Option<bool>("--json", "Emit stable JSON instead of compact text."));

    private static void AddOptions(Command command, QueryOptions options, bool includeDepth)
    {
        command.AddOption(options.Root);
        command.AddOption(options.MaxResults);
        command.AddOption(options.Json);
        if (includeDepth)
            command.AddOption(options.Depth);
    }

    private static void PrintVersion()
    {
        var version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
        Console.WriteLine($"codemap {version}");
    }

    private static async Task<int> RunIndexAsync(string path, bool force)
    {
        try
        {
            Console.WriteLine((await new IncrementalCodeMapIndexer().IndexAsync(path, force, ShutdownToken)).ToString());
            return Exit(0);
        }
        catch (OperationCanceledException) when (ShutdownToken.IsCancellationRequested)
        {
            Console.Error.WriteLine("codemap index canceled.");
            return Exit(130);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"codemap index failed: {exception.Message}");
            return Exit(1);
        }
    }

    private static async Task<int> RunUpdateAsync(string path)
    {
        try
        {
            Console.WriteLine((await new IncrementalCodeMapIndexer().UpdateAsync(path, ShutdownToken)).ToString());
            return Exit(0);
        }
        catch (OperationCanceledException) when (ShutdownToken.IsCancellationRequested)
        {
            Console.Error.WriteLine("codemap update canceled.");
            return Exit(130);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"codemap update failed: {exception.Message}");
            return Exit(1);
        }
    }

    private static async Task<int> RunFindAsync(string query, string? root, int maxResults, bool json)
    {
        try
        {
            var loaded = await LoadQueryServiceAsync(root);
            await using var service = loaded.Service;
            var stale = loaded.Stale;
            var matches = service.Find(query, maxResults);
            if (json)
            {
                var reason = matches.Count == 0 ? "no_matches" : null;
                WriteJson(new QueryResponse(JsonSchemaVersion, query, matches.Select(ToMatch).ToArray(), Array.Empty<QueryRelation>(), stale, reason));
            }
            else
            {
                WarnIfStale(stale);
                foreach (var match in matches)
                    WriteFindMatch(match, service);
            }
            return Exit(matches.Count == 0 ? 2 : 0);
        }
        catch (Exception exception) { return HandleQueryError(exception, json); }
    }

    private static async Task<int> RunRefsAsync(string query, string? root, int maxResults, int depth, bool json, bool evidence, double minConfidence)
    {
        return await RunResolvedAsync(query, root, maxResults, json, evidence, minConfidence, callableOnly: false, (service, symbol) =>
        {
            var implemented = service.ImplementationRelations(symbol, maxResults).Where(item => MeetsMinConfidence(item.Edge, minConfidence)).ToArray();
            var referenced = service.ReferencedByRelations(symbol, maxResults).Where(item => MeetsMinConfidence(item.Edge, minConfidence)).ToArray();
            var fileById = PreloadEvidenceFiles(service, evidence, implemented.Select(item => item.Edge).Concat(referenced.Select(item => item.Edge)));
            var relations = implemented.Select(item => ToRelation("implemented-by", item.Symbol, symbol, null, item.Edge, service, evidence, preloadedFileById: fileById))
                .Concat(referenced.Select(item => ToRelation("referenced-by", item.Symbol, symbol, null, item.Edge, service, evidence, preloadedFileById: fileById)))
                .ToArray();
            if (json) return new QueryResponse(SchemaVersion(evidence), query, [ToMatch(symbol)], relations);
            Console.WriteLine(ToDisplay(symbol));
            WriteSymbolFile(symbol);
            WriteSection("implemented-by", implemented.Select(relation => relation.Symbol).ToArray());
            WriteSection("referenced-by", referenced.Select(relation => relation.Symbol).ToArray());
            return new QueryResponse(0, string.Empty, [], []);
        });
    }

    private static async Task<int> RunCallersAsync(string query, string? root, int maxResults, int depth, bool json, bool evidence, double minConfidence)
    {
        return await RunResolvedAsync(query, root, maxResults, json, evidence, minConfidence, callableOnly: true, (service, symbol) =>
        {
            var callers = service.CallerRelations(symbol, maxResults).Where(item => MeetsMinConfidence(item.Edge, minConfidence)).ToArray();
            var fileById = PreloadEvidenceFiles(service, evidence, callers.Select(item => item.Edge));
            var relations = callers.Select(item => ToRelation("caller", item.Symbol, symbol, null, item.Edge, service, evidence, preloadedFileById: fileById))
                .ToArray();
            if (json) return new QueryResponse(SchemaVersion(evidence), query, [ToMatch(symbol)], relations);
            Console.WriteLine(ToDisplay(symbol));
            WriteSymbolFile(symbol);
            WriteSection("callers", callers.Select(relation => relation.Symbol).ToArray());
            return new QueryResponse(0, string.Empty, [], []);
        });
    }

    private static async Task<int> RunCalleesAsync(string query, string? root, int maxResults, int depth, bool json, bool evidence, double minConfidence)
    {
        return await RunResolvedAsync(query, root, maxResults, json, evidence, minConfidence, callableOnly: true, (service, symbol) =>
        {
            var callees = service.CalleeRelations(symbol, depth, maxResults).Where(item => MeetsMinConfidence(item.Edge, minConfidence)).ToArray();
            var fileById = PreloadEvidenceFiles(service, evidence, callees.Select(item => item.Edge));
            var relations = callees.Select(item => ToRelation("callee", symbol, item.Symbol, null, item.Edge, service, evidence, preloadedFileById: fileById))
                .ToArray();
            if (json) return new QueryResponse(SchemaVersion(evidence), query, [ToMatch(symbol)], relations);
            Console.WriteLine(ToDisplay(symbol));
            WriteSymbolFile(symbol);
            WriteSection("callees", callees.Select(relation => relation.Symbol).ToArray());
            return new QueryResponse(0, string.Empty, [], []);
        });
    }

    private static async Task<int> RunImplAsync(string query, string? root, int maxResults, int depth, bool json, bool evidence, double minConfidence)
    {
        return await RunResolvedAsync(query, root, maxResults, json, evidence, minConfidence, callableOnly: false, (service, symbol) =>
        {
            var implementations = service.ImplementationRelations(symbol, maxResults).Where(item => MeetsMinConfidence(item.Edge, minConfidence)).ToArray();
            var fileById = PreloadEvidenceFiles(service, evidence, implementations.Select(item => item.Edge));
            var relations = implementations.Select(item => ToRelation("implemented-by", symbol, item.Symbol, null, item.Edge, service, evidence, preloadedFileById: fileById))
                .ToArray();
            if (json) return new QueryResponse(SchemaVersion(evidence), query, [ToMatch(symbol)], relations);
            Console.WriteLine(ToDisplay(symbol));
            WriteSection("implementations", implementations.Select(relation => relation.Symbol).ToArray());
            return new QueryResponse(0, string.Empty, [], []);
        });
    }

    private static async Task<int> RunImpactAsync(string query, string? root, int maxResults, int depth, bool json, bool evidence, double minConfidence, string profile = "code")
    {
        return await RunResolvedAsync(query, root, maxResults, json, evidence, minConfidence, callableOnly: false, (service, symbol) =>
        {
            var impact = service.Impact(symbol, depth, maxResults, profile).Where(item => MeetsMinConfidence(item.Via, minConfidence)).ToArray();
            var fileById = PreloadEvidenceFiles(service, evidence, impact.Select(item => item.Via));
            var relations = impact.Select(item => ToRelation("impact", item.Symbol, symbol, item.Depth, item.Via, service, evidence, preloadedFileById: fileById))
                .ToArray();
            if (json) return new QueryResponse(SchemaVersion(evidence), query, [ToMatch(symbol)], relations);
            Console.WriteLine(ToDisplay(symbol));
            foreach (var item in impact)
                Console.WriteLine($"<- {ToDisplay(item.Symbol)}");
            return new QueryResponse(0, string.Empty, [], []);
        });
    }

    private static async Task<int> RunPairRelationAsync(string sourceQuery, string targetQuery, string? root, int maxResults, bool evidence, double minConfidence, bool json)
    {
        if (!evidence)
        {
            const string message = "relation requires --evidence; relation responses use JSON schema v5.";
            if (json)
                WriteJson(new ErrorResponse(JsonSchemaVersion, new ErrorDto("query_failed", message)));
            else
                Console.Error.WriteLine(message);
            return Exit(1);
        }
        if (!json)
        {
            Console.Error.WriteLine("relation requires --json when using --evidence output.");
            return Exit(1);
        }
        try
        {
            var loaded = await LoadQueryServiceAsync(root);
            await using var service = loaded.Service;
            var stale = loaded.Stale;
            var sourceCandidates = service.ResolveSymbol(sourceQuery, callableOnly: false, maxResults).Matches;
            if (sourceCandidates.Count == 0)
            {
                WriteJson(new QueryResponse(JsonSchemaVersionWithEvidence, sourceQuery, [], [], stale, "no_matches"));
                return Exit(2);
            }
            if (sourceCandidates.Count > 1)
            {
                WriteJson(new QueryResponse(JsonSchemaVersionWithEvidence, sourceQuery, sourceCandidates.Take(maxResults).Select(ToMatch).ToArray(), [], stale, "ambiguous"));
                return Exit(2);
            }
            var targetCandidates = service.ResolveSymbol(targetQuery, callableOnly: false, maxResults).Matches;
            if (targetCandidates.Count == 0)
            {
                WriteJson(new QueryResponse(JsonSchemaVersionWithEvidence, targetQuery, [ToMatch(sourceCandidates[0])], [], stale, "no_matches"));
                return Exit(2);
            }
            if (targetCandidates.Count > 1)
            {
                WriteJson(new QueryResponse(JsonSchemaVersionWithEvidence, targetQuery, targetCandidates.Take(maxResults).Select(ToMatch).ToArray(), [], stale, "ambiguous"));
                return Exit(2);
            }
            var source = sourceCandidates[0];
            var target = targetCandidates[0];
            var relations = service.Relations(source.Id, target.Id, edgeKind: null, maxResults, minConfidence)
                .Select(item => ToRelation("relation", item.Source, item.Target, null, item.Edge, service, evidence: true, item.Evidence))
                .ToArray();
            WriteJson(new PairRelationResponse(JsonSchemaVersionWithEvidence, sourceQuery, targetQuery, [ToMatch(source), ToMatch(target)], relations, stale));
            return Exit(0);
        }
        catch (Exception exception) { return HandleQueryError(exception, json, evidence); }
    }

    private static async Task<int> RunMapAsync(string? focus, string? project, int tokenBudget, string format, string? root, bool json)
    {
        try
        {
            if (tokenBudget <= 0) throw new ArgumentOutOfRangeException(nameof(tokenBudget), "Token budget must be positive.");
            var isMermaid = string.Equals(format, "mermaid", StringComparison.OrdinalIgnoreCase);




            CodeMapSnapshot? graph = null;
            CodeMapQueryService service;
            bool stale;
            if (project is not null)
            {
                var loaded = await LoadMapProjectScopedAsync(root, project);
                graph = loaded.Graph;
                service = loaded.Service;
                stale = loaded.Stale;
            }
            else if (isMermaid)
            {
                var loaded = await LoadMapSnapshotAsync(root);
                graph = loaded.Graph;
                service = loaded.Service;
                stale = loaded.Stale;
            }
            else
            {
                var loaded = await LoadQueryServiceAsync(root);
                service = loaded.Service;
                stale = loaded.Stale;
            }
            await using var _ = service;
            var map = service.BuildMap(focus, project, tokenBudget);
            var mermaid = isMermaid
                ? RepoMapFormatter.ToMermaid(graph!, ArchitectureChecker.LoadProjectReferences(ProjectRoot(root)))
                : null;
            if (json || string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
                WriteJson(new MapResponse(JsonSchemaVersion, project, focus, tokenBudget, map.EstimatedTokens, map.Lines, stale, mermaid, format));
            else if (mermaid is not null)
                Console.WriteLine(mermaid);
            else
            {
                WarnIfStale(stale);
                Console.WriteLine(map.Text);
            }
            return Exit(0);
        }
        catch (Exception exception) { return HandleQueryError(exception, json); }
    }

    private static async Task<int> RunContextAsync(string task, string? root, int maxResults, int tokenBudget, bool evidence, double minConfidence, bool json)
    {
        try
        {
            if (tokenBudget <= 0) throw new ArgumentOutOfRangeException(nameof(tokenBudget), "Token budget must be positive.");


            var loaded = await LoadQueryServiceAsync(root);
            await using var service = loaded.Service;
            var stale = loaded.Stale;
            var matches = service.Find(task, maxResults);
            var relationItems = new List<(string Kind, IndexedSymbol Source, IndexedSymbol Target, int? Depth, IndexedEdge Edge)>();
            foreach (var match in matches.Take(Math.Min(3, maxResults)))
            {
                relationItems.AddRange(service.CallerRelations(match, 5).Where(item => MeetsMinConfidence(item.Edge, minConfidence)).Select(item => ("caller", item.Symbol, match, (int?)null, item.Edge)));
                relationItems.AddRange(service.CalleeRelations(match, 1, 5).Where(item => MeetsMinConfidence(item.Edge, minConfidence)).Select(item => ("callee", match, item.Symbol, (int?)null, item.Edge)));
                relationItems.AddRange(service.ImplementationRelations(match, 5).Where(item => MeetsMinConfidence(item.Edge, minConfidence)).Select(item => ("implemented-by", match, item.Symbol, (int?)null, item.Edge)));
            }
            var fileById = PreloadEvidenceFiles(service, evidence, relationItems.Select(item => item.Edge));
            var relations = relationItems.Select(item => ToRelation(item.Kind, item.Source, item.Target, item.Depth, item.Edge, service, evidence, preloadedFileById: fileById)).ToList();
            var map = service.BuildMap(matches.FirstOrDefault()?.Name ?? task, project: null, tokenBudget);
            if (json)
            {
                WriteJson(new ContextResponse(SchemaVersion(evidence), task, matches.Select(ToMatch).ToArray(), relations, map.Lines, stale));
                return Exit(matches.Count == 0 ? 2 : 0);
            }

            WarnIfStale(stale);
            Console.WriteLine($"# Context: {task}");
            foreach (var match in matches.Take(maxResults))
                WriteFindMatch(match, service);
            if (relations.Count > 0)
            {
                Console.WriteLine("relations:");
                foreach (var relation in relations.Take(maxResults))
                    Console.WriteLine($"  {relation.Kind}: {relation.Source} -> {relation.Target}");
            }
            Console.WriteLine();
            Console.WriteLine(map.Text);
            return Exit(matches.Count == 0 ? 2 : 0);
        }
        catch (Exception exception) { return HandleQueryError(exception, json, evidence); }
    }

    private static async Task<int> RunResolvedAsync(
        string query,
        string? root,
        int maxResults,
        bool json,
        bool evidence,
        double minConfidence,
        bool callableOnly,
        Func<CodeMapQueryService, IndexedSymbol, QueryResponse> action)
    {
        try
        {
            var loaded = await LoadQueryServiceAsync(root);
            await using var service = loaded.Service;
            var stale = loaded.Stale;
            var candidates = service.ResolveSymbol(query, callableOnly, maxResults).Matches;
            if (candidates.Count == 0)
            {
                if (json) WriteJson(new QueryResponse(SchemaVersion(evidence), query, [], [], stale, "no_matches"));
                else Console.Error.WriteLine($"No matches found for '{query}'.");
                return Exit(2);
            }
            if (candidates.Count > 1)
            {
                if (json)
                    WriteJson(new QueryResponse(SchemaVersion(evidence), query, candidates.Take(maxResults).Select(ToMatch).ToArray(), Array.Empty<QueryRelation>(), stale, "ambiguous"));
                else
                {
                    Console.Error.WriteLine($"Ambiguous symbol '{query}'. Candidates:");
                    foreach (var candidate in candidates.Take(maxResults)) Console.Error.WriteLine($"  {ToDisplay(candidate)}");
                }
                return Exit(2);
            }
            if (!json) WarnIfStale(stale);
            var response = action(service, candidates[0]);
            if (json) WriteJson(response with { Query = query, Stale = stale });
            return Exit(0);
        }
        catch (Exception exception) { return HandleQueryError(exception, json, evidence); }
    }

    private static int SchemaVersion(bool evidence) => evidence ? JsonSchemaVersionWithEvidence : JsonSchemaVersion;

    private static bool ValidateMinConfidence(double value, bool json, bool evidence)
    {
        if (RelationConfidence.IsValid(value))
            return true;

        if (json)
            WriteJson(new ErrorResponse(SchemaVersion(evidence), new ErrorDto("query_failed", RelationConfidence.InvalidMessage)));
        else
            Console.Error.WriteLine(RelationConfidence.InvalidMessage);
        return false;
    }

    private static async Task<(CodeMapQueryService Service, bool Stale)> LoadQueryServiceAsync(string? root, CodeMapMcpContext? freshnessCache = null)
    {
        var databasePath = FindDatabase(root);
        var store = new CodeMapQueryStore(databasePath);
        var connectionTask = store.OpenReadOnlyConnectionAsync();
        var staleTask = freshnessCache is null ? IsStaleAsync(databasePath) : IsStaleAsync(databasePath, freshnessCache);
        try
        {
            await Task.WhenAll(connectionTask, staleTask);
        }
        catch
        {
            if (connectionTask.IsCompletedSuccessfully)
                await connectionTask.Result.DisposeAsync();
            throw;
        }
        return (new CodeMapQueryService(connectionTask.Result), staleTask.Result);
    }

    private static async Task<(CodeMapSnapshot Graph, CodeMapQueryService Service, bool Stale)> LoadMapSnapshotAsync(string? root, CodeMapMcpContext? freshnessCache = null)
    {
        var databasePath = FindDatabase(root);
        var store = new CodeMapQueryStore(databasePath);
        var graphTask = store.LoadAsync();
        var staleTask = freshnessCache is null ? IsStaleAsync(databasePath) : IsStaleAsync(databasePath, freshnessCache);
        await Task.WhenAll(graphTask, staleTask);
        var graph = graphTask.Result;
        return (graph, new CodeMapQueryService(graph), staleTask.Result);
    }

    private static async Task<(CodeMapSnapshot Graph, CodeMapQueryService Service, bool Stale)> LoadMapProjectScopedAsync(string? root, string project)
    {
        var databasePath = FindDatabase(root);
        var graphTask = new CodeMapQueryStore(databasePath).LoadProjectScopedAsync(project, ShutdownToken);
        var staleTask = IsStaleAsync(databasePath);
        await Task.WhenAll(graphTask, staleTask);
        var graph = graphTask.Result;
        return (graph, new CodeMapQueryService(graph), staleTask.Result);
    }

    private static async Task<bool> IsStaleAsync(string databasePath)
    {






        try
        {
            var projectRoot = Path.GetDirectoryName(Path.GetDirectoryName(databasePath))!;
            return !await new IncrementalCodeMapIndexer().IsUpToDateAsync(projectRoot, ShutdownToken);
        }
        catch (OperationCanceledException) when (ShutdownToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }









    private static async Task<bool> IsStaleAsync(string databasePath, CodeMapMcpContext freshnessCache)
    {
        try
        {
            var projectRoot = Path.GetDirectoryName(Path.GetDirectoryName(databasePath))!;
            return !await freshnessCache.IsUpToDateAsync(projectRoot, ShutdownToken);
        }
        catch (OperationCanceledException) when (ShutdownToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static void WarnIfStale(bool stale)
    {
        if (stale)
            Console.Error.WriteLine("warning: index may be stale (files changed since the last index/update).\nRun: codemap update");
    }

    private static string FindDatabase(string? root)
    {
        var start = string.IsNullOrWhiteSpace(root) ? Directory.GetCurrentDirectory() : Path.GetFullPath(root);
        if (File.Exists(start)) start = Path.GetDirectoryName(start)!;
        var directory = new DirectoryInfo(start);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, ".codemap", "index.db");
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent!;
        }
        throw new FileNotFoundException("No CodeMap index found.\nRun: codemap index");
    }

    private static int HandleQueryError(Exception exception, bool json, bool evidence = false)
    {
        var (code, message) = ClassifyError(exception);
        if (json)
            WriteJson(new ErrorResponse(SchemaVersion(evidence), new ErrorDto(code, message)));
        else
            Console.Error.WriteLine(message);
        return Exit(1);
    }

    private static (string Code, string Message) ClassifyError(Exception exception)
    {
        if (exception is FileNotFoundException && exception.Message.Contains("No CodeMap index found", StringComparison.Ordinal))
            return ("index_not_found", "No CodeMap index found.\nRun: codemap index");
        if (exception is InvalidOperationException && exception.Message.Contains("CodeMap index schema is outdated", StringComparison.Ordinal))
            return ("schema_outdated", exception.Message);
        if (exception is GitUnavailableException git)
            return ("git_unavailable", git.Message);
        return ("query_failed", $"codemap query failed: {exception.Message}");
    }

    private static int Exit(int code)
    {
        Environment.ExitCode = code;
        return code;
    }


    private static void WriteFindMatch(IndexedSymbol symbol, CodeMapQueryService service)
    {
        Console.WriteLine(symbol.Name);
        WriteSymbolFile(symbol);
        var members = service.Members(symbol);
        if (members.Count > 0)
        {
            Console.WriteLine("members:");
            foreach (var member in members) Console.WriteLine($"  {MemberLabel(member)}");
        }
        Console.WriteLine();
    }

    private static void WriteSymbolFile(IndexedSymbol symbol) =>
        Console.WriteLine($"  kind: {symbol.Kind.ToString().ToLowerInvariant()}\n  file: {symbol.RelativePath}:{symbol.StartLine ?? 0}");

    private static void WriteSection(string title, IReadOnlyList<IndexedSymbol> symbols)
    {
        Console.WriteLine();
        Console.WriteLine($"{title}:");
        foreach (var symbol in symbols) Console.WriteLine($"  {ToDisplay(symbol)}");
    }

    private static string ToDisplay(IndexedSymbol symbol)
    {
        if (symbol.Kind is not (NodeKind.Method or NodeKind.Constructor))
            return symbol.Name;
        var qualified = symbol.QualifiedName;
        var parameterStart = qualified.IndexOf('(');
        var baseName = parameterStart >= 0 ? qualified[..parameterStart] : qualified;
        var segments = baseName.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var methodName = segments.Length >= 2 ? segments[^2] + "." + segments[^1] : symbol.Name;
        return methodName + ParameterList(symbol);
    }

    private static string MemberLabel(IndexedSymbol symbol) =>
        symbol.Kind is NodeKind.Method or NodeKind.Constructor ? symbol.Name + ParameterList(symbol) : symbol.Name;

    private static string ParameterList(IndexedSymbol symbol)
    {
        var signature = symbol.Signature;
        if (signature is null) return "()";
        var start = signature.IndexOf('(');
        var end = signature.LastIndexOf(')');
        return start >= 0 && end > start ? signature[start..(end + 1)] : "()";
    }

    private static MatchDto ToMatch(IndexedSymbol symbol) => new(
        symbol.Id, symbol.Project, symbol.Kind.ToString().ToLowerInvariant(), symbol.Name, symbol.QualifiedName,
        symbol.Signature, symbol.RelativePath, symbol.StartLine, symbol.EndLine, symbol.Language);

    private static QueryRelation ToRelation(
        string kind,
        IndexedSymbol source,
        IndexedSymbol target,
        int? depth,
        IndexedEdge edge,
        CodeMapQueryService service,
        bool evidence,
        RelationEvidence? explicitEvidence = null,
        IReadOnlyDictionary<string, IndexedFile>? preloadedFileById = null)
    {
        EvidenceLocationDto? location = null;
        string? evidenceLabel = null;
        if (evidence)
        {
            var evidenceValue = explicitEvidence;
            if (evidenceValue is null)
            {
                var fileById = preloadedFileById ?? service.Files().ToDictionary(file => file.Id, StringComparer.Ordinal);
                evidenceValue = RelationEvidenceMapper.FromEdge(edge, source, target,
                    edge.SourceFileId is not null && fileById.TryGetValue(edge.SourceFileId, out var file) ? file.RelativePath : null,
                    edge.Line);
            }
            evidenceLabel = evidenceValue.Evidence;
            location = evidenceValue.File is null && evidenceValue.Line is null
                ? null
                : new EvidenceLocationDto(evidenceValue.File ?? string.Empty, evidenceValue.Line);
        }
        return new QueryRelation(
            kind,
            ToDisplay(source),
            ToDisplay(target),
            source.Id,
            target.Id,
            depth,
            edge.Kind.ToString(),
            edge.ResolutionKind.ToString().ToLowerInvariant(),
            edge.Confidence,
            location,
            evidenceLabel);
    }

    private static bool MeetsMinConfidence(IndexedEdge edge, double minConfidence) =>
        edge.Confidence is null || edge.Confidence >= minConfidence;

    private static IReadOnlyDictionary<string, IndexedFile>? PreloadEvidenceFiles(
        CodeMapQueryService service,
        bool evidence,
        IEnumerable<IndexedEdge> edges) =>
        evidence ? service.FindFilesByIds(edges.Select(edge => edge.SourceFileId).OfType<string>()) : null;

    private static void WriteJson<T>(T value) => Console.WriteLine(JsonSerializer.Serialize(value, JsonOptions));

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private sealed record MatchDto(string Id, string Project, string Kind, string Name, string QualifiedName, string? Signature, string File, int? StartLine, int? EndLine, string Language);
    private sealed record EvidenceLocationDto(string File, int? Line);
    private sealed record QueryRelation(string Kind, string Source, string Target, string SourceId, string TargetId, int? Depth, string EdgeKind, string ResolutionKind, double? Confidence, EvidenceLocationDto? Location = null, string? Evidence = null);
    private sealed record QueryResponse(int Version, string Query, IReadOnlyList<MatchDto> Matches, IReadOnlyList<QueryRelation> Relations, bool Stale = false, string? Reason = null);
    private sealed record PairRelationResponse(int Version, string Source, string Target, IReadOnlyList<MatchDto> Matches, IReadOnlyList<QueryRelation> Relations, bool Stale = false, string? Reason = null);
    private sealed record MapResponse(int Version, string? Project, string? Focus, int TokenBudget, int EstimatedTokens, IReadOnlyList<string> Lines, bool Stale = false, string? Mermaid = null, string? Format = null);
    private sealed record ContextResponse(int Version, string Task, IReadOnlyList<MatchDto> Matches, IReadOnlyList<QueryRelation> Relations, IReadOnlyList<string> MapLines, bool Stale = false);
    private sealed record ErrorDto(string Code, string Message);
    private sealed record ErrorResponse(int Version, ErrorDto Error);
}
