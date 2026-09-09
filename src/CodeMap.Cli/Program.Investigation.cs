using System.CommandLine;
using System.Text.Json;
using CodeMap.Core.Models;
using CodeMap.Engine.Application;
using CodeMap.Engine.Application.Investigation;

namespace CodeMap.Cli;

public static partial class Program
{
    internal static void AddInvestigateCommand(RootCommand root)
    {
        var query = new Argument<string>("query", "Root symbol name or qualified name.");
        var goal = new Option<string>("--goal", "Investigation goal: debug, trace, impact, or understand.") { IsRequired = true };
        var rootPath = new Option<string?>("--root", "Project root or directory containing .codemap/index.db.");
        var tokens = new Option<int>("--tokens", () => 2000, "Approximate investigation token budget.");
        var maxResults = new Option<int>("--max-results", () => 200, "Maximum number of candidates.");
        var depth = new Option<int?>("--depth", "Override the profile traversal depth (1..8).");
        var minConfidence = new Option<double>("--min-confidence", () => 0, "Minimum confidence for relation edges (0..1).");
        var sourceMode = new Option<string>("--source-mode", () => "minimal", "Source evidence mode: none, minimal, or scope.");
        var json = new Option<bool>("--json", "Emit the investigation v1 JSON contract.");
        var command = new Command("investigate", "Run a deterministic, goal-directed investigation bundle.");
        command.AddArgument(query);
        command.AddOption(goal);
        command.AddOption(rootPath);
        command.AddOption(tokens);
        command.AddOption(maxResults);
        command.AddOption(depth);
        command.AddOption(minConfidence);
        command.AddOption(sourceMode);
        command.AddOption(json);
        command.SetHandler(async context =>
        {
            var queryValue = context.ParseResult.GetValueForArgument(query);
            var goalValue = context.ParseResult.GetValueForOption(goal);
            var rootValue = context.ParseResult.GetValueForOption(rootPath);
            var tokenBudget = context.ParseResult.GetValueForOption(tokens);
            var maxResultsValue = context.ParseResult.GetValueForOption(maxResults);
            var depthValue = context.ParseResult.GetValueForOption(depth);
            var minConfidenceValue = context.ParseResult.GetValueForOption(minConfidence);
            var sourceModeValue = context.ParseResult.GetValueForOption(sourceMode);
            var jsonValue = context.ParseResult.GetValueForOption(json);
            context.ExitCode = await RunInvestigateAsync(queryValue, goalValue, rootValue, tokenBudget, maxResultsValue,
                depthValue, minConfidenceValue, sourceModeValue, jsonValue);
        });
        root.AddCommand(command);
    }

    private static async Task<int> RunInvestigateAsync(
        string query, string goalText, string? root, int tokenBudget, int maxResults, int? depth,
        double minConfidence, string sourceMode, bool json)
    {
        if (!Enum.TryParse<InvestigationGoal>(goalText, true, out var goal))
            return InvestigationError(json, query, goalText, "query_failed", "goal must be one of: debug, trace, impact, understand.");
        var response = await Application.InvestigateAsync(
            new InvestigationRequest(query, goal, root, tokenBudget, maxResults, depth, minConfidence, true, sourceMode),
            ShutdownToken);
        if (!response.Succeeded)
        {
            if (response.Error!.Code == "ambiguous" && response.Value is { } ambiguous)
            {
                if (json)
                    WriteJson(new
                    {
                        version = 1,
                        query,
                        goal = goal.ToString().ToLowerInvariant(),
                        root = (object?)null,
                        isAmbiguous = true,
                        ambiguousCandidates = ambiguous.Resolution.AmbiguousCandidates.Select(ToMatch).ToArray(),
                        items = Array.Empty<object>(),
                        sourceSpans = Array.Empty<object>(),
                        budget = new { requested = ambiguous.Budget.RequestedBudget, estimated = 0, truncated = false, reason = (string?)null },
                        coverage = ToCoverage(ambiguous.Coverage),
                        stale = response.Stale,
                        warnings = Array.Empty<string>(),
                        reason = "ambiguous"
                    });
                else
                    Console.Error.WriteLine(response.Error.Message);
                return Exit(2);
            }
            return InvestigationError(json, query, goal.ToString().ToLowerInvariant(), response.Error.Code, response.Error.Message, response.Stale);
        }

        var result = response.Value!;
        var payload = new
        {
            version = 1,
            query,
            goal = goal.ToString().ToLowerInvariant(),
            root = result.Resolution.Root is null ? null : ToMatch(result.Resolution.Root),
            isAmbiguous = result.Resolution.IsAmbiguous,
            ambiguousCandidates = result.Resolution.AmbiguousCandidates.Select(ToMatch).ToArray(),
            items = result.Items.Select(ToInvestigationItem).ToArray(),
            sourceSpans = result.SourceSpans,
            budget = new
            {
                requested = result.Budget.RequestedBudget,
                estimated = result.Budget.EstimatedTokens,
                truncated = result.Budget.Truncated,
                reason = result.Budget.TruncationReason
            },
            coverage = ToCoverage(result.Coverage),
            stale = response.Stale,
            warnings = Array.Empty<string>(),
            reason = (string?)null
        };
        if (json)
            WriteJson(payload);
        else
        {
            Console.WriteLine($"{goal.ToString().ToLowerInvariant()} investigation: {result.Items.Count} candidates");
            foreach (var item in result.Items)
                Console.WriteLine($"- {item.Symbol.DisplayName} ({item.Provider}, depth {item.Depth})");
        }
        return Exit(0);

        object ToInvestigationItem(InvestigationCandidate candidate) => new
        {
            symbol = ToMatch(candidate.Symbol),
            via = candidate.Via is null ? null : new
            {
                edgeKind = candidate.Via.Kind.ToString(),
                resolutionKind = candidate.Via.ResolutionKind.ToString().ToLowerInvariant(),
                confidence = candidate.Via.Confidence,
                location = candidate.Via.SourceFileId is null && candidate.Via.Line is null
                    ? null
                    : new { file = candidate.Symbol.RelativePath, line = candidate.Via.Line }
            },
            depth = candidate.Depth,
            provider = candidate.Provider,
            alsoFoundBy = candidate.AlsoFoundBy
        };

        static object ToCoverage(CodeMap.Core.Models.Investigation.InvestigationCoverage coverage) => new
        {
            providers = coverage.Providers.Select(status => new
            {
                provider = status.Provider == CodeMap.Core.Models.Investigation.InvestigationProviderKind.LocalSlice
                    ? "localslice"
                    : status.Provider.ToString().ToLowerInvariant(),
                state = status.State.ToString().ToLowerInvariant(),
                reason = status.Reason,
                foundCount = status.FoundCount
            }).ToArray(),
            remaining = coverage.Remaining,
            negativeEvidence = coverage.NegativeEvidence
        };
    }

    private static int InvestigationError(bool json, string query, string goal, string code, string message, bool stale = false)
    {
        if (json)
            WriteJson(new { version = 1, query, goal, error = new { code, message }, reason = code, stale });
        else
            Console.Error.WriteLine(message);
        return Exit(code is "no_matches" or "ambiguous" ? 2 : 1);
    }
}
