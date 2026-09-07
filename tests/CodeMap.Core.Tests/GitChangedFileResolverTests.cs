using System.Diagnostics;
using CodeMap.Storage;

namespace CodeMap.Core.Tests;








public sealed class GitChangedFileResolverTests
{
    [Fact]
    public void ListChangedFilesWithStatus_DeletedTrackedFile_IsUnresolvable()
    {
        var workingDirectory = InitRepoWithBaseline(("kept.cs", "// kept"), ("removed.cs", "// removed"));
        try
        {
            File.Delete(Path.Combine(workingDirectory, "removed.cs"));

            var result = GitChangedFileResolver.ListChangedFilesWithStatus(workingDirectory, "HEAD", CancellationToken.None);

            Assert.False(result.AnalysisComplete);
            Assert.Contains("removed.cs", result.UnresolvableChangedPaths);
            Assert.DoesNotContain("removed.cs", result.NewPathChangedFiles);
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }

    [Fact]
    public void ListChangedFilesWithStatus_RenamedFile_NewPathResolvableOldPathUnresolvable()
    {
        var workingDirectory = InitRepoWithBaseline(("old-name.cs", "namespace Fixture;\n\npublic class Widget { public void Method1() {} public void Method2() {} public void Method3() {} }\n"));
        try
        {
            File.Move(Path.Combine(workingDirectory, "old-name.cs"), Path.Combine(workingDirectory, "new-name.cs"));

            var result = GitChangedFileResolver.ListChangedFilesWithStatus(workingDirectory, "HEAD", CancellationToken.None);

            Assert.False(result.AnalysisComplete);
            Assert.Contains("old-name.cs", result.UnresolvableChangedPaths);
            Assert.Contains("new-name.cs", result.NewPathChangedFiles);
            Assert.DoesNotContain("old-name.cs", result.NewPathChangedFiles);
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }

    [Fact]
    public void ListChangedFilesWithStatus_PureModify_IsComplete()
    {
        var workingDirectory = InitRepoWithBaseline(("modified.cs", "// v1"));
        try
        {
            File.WriteAllText(Path.Combine(workingDirectory, "modified.cs"), "// v2");

            var result = GitChangedFileResolver.ListChangedFilesWithStatus(workingDirectory, "HEAD", CancellationToken.None);

            Assert.True(result.AnalysisComplete);
            Assert.Empty(result.UnresolvableChangedPaths);
            Assert.Contains("modified.cs", result.NewPathChangedFiles);
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }

    [Fact]
    public void ListChangedFilesWithStatus_UntrackedNewFile_IsCompleteAndResolvable()
    {
        var workingDirectory = InitRepoWithBaseline(("existing.cs", "// existing"));
        try
        {
            File.WriteAllText(Path.Combine(workingDirectory, "untracked.cs"), "// new");

            var result = GitChangedFileResolver.ListChangedFilesWithStatus(workingDirectory, "HEAD", CancellationToken.None);

            Assert.True(result.AnalysisComplete);
            Assert.Contains("untracked.cs", result.NewPathChangedFiles);
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }





    [Fact]
    public void ListChangedFiles_ReturnsFullChangedPathList_IncludingDeletedFiles()
    {
        var workingDirectory = InitRepoWithBaseline(("kept.cs", "// kept"), ("removed.cs", "// removed"));
        try
        {
            File.Delete(Path.Combine(workingDirectory, "removed.cs"));

            var changedFiles = GitChangedFileResolver.ListChangedFiles(workingDirectory, "HEAD", CancellationToken.None);
            var withStatus = GitChangedFileResolver.ListChangedFilesWithStatus(workingDirectory, "HEAD", CancellationToken.None);

            Assert.Equal(withStatus.AllChangedPaths, changedFiles);
            Assert.Contains("removed.cs", changedFiles);
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }

    private static string InitRepoWithBaseline(params (string RelativePath, string Content)[] files)
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "codemap-git-status-" + Guid.NewGuid());
        Directory.CreateDirectory(workingDirectory);
        foreach (var (relativePath, content) in files)
            File.WriteAllText(Path.Combine(workingDirectory, relativePath), content);

        RunGit(workingDirectory, "init", "-q");
        RunGit(workingDirectory, "add", ".");
        RunGit(workingDirectory, "-c", "user.name=CodeMap Tests", "-c", "user.email=codemap@example.invalid", "commit", "-qm", "baseline");
        return workingDirectory;
    }

    private static void RunGit(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start git.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Task.WhenAll(stdout, stderr).GetAwaiter().GetResult();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git exited {process.ExitCode}: {stderr.Result}");
    }

    private static void CleanUp(string workingDirectory)
    {
        if (Directory.Exists(workingDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(workingDirectory, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(workingDirectory, recursive: true);
        }
    }
}