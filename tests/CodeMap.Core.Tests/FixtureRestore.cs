using System.Diagnostics;

namespace CodeMap.Core.Tests;











internal static class FixtureRestore
{
    private static readonly HashSet<string> Restored = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock Gate = new();

    internal static string EnsureRestored(string fixtureName)
    {
        var path = GetFixturePath(fixtureName);
        lock (Gate)
        {
            if (Restored.Contains(path))
                return path;

            var startInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = "restore",
                WorkingDirectory = path,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"Failed to start 'dotnet restore' for fixture '{fixtureName}'.");
            var stdOut = process.StandardOutput.ReadToEnd();
            var stdErr = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(TimeSpan.FromSeconds(60)))
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException($"'dotnet restore' timed out for fixture '{fixtureName}'.");
            }
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"'dotnet restore' failed for fixture '{fixtureName}' (exit {process.ExitCode}).\n{stdOut}\n{stdErr}");

            Restored.Add(path);
            return path;
        }
    }

    private static string GetFixturePath(string fixtureName)
    {
        var testDir = AppContext.BaseDirectory;
        var solutionRoot = Path.GetFullPath(Path.Combine(testDir, "..", "..", "..", "..", ".."));
        return Path.Combine(solutionRoot, "tests", "Fixtures", fixtureName);
    }
}