using CodeMap.Core.Models;
using CodeMap.Storage;
using Microsoft.Data.Sqlite;

namespace CodeMap.Core.Tests;






[Collection("MsBuild")]
public sealed class DotNetIndexerTests
{
    [Fact]
    public async Task AspNetFixture_FullIndex_ProducesRouteDiRazorNodesAndEdges()
    {
        var workingDirectory = CopyFixture("AspNetFixture");
        try
        {
            var summary = await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);
            Assert.Contains("AspNetFixture", summary.AnalyzedProjects);

            var graph = await new CodeMapQueryStore(Path.Combine(workingDirectory, ".codemap", "index.db")).LoadAsync();
            Assert.Contains(graph.Symbols, s => s.Kind == NodeKind.Route);
            Assert.Contains(graph.Symbols, s => s.Kind == NodeKind.DependencyRegistration);
            Assert.Contains(graph.Symbols, s => s.Kind == NodeKind.RazorComponent);
            Assert.Contains(graph.Symbols, s => s.Kind == NodeKind.RazorPage);
            Assert.Contains(graph.Edges, e => e.Kind == EdgeKind.RoutesTo);
            Assert.Contains(graph.Edges, e => e.Kind == EdgeKind.Registers);
            Assert.Contains(graph.Edges, e => e.Kind == EdgeKind.ResolvesTo);
            Assert.Contains(graph.Edges, e => e.Kind == EdgeKind.Renders);
            Assert.Contains(graph.Edges, e => e.Kind == EdgeKind.BindsTo);
            Assert.Contains(graph.Edges, e => e.Kind == EdgeKind.HandlesEvent);


            Assert.Contains(graph.Files, f => f.RelativePath.EndsWith(".razor", StringComparison.OrdinalIgnoreCase) && f.Language == "razor");
            Assert.Contains(graph.Files, f => f.RelativePath.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase) && f.Language == "razor");
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }

    [Fact]
    public async Task WpfFixture_FullIndex_ProducesXamlViewAndBindingEdges()
    {
        var workingDirectory = CopyFixture("WpfFixture");
        try
        {
            var summary = await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);
            Assert.Contains("WpfFixture", summary.AnalyzedProjects);

            var graph = await new CodeMapQueryStore(Path.Combine(workingDirectory, ".codemap", "index.db")).LoadAsync();
            Assert.Contains(graph.Symbols, s => s.Kind == NodeKind.XamlView);
            Assert.Contains(graph.Edges, e => e.Kind == EdgeKind.UsesViewModel);
            Assert.Contains(graph.Edges, e => e.Kind == EdgeKind.BindsTo);
            Assert.Contains(graph.Edges, e => e.Kind == EdgeKind.HandlesEvent);
            Assert.Contains(graph.Files, f => f.RelativePath.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase) && f.Language == "xaml");
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }

    [Fact]
    public async Task MarkupOnlyChange_ReanalyzesOnlyOwningProjectAndUpdatesEdges()
    {
        var workingDirectory = CopyFixture("AspNetFixture");
        try
        {
            var indexer = new IncrementalCodeMapIndexer();
            await indexer.IndexAsync(workingDirectory, force: true, CancellationToken.None);

            var razorPath = Path.Combine(workingDirectory, "Components", "OrderSummary.razor");
            await File.WriteAllTextAsync(razorPath, """
                @* Total renamed to Amount to exercise a markup-only structural change. *@
                <div class="order-summary">
                    <p>@Total</p>
                    <p>updated</p>
                </div>
                """);

            var update = await indexer.UpdateAsync(workingDirectory, CancellationToken.None);
            Assert.Contains("AspNetFixture", update.AnalyzedProjects);
            Assert.Equal(1, update.Updated);
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }

    [Fact]
    public async Task NoOpUpdate_AfterAspNetFixtureIndex_ReportsAllSkipped()
    {
        var workingDirectory = CopyFixture("AspNetFixture");
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
    public async Task MarkupFileAdded_IsDetectedAsAddedFile()
    {
        var workingDirectory = CopyFixture("AspNetFixture");
        try
        {
            var indexer = new IncrementalCodeMapIndexer();
            await indexer.IndexAsync(workingDirectory, force: true, CancellationToken.None);

            var newRazorPath = Path.Combine(workingDirectory, "Components", "ExtraWidget.razor");
            await File.WriteAllTextAsync(newRazorPath, "<div>extra widget</div>\n");

            var update = await indexer.UpdateAsync(workingDirectory, CancellationToken.None);
            Assert.Equal(1, update.Added);

            var graph = await new CodeMapQueryStore(Path.Combine(workingDirectory, ".codemap", "index.db")).LoadAsync();
            Assert.Contains(graph.Files, f => f.RelativePath.Contains("ExtraWidget.razor", StringComparison.Ordinal));
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }

    [Fact]
    public async Task MarkupFileDeleted_IsDetectedAsRemovedFile()
    {
        var workingDirectory = CopyFixture("AspNetFixture");
        try
        {
            var indexer = new IncrementalCodeMapIndexer();
            await indexer.IndexAsync(workingDirectory, force: true, CancellationToken.None);

            var indexPage = Path.Combine(workingDirectory, "Pages", "Index.cshtml");
            File.Delete(indexPage);

            var update = await indexer.UpdateAsync(workingDirectory, CancellationToken.None);
            Assert.Equal(1, update.Removed);

            var graph = await new CodeMapQueryStore(Path.Combine(workingDirectory, ".codemap", "index.db")).LoadAsync();
            Assert.DoesNotContain(graph.Symbols, s => s.Kind == NodeKind.RazorPage);
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }

    private static string CopyFixture(string fixtureName)
    {




        var source = FixtureRestore.EnsureRestored(fixtureName);
        var destination = Path.Combine(Path.GetTempPath(), $"codemap-dotnet-{fixtureName}-" + Guid.NewGuid());
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