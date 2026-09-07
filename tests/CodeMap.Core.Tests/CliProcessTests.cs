using System.Diagnostics;

namespace CodeMap.Core.Tests;

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
}