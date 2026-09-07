using CodeMap.Storage;
using Microsoft.Data.Sqlite;

namespace CodeMap.Core.Tests;























[Collection("MsBuild")]
public sealed class UntouchedSymbolUniverseScanTests
{







    [Fact]
    public async Task UpdateAsync_SubsetScan_IgnoresUnrelatedUntouchedSymbolsAndPreservesCollision()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "codemap-subset-scan-" + Guid.NewGuid());
        CopyFixture(GetFixtureDirectory(), workingDirectory);
        try
        {





            var bulkPath = Path.Combine(workingDirectory, "ProjB", "Bulk.cs");
            var bulkMembers = string.Join("\n", Enumerable.Range(0, 50).Select(i => $"    public string Field{i} => \"{i}\";"));
            await File.WriteAllTextAsync(bulkPath, $$"""
                namespace Shared;

                public class BulkUnrelated
                {
                {{bulkMembers}}
                }
                """);
            CreateUnrelatedProject(workingDirectory, "ProjC", "public string GetGreeting() => \"hi\";");
            WriteSolutionFile(workingDirectory, "ProjA", "ProjB", "ProjC");

            var indexer = new IncrementalCodeMapIndexer();
            await indexer.IndexAsync(workingDirectory, force: true, CancellationToken.None);

            var databasePath = Path.Combine(workingDirectory, ".codemap", "index.db");
            var beforeGraph = await new CodeMapQueryStore(databasePath).LoadAsync();
            var beforeWidgets = new CodeMapQueryService(beforeGraph).Find("Widget", maxResults: 10);
            var projAWidgetIdBefore = Assert.Single(beforeWidgets, symbol => symbol.Project == "ProjA").Id;
            var projBWidgetIdBefore = Assert.Single(beforeWidgets, symbol => symbol.Project == "ProjB").Id;


            var unrelatedPath = Path.Combine(workingDirectory, "ProjC", "Unrelated.cs");
            await File.WriteAllTextAsync(unrelatedPath, """
                namespace Unrelated;

                public class Greeter
                {
                    public string GetGreeting() => "hi-edited";
                }
                """);

            var updated = await indexer.UpdateAsync(workingDirectory, CancellationToken.None);
            Assert.Contains("ProjC", updated.AnalyzedProjects);
            Assert.DoesNotContain("ProjA", updated.AnalyzedProjects);
            Assert.DoesNotContain("ProjB", updated.AnalyzedProjects);

            var afterGraph = await new CodeMapQueryStore(databasePath).LoadAsync();
            var service = new CodeMapQueryService(afterGraph);
            var afterWidgets = service.Find("Widget", maxResults: 10);
            Assert.Equal(2, afterWidgets.Count);
            Assert.Contains(afterWidgets, symbol => symbol.Id == projAWidgetIdBefore && symbol.Project == "ProjA");
            Assert.Contains(afterWidgets, symbol => symbol.Id == projBWidgetIdBefore && symbol.Project == "ProjB");



            var bulkSymbols = service.Find("BulkUnrelated", maxResults: 5);
            Assert.Single(bulkSymbols);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(workingDirectory))
                Directory.Delete(workingDirectory, recursive: true);
        }
    }









    [Fact]
    public async Task UpdateAsync_FullScanFallback_AboveThreshold_PreservesCollision()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "codemap-fullscan-fallback-" + Guid.NewGuid());
        CopyFixture(GetFixtureDirectory(), workingDirectory);
        try
        {
            CreateUnrelatedProject(workingDirectory, "ProjC", "public string GetGreeting() => \"hi\";");
            WriteSolutionFile(workingDirectory, "ProjA", "ProjB", "ProjC");

            var indexer = new IncrementalCodeMapIndexer();
            await indexer.IndexAsync(workingDirectory, force: true, CancellationToken.None);

            var databasePath = Path.Combine(workingDirectory, ".codemap", "index.db");
            var beforeGraph = await new CodeMapQueryStore(databasePath).LoadAsync();
            var beforeWidgets = new CodeMapQueryService(beforeGraph).Find("Widget", maxResults: 10);
            var projAWidgetIdBefore = Assert.Single(beforeWidgets, symbol => symbol.Project == "ProjA").Id;
            var projBWidgetIdBefore = Assert.Single(beforeWidgets, symbol => symbol.Project == "ProjB").Id;



            var manyMembers = string.Join("\n", Enumerable.Range(0, 420).Select(i => $"    public string Field{i} => \"{i}\";"));
            var unrelatedPath = Path.Combine(workingDirectory, "ProjC", "Unrelated.cs");
            await File.WriteAllTextAsync(unrelatedPath, $$"""
                namespace Unrelated;

                public class Greeter
                {
                    public string GetGreeting() => "hi-edited";
                }

                public class ManyMembers
                {
                {{manyMembers}}
                }
                """);

            var updated = await indexer.UpdateAsync(workingDirectory, CancellationToken.None);
            Assert.Contains("ProjC", updated.AnalyzedProjects);
            Assert.DoesNotContain("ProjA", updated.AnalyzedProjects);
            Assert.DoesNotContain("ProjB", updated.AnalyzedProjects);

            var afterGraph = await new CodeMapQueryStore(databasePath).LoadAsync();
            var afterWidgets = new CodeMapQueryService(afterGraph).Find("Widget", maxResults: 10);
            Assert.Equal(2, afterWidgets.Count);
            Assert.Contains(afterWidgets, symbol => symbol.Id == projAWidgetIdBefore && symbol.Project == "ProjA");
            Assert.Contains(afterWidgets, symbol => symbol.Id == projBWidgetIdBefore && symbol.Project == "ProjB");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(workingDirectory))
                Directory.Delete(workingDirectory, recursive: true);
        }
    }










    [Fact]
    public async Task UpdateAsync_SubsetScan_PreservesExistingNumericSuffixCollisionRows()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "codemap-subset-suffix-" + Guid.NewGuid());
        CopyFixture(GetFixtureDirectory(), workingDirectory);
        try
        {


            var projCDir = Path.Combine(workingDirectory, "ProjC");
            Directory.CreateDirectory(projCDir);
            await File.WriteAllTextAsync(Path.Combine(projCDir, "ProjC.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(Path.Combine(projCDir, "Widget.cs"), """
                namespace Shared;

                public class Widget
                {
                    public string C => GetC();

                    private string GetC() => "c";
                }
                """);
            CreateUnrelatedProject(workingDirectory, "ProjD", "public string GetGreeting() => \"hi\";");
            WriteSolutionFile(workingDirectory, "ProjA", "ProjB", "ProjC", "ProjD");

            var indexer = new IncrementalCodeMapIndexer();
            await indexer.IndexAsync(workingDirectory, force: true, CancellationToken.None);

            var databasePath = Path.Combine(workingDirectory, ".codemap", "index.db");
            var beforeGraph = await new CodeMapQueryStore(databasePath).LoadAsync();
            var beforeService = new CodeMapQueryService(beforeGraph);
            var beforeWidgets = beforeService.Find("Widget", maxResults: 10);
            Assert.Equal(3, beforeWidgets.Count);
            var projAWidgetIdBefore = Assert.Single(beforeWidgets, symbol => symbol.Project == "ProjA").Id;
            var projBWidgetIdBefore = Assert.Single(beforeWidgets, symbol => symbol.Project == "ProjB").Id;
            var projCWidgetIdBefore = Assert.Single(beforeWidgets, symbol => symbol.Project == "ProjC").Id;



            var unrelatedPath = Path.Combine(workingDirectory, "ProjD", "Unrelated.cs");
            await File.WriteAllTextAsync(unrelatedPath, """
                namespace Unrelated;

                public class Greeter
                {
                    public string GetGreeting() => "hi-edited";
                }
                """);
            var updated = await indexer.UpdateAsync(workingDirectory, CancellationToken.None);
            Assert.Contains("ProjD", updated.AnalyzedProjects);
            Assert.DoesNotContain("ProjA", updated.AnalyzedProjects);
            Assert.DoesNotContain("ProjB", updated.AnalyzedProjects);
            Assert.DoesNotContain("ProjC", updated.AnalyzedProjects);

            var afterGraph = await new CodeMapQueryStore(databasePath).LoadAsync();
            var afterService = new CodeMapQueryService(afterGraph);
            var afterWidgets = afterService.Find("Widget", maxResults: 10);
            Assert.Equal(3, afterWidgets.Count);
            Assert.Contains(afterWidgets, symbol => symbol.Id == projAWidgetIdBefore && symbol.Project == "ProjA");
            Assert.Contains(afterWidgets, symbol => symbol.Id == projBWidgetIdBefore && symbol.Project == "ProjB");
            Assert.Contains(afterWidgets, symbol => symbol.Id == projCWidgetIdBefore && symbol.Project == "ProjC");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(workingDirectory))
                Directory.Delete(workingDirectory, recursive: true);
        }
    }








    private static void WriteSolutionFile(string workingDirectory, params string[] projectNames)
    {
        var projectEntries = string.Join("\n", projectNames.Select(name => $"  <Project Path=\"{name}/{name}.csproj\" />"));
        File.WriteAllText(Path.Combine(workingDirectory, "CollidingProjects.slnx"), $"<Solution>\n{projectEntries}\n</Solution>\n");
    }

    private static void CreateUnrelatedProject(string workingDirectory, string projectName, string memberBody)
    {
        var dir = Path.Combine(workingDirectory, projectName);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, $"{projectName}.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(dir, "Unrelated.cs"), $$"""
            namespace Unrelated;

            public class Greeter
            {
                {{memberBody}}
            }
            """);
    }

    private static string GetFixtureDirectory()
    {
        var testDir = AppContext.BaseDirectory;
        var solutionRoot = Path.GetFullPath(Path.Combine(testDir, "..", "..", "..", "..", ".."));
        return Path.Combine(solutionRoot, "tests", "Fixtures", "CollidingProjects");
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
    }
}