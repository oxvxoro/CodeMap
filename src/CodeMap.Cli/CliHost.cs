using System.CommandLine;

namespace CodeMap.Cli;

/// <summary>
/// CLI process host. Command construction and handlers remain testable in the
/// partial command files; this class owns process lifetime and cancellation.
/// </summary>
internal static class CliHost
{
    internal static async Task<int> RunAsync(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        RootCommand rootCommand = Program.CreateRootCommand(cancellation.Token);
        var invocationCode = await rootCommand.InvokeAsync(args);
        return Environment.ExitCode != 0 ? Environment.ExitCode : invocationCode;
    }
}
