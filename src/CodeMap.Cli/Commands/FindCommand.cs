using System.CommandLine;

namespace CodeMap.Cli;

public static partial class Program
{
    internal static void AddFindCommand(RootCommand root)
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
}
