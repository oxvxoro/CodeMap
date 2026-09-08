using System.CommandLine;
using System.Text.Json;
using CodeMap.Core;
using CodeMap.Mcp;
using CodeMap.Storage;

namespace CodeMap.Cli;

public static partial class Program
{
    private static void AddImpactCommand(RootCommand root)
    {
        var query = new Argument<string?>("query", "Symbol name or qualified name.") { Arity = ArgumentArity.ZeroOrOne };
        var options = CreateQueryOptions();
        var evidence = new Option<bool>("--evidence", "Emit JSON schema v5 with relation evidence.");
        var minConfidence = new Option<double>("--min-confidence", () => 0, "Minimum confidence for relation edges (0..1).");
        var changed = new Option<bool>("--changed", "Analyze git-changed files instead of a single symbol.");
        var baseRev = new Option<string?>("--base", "Git base revision for --changed (default: HEAD).");
        var profile = new Option<string>("--profile", () => "code", "Impact edge set: code (default) or app.");
        var command = new Command("impact", "Show reverse dependency impact.");
        command.AddArgument(query);
        command.AddOption(options.Root);
        command.AddOption(options.MaxResults);
        command.AddOption(options.Depth);
        command.AddOption(options.Json);
        command.AddOption(evidence);
        command.AddOption(minConfidence);
        command.AddOption(changed);
        command.AddOption(baseRev);
        command.AddOption(profile);
        command.SetHandler(async context =>
        {
            var queryValue = context.ParseResult.GetValueForArgument<string?>(query);
            var rootPath = context.ParseResult.GetValueForOption(options.Root);
            var maxResults = context.ParseResult.GetValueForOption(options.MaxResults);
            var depth = context.ParseResult.GetValueForOption(options.Depth);
            var json = context.ParseResult.GetValueForOption(options.Json);
            var evidenceValue = context.ParseResult.GetValueForOption(evidence);
            var minConfidenceValue = context.ParseResult.GetValueForOption(minConfidence);
            var changedValue = context.ParseResult.GetValueForOption(changed);
            var baseValue = context.ParseResult.GetValueForOption(baseRev);
            var profileValue = context.ParseResult.GetValueForOption(profile) ?? "code";
            if (!ValidateMinConfidence(minConfidenceValue, json, evidenceValue))
            {
                context.ExitCode = Exit(1);
                return;
            }
            if (!CodeMapQueryService.IsValidImpactProfile(profileValue))
            {
                const string message = "--profile must be one of: code, app.";
                if (json) WriteJson(new ErrorResponse(SchemaVersion(evidenceValue), new ErrorDto("query_failed", message)));
                else Console.Error.WriteLine(message);
                context.ExitCode = Exit(1);
                return;
            }
            if (changedValue)
            {
                context.ExitCode = await RunChangedImpactAsync(rootPath, maxResults, depth, json, evidenceValue, minConfidenceValue, baseValue, profileValue);
                return;
            }
            if (string.IsNullOrWhiteSpace(queryValue))
            {
                Console.Error.WriteLine("impact requires a symbol query or --changed.");
                context.ExitCode = Exit(1);
                return;
            }
            context.ExitCode = await RunImpactAsync(queryValue, rootPath, maxResults, depth, json, evidenceValue, minConfidenceValue, profileValue);
        });
        root.AddCommand(command);
    }

    private static void AddDiffCommand(RootCommand root)
    {
        var baseRev = new Argument<string>("base", "Git base revision to compare against.");
        var options = CreateQueryOptions();
        var evidence = new Option<bool>("--evidence", "Emit JSON schema v5 with relation evidence.");
        var minConfidence = new Option<double>("--min-confidence", () => 0, "Minimum confidence for relation edges (0..1).");
        var command = new Command("diff", "Show impact of git-changed files since a base revision.");
        command.AddArgument(baseRev);
        command.AddOption(options.Root);
        command.AddOption(options.MaxResults);
        command.AddOption(options.Depth);
        command.AddOption(options.Json);
        command.AddOption(evidence);
        command.AddOption(minConfidence);
        command.SetHandler(async (string baseValue, string? rootPath, int maxResults, int depth, bool json, bool evidenceValue, double minConfidenceValue) =>
        {
            if (!ValidateMinConfidence(minConfidenceValue, json, evidenceValue))
            {
                Exit(1);
                return;
            }
            await RunChangedImpactAsync(rootPath, maxResults, depth, json, evidenceValue, minConfidenceValue, baseValue);
        }, baseRev, options.Root, options.MaxResults, options.Depth, options.Json, evidence, minConfidence);
        root.AddCommand(command);
    }

    private static void AddCheckCommand(RootCommand root)
    {
        var options = CreateQueryOptions();
        var command = new Command("check", "Run architecture quality checks.");
        var architecture = new Command("architecture", "Check cycles, layer rules, fan-in/out, and orphan symbols.");
        architecture.AddOption(options.Root);
        architecture.AddOption(options.Json);
        architecture.SetHandler(async (string? rootPath, bool json) => await RunArchitectureCheckAsync(rootPath, json), options.Root, options.Json);
        command.AddCommand(architecture);
        root.AddCommand(command);
    }

    private static void AddWatchCommand(RootCommand root)
    {
        var path = new Argument<string>("path", () => Directory.GetCurrentDirectory(), "Repository root to watch.") { Arity = ArgumentArity.ZeroOrOne };
        var debounce = new Option<int>("--debounce-ms", () => 500, "Debounce interval before re-indexing.");
        var command = new Command("watch", "Watch for file changes and update the index in the background.");
        command.AddArgument(path);
        command.AddOption(debounce);
        command.SetHandler(async (string watchPath, int debounceMs) => await RunWatchAsync(watchPath, debounceMs), path, debounce);
        root.AddCommand(command);
    }

    private static void AddReportCommand(RootCommand root)
    {
        var output = new Option<string>("--out", "Write an HTML report to this path.") { IsRequired = true };
        var rootPath = new Option<string?>("--root", "Project root or directory containing .codemap/index.db.");
        var command = new Command("report", "Generate an HTML architecture and risk report.");
        command.AddOption(output);
        command.AddOption(rootPath);
        command.SetHandler(async (string outPath, string? rootValue) => await RunReportAsync(outPath, rootValue), output, rootPath);
        root.AddCommand(command);
    }

    private static void AddFlowCommand(RootCommand root)
    {
        var entry = new Argument<string>("entry", "Entry symbol/route name or qualified name.");
        var kind = new Option<string>("--kind", () => "all", "Traversal edge set: http, ui, or all.");
        var depth = new Option<int>("--depth", () => CodeMapQueryService.FlowDefaultDepth, "Traversal depth (1..8).");
        var rootPath = new Option<string?>("--root", "Project root or directory containing .codemap/index.db.");
        var maxResults = new Option<int>("--max-results", () => DefaultMaxResults, "Maximum number of returned relations.");
        var minConfidence = new Option<double>("--min-confidence", () => 0, "Minimum confidence for flow edges (0..1).");
        var json = new Option<bool>("--json", "Emit stable JSON instead of compact text.");
        var evidence = new Option<bool>("--evidence", "Emit JSON schema v5 with relation evidence.");
        var command = new Command("flow", "Traverse HTTP/UI application flow edges from an entry symbol or route.");
        command.AddArgument(entry);
        command.AddOption(kind);
        command.AddOption(depth);
        command.AddOption(rootPath);
        command.AddOption(maxResults);
        command.AddOption(minConfidence);
        command.AddOption(json);
        command.AddOption(evidence);
        command.SetHandler(async (string entryValue, string kindValue, int depthValue, string? rootValue, int maxResultsValue, double minConfidenceValue, bool jsonValue, bool evidenceValue) =>
        {
            if (!ValidateMinConfidence(minConfidenceValue, jsonValue, evidenceValue))
            {
                Exit(1);
                return;
            }
            if (!CodeMapQueryService.IsValidFlowKind(kindValue))
            {
                const string message = "--kind must be one of: http, ui, all.";
                if (jsonValue) WriteJson(new ErrorResponse(SchemaVersion(evidenceValue), new ErrorDto("query_failed", message)));
                else Console.Error.WriteLine(message);
                Exit(1);
                return;
            }
            if (!CodeMapQueryService.IsValidFlowDepth(depthValue))
            {
                var message = $"--depth must be between {CodeMapQueryService.FlowMinDepth} and {CodeMapQueryService.FlowMaxDepth}.";
                if (jsonValue) WriteJson(new ErrorResponse(SchemaVersion(evidenceValue), new ErrorDto("query_failed", message)));
                else Console.Error.WriteLine(message);
                Exit(1);
                return;
            }
            Exit(await RunFlowAsync(entryValue, kindValue, depthValue, rootValue, maxResultsValue, minConfidenceValue, jsonValue, evidenceValue));
        }, entry, kind, depth, rootPath, maxResults, minConfidence, json, evidence);
        root.AddCommand(command);
    }

    private static async Task<int> RunFlowAsync(
        string entryQuery,
        string kind,
        int depth,
        string? root,
        int maxResults,
        double minConfidence,
        bool json,
        bool evidence)
    {
        try
        {
            var loaded = await LoadQueryServiceAsync(root);
            await using var service = loaded.Service;
            var stale = loaded.Stale;
            var candidates = service.ResolveSymbol(entryQuery, callableOnly: false, maxResults).Matches;
            if (candidates.Count == 0)
            {
                if (json) WriteJson(new QueryResponse(SchemaVersion(evidence), entryQuery, [], [], stale, "no_matches"));
                else Console.Error.WriteLine($"No matches found for '{entryQuery}'.");
                return Exit(2);
            }
            if (candidates.Count > 1)
            {
                if (json)
                    WriteJson(new QueryResponse(SchemaVersion(evidence), entryQuery, candidates.Take(maxResults).Select(ToMatch).ToArray(), [], stale, "ambiguous"));
                else
                {
                    Console.Error.WriteLine($"Ambiguous symbol '{entryQuery}'. Candidates:");
                    foreach (var candidate in candidates.Take(maxResults)) Console.Error.WriteLine($"  {ToDisplay(candidate)}");
                }
                return Exit(2);
            }

            var entrySymbol = candidates[0];
            var flow = service.Flow(entrySymbol, kind, depth, maxResults, minConfidence);
            var sourceById = service.FindByIds(flow.Select(item => item.Via.SourceId));
            var fileById = evidence ? service.FindFilesByIds(flow.Select(item => item.Via.SourceFileId).OfType<string>()) : null;
            var relations = flow.Select(item => ToFlowRelation(item, entrySymbol, sourceById, service, evidence, fileById)).ToArray();

            if (json)
            {
                WriteJson(new QueryResponse(SchemaVersion(evidence), entryQuery, [ToMatch(entrySymbol)], relations, stale));
                return Exit(0);
            }

            WarnIfStale(stale);
            Console.WriteLine(ToDisplay(entrySymbol));
            foreach (var relation in relations)
                Console.WriteLine($"{relation.Depth}: {relation.EdgeKind} {relation.Source} -> {relation.Target}");
            return Exit(0);
        }
        catch (Exception exception) { return HandleQueryError(exception, json, evidence); }
    }







    private static QueryRelation ToFlowRelation(
        ImpactItem item,
        IndexedSymbol entry,
        IReadOnlyDictionary<string, IndexedSymbol> sourceById,
        CodeMapQueryService service,
        bool evidence,
        IReadOnlyDictionary<string, IndexedFile>? preloadedFileById = null)
    {
        var sourceSymbol = sourceById.GetValueOrDefault(item.Via.SourceId) ?? entry;
        return ToRelation("flow", sourceSymbol, item.Symbol, item.Depth, item.Via, service, evidence, preloadedFileById: preloadedFileById);
    }

    private static void AddMcpCommand(RootCommand root)
    {
        var rootPath = new Option<string?>("--root", "Default project root for MCP tool calls.");
        var command = new Command("mcp", "Start the CodeMap MCP server on stdio.");
        command.AddOption(rootPath);
        command.SetHandler(async (string? rootValue) => await RunMcpAsync(rootValue), rootPath);
        root.AddCommand(command);
    }

    private static void AddLspCommand(RootCommand root)
    {
        var rootPath = new Option<string?>("--root", "Default project root for LSP-style requests.");
        var command = new Command("lsp", "Start a minimal JSON-line query server on stdio.");
        command.AddOption(rootPath);
        command.SetHandler(async (string? rootValue) => await RunLspAsync(rootValue), rootPath);
        root.AddCommand(command);
    }

    private static async Task<int> RunChangedImpactAsync(string? root, int maxResults, int depth, bool json, bool evidence, double minConfidence, string? baseRevision, string profile = "code")
    {
        try
        {
            var projectRoot = ResolveProjectRoot(root);
            var changedFilesResult = GitChangedFileResolver.ListChangedFilesWithStatus(projectRoot, baseRevision, ShutdownToken);



            var changedFiles = changedFilesResult.AllChangedPaths;
            var loaded = await LoadQueryServiceAsync(root);
            await using var service = loaded.Service;
            var stale = loaded.Stale;
            var directSymbols = service.SymbolsInFiles(changedFilesResult.NewPathChangedFiles, int.MaxValue);
            var displaySymbols = directSymbols.Take(maxResults).ToArray();
            var analysisImpact = service.ImpactUnion(directSymbols, depth, int.MaxValue, profile);
            var displayImpact = analysisImpact.Take(Math.Max(1, maxResults)).ToArray();
            var risk = RiskScorer.Score(directSymbols, analysisImpact);
            var relationItems = displayImpact
                .Select(item => (Item: item, Root: directSymbols.FirstOrDefault(symbol => symbol.Id == item.RootId)))
                .Where(item => item.Root is not null)
                .Where(item => MeetsMinConfidence(item.Item.Via, minConfidence))
                .ToArray();
            var fileById = PreloadEvidenceFiles(service, evidence, relationItems.Select(item => item.Item.Via));
            var relations = relationItems
                .Select(item => ToRelation("impact", item.Item.Symbol, item.Root!, item.Item.Depth, item.Item.Via, service, evidence, preloadedFileById: fileById))
                .ToArray();

            if (json)
            {
                WriteJson(new DiffResponse(
                    SchemaVersion(evidence),
                    baseRevision ?? "HEAD",
                    changedFiles,
                    displaySymbols.Select(ToMatch).ToArray(),
                    relations,
                    new RiskDto(risk.Level, risk.PublicApis, risk.Callers, risk.CrossProject, risk.Untested, risk.WeightedTotal),
                    stale,




                    changedFilesResult.AnalysisComplete,
                    changedFilesResult.UnresolvableChangedPaths));
            }
            else
            {
                WarnIfStale(stale);
                if (!changedFilesResult.AnalysisComplete)
                    Console.Error.WriteLine($"warning: {changedFilesResult.UnresolvableChangedPaths.Count} deleted/renamed path(s) could not be resolved in the current index; impact may be incomplete.\n  {string.Join("\n  ", changedFilesResult.UnresolvableChangedPaths)}");
                Console.WriteLine($"changed files: {changedFiles.Count}");
                foreach (var file in changedFiles) Console.WriteLine($"  {file}");
                Console.WriteLine($"direct symbols: {directSymbols.Count}");
                foreach (var symbol in displaySymbols) Console.WriteLine($"  {ToDisplay(symbol)}");
                Console.WriteLine($"risk: {risk.Level} (publicApi={risk.PublicApis}, callers={risk.Callers}, crossProject={risk.CrossProject}, untested={risk.Untested})");
                foreach (var item in relationItems)
                    Console.WriteLine($"<- {ToDisplay(item.Item.Symbol)} (depth {item.Item.Depth})");
            }
            return Exit(0);
        }
        catch (GitUnavailableException exception) { return HandleGitError(exception, json, evidence); }
        catch (Exception exception) { return HandleQueryError(exception, json, evidence); }
    }

    private static async Task<int> RunArchitectureCheckAsync(string? root, bool json)
    {
        try
        {
            var projectRoot = ProjectRoot(root);
            var loaded = await LoadMapSnapshotAsync(root);
            await using var service = loaded.Service;
            var rules = ArchitectureChecker.LoadRules(projectRoot);
            var references = ArchitectureChecker.LoadProjectReferences(projectRoot);
            var violations = ArchitectureChecker.Check(loaded.Graph, references, rules);
            if (json)
                WriteJson(new ArchitectureResponse(JsonSchemaVersion, violations.Select(v => new ArchitectureViolationDto(v.Kind, v.Message, v.Source, v.Target)).ToArray(), loaded.Stale));
            else
            {
                WarnIfStale(loaded.Stale);
                if (violations.Count == 0)
                    Console.WriteLine("No architecture violations.");
                else
                    foreach (var violation in violations)
                        Console.WriteLine($"{violation.Kind}: {violation.Message}");
            }
            return Exit(violations.Count == 0 ? 0 : 1);
        }
        catch (Exception exception) { return HandleQueryError(exception, json); }
    }

    private static async Task<int> RunWatchAsync(string path, int debounceMs)
    {
        var root = Path.GetFullPath(path);
        var indexer = new IncrementalCodeMapIndexer();
        var updateGate = new SemaphoreSlim(1, 1);
        var pendingFullUpdate = 0;
        Console.WriteLine($"Watching {root} (debounce {debounceMs}ms). Press Ctrl+C to stop.");
        using var watcher = new FileSystemWatcher(root)
        {
            IncludeSubdirectories = true,
            EnableRaisingEvents = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
        };
        var debounceGate = new object();
        CancellationTokenSource? activeDebounce = null;
        var stopping = false;
        async Task RunUpdateAsync(bool fullScan)
        {
            await updateGate.WaitAsync(ShutdownToken);
            try
            {
                var summary = fullScan
                    ? await indexer.UpdateAsync(root, ShutdownToken)
                    : await indexer.UpdateAsync(root, ShutdownToken);
                Console.WriteLine($"{DateTimeOffset.Now:u} {summary}");
            }
            finally
            {
                updateGate.Release();
                if (Interlocked.Exchange(ref pendingFullUpdate, 0) == 1)
                    _ = Task.Run(() => RunUpdateAsync(fullScan: true), ShutdownToken);
            }
        }
        void ScheduleUpdate()
        {
            var debounce = new CancellationTokenSource();
            lock (debounceGate)
            {
                if (stopping)
                {
                    debounce.Dispose();
                    return;
                }
                activeDebounce?.Cancel();
                activeDebounce = debounce;
            }
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(Math.Max(100, debounceMs), debounce.Token);
                    await RunUpdateAsync(fullScan: false);
                }
                catch (OperationCanceledException) { }
                catch (Exception exception)
                {
                    Console.Error.WriteLine($"watch update failed: {exception.Message}");
                }
                finally
                {
                    lock (debounceGate)
                    {
                        if (ReferenceEquals(activeDebounce, debounce))
                            activeDebounce = null;
                        debounce.Dispose();
                    }
                }
            });
        }
        watcher.Changed += (_, eventArgs) => { if (!ShouldWatch(root, eventArgs.FullPath)) return; ScheduleUpdate(); };
        watcher.Created += (_, eventArgs) => { if (!ShouldWatch(root, eventArgs.FullPath)) return; ScheduleUpdate(); };
        watcher.Deleted += (_, eventArgs) => { if (!ShouldWatch(root, eventArgs.FullPath)) return; ScheduleUpdate(); };




        watcher.Renamed += (_, eventArgs) => { if (!ShouldWatch(root, eventArgs.OldFullPath) && !ShouldWatch(root, eventArgs.FullPath)) return; ScheduleUpdate(); };
        watcher.Error += (_, eventArgs) =>
        {
            Console.Error.WriteLine($"watch error: {eventArgs.GetException().Message}");
            Interlocked.Exchange(ref pendingFullUpdate, 1);
            ScheduleUpdate();
        };
        try
        {
            await Task.Delay(Timeout.Infinite, ShutdownToken);
            return Exit(130);
        }
        catch (OperationCanceledException)
        {
            return Exit(130);
        }
        finally
        {
            watcher.EnableRaisingEvents = false;
            lock (debounceGate)
            {
                stopping = true;
                activeDebounce?.Cancel();
                activeDebounce = null;
            }
        }
    }

    private static async Task<int> RunReportAsync(string outPath, string? root)
    {
        try
        {
            var projectRoot = ProjectRoot(root);
            var loaded = await LoadMapSnapshotAsync(root);
            await using var service = loaded.Service;
            var references = ArchitectureChecker.LoadProjectReferences(projectRoot);
            var rules = ArchitectureChecker.LoadRules(projectRoot);
            var violations = ArchitectureChecker.Check(loaded.Graph, references, rules);
            var mermaid = RepoMapFormatter.ToMermaid(loaded.Graph, references);










            var reportImpact = loaded.Graph.Edges
                .Select(edge => (Edge: edge, Caller: loaded.Graph.FindById(edge.SourceId)))
                .Where(item => item.Caller is not null)
                .Select(item => new ImpactItem(item.Caller!, item.Edge, 1, RootId: item.Edge.TargetId))
                .ToArray();
            var risk = RiskScorer.Score(loaded.Graph.Symbols, reportImpact);
            var html = RepoMapFormatter.ToHtml(mermaid, risk, violations);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
            await File.WriteAllTextAsync(outPath, html, ShutdownToken);
            Console.WriteLine($"Report written to {outPath}");
            return Exit(0);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"codemap report failed: {exception.Message}");
            return Exit(1);
        }
    }

    private static async Task<int> RunMcpAsync(string? root)
    {
        try
        {
            await CodeMapMcpServer.RunAsync(ResolveProjectRoot(root), ShutdownToken);
            return Exit(0);
        }
        catch (OperationCanceledException) when (ShutdownToken.IsCancellationRequested)
        {
            return Exit(130);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"codemap mcp failed: {exception.Message}");
            return Exit(1);
        }
    }

    private static async Task<int> RunLspAsync(string? root)
    {
        var defaultRoot = string.IsNullOrWhiteSpace(root)
            ? Directory.GetCurrentDirectory()
            : Path.GetFullPath(root);






        using var freshnessCache = new CodeMapMcpContext(defaultRoot);
        Console.Error.WriteLine("CodeMap LSP bridge ready. Send one JSON object per line.");
        while (!ShutdownToken.IsCancellationRequested)
        {
            var line = await Console.In.ReadLineAsync(ShutdownToken);
            if (line is null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var document = JsonDocument.Parse(line);
                var command = document.RootElement.GetProperty("command").GetString() ?? "";
                var queryRoot = document.RootElement.TryGetProperty("root", out var rootElement) ? rootElement.GetString() : defaultRoot;
                var response = command switch
                {
                    "find" => await LspFindAsync(document.RootElement.GetProperty("query").GetString()!, queryRoot, freshnessCache),
                    "impact" => await LspImpactAsync(
                        document.RootElement.GetProperty("query").GetString()!,
                        queryRoot,
                        freshnessCache,
                        document.RootElement.TryGetProperty("profile", out var impactProfileElement) ? impactProfileElement.GetString() ?? "code" : "code"),
                    "context" => await LspContextAsync(document.RootElement.GetProperty("task").GetString()!, queryRoot, freshnessCache),
                    "relation" => await LspRelationAsync(
                        document.RootElement.GetProperty("source").GetString()!,
                        document.RootElement.GetProperty("target").GetString()!,
                        queryRoot,
                        freshnessCache,
                        document.RootElement.TryGetProperty("minConfidence", out var minConfidenceElement) ? minConfidenceElement.GetDouble() : 0),
                    "flow" => await LspFlowAsync(
                        document.RootElement.GetProperty("entry").GetString()!,
                        document.RootElement.TryGetProperty("kind", out var kindElement) ? kindElement.GetString() ?? "all" : "all",
                        document.RootElement.TryGetProperty("depth", out var depthElement) ? depthElement.GetInt32() : CodeMapQueryService.FlowDefaultDepth,
                        queryRoot,
                        freshnessCache,
                        document.RootElement.TryGetProperty("minConfidence", out var flowMinConfidenceElement) ? flowMinConfidenceElement.GetDouble() : 0),
                    _ => new { error = $"unknown command '{command}'" }
                };
                Console.WriteLine(JsonSerializer.Serialize(response, JsonOptions));
            }
            catch (Exception exception)
            {
                Console.WriteLine(JsonSerializer.Serialize(new { error = exception.Message }, JsonOptions));
            }
        }
        return Exit(0);
    }

    private static async Task<object> LspFindAsync(string query, string? root, CodeMapMcpContext freshnessCache)
    {
        var loaded = await LoadQueryServiceAsync(root, freshnessCache);
        await using var service = loaded.Service;
        return new { matches = service.Find(query, 20).Select(ToMatch).ToArray(), stale = loaded.Stale };
    }

    private static async Task<object> LspImpactAsync(string query, string? root, CodeMapMcpContext freshnessCache, string profile = "code")
    {
        if (!CodeMapQueryService.IsValidImpactProfile(profile))
            return new { error = new { code = "query_failed", message = "profile must be one of: code, app." } };
        var loaded = await LoadQueryServiceAsync(root, freshnessCache);
        await using var service = loaded.Service;
        var resolution = service.ResolveSymbol(query, callableOnly: false, 20);
        if (resolution.Matches.Count == 0) return new { matches = Array.Empty<MatchDto>(), relations = Array.Empty<QueryRelation>(), stale = loaded.Stale };
        if (resolution.Matches.Count > 1) return new { error = "ambiguous", query, stale = loaded.Stale };
        var symbol = resolution.Matches[0];
        var impact = service.Impact(symbol, 2, 20, profile);
        return new
        {
            matches = new[] { ToMatch(symbol) },
            relations = impact.Select(item => ToRelation("impact", item.Symbol, symbol, item.Depth, item.Via, service, evidence: true)).ToArray(),
            stale = loaded.Stale
        };
    }

    private static async Task<object> LspRelationAsync(string source, string target, string? root, CodeMapMcpContext freshnessCache, double minConfidence)
    {
        if (!RelationConfidence.IsValid(minConfidence))
            return new
            {
                error = new
                {
                    code = "query_failed",
                    message = RelationConfidence.InvalidMessage
                }
            };

        var loaded = await LoadQueryServiceAsync(root, freshnessCache);
        await using var service = loaded.Service;
        var sourceResolution = service.ResolveSymbol(source, callableOnly: false, 20);
        if (sourceResolution.Matches.Count == 0)
            return new { matches = Array.Empty<MatchDto>(), relations = Array.Empty<QueryRelation>(), stale = loaded.Stale };
        if (sourceResolution.Matches.Count > 1)
            return new { error = "ambiguous", query = source, stale = loaded.Stale };
        var sourceSymbol = sourceResolution.Matches[0];

        var targetResolution = service.ResolveSymbol(target, callableOnly: false, 20);
        if (targetResolution.Matches.Count == 0)
            return new { matches = Array.Empty<MatchDto>(), relations = Array.Empty<QueryRelation>(), stale = loaded.Stale };
        if (targetResolution.Matches.Count > 1)
            return new { error = "ambiguous", query = target, stale = loaded.Stale };
        var targetSymbol = targetResolution.Matches[0];
        var relations = service.Relations(sourceSymbol.Id, targetSymbol.Id, edgeKind: null, maxResults: 20, minConfidence)
            .Select(item => ToRelation("relation", item.Source, item.Target, null, item.Edge, service, evidence: true, item.Evidence))
            .ToArray();
        return new
        {
            matches = new[] { ToMatch(sourceSymbol), ToMatch(targetSymbol) },
            relations,
            stale = loaded.Stale
        };
    }

    private static async Task<object> LspFlowAsync(string entry, string kind, int depth, string? root, CodeMapMcpContext freshnessCache, double minConfidence)
    {
        if (!RelationConfidence.IsValid(minConfidence))
            return new { error = new { code = "query_failed", message = RelationConfidence.InvalidMessage } };
        if (!CodeMapQueryService.IsValidFlowKind(kind))
            return new { error = new { code = "query_failed", message = "kind must be one of: http, ui, all." } };
        if (!CodeMapQueryService.IsValidFlowDepth(depth))
            return new { error = new { code = "query_failed", message = $"depth must be between {CodeMapQueryService.FlowMinDepth} and {CodeMapQueryService.FlowMaxDepth}." } };

        var loaded = await LoadQueryServiceAsync(root, freshnessCache);
        await using var service = loaded.Service;
        var resolution = service.ResolveSymbol(entry, callableOnly: false, 20);
        if (resolution.Matches.Count == 0)
            return new { matches = Array.Empty<MatchDto>(), relations = Array.Empty<QueryRelation>(), stale = loaded.Stale };
        if (resolution.Matches.Count > 1)
            return new { error = "ambiguous", query = entry, stale = loaded.Stale };
        var entrySymbol = resolution.Matches[0];
        var flow = service.Flow(entrySymbol, kind, depth, 20, minConfidence);
        var sourceById = service.FindByIds(flow.Select(item => item.Via.SourceId));
        var fileById = service.FindFilesByIds(flow.Select(item => item.Via.SourceFileId).OfType<string>());
        return new
        {
            matches = new[] { ToMatch(entrySymbol) },
            relations = flow.Select(item => ToFlowRelation(item, entrySymbol, sourceById, service, evidence: true, fileById)).ToArray(),
            stale = loaded.Stale
        };
    }

    private static async Task<object> LspContextAsync(string task, string? root, CodeMapMcpContext freshnessCache)
    {


        var loaded = await LoadQueryServiceAsync(root, freshnessCache);
        await using var service = loaded.Service;
        var matches = service.Find(task, 5);
        var map = service.BuildMap(matches.FirstOrDefault()?.Name ?? task, null, 500);
        return new { matches = matches.Select(ToMatch).ToArray(), mapLines = map.Lines, stale = loaded.Stale };
    }

    private static string ProjectRoot(string? root)
    {
        var databasePath = FindDatabase(root);
        return Path.GetDirectoryName(Path.GetDirectoryName(databasePath))!;
    }

    private static string ResolveProjectRoot(string? root) =>
        string.IsNullOrWhiteSpace(root) ? Directory.GetCurrentDirectory() : Path.GetFullPath(root);

    private static int HandleGitError(GitUnavailableException exception, bool json, bool evidence = false)
    {
        if (json)
            WriteJson(new ErrorResponse(SchemaVersion(evidence), new ErrorDto("git_unavailable", exception.Message)));
        else
            Console.Error.WriteLine(exception.Message);
        return Exit(1);
    }

    private static bool ShouldWatch(string root, string fullPath) =>
        !IgnoreRules.IsIgnored(root, fullPath) && !IgnoreRules.IsOutsideRoot(root, fullPath);

    private sealed record RiskDto(string Level, int PublicApis, int Callers, int CrossProject, int Untested, double WeightedTotal);
    private sealed record DiffResponse(int Version, string Base, IReadOnlyList<string> ChangedFiles, IReadOnlyList<MatchDto> Matches, IReadOnlyList<QueryRelation> Relations, RiskDto Risk, bool Stale, bool AnalysisComplete, IReadOnlyList<string> UnresolvedChangedFiles);
    private sealed record ArchitectureViolationDto(string Kind, string Message, string? Source, string? Target);
    private sealed record ArchitectureResponse(int Version, IReadOnlyList<ArchitectureViolationDto> Violations, bool Stale);
}
