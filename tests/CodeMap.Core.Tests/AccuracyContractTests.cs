using System.Text.Json;
using CodeMap.Storage;
using Microsoft.Data.Sqlite;

namespace CodeMap.Core.Tests;

[Collection("MsBuild")]
public sealed class AccuracyContractTests
{
    [Fact]
    public async Task CallersJson_ExposesSemanticConfidenceAcrossProjects()
    {
        var workingDirectory = CopyFixture("MultiProject");
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);

            var output = await CliProcess.RunAsync($"callers Greet --root \"{workingDirectory}\" --json");
            Assert.Equal(0, output.ExitCode);

            using var document = JsonDocument.Parse(output.StdOut);
            Assert.Equal(4, document.RootElement.GetProperty("version").GetInt32());
            var relations = document.RootElement.GetProperty("relations").EnumerateArray().ToArray();
            Assert.NotEmpty(relations);
            Assert.All(relations, relation =>
            {
                Assert.Equal("caller", relation.GetProperty("kind").GetString());
                Assert.Equal("semantic", relation.GetProperty("resolutionKind").GetString());
                Assert.Equal(1.0, relation.GetProperty("confidence").GetDouble());
                Assert.Equal("Calls", relation.GetProperty("edgeKind").GetString());
            });
            Assert.Contains(relations, relation => relation.GetProperty("source").GetString()!.Contains("Call", StringComparison.Ordinal));
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }

    [Fact]
    public async Task Homonym_DisambiguatedQualifiedName_ReturnsRelations()
    {
        var workingDirectory = CopyFixture("CollidingProjects");
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);

            var ambiguous = await CliProcess.RunAsync($"impl Widget --root \"{workingDirectory}\" --json");
            Assert.Equal(2, ambiguous.ExitCode);
            Assert.Equal("ambiguous", JsonDocument.Parse(ambiguous.StdOut).RootElement.GetProperty("reason").GetString());

            var resolved = await CliProcess.RunAsync($"impl sym://T:Shared.Widget --root \"{workingDirectory}\" --json");
            Assert.Equal(0, resolved.ExitCode);
            using var document = JsonDocument.Parse(resolved.StdOut);
            Assert.Equal(4, document.RootElement.GetProperty("version").GetInt32());
            Assert.Single(document.RootElement.GetProperty("matches").EnumerateArray());
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }

    [Fact]
    public async Task DeleteThenUpdate_RemovesSymbolsAndRelationsFromQueries()
    {
        var workingDirectory = CopyFixture("MultiProject");
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
                }
                """);

            var extraPath = Path.Combine(workingDirectory, "ProjB", "Extra.cs");
            await File.WriteAllTextAsync(extraPath, """
                namespace Fixture.ProjB;

                public sealed class Extra
                {
                    public string Farewell() => "bye";

                    public string CallGreet() => new Greeter().Greet();
                }
                """);
            await indexer.UpdateAsync(workingDirectory, CancellationToken.None);

            var graphBeforeDelete = await new CodeMapQueryStore(Path.Combine(workingDirectory, ".codemap", "index.db")).LoadAsync();
            var serviceBefore = new CodeMapQueryService(graphBeforeDelete);
            var greetBefore = Assert.Single(serviceBefore.Find("Greet", 10));
            Assert.Contains(serviceBefore.CallerRelations(greetBefore, 20), relation => relation.Symbol.Name == "CallGreet");

            File.Delete(extraPath);
            await indexer.UpdateAsync(workingDirectory, CancellationToken.None);

            var databasePath = Path.Combine(workingDirectory, ".codemap", "index.db");
            var graph = await new CodeMapQueryStore(databasePath).LoadAsync();
            var service = new CodeMapQueryService(graph);

            Assert.DoesNotContain(service.Find("Farewell", 10), _ => true);
            Assert.DoesNotContain(service.Find("CallGreet", 10), _ => true);
            var greet = Assert.Single(service.Find("Greet", 10));
            Assert.DoesNotContain(service.CallerRelations(greet, 20), relation => relation.Symbol.Name == "CallGreet");

            var cliFind = await CliProcess.RunAsync($"find Farewell --root \"{workingDirectory}\" --json");
            Assert.Equal(2, cliFind.ExitCode);
            Assert.Equal("no_matches", JsonDocument.Parse(cliFind.StdOut).RootElement.GetProperty("reason").GetString());
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }

    [Fact]
    public async Task WebFixture_QueryStoreLoadsEdgeAccuracyMetadata()
    {
        var workingDirectory = CopyFixture("WebFixture");
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);
            var graph = await new CodeMapQueryStore(Path.Combine(workingDirectory, ".codemap", "index.db")).LoadAsync();
            Assert.Contains(graph.Edges, edge => edge.Kind == Core.Models.EdgeKind.UsesCss && edge.ResolutionKind == Core.Models.EdgeResolutionKind.Syntactic && edge.Confidence == 0.85);
            Assert.Contains(graph.Edges, edge => edge.Kind == Core.Models.EdgeKind.Calls && edge.ResolutionKind == Core.Models.EdgeResolutionKind.Syntactic && edge.Confidence == 0.90);
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }

    [Fact]
    public async Task FindJson_UsesSchemaVersionFour()
    {
        var workingDirectory = CopyFixture("MultiProject");
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);
            var output = await CliProcess.RunAsync($"find Greeter --root \"{workingDirectory}\" --json");
            using var document = JsonDocument.Parse(output.StdOut);
            Assert.Equal(4, document.RootElement.GetProperty("version").GetInt32());
            var match = document.RootElement.GetProperty("matches")[0];
            Assert.True(match.TryGetProperty("language", out _));
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }

    [Fact]
    public async Task DotNetFlow_V4Query_UnchangedContractStillOmitsEvidenceFields()
    {
        var workingDirectory = CopyFixture("AspNetFixture");
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);


            var output = await CliProcess.RunAsync($"find OrdersController --root \"{workingDirectory}\" --json");
            Assert.Equal(0, output.ExitCode);
            using var document = JsonDocument.Parse(output.StdOut);
            Assert.Equal(4, document.RootElement.GetProperty("version").GetInt32());
            Assert.False(document.RootElement.TryGetProperty("evidence", out _));
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }

    [Fact]
    public async Task DotNetFlow_V5Evidence_ExposesNewEdgeKindsWithLocationAndConfidence()
    {
        var workingDirectory = CopyFixture("AspNetFixture");
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);

            var output = await CliProcess.RunAsync($"flow \"GET /orders/{{id}}\" --kind http --root \"{workingDirectory}\" --json --evidence");
            Assert.Equal(0, output.ExitCode);
            using var document = JsonDocument.Parse(output.StdOut);
            Assert.Equal(5, document.RootElement.GetProperty("version").GetInt32());
            var relations = document.RootElement.GetProperty("relations").EnumerateArray().ToArray();
            var routesTo = Assert.Single(relations, relation => relation.GetProperty("edgeKind").GetString() == "RoutesTo");
            Assert.Equal("semantic", routesTo.GetProperty("resolutionKind").GetString());
            Assert.Equal(1.0, routesTo.GetProperty("confidence").GetDouble());
            Assert.True(routesTo.TryGetProperty("location", out var location));
            Assert.True(location.TryGetProperty("file", out _));
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }

    [Fact]
    public async Task LambdaRouteHandler_SurvivesPersistenceWithSemanticRoutesToEdge()
    {
        var workingDirectory = CopyFixture("AspNetFixture");
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);
            var graph = await new CodeMapQueryStore(Path.Combine(workingDirectory, ".codemap", "index.db")).LoadAsync();

            var route = Assert.Single(graph.Symbols, s => s.Kind == Core.Models.NodeKind.Route && s.QualifiedName == "POST /orders");
            var routesTo = Assert.Single(graph.Edges, e => e.Kind == Core.Models.EdgeKind.RoutesTo && e.SourceId == route.Id);
            var handler = Assert.Single(graph.Symbols, s => s.Id == routesTo.TargetId);




            Assert.Equal(Core.Models.NodeKind.Function, handler.Kind);
            Assert.Equal(Core.Models.EdgeResolutionKind.Semantic, routesTo.ResolutionKind);
            Assert.Equal(1.0, routesTo.Confidence);
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }

    [Fact]
    public async Task RazorEdges_ExposeSyntacticConfidenceInEvidenceOutput()
    {
        var workingDirectory = CopyFixture("AspNetFixture");
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);
            var graph = await new CodeMapQueryStore(Path.Combine(workingDirectory, ".codemap", "index.db")).LoadAsync();
            Assert.Contains(graph.Edges, edge => edge.Kind == Core.Models.EdgeKind.Renders && edge.ResolutionKind == Core.Models.EdgeResolutionKind.Syntactic && edge.Confidence == 0.90);
            Assert.Contains(graph.Edges, edge => edge.Kind == Core.Models.EdgeKind.BindsTo && edge.ResolutionKind == Core.Models.EdgeResolutionKind.Syntactic && edge.Confidence == 0.85);
            Assert.Contains(graph.Edges, edge => edge.Kind == Core.Models.EdgeKind.HandlesEvent && edge.ResolutionKind == Core.Models.EdgeResolutionKind.Syntactic && edge.Confidence == 0.90);
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }

    [Fact]
    public async Task XamlEdges_ExposeSyntacticConfidenceInEvidenceOutput()
    {
        var workingDirectory = CopyFixture("WpfFixture");
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);
            var graph = await new CodeMapQueryStore(Path.Combine(workingDirectory, ".codemap", "index.db")).LoadAsync();
            Assert.Contains(graph.Edges, edge => edge.Kind == Core.Models.EdgeKind.UsesViewModel && edge.ResolutionKind == Core.Models.EdgeResolutionKind.Syntactic && edge.Confidence == 0.90);
            Assert.Contains(graph.Edges, edge => edge.Kind == Core.Models.EdgeKind.BindsTo && edge.ResolutionKind == Core.Models.EdgeResolutionKind.Syntactic && edge.Confidence == 0.85);
            Assert.Contains(graph.Edges, edge => edge.Kind == Core.Models.EdgeKind.HandlesEvent && edge.ResolutionKind == Core.Models.EdgeResolutionKind.Syntactic && edge.Confidence == 0.90);
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }

    [Fact]
    public async Task DiRegistration_ExposesSemanticConfidenceInEvidenceOutput()
    {
        var workingDirectory = CopyFixture("AspNetFixture");
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);
            var graph = await new CodeMapQueryStore(Path.Combine(workingDirectory, ".codemap", "index.db")).LoadAsync();
            Assert.Contains(graph.Edges, edge => edge.Kind == Core.Models.EdgeKind.Registers && edge.ResolutionKind == Core.Models.EdgeResolutionKind.Semantic && edge.Confidence == 1.0);
            Assert.Contains(graph.Edges, edge => edge.Kind == Core.Models.EdgeKind.ResolvesTo && edge.ResolutionKind == Core.Models.EdgeResolutionKind.Semantic && edge.Confidence == 1.0);
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }

    private static string CopyFixture(string fixtureName)
    {


        var source = fixtureName is "AspNetFixture" or "WpfFixture"
            ? FixtureRestore.EnsureRestored(fixtureName)
            : Path.Combine(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..")), "tests", "Fixtures", fixtureName);
        var destination = Path.Combine(Path.GetTempPath(), $"codemap-accuracy-{fixtureName}-" + Guid.NewGuid());
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

    private static void CleanUp(string workingDirectory)
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(workingDirectory))
            Directory.Delete(workingDirectory, recursive: true);
    }

}