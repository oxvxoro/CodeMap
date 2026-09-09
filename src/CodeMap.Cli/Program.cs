using System.CommandLine;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeMap.CSharp;
using CodeMap.Core.Models;
using CodeMap.Engine.Application;
using CodeMap.Mcp;
using CodeMap.Storage;
using CodeMap.Cli.Presentation;

namespace CodeMap.Cli;

public static partial class Program
{
    private const int JsonSchemaVersion = CliSchemaVersions.Query;
    private const int JsonSchemaVersionWithEvidence = CliSchemaVersions.QueryWithEvidence;
    private const int SemanticSliceSchemaVersion = CliSchemaVersions.SemanticSlice;
    private const int DefaultMaxResults = 20;
    private const int DefaultDepth = 1;
    private const int DefaultMapTokens = 500;
    private static CancellationToken ShutdownToken { get; set; }
    private static CodeMapApplication Application { get; } = new(new UncachedIndexFreshnessService());

    public static Task<int> Main(string[] args) => CliHost.RunAsync(args);

    internal static RootCommand CreateRootCommand(CancellationToken shutdownToken)
    {
        ShutdownToken = shutdownToken;
        var rootCommand = new RootCommand("CodeMap — local semantic code indexer and query CLI.");
        CommandCatalog.Register(rootCommand);

        return rootCommand;
    }

    internal static void AddPairRelationCommand(RootCommand root)
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

    internal static void AddRelationCommand(
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

    internal static void AddContextCommand(RootCommand root)
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

    internal static void AddMapCommand(RootCommand root)
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

    internal static void AddScipImportCommand(Command scip)
    {
        var artifact = new Argument<string>("artifact", "Path to the SCIP .scip artifact.");
        var name = new Option<string>("--name", "Stable provider name for this import.") { IsRequired = true };
        var rootPath = new Option<string?>("--root", "Repository root containing an existing CodeMap index.");
        var replace = new Option<bool>("--replace", () => true, "Replace a prior import with the same name.");
        var json = new Option<bool>("--json", "Emit JSON summary.");
        var command = new Command("import", "Import an externally generated SCIP artifact.");
        command.AddArgument(artifact);
        command.AddOption(name);
        command.AddOption(rootPath);
        command.AddOption(replace);
        command.AddOption(json);
        command.SetHandler(async (string artifactValue, string nameValue, string? rootValue, bool replaceValue, bool jsonValue) =>
            await RunScipImportAsync(artifactValue, nameValue, rootValue, replaceValue, jsonValue), artifact, name, rootPath, replace, json);
        scip.AddCommand(command);
    }

    internal static void AddScipRemoveCommand(Command scip)
    {
        var name = new Argument<string>("name", "SCIP import name to remove.");
        var rootPath = new Option<string?>("--root", "Repository root containing the CodeMap index.");
        var remove = new Command("remove", "Remove an imported SCIP provider.");
        remove.AddArgument(name);
        remove.AddOption(rootPath);
        remove.SetHandler(async (string nameValue, string? rootValue) => await RunScipRemoveAsync(nameValue, rootValue), name, rootPath);
        scip.AddCommand(remove);
    }

    internal static void AddScipListCommand(Command scip)
    {
        var rootPath = new Option<string?>("--root", "Repository root containing the CodeMap index.");
        var json = new Option<bool>("--json", "Emit JSON provider registrations.");
        var list = new Command("list", "List imported SCIP providers.");
        list.AddOption(rootPath);
        list.AddOption(json);
        list.SetHandler(async (string? rootValue, bool jsonValue) => await RunScipListAsync(rootValue, jsonValue), rootPath, json);
        scip.AddCommand(list);
    }

    internal static void AddSliceCommand(RootCommand root)
    {
        var query = new Argument<string>("query", "C# executable symbol to analyze.");
        var rootPath = new Option<string?>("--root", "Project root or directory containing .codemap/index.db.");
        var direction = new Option<string>("--direction", () => "backward", "Slice direction: backward or forward.");
        var line = new Option<int?>("--line", "Optional 1-based source line used as the slice seed.");
        var column = new Option<int?>("--column", "Optional 1-based source column used with --line.");
        var maxResults = new Option<int>("--max-results", () => 80, "Maximum number of returned slice items (1..500).");
        var includeSource = new Option<bool>("--include-source", "Include the selected scope source text.");
        var json = new Option<bool>("--json", "Emit stable JSON instead of compact text.");
        var command = new Command("slice", "Compute an intraprocedural C# semantic dependency slice.");
        command.AddArgument(query);
        command.AddOption(rootPath);
        command.AddOption(direction);
        command.AddOption(line);
        command.AddOption(column);
        command.AddOption(maxResults);
        command.AddOption(includeSource);
        command.AddOption(json);
        command.SetHandler(async (string queryValue, string? rootValue, string directionValue, int? lineValue, int? columnValue, int maxResultsValue, bool includeSourceValue, bool jsonValue) =>
            await RunSliceAsync(queryValue, rootValue, directionValue, lineValue, columnValue, maxResultsValue, includeSourceValue, jsonValue),
            query, rootPath, direction, line, column, maxResults, includeSource, json);
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

    internal static void PrintVersion()
    {
        var version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
        Console.WriteLine($"codemap {version}");
    }

    internal static async Task<int> RunIndexAsync(string path, bool force)
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

    internal static async Task<int> RunUpdateAsync(string path)
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
            var response = await Application.FindAsync(new FindRequest(query, root, maxResults), ShutdownToken);
            if (!response.Succeeded)
                return HandleApplicationError(response.Error!, json, response.Stale);
            var result = response.Value!;
            var matches = result.Matches;
            var stale = response.Stale;
            if (json)
            {
                var reason = matches.Count == 0 ? "no_matches" : null;
                WriteJson(new QueryResponse(JsonSchemaVersion, query, matches.Select(ToMatch).ToArray(), Array.Empty<QueryRelation>(), stale, reason));
            }
            else
            {
                WarnIfStale(stale);
                foreach (var match in matches)
                    WriteFindMatch(match, result.Members.GetValueOrDefault(match.Id) ?? Array.Empty<IndexedSymbol>());
            }
            return Exit(matches.Count == 0 ? 2 : 0);
        }
        catch (Exception exception) { return HandleQueryError(exception, json); }
    }

    private static async Task<int> RunScipImportAsync(string artifact, string name, string? root, bool replace, bool json)
    {
        try
        {
            var repositoryRoot = root is null ? Directory.GetCurrentDirectory() : Path.GetFullPath(root);
            var summary = await new ScipImportService().ImportAsync(repositoryRoot, artifact,
                new ScipImportOptions(name, replace), ShutdownToken);
            if (json) WriteJson(new ScipImportResponse(1, summary));
            else Console.WriteLine($"Imported {summary.Symbols} SCIP symbols from {summary.Files} files into {summary.Project}.");
            return Exit(0);
        }
        catch (OperationCanceledException) when (ShutdownToken.IsCancellationRequested)
        {
            Console.Error.WriteLine("codemap import scip canceled.");
            return Exit(130);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"codemap import scip failed: {exception.Message}");
            return Exit(1);
        }
    }

    private static async Task<int> RunScipRemoveAsync(string name, string? root)
    {
        try
        {
            await new ScipImportService().RemoveAsync(root is null ? Directory.GetCurrentDirectory() : root, name, ShutdownToken);
            Console.WriteLine($"Removed SCIP provider scip:{name}.");
            return Exit(0);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"codemap scip remove failed: {exception.Message}");
            return Exit(1);
        }
    }

    private static async Task<int> RunScipListAsync(string? root, bool json)
    {
        try
        {
            var providers = await new ScipImportService().ListAsync(root is null ? Directory.GetCurrentDirectory() : root, ShutdownToken);
            if (json) WriteJson(new { version = 1, providers });
            else foreach (var provider in providers)
                Console.WriteLine($"{provider.Name}\n  project: {provider.Project}\n  artifact: {provider.Artifact}\n  files: {provider.Files}\n  fresh: {(provider.Fresh ? "yes" : "no")}");
            return Exit(0);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"codemap scip list failed: {exception.Message}");
            return Exit(1);
        }
    }

    private static async Task<int> RunSliceAsync(string query, string? root, string direction, int? line, int? column, int maxResults, bool includeSource, bool json)
    {
        if (!Enum.TryParse<SliceDirection>(direction, ignoreCase: true, out var parsedDirection))
        {
            if (json) WriteJson(new ErrorResponse(SemanticSliceSchemaVersion, new ErrorDto("query_failed", "Direction must be 'backward' or 'forward'.")));
            else Console.Error.WriteLine("Direction must be 'backward' or 'forward'.");
            return Exit(1);
        }
        try
        {
            var databasePath = CodeMapIndexLocator.FindDatabase(root);
            var projectRoot = Path.GetDirectoryName(Path.GetDirectoryName(databasePath))!;
            var result = await new CodeMapSemanticSliceService().SliceAsync(projectRoot,
                new SemanticSliceRequest(parsedDirection, line, column, maxResults, query, includeSource), ShutdownToken);
            if (json)
            {
                WriteJson(new SemanticSliceResponse(SemanticSliceSchemaVersion, query, parsedDirection.ToString().ToLowerInvariant(), result.EntrySymbol.Id, result.Scope,
                    result.Items, result.Dependencies, result.Truncated, result.Source));
            }
            else
            {
                Console.WriteLine(result.Scope.DisplayName);
                Console.WriteLine($"  file: {result.Scope.File}:{result.Scope.StartLine ?? 0}");
                foreach (var item in result.Items)
                    Console.WriteLine($"  [{item.Id}] {item.Kind.ToString().ToLowerInvariant()}: {item.Display}");
                if (result.Source is not null)
                {
                    Console.WriteLine("source:");
                    Console.WriteLine(result.Source);
                }
            }
            return Exit(0);
        }
        catch (Exception exception) { return HandleQueryError(exception, json, version: SemanticSliceSchemaVersion); }
    }

    internal static async Task<int> RunRefsAsync(string query, string? root, int maxResults, int depth, bool json, bool evidence, double minConfidence)
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

    internal static async Task<int> RunCallersAsync(string query, string? root, int maxResults, int depth, bool json, bool evidence, double minConfidence)
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

    internal static async Task<int> RunCalleesAsync(string query, string? root, int maxResults, int depth, bool json, bool evidence, double minConfidence)
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

    internal static async Task<int> RunImplAsync(string query, string? root, int maxResults, int depth, bool json, bool evidence, double minConfidence)
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
        try
        {
            var response = await Application.ImpactAsync(new ImpactRequest(query, root, depth, maxResults, profile, minConfidence), ShutdownToken);
            if (!response.Succeeded)
                return HandleApplicationError(response.Error!, json, response.Stale);
            var result = response.Value!;
            var relations = result.Items.Select(item =>
            {
                var file = item.Via.SourceFileId is not null && result.Files.TryGetValue(item.Via.SourceFileId, out var indexedFile)
                    ? indexedFile.RelativePath
                    : null;
                var relationEvidence = evidence ? RelationEvidenceMapper.FromEdge(item.Via, item.Symbol, result.Symbol, file, item.Via.Line) : null;
                return ToApplicationRelation("impact", item.Symbol, result.Symbol, item.Depth, item.Via, evidence, relationEvidence);
            }).ToArray();
            if (json)
            {
                WriteJson(new QueryResponse(SchemaVersion(evidence), query, [ToMatch(result.Symbol)], relations, response.Stale));
                return Exit(0);
            }
            WarnIfStale(response.Stale);
            Console.WriteLine(ToDisplay(result.Symbol));
            foreach (var item in result.Items)
                Console.WriteLine($"<- {ToDisplay(item.Symbol)}");
            return Exit(0);
        }
        catch (Exception exception) { return HandleQueryError(exception, json, evidence); }
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
        var databasePath = CodeMapIndexLocator.FindDatabase(root);
        var store = new CodeMapQueryStore(databasePath);
        var connectionTask = store.OpenReadOnlyConnectionAsync();
        var staleTask = CodeMapIndexLocator.IsStaleAsync(
            databasePath,
            ShutdownToken,
            freshnessCache is null ? null : new Func<string, CancellationToken, Task<bool>>(freshnessCache.IsUpToDateAsync));
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
        var databasePath = CodeMapIndexLocator.FindDatabase(root);
        var store = new CodeMapQueryStore(databasePath);
        var graphTask = store.LoadAsync();
        var staleTask = CodeMapIndexLocator.IsStaleAsync(
            databasePath,
            ShutdownToken,
            freshnessCache is null ? null : new Func<string, CancellationToken, Task<bool>>(freshnessCache.IsUpToDateAsync));
        await Task.WhenAll(graphTask, staleTask);
        var graph = graphTask.Result;
        return (graph, new CodeMapQueryService(graph), staleTask.Result);
    }

    private static async Task<(CodeMapSnapshot Graph, CodeMapQueryService Service, bool Stale)> LoadMapProjectScopedAsync(string? root, string project)
    {
        var databasePath = CodeMapIndexLocator.FindDatabase(root);
        var graphTask = new CodeMapQueryStore(databasePath).LoadProjectScopedAsync(project, ShutdownToken);
        var staleTask = CodeMapIndexLocator.IsStaleAsync(databasePath, ShutdownToken);
        await Task.WhenAll(graphTask, staleTask);
        var graph = graphTask.Result;
        return (graph, new CodeMapQueryService(graph), staleTask.Result);
    }

    private static void WarnIfStale(bool stale)
    {
        if (stale)
            Console.Error.WriteLine("warning: index may be stale (files changed since the last index/update).\nRun: codemap update");
    }

    private static int HandleQueryError(Exception exception, bool json, bool evidence = false, int? version = null)
    {
        var (code, message) = CodeMapErrorClassifier.Classify(exception);
        if (json)
            WriteJson(new ErrorResponse(version ?? SchemaVersion(evidence), new ErrorDto(code, message)));
        else
            Console.Error.WriteLine(message);
        return Exit(1);
    }

    private static int HandleApplicationError(QueryError error, bool json, bool stale = false)
    {
        if (json)
            WriteJson(new ErrorResponse(SchemaVersion(false), new ErrorDto(error.Code, error.Message)));
        else
            Console.Error.WriteLine(error.Message);
        return Exit(error.Code is "no_matches" or "ambiguous" ? CliExitCodes.NoMatchOrAmbiguous : CliExitCodes.Error);
    }

    private static int Exit(int code)
    {
        Environment.ExitCode = code;
        return code;
    }


    private static void WriteFindMatch(IndexedSymbol symbol, IReadOnlyList<IndexedSymbol> members)
    {
        Console.WriteLine(symbol.Name);
        WriteSymbolFile(symbol);
        if (members.Count > 0)
        {
            Console.WriteLine("members:");
            foreach (var member in members) Console.WriteLine($"  {MemberLabel(member)}");
        }
        Console.WriteLine();
    }

    private static void WriteFindMatch(IndexedSymbol symbol, CodeMapQueryService service) =>
        WriteFindMatch(symbol, service.Members(symbol));

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
    private sealed record SemanticSliceResponse(int Version, string Query, string Direction, string SymbolId, SliceScope Scope, IReadOnlyList<SliceItem> Items, IReadOnlyList<SliceDependency> Dependencies, bool Truncated, string? Source = null);
    private sealed record ScipImportResponse(int Version, ScipImportSummary Import);
    private sealed record ErrorDto(string Code, string Message);
    private sealed record ErrorResponse(int Version, ErrorDto Error);
}
