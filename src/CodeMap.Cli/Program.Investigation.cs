using System.CommandLine;
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
        var includeHeuristic = new Option<bool>("--include-heuristic", () => true, "Include heuristic relation evidence.");
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
        command.AddOption(includeHeuristic);
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
            var includeHeuristicValue = context.ParseResult.GetValueForOption(includeHeuristic);
            var sourceModeValue = context.ParseResult.GetValueForOption(sourceMode);
            var jsonValue = context.ParseResult.GetValueForOption(json);
            context.ExitCode = await RunInvestigateAsync(queryValue, goalValue ?? string.Empty, rootValue, tokenBudget, maxResultsValue,
                depthValue, minConfidenceValue, includeHeuristicValue, sourceModeValue ?? "minimal", jsonValue);
        });
        root.AddCommand(command);
    }

    private static async Task<int> RunInvestigateAsync(
        string query, string goalText, string? root, int tokenBudget, int maxResults, int? depth,
        double minConfidence, bool includeHeuristic, string sourceMode, bool json)
    {
        if (!Enum.TryParse<InvestigationGoal>(goalText, true, out var goal))
            return InvestigationError(json, query, goalText, "query_failed", "goal must be one of: debug, trace, impact, understand.");
        var response = await Application.InvestigateAsync(
            new InvestigationRequest(query, goal, root, tokenBudget, maxResults, depth, minConfidence, includeHeuristic, sourceMode),
            ShutdownToken);
        if (!response.Succeeded)
        {
            if (response.Error!.Code == "ambiguous" && response.Value is { } ambiguous)
            {
                    if (json)
                    WriteJson(InvestigationPresentationMapper.ToAmbiguousResponse(query, goal, ambiguous, response.Stale));
                else
                    Console.Error.WriteLine(response.Error.Message);
                return Exit(2);
            }
            return InvestigationError(json, query, goal.ToString().ToLowerInvariant(), response.Error.Code, response.Error.Message, response.Stale);
        }

        var result = response.Value!;
        var payload = InvestigationPresentationMapper.ToResponse(query, goal, result, response.Stale);
        if (json)
            WriteJson(payload);
        else
        {
            Console.WriteLine($"{goal.ToString().ToLowerInvariant()} investigation: {result.Items.Count} candidates");
            foreach (var item in result.Items)
                Console.WriteLine($"- {item.Symbol.DisplayName} ({item.Provider}, depth {item.Depth})");
        }
        return Exit(0);
    }

    private static int InvestigationError(bool json, string query, string goal, string code, string message, bool stale = false)
    {
        if (json)
            WriteJson(InvestigationPresentationMapper.ToError(query, goal, new QueryError(code, message), stale));
        else
            Console.Error.WriteLine(message);
        return Exit(code is "no_matches" or "ambiguous" ? 2 : 1);
    }
}
