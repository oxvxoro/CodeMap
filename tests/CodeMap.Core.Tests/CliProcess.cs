using System.Diagnostics;
using System.Text;

namespace CodeMap.Core.Tests;

internal static class CliProcess
{
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);
    internal static readonly TimeSpan IndexTimeout = TimeSpan.FromSeconds(90);
    private const int TailOutputBytes = 4096;

    internal static string GetCliDllPath()
    {
        var testDir = AppContext.BaseDirectory;
        var solutionRoot = Path.GetFullPath(Path.Combine(testDir, "..", "..", "..", "..", ".."));
        return Path.Combine(solutionRoot, "src", "CodeMap.Cli", "bin", "Debug", "net10.0", "codemap.dll");
    }

    internal static Task<CliRunResult> RunAsync(string arguments, TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
        RunCoreAsync(arguments, input: null, timeout, interMessageDelay: null, cancellationToken);

    internal static Task<CliRunResult> RunWithInputAsync(
        string arguments,
        string input,
        TimeSpan? timeout = null,
        TimeSpan? interMessageDelay = null,
        CancellationToken cancellationToken = default) =>
        RunCoreAsync(arguments, input, timeout, interMessageDelay, cancellationToken);

    private static async Task<CliRunResult> RunCoreAsync(
        string arguments,
        string? input,
        TimeSpan? timeout,
        TimeSpan? interMessageDelay,
        CancellationToken cancellationToken)
    {
        var cliDll = GetCliDllPath();
        if (!File.Exists(cliDll))
            throw new FileNotFoundException("Build the CLI before running process tests.", cliDll);

        var effectiveTimeout = timeout ?? (arguments.Contains("index ", StringComparison.Ordinal)
            || arguments.StartsWith("index", StringComparison.Ordinal)
            ? IndexTimeout
            : DefaultTimeout);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(effectiveTimeout);
        var token = timeoutCts.Token;

        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"exec \"{cliDll}\" {arguments}",
            RedirectStandardInput = input is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start CLI process.");

        var stdOutBuilder = new StringBuilder();
        var stdErrBuilder = new StringBuilder();
        var stdOutTask = PumpAsync(process.StandardOutput, stdOutBuilder, token);
        var stdErrTask = PumpAsync(process.StandardError, stdErrBuilder, token);

        try
        {
            if (input is not null)
            {
                foreach (var message in input.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries))
                {
                    token.ThrowIfCancellationRequested();
                    await process.StandardInput.WriteLineAsync(message.AsMemory(), token);
                    await process.StandardInput.FlushAsync(token);
                    if (interMessageDelay is not null)
                        await Task.Delay(interMessageDelay.Value, token);
                }
                process.StandardInput.Close();
            }

            await process.WaitForExitAsync(token);
            await Task.WhenAll(stdOutTask, stdErrTask);
            return new CliRunResult(process.ExitCode, stdOutBuilder.ToString(), stdErrBuilder.ToString());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await KillAndDrainAsync(process, stdOutTask, stdErrTask);
            throw new TimeoutException(
                BuildFailureMessage($"codemap timed out after {effectiveTimeout.TotalSeconds:0}s.", arguments, stdOutBuilder, stdErrBuilder));
        }
        catch (OperationCanceledException)
        {
            await KillAndDrainAsync(process, stdOutTask, stdErrTask);
            throw;
        }
        catch
        {
            await KillAndDrainAsync(process, stdOutTask, stdErrTask);
            throw;
        }
    }

    private static async Task PumpAsync(StreamReader reader, StringBuilder buffer, CancellationToken cancellationToken)
    {
        var chunk = new char[4096];
        while (true)
        {
            var read = await reader.ReadAsync(chunk, cancellationToken);
            if (read == 0)
                break;
            buffer.Append(chunk, 0, read);
        }
    }

    private static async Task KillAndDrainAsync(Process process, Task stdOutTask, Task stdErrTask)
    {
        TryKill(process);
        try
        {
            await Task.WhenAll(stdOutTask, stdErrTask).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch
        {

        }
    }

    private static string BuildFailureMessage(string prefix, string arguments, StringBuilder stdOut, StringBuilder stdErr)
    {
        var combined = stdOut.ToString() + stdErr;
        var tail = combined.Length <= TailOutputBytes
            ? combined
            : combined[^TailOutputBytes..];
        return $"{prefix}{Environment.NewLine}arguments: {arguments}{Environment.NewLine}output tail:{Environment.NewLine}{tail}";
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }
}

internal sealed record CliRunResult(int ExitCode, string StdOut, string StdErr);

[CollectionDefinition("MsBuild", DisableParallelization = true)]
public sealed class MsBuildCollection
{
}