using System.Diagnostics;

namespace CodeMap.Storage;

public sealed class GitUnavailableException : InvalidOperationException
{
    public GitUnavailableException(string message) : base(message) { }
}














public sealed record ChangedFilesResult(
    IReadOnlyList<string> AllChangedPaths,
    IReadOnlyList<string> NewPathChangedFiles,
    IReadOnlyList<string> UnresolvableChangedPaths)
{
    public bool AnalysisComplete => UnresolvableChangedPaths.Count == 0;
}

public static class GitChangedFileResolver
{
    public static IReadOnlyList<string> ListChangedFiles(string repoRoot, string? baseRevision, CancellationToken cancellationToken = default) =>
        ListChangedFilesWithStatus(repoRoot, baseRevision, cancellationToken).AllChangedPaths;

    public static ChangedFilesResult ListChangedFilesWithStatus(string repoRoot, string? baseRevision, CancellationToken cancellationToken = default)
    {
        EnsureGitRepository(repoRoot, cancellationToken);

        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unresolvable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddNameStatusEntries(files, unresolvable, RunGit(repoRoot,
            ["diff", "--name-status", "-z", "--find-renames", "--relative", string.IsNullOrWhiteSpace(baseRevision) ? "HEAD" : baseRevision], cancellationToken));
        AddNameStatusEntries(files, unresolvable, RunGit(repoRoot,
            ["diff", "--name-status", "-z", "--find-renames", "--relative"], cancellationToken));


        foreach (var line in Lines(RunGit(repoRoot, ["ls-files", "--others", "--exclude-standard"], cancellationToken)))
            Add(files, line);

        var allPaths = files.Concat(unresolvable).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new ChangedFilesResult(
            allPaths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray(),
            files.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray(),
            unresolvable.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray());
    }











    private static void AddNameStatusEntries(HashSet<string> files, HashSet<string> unresolvable, string nameStatusOutput)
    {
        var fields = nameStatusOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var index = 0;
        while (index < fields.Length)
        {
            var status = fields[index++];
            if (status.Length == 0 || index >= fields.Length)
                break;
            var statusCode = status[0];
            if (statusCode is 'R' or 'C')
            {
                if (index + 1 >= fields.Length)
                    break;
                var oldPath = fields[index++];
                var newPath = fields[index++];


                if (statusCode == 'R')
                    Add(unresolvable, oldPath);
                Add(files, newPath);
                continue;
            }

            var path = fields[index++];
            switch (statusCode)
            {
                case 'A' or 'M':
                    Add(files, path);
                    break;
                default:


                    Add(unresolvable, path);
                    break;
            }
        }
    }

    private static void EnsureGitRepository(string repoRoot, CancellationToken cancellationToken)
    {
        try
        {
            var output = RunGit(repoRoot, ["rev-parse", "--show-toplevel"], cancellationToken);
            if (string.IsNullOrWhiteSpace(output))
                throw new GitUnavailableException("Not a git repository.");
        }
        catch (GitUnavailableException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new GitUnavailableException($"Unable to inspect git repository: {exception.Message}");
        }
    }

    private static void Add(HashSet<string> files, string line)
    {
        var trimmed = CodeMapPath.Normalize(line);
        if (trimmed.Length > 0)
            files.Add(trimmed);
    }

    private static IEnumerable<string> Lines(string output) =>
        output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

    private static string RunGit(string repoRoot, string[] arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-C");
        startInfo.ArgumentList.Add(repoRoot);
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        Process process;
        try
        {
            process = Process.Start(startInfo)
                ?? throw new GitUnavailableException("Failed to start git.");
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new GitUnavailableException($"Unable to start git: {exception.Message}");
        }
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            process.WaitForExitAsync(cancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(30), cancellationToken)
                .GetAwaiter()
                .GetResult();
        }
        catch
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch {  }
            throw;
        }
        var output = stdout.GetAwaiter().GetResult();
        var error = stderr.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
            throw new GitUnavailableException(string.IsNullOrWhiteSpace(error)
                ? $"git exited {process.ExitCode}."
                : error.Trim());
        return output;
    }
}
