using System.CommandLine;

namespace CodeMap.Cli;

internal static class CommandCatalog
{
    internal static void Register(RootCommand rootCommand)
    {
        var versionCommand = new Command("version", "Show version information.");
        versionCommand.SetHandler(Program.PrintVersion);
        rootCommand.AddCommand(versionCommand);

        var indexPath = new Argument<string>("path", () => Directory.GetCurrentDirectory(), "Repository, solution, or project path.") { Arity = ArgumentArity.ZeroOrOne };
        var force = new Option<bool>("--force", "Rebuild the index even when an index already exists.");
        var indexCommand = new Command("index", "Build a complete semantic index.");
        indexCommand.AddArgument(indexPath);
        indexCommand.AddOption(force);
        indexCommand.SetHandler(async (string path, bool rebuild) => await Program.RunIndexAsync(path, rebuild), indexPath, force);
        rootCommand.AddCommand(indexCommand);

        var updatePath = new Argument<string>("path", () => Directory.GetCurrentDirectory(), "Repository, solution, or project path.") { Arity = ArgumentArity.ZeroOrOne };
        var updateCommand = new Command("update", "Update the index using content hashes.");
        updateCommand.AddArgument(updatePath);
        updateCommand.SetHandler(async (string path) => await Program.RunUpdateAsync(path), updatePath);
        rootCommand.AddCommand(updateCommand);
        var scipCommand = new Command("scip", "Manage imported SCIP providers.");
        Program.AddScipImportCommand(scipCommand);
        Program.AddScipListCommand(scipCommand);
        Program.AddScipRemoveCommand(scipCommand);
        rootCommand.AddCommand(scipCommand);

        Program.AddFindCommand(rootCommand);
        Program.AddPairRelationCommand(rootCommand);
        Program.AddRelationCommand(rootCommand, "refs", "Show references to a symbol.", Program.RunRefsAsync);
        Program.AddRelationCommand(rootCommand, "callers", "Show callers of a method.", Program.RunCallersAsync);
        Program.AddRelationCommand(rootCommand, "callees", "Show methods called by a method.", Program.RunCalleesAsync, withDepth: true);
        Program.AddRelationCommand(rootCommand, "impl", "Show implementations of a type or method.", Program.RunImplAsync);
        Program.AddImpactCommand(rootCommand);
        Program.AddDiffCommand(rootCommand);
        Program.AddCheckCommand(rootCommand);
        Program.AddWatchCommand(rootCommand);
        Program.AddStatusCommand(rootCommand);
        Program.AddReportCommand(rootCommand);
        Program.AddMcpCommand(rootCommand);
        Program.AddLspCommand(rootCommand);
        Program.AddMapCommand(rootCommand);
        Program.AddContextCommand(rootCommand);
        Program.AddFlowCommand(rootCommand);
        Program.AddSliceCommand(rootCommand);
        Program.AddInvestigateCommand(rootCommand);
    }
}
