using CodeMap.CSharp;
using CodeMap.Mcp;
using CodeMap.Storage;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace CodeMap.Core.Tests;










[Collection("MsBuild")]
public sealed class IncrementalUpdateTests
{
    [Fact]
    public async Task QueryStore_BuildingIndex_RejectsAllReaderEntryPoints()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "codemap-building-index-" + Guid.NewGuid());
        var databasePath = Path.Combine(workingDirectory, ".codemap", "index.db");
        try
        {
            await new SqliteCodeMapStore(databasePath).InitializeAsync(CancellationToken.None);
            await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO metadata(key, value) VALUES('schema_version', $schemaVersion);
                    INSERT INTO metadata(key, value) VALUES('index_state', 'building');
                    """;
                command.Parameters.AddWithValue("$schemaVersion", SqliteCodeMapStore.SchemaVersion);
                await command.ExecuteNonQueryAsync();
            }

            var store = new CodeMapQueryStore(databasePath);
            await Assert.ThrowsAsync<IndexBuildingException>(() => store.OpenReadOnlyConnectionAsync());
            await Assert.ThrowsAsync<IndexBuildingException>(() => store.LoadAsync());
            await Assert.ThrowsAsync<IndexBuildingException>(() => store.LoadProjectScopedAsync("AnyProject"));
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }

    [Fact]
    public async Task McpTool_BuildingIndex_ReturnsStructuredStaleResponse()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "codemap-mcp-building-index-" + Guid.NewGuid());
        var databasePath = Path.Combine(workingDirectory, ".codemap", "index.db");
        try
        {
            await new SqliteCodeMapStore(databasePath).InitializeAsync(CancellationToken.None);
            await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO metadata(key, value) VALUES('schema_version', $schemaVersion);
                    INSERT INTO metadata(key, value) VALUES('index_state', 'building');
                    """;
                command.Parameters.AddWithValue("$schemaVersion", SqliteCodeMapStore.SchemaVersion);
                await command.ExecuteNonQueryAsync();
            }

            using var context = new CodeMapMcpContext(workingDirectory);
            var response = await CodeMapTools.FindSymbol(context, "Anything", cancellationToken: CancellationToken.None);
            using var document = JsonDocument.Parse(response);
            Assert.True(document.RootElement.GetProperty("stale").GetBoolean());
            Assert.Equal("index_building", document.RootElement.GetProperty("error").GetProperty("code").GetString());
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }

    [Fact]
    public async Task QueryStore_IndexWithoutStateMetadata_RemainsReadable()
    {
        var workingDirectory = CopyFixtureToTempDirectory();
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);
            var databasePath = Path.Combine(workingDirectory, ".codemap", "index.db");
            await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT value FROM metadata WHERE key = 'index_state'";
                Assert.Equal("ready", await command.ExecuteScalarAsync());

                command.CommandText = "DELETE FROM metadata WHERE key = 'index_state'";
                await command.ExecuteNonQueryAsync();
            }

            var graph = await new CodeMapQueryStore(databasePath).LoadAsync();
            Assert.Contains(graph.Symbols, symbol => symbol.Name == "Greeter");
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }

    [Fact]
    public async Task UpdateAsync_NoOpReportsNoChanges()
    {
        var workingDirectory = CopyFixtureToTempDirectory();
        try
        {
            var indexer = new IncrementalCodeMapIndexer();
            var indexed = await indexer.IndexAsync(workingDirectory, force: true, CancellationToken.None);

            var noOp = await indexer.UpdateAsync(workingDirectory, CancellationToken.None);

            Assert.Equal(0, noOp.Added);
            Assert.Equal(0, noOp.Updated);
            Assert.Equal(0, noOp.Removed);
            Assert.Equal(indexed.IndexedFiles, noOp.Skipped);
            Assert.Equal(indexed.Symbols, noOp.Symbols);
            Assert.Equal(indexed.Edges, noOp.Edges);
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }

    [Fact]
    public async Task UpdateAsync_StateExistsButDatabaseMissing_RebuildsIndex()
    {
        var workingDirectory = CopyFixtureToTempDirectory();
        try
        {
            var indexer = new IncrementalCodeMapIndexer();
            await indexer.IndexAsync(workingDirectory, force: true, CancellationToken.None);

            var databasePath = Path.Combine(workingDirectory, ".codemap", "index.db");
            Assert.True(File.Exists(Path.Combine(workingDirectory, ".codemap", "state.json")));
            SqliteConnection.ClearAllPools();
            File.Delete(databasePath);
            File.Delete(databasePath + "-wal");
            File.Delete(databasePath + "-shm");

            var result = await indexer.UpdateAsync(workingDirectory, CancellationToken.None);

            Assert.True(File.Exists(databasePath));
            Assert.NotEmpty(result.AnalyzedProjects);
            Assert.True(result.Symbols > 0);
            var graph = await new CodeMapQueryStore(databasePath).LoadAsync();
            Assert.Contains(graph.Symbols, symbol => symbol.Name == "Greeter");
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }

    [Fact]
    public async Task IsUpToDateAsync_StateExistsButDatabaseMissing_ReturnsFalse()
    {
        var workingDirectory = CopyFixtureToTempDirectory();
        try
        {
            var indexer = new IncrementalCodeMapIndexer();
            await indexer.IndexAsync(workingDirectory, force: true, CancellationToken.None);

            var databasePath = Path.Combine(workingDirectory, ".codemap", "index.db");
            SqliteConnection.ClearAllPools();
            File.Delete(databasePath);
            File.Delete(databasePath + "-wal");
            File.Delete(databasePath + "-shm");

            Assert.False(await indexer.IsUpToDateAsync(workingDirectory, CancellationToken.None));
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }

    [Fact]
    public async Task UpdateAsync_DetectsModifiedAddedAndRemovedFiles()
    {
        var workingDirectory = CopyFixtureToTempDirectory();
        try
        {
            var indexer = new IncrementalCodeMapIndexer();
            await indexer.IndexAsync(workingDirectory, force: true, CancellationToken.None);

            var greeterPath = Path.Combine(workingDirectory, "ProjB", "Greeter.cs");
            await File.WriteAllTextAsync(greeterPath, """
                namespace Fixture.ProjB;

                public class Greeter
                {
                    public string Greet() => "hi";
                    public string Farewell() => "bye";
                }
                """);
            var modified = await indexer.UpdateAsync(workingDirectory, CancellationToken.None);
            Assert.Equal(1, modified.Updated);
            Assert.Equal(0, modified.Added);
            Assert.Equal(0, modified.Removed);
            Assert.Contains("ProjA", modified.AnalyzedProjects);
            Assert.Contains("ProjB", modified.AnalyzedProjects);

            var databasePath = Path.Combine(workingDirectory, ".codemap", "index.db");
            var graphAfterEdit = await new CodeMapQueryStore(databasePath).LoadAsync();
            Assert.Contains(graphAfterEdit.Symbols, s => s.Name == "Farewell");

            var extraFilePath = Path.Combine(workingDirectory, "ProjB", "Extra.cs");
            await File.WriteAllTextAsync(extraFilePath, "namespace Fixture.ProjB;\n\npublic sealed class Extra { }\n");
            var added = await indexer.UpdateAsync(workingDirectory, CancellationToken.None);
            Assert.Equal(1, added.Added);

            File.Delete(extraFilePath);
            var removed = await indexer.UpdateAsync(workingDirectory, CancellationToken.None);
            Assert.Equal(1, removed.Removed);
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }

    [Fact]
    public async Task UpdateAsync_WebBindingsModeChange_ReanalyzesWithoutSourceChanges()
    {
        var workingDirectory = CopyWebFixtureToTempDirectory();
        var originalBindings = Environment.GetEnvironmentVariable("CODEMAP_WEB_BINDINGS");
        try
        {
            Environment.SetEnvironmentVariable("CODEMAP_WEB_BINDINGS", "enabled");
            var indexer = new IncrementalCodeMapIndexer();
            await indexer.IndexAsync(workingDirectory, force: true, CancellationToken.None);

            var statePath = Path.Combine(workingDirectory, ".codemap", "state.json");
            var enabledHash = ReadConfigHash(await File.ReadAllTextAsync(statePath));
            var databasePath = Path.Combine(workingDirectory, ".codemap", "index.db");
            var enabledGraph = await new CodeMapQueryStore(databasePath).LoadAsync();
            Assert.Contains(enabledGraph.Edges, edge => edge.Kind == CodeMap.Core.Models.EdgeKind.Calls && edge.ResolutionKind == CodeMap.Core.Models.EdgeResolutionKind.Syntactic);

            Environment.SetEnvironmentVariable("CODEMAP_WEB_BINDINGS", "0");
            var result = await indexer.UpdateAsync(workingDirectory, CancellationToken.None);

            Assert.Contains(result.AnalyzedProjects, project => project.StartsWith("web:", StringComparison.Ordinal));
            Assert.NotEqual(enabledHash, ReadConfigHash(await File.ReadAllTextAsync(statePath)));
            var disabledGraph = await new CodeMapQueryStore(databasePath).LoadAsync();
            Assert.DoesNotContain(disabledGraph.Edges, edge => edge.Kind == CodeMap.Core.Models.EdgeKind.Calls && edge.ResolutionKind == CodeMap.Core.Models.EdgeResolutionKind.Syntactic);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEMAP_WEB_BINDINGS", originalBindings);
            CleanUp(workingDirectory);
        }
    }

    [Fact]
    public async Task UpdateAsync_ConfigHashMismatch_DoesNotReusePreviousState()
    {
        var workingDirectory = CopyFixtureToTempDirectory();
        try
        {
            var indexer = new IncrementalCodeMapIndexer();
            await indexer.IndexAsync(workingDirectory, force: true, CancellationToken.None);

            var statePath = Path.Combine(workingDirectory, ".codemap", "state.json");
            await File.WriteAllTextAsync(statePath, ReplaceConfigHash(await File.ReadAllTextAsync(statePath), "incompatible"));

            var result = await indexer.UpdateAsync(workingDirectory, CancellationToken.None);

            Assert.Contains("ProjA", result.AnalyzedProjects);
            Assert.Contains("ProjB", result.AnalyzedProjects);
            Assert.NotEqual("incompatible", ReadConfigHash(await File.ReadAllTextAsync(statePath)));
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }

    [Fact]
    public async Task AnalyzerVersionMismatch_StopsUpdateAndQuery()
    {
        var workingDirectory = CopyFixtureToTempDirectory();
        try
        {
            var indexer = new IncrementalCodeMapIndexer();
            await indexer.IndexAsync(workingDirectory, force: true, CancellationToken.None);

            var state = await File.ReadAllTextAsync(Path.Combine(workingDirectory, ".codemap", "state.json"));
            using var stateDocument = JsonDocument.Parse(state);
            Assert.Equal(SqliteCodeMapStore.SchemaVersion, stateDocument.RootElement.GetProperty("schemaVersion").GetString());
            Assert.Equal(4, stateDocument.RootElement.GetProperty("indexFormatVersion").GetInt32());
            Assert.Equal(CSharpLanguageAnalyzer.AnalyzerVersion, stateDocument.RootElement.GetProperty("analyzerVersions").GetProperty("csharp").GetString());
            Assert.NotEmpty(stateDocument.RootElement.GetProperty("configHash").GetString()!);
            Assert.NotEmpty(stateDocument.RootElement.GetProperty("toolVersion").GetString()!);

            var databasePath = Path.Combine(workingDirectory, ".codemap", "index.db");
            await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "UPDATE metadata SET value = '0' WHERE key = 'analyzer_version_csharp'";
                await command.ExecuteNonQueryAsync();
            }

            var updateError = await Assert.ThrowsAsync<InvalidOperationException>(() => indexer.UpdateAsync(workingDirectory));
            Assert.Contains("analyzer (csharp)", updateError.Message, StringComparison.Ordinal);
            var queryError = await Assert.ThrowsAsync<InvalidOperationException>(() => new CodeMapQueryStore(databasePath).LoadAsync());
            Assert.Contains("analyzer (csharp)", queryError.Message, StringComparison.Ordinal);
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }







    [Fact]
    public async Task UpdateAsync_ReplacesStateFileWithoutLeavingTempFile()
    {
        var workingDirectory = CopyFixtureToTempDirectory();
        try
        {
            var indexer = new IncrementalCodeMapIndexer();
            await indexer.IndexAsync(workingDirectory, force: true, CancellationToken.None);

            var statePath = Path.Combine(workingDirectory, ".codemap", "state.json");
            Assert.True(File.Exists(statePath));
            var firstContent = await File.ReadAllTextAsync(statePath);

            var greeterPath = Path.Combine(workingDirectory, "ProjB", "Greeter.cs");
            await File.WriteAllTextAsync(greeterPath, """
                namespace Fixture.ProjB;

                public class Greeter
                {
                    public string Greet() => "hi";
                    public string Farewell() => "bye";
                }
                """);
            await indexer.UpdateAsync(workingDirectory, CancellationToken.None);

            var secondContent = await File.ReadAllTextAsync(statePath);
            Assert.NotEqual(firstContent, secondContent);

            var leftoverTempFiles = Directory.GetFiles(Path.Combine(workingDirectory, ".codemap"), "state.json.*.tmp");
            Assert.Empty(leftoverTempFiles);
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }







    [Fact]
    public async Task UpdateAsync_PreservesPriorStateWhenCancelledBeforeWriteCompletes()
    {
        var workingDirectory = CopyFixtureToTempDirectory();
        try
        {
            var indexer = new IncrementalCodeMapIndexer();
            await indexer.IndexAsync(workingDirectory, force: true, CancellationToken.None);

            var statePath = Path.Combine(workingDirectory, ".codemap", "state.json");
            var originalContent = await File.ReadAllTextAsync(statePath);

            var greeterPath = Path.Combine(workingDirectory, "ProjB", "Greeter.cs");
            await File.WriteAllTextAsync(greeterPath, """
                namespace Fixture.ProjB;

                public class Greeter
                {
                    public string Greet() => "hi";
                    public string Farewell() => "bye";
                }
                """);

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => indexer.UpdateAsync(workingDirectory, cancellation.Token));

            var afterCancelContent = await File.ReadAllTextAsync(statePath);
            Assert.Equal(originalContent, afterCancelContent);

            var leftoverTempFiles = Directory.GetFiles(Path.Combine(workingDirectory, ".codemap"), "state.json.*.tmp");
            Assert.Empty(leftoverTempFiles);
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }






    [Fact]
    public async Task UpdateAsync_ProjectFileOnlyChange_MarksProjectDirtyWithZeroFileCounts()
    {
        var workingDirectory = CopyFixtureToTempDirectory();
        try
        {
            var indexer = new IncrementalCodeMapIndexer();
            var indexed = await indexer.IndexAsync(workingDirectory, force: true, CancellationToken.None);

            var projectPath = Path.Combine(workingDirectory, "ProjB", "ProjB.csproj");
            var projectFileContent = await File.ReadAllTextAsync(projectPath);
            await File.WriteAllTextAsync(projectPath, projectFileContent + "<!-- touched -->");

            var result = await indexer.UpdateAsync(workingDirectory, CancellationToken.None);

            Assert.Equal(0, result.Added);
            Assert.Equal(0, result.Updated);
            Assert.Equal(0, result.Removed);
            Assert.Equal(indexed.IndexedFiles, result.Skipped);
            Assert.Contains("ProjB", result.AnalyzedProjects);
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }











    [Fact]
    public async Task UpdateAsync_ExternalAssemblyMissing_MarksOwningProjectDirty()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "codemap-external-dirty-" + Guid.NewGuid());
        CopyFixture(FixtureRestore.EnsureRestored("AspNetFixture"), workingDirectory);
        try
        {
            var indexer = new IncrementalCodeMapIndexer();
            await indexer.IndexAsync(workingDirectory, force: true, CancellationToken.None);

            var statePath = Path.Combine(workingDirectory, ".codemap", "state.json");
            var stateText = await File.ReadAllTextAsync(statePath);
            using var stateDocument = JsonDocument.Parse(stateText);
            var owningProject = stateDocument.RootElement.GetProperty("projects").EnumerateArray()
                .FirstOrDefault(project => project.TryGetProperty("externalAssemblies", out var external)
                    && external.ValueKind == JsonValueKind.Array && external.GetArrayLength() > 0);
            Assert.NotEqual(default, owningProject);
            var owningProjectName = owningProject.GetProperty("projectName").GetString()!;

            var throwawayAssemblyPath = Path.Combine(workingDirectory, "throwaway-external.dll");
            await File.WriteAllBytesAsync(throwawayAssemblyPath, [1, 2, 3, 4]);
            var throwawayHash = CSharpWorkspaceIndexer.ComputeAssemblyFileHash(throwawayAssemblyPath);

            var editedStateText = RedirectFirstExternalAssembly(stateText, owningProjectName, throwawayAssemblyPath, throwawayHash);
            await File.WriteAllTextAsync(statePath, editedStateText);



            var upToDateResult = await indexer.UpdateAsync(workingDirectory, CancellationToken.None);
            Assert.DoesNotContain(owningProjectName, upToDateResult.AnalyzedProjects);

            File.Delete(throwawayAssemblyPath);

            var dirtyResult = await indexer.UpdateAsync(workingDirectory, CancellationToken.None);
            Assert.Contains(owningProjectName, dirtyResult.AnalyzedProjects);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(workingDirectory))
                Directory.Delete(workingDirectory, recursive: true);
        }
    }






    [Fact]
    public async Task UpdateAsync_ProjectRemovedFromSolution_IsRemovedEvenThoughCsprojRemainsOnDisk()
    {
        var workingDirectory = CopyFixtureToTempDirectory();
        try
        {
            var indexer = new IncrementalCodeMapIndexer();
            await indexer.IndexAsync(workingDirectory, force: true, CancellationToken.None);

            var slnxPath = Path.Combine(workingDirectory, "MultiProject.slnx");
            await File.WriteAllTextAsync(slnxPath, """
                <Solution>
                  <Project Path="ProjA/ProjA.csproj" />
                </Solution>
                """);

            var projAProjectPath = Path.Combine(workingDirectory, "ProjA", "ProjA.csproj");
            Assert.Contains("ProjB.csproj", await File.ReadAllTextAsync(projAProjectPath), StringComparison.Ordinal);

            var result = await indexer.UpdateAsync(workingDirectory, CancellationToken.None);

            Assert.DoesNotContain("ProjB", result.AnalyzedProjects);
            Assert.True(File.Exists(Path.Combine(workingDirectory, "ProjB", "ProjB.csproj")), "ProjB.csproj should remain on disk");

            var databasePath = Path.Combine(workingDirectory, ".codemap", "index.db");
            var graph = await new CodeMapQueryStore(databasePath).LoadAsync();
            Assert.DoesNotContain(graph.Symbols, symbol => symbol.Project == "ProjB");
            Assert.DoesNotContain(graph.Files, file => file.Project == "ProjB");

            var statePath = Path.Combine(workingDirectory, ".codemap", "state.json");
            using var stateDocument = JsonDocument.Parse(await File.ReadAllTextAsync(statePath));
            var projectNames = stateDocument.RootElement.GetProperty("projects").EnumerateArray()
                .Select(project => project.GetProperty("projectName").GetString())
                .ToArray();
            Assert.DoesNotContain("ProjB", projectNames);
            Assert.Contains("ProjA", projectNames);
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }







    [Fact]
    public async Task UpdateAsync_CsprojOutsideSolution_IsNotDiscoveredUntilAddedToSolution()
    {
        var workingDirectory = CopyFixtureToTempDirectory();
        try
        {
            var indexer = new IncrementalCodeMapIndexer();
            await indexer.IndexAsync(workingDirectory, force: true, CancellationToken.None);

            var projCDirectory = Path.Combine(workingDirectory, "ProjC");
            Directory.CreateDirectory(projCDirectory);
            await File.WriteAllTextAsync(Path.Combine(projCDirectory, "ProjC.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(Path.Combine(projCDirectory, "Widget.cs"), "namespace Fixture.ProjC;\n\npublic sealed class Widget { }\n");


            var greeterPath = Path.Combine(workingDirectory, "ProjB", "Greeter.cs");
            await File.AppendAllTextAsync(greeterPath, Environment.NewLine + "// touch");
            var sourceOnlyResult = await indexer.UpdateAsync(workingDirectory, CancellationToken.None);
            Assert.DoesNotContain("ProjC", sourceOnlyResult.AnalyzedProjects);

            var databasePath = Path.Combine(workingDirectory, ".codemap", "index.db");
            var graphBeforeSolutionEdit = await new CodeMapQueryStore(databasePath).LoadAsync();
            Assert.DoesNotContain(graphBeforeSolutionEdit.Symbols, symbol => symbol.Name == "Widget");

            var slnxPath = Path.Combine(workingDirectory, "MultiProject.slnx");
            await File.WriteAllTextAsync(slnxPath, """
                <Solution>
                  <Project Path="ProjA/ProjA.csproj" />
                  <Project Path="ProjB/ProjB.csproj" />
                  <Project Path="ProjC/ProjC.csproj" />
                </Solution>
                """);
            var solutionAddResult = await indexer.UpdateAsync(workingDirectory, CancellationToken.None);
            Assert.Contains("ProjC", solutionAddResult.AnalyzedProjects);

            var graphAfterSolutionEdit = await new CodeMapQueryStore(databasePath).LoadAsync();
            Assert.Contains(graphAfterSolutionEdit.Symbols, symbol => symbol.Name == "Widget");
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }





    [Fact]
    public async Task UpdateAsync_DottedProjectName_SolutionMetadataOnlyEdit_PreservesProject()
    {
        var workingDirectory = CopyFixtureToTempDirectory();
        try
        {
            var companyApiDirectory = Path.Combine(workingDirectory, "Company.Api");
            Directory.CreateDirectory(companyApiDirectory);
            await File.WriteAllTextAsync(Path.Combine(companyApiDirectory, "Company.Api.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(Path.Combine(companyApiDirectory, "ApiService.cs"), """
                namespace Fixture.Company.Api;

                public sealed class ApiService
                {
                    public string Ping() => "pong";
                }
                """);

            var slnxPath = Path.Combine(workingDirectory, "MultiProject.slnx");
            await File.WriteAllTextAsync(slnxPath, """
                <Solution>
                  <Project Path="ProjA/ProjA.csproj" />
                  <Project Path="ProjB/ProjB.csproj" />
                  <Project Path="Company.Api/Company.Api.csproj" />
                </Solution>
                """);

            var indexer = new IncrementalCodeMapIndexer();
            var indexed = await indexer.IndexAsync(workingDirectory, force: true, CancellationToken.None);
            Assert.Contains("Company.Api", indexed.AnalyzedProjects);

            await File.WriteAllTextAsync(slnxPath, """
                <Solution>
                  <Project Path="ProjA/ProjA.csproj" />
                  <Project Path="ProjB/ProjB.csproj" />
                  <Project Path="Company.Api/Company.Api.csproj" />
                </Solution>
                <!-- reformatted -->
                """);

            var result = await indexer.UpdateAsync(workingDirectory, CancellationToken.None);

            Assert.Contains("Company.Api", result.AnalyzedProjects);
            var databasePath = Path.Combine(workingDirectory, ".codemap", "index.db");
            var graph = await new CodeMapQueryStore(databasePath).LoadAsync();
            Assert.Contains(graph.Symbols, symbol => symbol.Project == "Company.Api" && symbol.Name == "Ping");

            var statePath = Path.Combine(workingDirectory, ".codemap", "state.json");
            using var stateDocument = JsonDocument.Parse(await File.ReadAllTextAsync(statePath));
            var projectNames = stateDocument.RootElement.GetProperty("projects").EnumerateArray()
                .Select(project => project.GetProperty("projectName").GetString())
                .ToArray();
            Assert.Contains("Company.Api", projectNames);
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }






    [Fact]
    public async Task UpdateAsync_SolutionMetadataOnlyEdit_PreservesExistingSymbolsAndEdges()
    {
        var workingDirectory = CopyFixtureToTempDirectory();
        try
        {
            var indexer = new IncrementalCodeMapIndexer();
            var indexed = await indexer.IndexAsync(workingDirectory, force: true, CancellationToken.None);

            var slnxPath = Path.Combine(workingDirectory, "MultiProject.slnx");
            var originalContent = await File.ReadAllTextAsync(slnxPath);
            await File.WriteAllTextAsync(slnxPath, originalContent + Environment.NewLine + "<!-- reformatted -->" + Environment.NewLine);

            var result = await indexer.UpdateAsync(workingDirectory, CancellationToken.None);

            Assert.Contains("ProjA", result.AnalyzedProjects);
            Assert.Contains("ProjB", result.AnalyzedProjects);
            var databasePath = Path.Combine(workingDirectory, ".codemap", "index.db");
            var graph = await new CodeMapQueryStore(databasePath).LoadAsync();
            Assert.Equal(indexed.Symbols, graph.Symbols.Count);
            Assert.Equal(indexed.Edges, graph.Edges.Count);
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }

    private static string RedirectFirstExternalAssembly(string stateText, string projectName, string newAssemblyPath, string newAssemblyHash)
    {
        using var document = JsonDocument.Parse(stateText);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!string.Equals(property.Name, "projects", StringComparison.Ordinal))
                {
                    property.WriteTo(writer);
                    continue;
                }

                writer.WritePropertyName(property.Name);
                writer.WriteStartArray();
                foreach (var project in property.Value.EnumerateArray())
                {
                    if (!string.Equals(project.GetProperty("projectName").GetString(), projectName, StringComparison.Ordinal))
                    {
                        project.WriteTo(writer);
                        continue;
                    }

                    writer.WriteStartObject();
                    foreach (var projectProperty in project.EnumerateObject())
                    {
                        if (!string.Equals(projectProperty.Name, "externalAssemblies", StringComparison.Ordinal))
                        {
                            projectProperty.WriteTo(writer);
                            continue;
                        }

                        writer.WritePropertyName(projectProperty.Name);
                        writer.WriteStartArray();
                        var first = true;
                        foreach (var external in projectProperty.Value.EnumerateArray())
                        {
                            if (!first)
                            {
                                external.WriteTo(writer);
                                continue;
                            }
                            first = false;
                            writer.WriteStartObject();
                            writer.WriteString("externalProjectName", external.GetProperty("externalProjectName").GetString());
                            writer.WriteString("assemblyPath", newAssemblyPath);
                            writer.WriteString("assemblyHash", newAssemblyHash);
                            writer.WriteEndObject();
                        }
                        writer.WriteEndArray();
                    }
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string ReadConfigHash(string stateText)
    {
        using var document = JsonDocument.Parse(stateText);
        return document.RootElement.GetProperty("configHash").GetString()!;
    }

    private static string ReplaceConfigHash(string stateText, string configHash)
    {
        using var document = JsonDocument.Parse(stateText);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (string.Equals(property.Name, "configHash", StringComparison.Ordinal))
                    writer.WriteString(property.Name, configHash);
                else
                    property.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void CopyFixture(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*.*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var targetPath = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.Copy(file, targetPath, overwrite: true);
        }
    }

    private static string CopyFixtureToTempDirectory()
    {
        var testDir = AppContext.BaseDirectory;
        var solutionRoot = Path.GetFullPath(Path.Combine(testDir, "..", "..", "..", "..", ".."));
        var source = Path.Combine(solutionRoot, "tests", "Fixtures", "MultiProject");
        var destination = Path.Combine(Path.GetTempPath(), "codemap-incremental-" + Guid.NewGuid());
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*.*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var targetPath = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.Copy(file, targetPath, overwrite: true);
        }
        return destination;
    }

    private static string CopyWebFixtureToTempDirectory()
    {
        var testDir = AppContext.BaseDirectory;
        var solutionRoot = Path.GetFullPath(Path.Combine(testDir, "..", "..", "..", "..", ".."));
        var source = Path.Combine(solutionRoot, "tests", "Fixtures", "WebFixture");
        var destination = Path.Combine(Path.GetTempPath(), "codemap-web-incremental-" + Guid.NewGuid());
        CopyFixture(source, destination);
        return destination;
    }

    private static void CleanUp(string workingDirectory)
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(workingDirectory))
            Directory.Delete(workingDirectory, recursive: true);
    }
}
