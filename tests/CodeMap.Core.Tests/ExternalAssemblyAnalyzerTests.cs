using System.Diagnostics;
using CodeMap.CSharp;
using CodeMap.Core.Models;
using CodeMap.Storage;
using Microsoft.Data.Sqlite;

namespace CodeMap.Core.Tests;










[Collection("MsBuild")]
public sealed class ExternalAssemblyAnalyzerTests
{
    private static readonly string AspNetFixturePath = FixtureRestore.EnsureRestored("AspNetFixture");





    [Fact]
    public async Task AnalyzeAsync_ProducesDedupedExternalProjectsForReachedAssemblies()
    {
        var indexer = new CSharpWorkspaceIndexer();
        var projects = await indexer.AnalyzeAsync(AspNetFixturePath, null, CancellationToken.None);

        var externalProjects = projects.Where(p => p.ProjectName.StartsWith("external:", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(externalProjects);


        var distinctNames = externalProjects.Select(p => p.ProjectName).Distinct(StringComparer.Ordinal).ToArray();
        Assert.Equal(externalProjects.Length, distinctNames.Length);

        foreach (var external in externalProjects)
        {
            Assert.NotEmpty(external.Result.Nodes);
            Assert.Equal(
                external.Result.Nodes.Count,
                external.Result.Nodes.Select(node => node.Id).Distinct(StringComparer.Ordinal).Count());

            Assert.All(external.Result.Nodes.Where(n => n.Kind != NodeKind.File), n => Assert.Null(n.SourceLocation));
        }
    }



    [Fact]
    public async Task AnalyzeAsync_SourceCallIntoExternalAssembly_ProducesMaterializedCallsEdge()
    {
        var indexer = new CSharpWorkspaceIndexer();
        var projects = await indexer.AnalyzeAsync(AspNetFixturePath, null, CancellationToken.None);
        var source = Assert.Single(projects, p => !p.ProjectName.StartsWith("external:", StringComparison.Ordinal));
        var allNodeIds = projects.SelectMany(p => p.Result.Nodes).Select(n => n.Id).ToHashSet(StringComparer.Ordinal);



        var callsToExternal = source.Result.Edges
            .Where(e => e.Kind == EdgeKind.Calls)
            .Where(e => !source.Result.Nodes.Any(n => n.Id == e.TargetId))
            .ToArray();

        Assert.NotEmpty(callsToExternal);
        Assert.All(callsToExternal, edge => Assert.Contains(edge.TargetId, allNodeIds));
    }



    [Fact]
    public async Task AnalyzeAsync_IsDeterministicAcrossRuns()
    {
        var indexer = new CSharpWorkspaceIndexer();
        var first = await indexer.AnalyzeAsync(AspNetFixturePath, null, CancellationToken.None);
        var second = await indexer.AnalyzeAsync(AspNetFixturePath, null, CancellationToken.None);

        var firstExternalNames = first.Where(p => p.ProjectName.StartsWith("external:", StringComparison.Ordinal))
            .Select(p => p.ProjectName).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        var secondExternalNames = second.Where(p => p.ProjectName.StartsWith("external:", StringComparison.Ordinal))
            .Select(p => p.ProjectName).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(firstExternalNames, secondExternalNames);

        foreach (var name in firstExternalNames)
        {
            var firstProject = first.Single(p => p.ProjectName == name);
            var secondProject = second.Single(p => p.ProjectName == name);
            Assert.Equal(firstProject.Result.Nodes.Count, secondProject.Result.Nodes.Count);
            Assert.Equal(firstProject.Result.Edges.Count, secondProject.Result.Edges.Count);
        }
    }



    [Fact]
    public async Task AnalyzeAsync_ExternalGraph_NeverInventsUnresolvedCallTargets()
    {
        var indexer = new CSharpWorkspaceIndexer();
        var projects = await indexer.AnalyzeAsync(AspNetFixturePath, null, CancellationToken.None);
        var externalProjects = projects.Where(p => p.ProjectName.StartsWith("external:", StringComparison.Ordinal)).ToArray();

        foreach (var external in externalProjects)
        {
            var nodeIds = external.Result.Nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);




            foreach (var edge in external.Result.Edges.Where(e => e.Kind is EdgeKind.Calls or EdgeKind.Constructs or EdgeKind.Inherits or EdgeKind.Implements or EdgeKind.Overrides))
            {
                Assert.Contains(edge.SourceId, nodeIds);
                Assert.Contains(edge.TargetId, nodeIds);
            }
        }
    }




    [Fact]
    public async Task IndexAsync_PersistsExternalSymbolsWithDecompiledOriginAndSourceEdgesSurvive()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "codemap-external-persist-" + Guid.NewGuid());
        CopyFixture(AspNetFixturePath, workingDirectory);
        try
        {
            var summary = await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);
            Assert.Contains(summary.AnalyzedProjects, name => name.StartsWith("external:", StringComparison.Ordinal));

            var databasePath = Path.Combine(workingDirectory, ".codemap", "index.db");
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
            await connection.OpenAsync();

            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT COUNT(*) FROM symbols s JOIN files f ON f.id = s.file_id WHERE f.project LIKE 'external:%' AND s.origin_kind = 'decompiled'";
                var decompiledCount = (long)(await command.ExecuteScalarAsync())!;
                Assert.True(decompiledCount > 0, "Expected at least one decompiled-origin external symbol to be persisted.");
            }

            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT COUNT(*) FROM symbols s JOIN files f ON f.id = s.file_id WHERE f.project NOT LIKE 'external:%' AND s.origin_kind = 'declared'";
                var declaredCount = (long)(await command.ExecuteScalarAsync())!;
                Assert.True(declaredCount > 0, "Expected source symbols to keep origin_kind='declared'.");
            }

            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    SELECT COUNT(*) FROM edges e
                    JOIN symbols src ON src.id = e.source_id
                    JOIN files srcFile ON srcFile.id = src.file_id
                    JOIN symbols tgt ON tgt.id = e.target_id
                    JOIN files tgtFile ON tgtFile.id = tgt.file_id
                    WHERE srcFile.project NOT LIKE 'external:%' AND tgtFile.project LIKE 'external:%'
                    """;
                var sourceToExternalEdges = (long)(await command.ExecuteScalarAsync())!;
                Assert.True(sourceToExternalEdges > 0, "Expected at least one persisted source -> external edge.");
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(workingDirectory))
                Directory.Delete(workingDirectory, recursive: true);
        }
    }




    [Fact]
    public async Task IndexAsync_ExternalSymbolsAreFindableButDoNotOutrankSourceOnExactMatch()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "codemap-external-find-" + Guid.NewGuid());
        CopyFixture(AspNetFixturePath, workingDirectory);
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);
            var databasePath = Path.Combine(workingDirectory, ".codemap", "index.db");
            var graph = await new CodeMapQueryStore(databasePath).LoadAsync();
            var service = new CodeMapQueryService(graph);



            var resolved = service.ResolveSymbol("OrderService", callableOnly: false, maxResults: 10);
            Assert.False(resolved.IsAmbiguous);
            Assert.Single(resolved.Matches);
            Assert.False(resolved.Matches[0].Project.StartsWith("external:", StringComparison.Ordinal));




            var externalFind = service.Find("WebApplication", maxResults: 10);
            Assert.All(externalFind, symbol => Assert.StartsWith("external:", symbol.Project, StringComparison.Ordinal));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(workingDirectory))
                Directory.Delete(workingDirectory, recursive: true);
        }
    }




    [Fact]
    public async Task IndexAsync_ArchitectureCheckNeverFlagsExternalSymbols()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "codemap-external-arch-" + Guid.NewGuid());
        CopyFixture(AspNetFixturePath, workingDirectory);
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);
            var databasePath = Path.Combine(workingDirectory, ".codemap", "index.db");
            var graph = await new CodeMapQueryStore(databasePath).LoadAsync();
            var projectReferences = ArchitectureChecker.LoadProjectReferences(workingDirectory);
            var rules = ArchitectureChecker.LoadRules(workingDirectory);

            var violations = ArchitectureChecker.Check(graph, projectReferences, rules);
            Assert.DoesNotContain(violations, v =>
                (v.Source is not null && v.Source.Length > 0 && graph.Symbols.Any(s => s.Id == v.Source && s.Project.StartsWith("external:", StringComparison.Ordinal)))
                || (v.Target is not null && v.Target.Length > 0 && graph.Symbols.Any(s => s.Id == v.Target && s.Project.StartsWith("external:", StringComparison.Ordinal))));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(workingDirectory))
                Directory.Delete(workingDirectory, recursive: true);
        }
    }



    [Fact]
    public async Task IndexAsync_CorruptExternalAssembly_DoesNotFailIndexingOrDanglingEdges()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "codemap-external-corrupt-" + Guid.NewGuid());
        CopyFixture(AspNetFixturePath, workingDirectory);
        try
        {







            var summary = await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);
            Assert.True(summary.Symbols > 0);

            var databasePath = Path.Combine(workingDirectory, ".codemap", "index.db");
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM edges e WHERE NOT EXISTS (SELECT 1 FROM symbols s WHERE s.id = e.target_id)";
            var danglingEdges = (long)(await command.ExecuteScalarAsync())!;
            Assert.Equal(0, danglingEdges);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(workingDirectory))
                Directory.Delete(workingDirectory, recursive: true);
        }
    }







    [Fact]
    public void Analyze_SuccessPath_DoesNotLeaveAssemblyFileHandleOpen()
    {
        var tempDllPath = Path.Combine(Path.GetTempPath(), "codemap-pefile-lifetime-" + Guid.NewGuid() + ".dll");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "CodeMap.Core.dll"), tempDllPath);
        try
        {
            var result = ExternalAssemblyAnalyzer.Analyze(
                tempDllPath,
                new HashSet<string> { "T:CodeMap.Core.Ids.CodeMapIdGenerator" },
                new CodeMap.Core.Ids.CodeMapIdGenerator(),
                CancellationToken.None);

            Assert.NotNull(result);
            Assert.NotEmpty(result!.Result.Nodes);

            var renamedPath = tempDllPath + ".renamed";
            File.Move(tempDllPath, renamedPath);
            File.Move(renamedPath, tempDllPath);
        }
        finally
        {
            if (File.Exists(tempDllPath))
                File.Delete(tempDllPath);
        }
    }



    [Fact]
    public void Analyze_NoMatchingRoot_StillDoesNotLeaveAssemblyFileHandleOpen()
    {
        var tempDllPath = Path.Combine(Path.GetTempPath(), "codemap-pefile-lifetime-nomatch-" + Guid.NewGuid() + ".dll");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "CodeMap.Core.dll"), tempDllPath);
        try
        {
            var result = ExternalAssemblyAnalyzer.Analyze(
                tempDllPath,
                new HashSet<string> { "T:Some.Namespace.DoesNotExist" },
                new CodeMap.Core.Ids.CodeMapIdGenerator(),
                CancellationToken.None);

            Assert.Null(result);

            var renamedPath = tempDllPath + ".renamed";
            File.Move(tempDllPath, renamedPath);
            File.Move(renamedPath, tempDllPath);
        }
        finally
        {
            if (File.Exists(tempDllPath))
                File.Delete(tempDllPath);
        }
    }







    private static void CopyFixture(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*.*", SearchOption.AllDirectories)
                     .Where(file => !file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)
                         && !file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)))
        {
            var relative = Path.GetRelativePath(source, file);
            var targetPath = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.Copy(file, targetPath, overwrite: true);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = "restore",
            WorkingDirectory = destination,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start 'dotnet restore' for the copied AspNetFixture.");
        var stdOut = process.StandardOutput.ReadToEnd();
        var stdErr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(TimeSpan.FromSeconds(60)))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("'dotnet restore' timed out for the copied AspNetFixture.");
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"'dotnet restore' failed for the copied AspNetFixture (exit {process.ExitCode}).\n{stdOut}\n{stdErr}");
    }
}