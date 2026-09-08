using System.Diagnostics;
using CodeMap.Storage;
using Microsoft.Data.Sqlite;

namespace CodeMap.Core.Tests;

[Collection("MsBuild")]
public sealed class CliProcessTests
{
    [Fact]
    public async Task RunAsync_CompletesForVersionCommand()
    {
        var result = await CliProcess.RunAsync("version");
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("codemap", result.StdOut, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_ThrowsTimeoutException_WhenProcessExceedsLimit()
    {
        var exception = await Assert.ThrowsAsync<TimeoutException>(() =>
            CliProcess.RunAsync("watch . --debounce-ms 60000", timeout: TimeSpan.FromSeconds(2)));

        Assert.Contains("timed out", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("arguments:", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunWithInputAsync_HonorsExternalCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var task = CliProcess.RunWithInputAsync("watch . --debounce-ms 60000", string.Empty, cancellationToken: cancellation.Token);
        await Task.Delay(250);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task RunWithInputAsync_DrainsOutputFromChildProcess()
    {
        var result = await CliProcess.RunWithInputAsync("version", string.Empty);
        Assert.Equal(0, result.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(result.StdOut));
    }

    [Fact(Timeout = 90_000)]
    public async Task Watch_BurstyEvents_StaysAliveAndIndexesFinalFileState()
    {
        var workingDirectory = CopyFixture("MultiProject");
        await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);
        using var process = StartWatchProcess(workingDirectory, debounceMs: 100);
        try
        {
            await WaitForWatchStartupAsync(process);
            var watchedFile = Path.Combine(workingDirectory, "ProjA", "WatchBurst.cs");
            for (var version = 0; version < 8; version++)
            {
                await File.WriteAllTextAsync(watchedFile, $"namespace Fixture.ProjA; public sealed class BurstVersion{version} {{ }}");
                await Task.Delay(25);
            }

            await WaitUntilAsync(async () =>
            {
                var graph = await new CodeMapQueryStore(Path.Combine(workingDirectory, ".codemap", "index.db")).LoadAsync();
                return graph.Symbols.Any(symbol => symbol.Name == "BurstVersion7");
            }, TimeSpan.FromSeconds(25));

            Assert.False(process.HasExited, await ReadProcessDiagnosticsAsync(process));
        }
        finally
        {
            StopProcess(process);
            AssertNoStateTempFiles(workingDirectory);
            CleanUp(workingDirectory);
        }
    }

    [Fact(Timeout = 90_000)]
    public async Task Watch_ChangeImmediatelyAfterStartupSignal_IsIndexed()
    {
        var workingDirectory = CopyFixture("MultiProject");
        await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);
        using var process = StartWatchProcess(workingDirectory, debounceMs: 100);
        try
        {
            await WaitForWatchStartupAsync(process);
            await File.WriteAllTextAsync(
                Path.Combine(workingDirectory, "ProjA", "WatchStartup.cs"),
                "namespace Fixture.ProjA; public sealed class WatchStartup { }");

            await WaitUntilAsync(async () =>
            {
                var graph = await new CodeMapQueryStore(Path.Combine(workingDirectory, ".codemap", "index.db")).LoadAsync();
                return graph.Symbols.Any(symbol => symbol.Name == "WatchStartup");
            }, TimeSpan.FromSeconds(25));

            Assert.False(process.HasExited, await ReadProcessDiagnosticsAsync(process));
        }
        finally
        {
            StopProcess(process);
            AssertNoStateTempFiles(workingDirectory);
            CleanUp(workingDirectory);
        }
    }

    [Fact(Timeout = 90_000)]
    public async Task Watch_ShutdownDuringPendingDebounce_LeavesStateFileClean()
    {
        var workingDirectory = CopyFixture("MultiProject");
        await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);
        using var process = StartWatchProcess(workingDirectory, debounceMs: 5_000);
        try
        {
            await WaitForWatchStartupAsync(process);
            await File.WriteAllTextAsync(Path.Combine(workingDirectory, "ProjA", "PendingDebounce.cs"), "namespace Fixture.ProjA; public sealed class PendingDebounce { }");
            await Task.Delay(100);
            StopProcess(process);

            AssertNoStateTempFiles(workingDirectory);
            Assert.True(File.Exists(Path.Combine(workingDirectory, ".codemap", "state.json")));
        }
        finally
        {
            StopProcess(process);
            CleanUp(workingDirectory);
        }
    }

    private static Process StartWatchProcess(string workingDirectory, int debounceMs)
    {
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"exec \"{CliProcess.GetCliDllPath()}\" watch \"{workingDirectory}\" --debounce-ms {debounceMs}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        });
        return process ?? throw new InvalidOperationException("Failed to start watch process.");
    }

    private static async Task WaitForWatchStartupAsync(Process process)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var startup = await process.StandardOutput.ReadLineAsync(timeout.Token);
        Assert.NotNull(startup);
        Assert.Contains("Watching", startup, StringComparison.Ordinal);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition())
                return;
            await Task.Delay(100);
        }

        throw new TimeoutException($"Condition was not met within {timeout}.");
    }

    private static void StopProcess(Process process)
    {
        if (process.HasExited)
            return;

        process.Kill(entireProcessTree: true);
        process.WaitForExit(5_000);
    }

    private static async Task<string> ReadProcessDiagnosticsAsync(Process process)
    {
        if (!process.HasExited)
            return string.Empty;
        return await process.StandardError.ReadToEndAsync();
    }

    private static void AssertNoStateTempFiles(string workingDirectory)
    {
        var stateDirectory = Path.Combine(workingDirectory, ".codemap");
        Assert.Empty(Directory.EnumerateFiles(stateDirectory, "state.json.*.tmp"));
    }

    private static string CopyFixture(string fixtureName)
    {
        var source = Path.Combine(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..")), "tests", "Fixtures", fixtureName);
        var destination = Path.Combine(Path.GetTempPath(), $"codemap-process-{fixtureName}-{Guid.NewGuid()}");
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*.*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }

        return destination;
    }

    private static void CleanUp(string path)
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }
}
