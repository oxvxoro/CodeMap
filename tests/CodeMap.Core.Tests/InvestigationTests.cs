using CodeMap.Core.Models;
using CodeMap.Engine.Application;
using CodeMap.Engine.Application.Investigation;
using CodeMap.Core.Models.Investigation;
using CodeMap.CSharp;
using CodeMap.Engine.Application.Investigation.Providers;
using CodeMap.Storage;

namespace CodeMap.Core.Tests;

public sealed class InvestigationTests
{
    [Theory]
    [InlineData(EdgeResolutionKind.Semantic, CertaintyTier.Semantic)]
    [InlineData(EdgeResolutionKind.Syntactic, CertaintyTier.Syntactic)]
    [InlineData(EdgeResolutionKind.Heuristic, CertaintyTier.Heuristic)]
    public void CertaintyTier_PreservesResolutionKind(EdgeResolutionKind input, CertaintyTier expected)
    {
        Assert.Equal(expected, input.FromResolutionKind());
    }

    [Fact]
    public void Deduplicator_PreservesPreferredCandidateAndOtherProviders()
    {
        var symbol = Symbol("target");
        var edge = new IndexedEdge("source", symbol.Id, EdgeKind.Calls, "file", 10, EdgeResolutionKind.Heuristic);
        var heuristic = new InvestigationCandidate(symbol, edge, 1, "callers", CertaintyTier.Heuristic, null, 0, 10);
        var semantic = new InvestigationCandidate(symbol, edge with { ResolutionKind = EdgeResolutionKind.Semantic }, 2, "flow", CertaintyTier.Semantic, null, 0, 10);

        var result = InvestigationCandidateDeduplicator.Deduplicate([heuristic, semantic]);

        var candidate = Assert.Single(result);
        Assert.Equal(CertaintyTier.Semantic, candidate.CertaintyTier);
        Assert.Equal(["callers"], candidate.AlsoFoundBy);
    }

    [Fact]
    public void Ranking_IsDeterministicAndUsesStableIdAsFinalTieBreak()
    {
        var first = new InvestigationCandidate(Symbol("b"), null, 1, "members", CertaintyTier.Semantic, null, 0, 1);
        var second = new InvestigationCandidate(Symbol("a"), null, 1, "members", CertaintyTier.Semantic, null, 0, 1);

        var ranked = new InvestigationRankingPolicy().Rank(InvestigationGoal.Understand, [first, second]);

        Assert.Equal(["a", "b"], ranked.Select(candidate => candidate.Symbol.Id));
    }

    [Fact]
    public void Ranking_UsesConfidenceThenCostBeforeStableId()
    {
        var lowConfidence = RelationCandidate("low", 0.4, 1);
        var highConfidence = RelationCandidate("high", 0.9, 20);
        var expensive = RelationCandidate("expensive", 0.8, 10);
        var cheap = RelationCandidate("cheap", 0.8, 2);

        var ranked = new InvestigationRankingPolicy().Rank(
            InvestigationGoal.Debug,
            [lowConfidence, highConfidence, expensive, cheap]);

        Assert.Equal(["high", "cheap", "expensive", "low"], ranked.Select(candidate => candidate.Symbol.Id));
    }

    [Fact]
    public void BudgetAllocator_ReturnsASelectedPrefix()
    {
        var candidates = new[]
        {
            new InvestigationCandidate(Symbol("a"), null, 1, "members", CertaintyTier.Semantic, null, 0, 3),
            new InvestigationCandidate(Symbol("b"), null, 1, "members", CertaintyTier.Semantic, null, 0, 3),
            new InvestigationCandidate(Symbol("c"), null, 1, "members", CertaintyTier.Semantic, null, 0, 3)
        };

        var selection = new InvestigationBudgetAllocator().Allocate(candidates, 6);

        Assert.Equal(["a", "b"], selection.Selected.Select(candidate => candidate.Symbol.Id));
        Assert.Equal(["c"], selection.Excluded.Select(candidate => candidate.Symbol.Id));
        Assert.True(selection.Truncated);
    }

    [Fact]
    public void BudgetAllocator_IsMonotonicWhenBudgetIncreases()
    {
        var candidates = new[]
        {
            new InvestigationCandidate(Symbol("a"), null, 1, "members", CertaintyTier.Semantic, null, 0, 6),
            new InvestigationCandidate(Symbol("b"), null, 1, "members", CertaintyTier.Semantic, null, 0, 5),
            new InvestigationCandidate(Symbol("c"), null, 1, "members", CertaintyTier.Semantic, null, 0, 1)
        };
        var allocator = new InvestigationBudgetAllocator();

        var smaller = allocator.Allocate(candidates, 5);
        var larger = allocator.Allocate(candidates, 6);

        Assert.Empty(smaller.Selected);
        Assert.Equal(["a"], larger.Selected.Select(candidate => candidate.Symbol.Id));
        Assert.All(smaller.Selected, candidate =>
            Assert.Contains(candidate.Symbol.Id, larger.Selected.Select(selected => selected.Symbol.Id)));
    }

    [Fact]
    public void CoverageAggregator_ReportsNegativeEvidencePerProvider()
    {
        var callers = new InvestigationCandidate(Symbol("caller"),
            new IndexedEdge("caller", "root", EdgeKind.Calls, "file", 1),
            1, "callers", CertaintyTier.Semantic, null, 0, 1);
        var impact = new InvestigationCandidate(Symbol("impact"),
            new IndexedEdge("impact", "root", EdgeKind.Calls, "file", 2),
            1, "impact", CertaintyTier.Semantic, null, 0, 1);
        var coverage = new CoverageAggregator().Aggregate(
            [ProviderCoverageStatus.Complete(InvestigationProviderKind.Callers, 1), ProviderCoverageStatus.Complete(InvestigationProviderKind.Impact, 1)],
            [callers, impact],
            [callers],
            null!,
            Symbol("root"),
            new InvestigationRequest("root", InvestigationGoal.Debug));

        Assert.Contains("callers: complete, remaining=0", coverage.NegativeEvidence);
        Assert.DoesNotContain("impact: complete, remaining=0", coverage.NegativeEvidence);
    }

    [Fact]
    public void FileTextAccessor_RejectsPathsOutsideRoot()
    {
        var directory = Directory.CreateTempSubdirectory("codemap-investigation-");
        try
        {
            File.WriteAllText(Path.Combine(directory.FullName, "inside.cs"), "inside");
            var accessor = new FileTextAccessor(directory.FullName);

            Assert.Equal("inside", accessor.Read("inside.cs", 1, 1));
            Assert.Null(accessor.Read(".." + Path.DirectorySeparatorChar + "outside.cs", 1, 1));
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    [Fact]
    public async Task LocalSliceProvider_PreservesLocalEvidenceWithoutGlobalLookup()
    {
        var root = Symbol("Process");
        var item = new SliceItem(
            7,
            SliceOperationKind.Condition,
            "result",
            "if (result)",
            new SourceLocation { StartLine = 42, StartColumn = 9, EndLine = 42, EndColumn = 20 });
        var dependency = new SliceDependency(3, 7, SliceDependencyKind.Condition);
        var slice = new SemanticSliceResult(
            root,
            new SliceScope(root.Id, root.DisplayName, "src/OrderService.cs", 30, 50),
            [item],
            [dependency],
            false);
        var provider = new LocalSliceProvider((_, _, _) => Task.FromResult(slice));

        var result = await provider.CollectAsync(
            null!,
            root,
            new InvestigationOverrides(20, 1, 0, true, 200, "repo"),
            CancellationToken.None);

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal(root.Id, candidate.Symbol.Id);
        Assert.NotNull(candidate.LocalEvidence);
        Assert.Equal("result", candidate.LocalEvidence!.SymbolName);
        Assert.Equal(SliceOperationKind.Condition, candidate.LocalEvidence.OperationKind);
        Assert.Equal("src/OrderService.cs", candidate.LocalEvidence.File);
        Assert.Equal([dependency], candidate.LocalEvidence.Dependencies);
        Assert.Equal(InvestigationEvidenceLocationOrigin.LocalSlice, candidate.EvidenceLocation!.Origin);
    }

    [Fact]
    public async Task LocalSliceProvider_PreservesSameNamedLocalItemsAndDependencyTopology()
    {
        var root = Symbol("Process");
        var first = new SliceItem(
            7,
            SliceOperationKind.Condition,
            "result",
            "if (result)",
            new SourceLocation { StartLine = 42, StartColumn = 9, EndLine = 42, EndColumn = 20 });
        var second = new SliceItem(
            8,
            SliceOperationKind.Assignment,
            "result",
            "result = normalized",
            new SourceLocation { StartLine = 43, StartColumn = 5, EndLine = 43, EndColumn = 25 });
        var firstDependency = new SliceDependency(3, 7, SliceDependencyKind.Condition);
        var sharedDependency = new SliceDependency(7, 8, SliceDependencyKind.Assignment);
        var secondDependency = new SliceDependency(8, 9, SliceDependencyKind.Use);
        var slice = new SemanticSliceResult(
            root,
            new SliceScope(root.Id, root.DisplayName, "src/OrderService.cs", 30, 50),
            [first, second],
            [firstDependency, sharedDependency, secondDependency],
            false);
        var provider = new LocalSliceProvider((_, _, _) => Task.FromResult(slice));

        var result = await provider.CollectAsync(
            null!,
            root,
            new InvestigationOverrides(20, 1, 0, true, 200, "repo"),
            CancellationToken.None);

        Assert.Equal(2, result.Candidates.Count);
        Assert.All(result.Candidates, candidate => Assert.Equal(root.Id, candidate.Symbol.Id));
        Assert.Equal([firstDependency, sharedDependency], result.Candidates[0].LocalEvidence!.Dependencies);
        Assert.Equal([sharedDependency, secondDependency], result.Candidates[1].LocalEvidence!.Dependencies);
        Assert.Equal([7, 8], result.Candidates.Select(candidate => candidate.LocalEvidence!.ItemId));
    }

    [Fact]
    public void Ranking_DebugPlacesFocusedLocalEvidenceBeforeDirectRelation()
    {
        var root = Symbol("Process");
        var local = InvestigationCandidate.FromLocalSlice(
            root,
            new LocalSliceEvidence(
                root.Id,
                1,
                SliceOperationKind.Condition,
                "result",
                "if (result)",
                "Process.cs",
                new SourceLocation { StartLine = 10, EndLine = 10 },
                Array.Empty<SliceDependency>()));
        var callee = new InvestigationCandidate(
            Symbol("Validate"),
            new IndexedEdge(root.Id, "Validate", EdgeKind.Calls, "file", 10),
            1,
            "callees",
            CertaintyTier.Semantic,
            1,
            0,
            1);

        var ranked = new InvestigationRankingPolicy().Rank(InvestigationGoal.Debug, [callee, local]);

        Assert.Equal(local.LocalEvidence, ranked[0].LocalEvidence);
        Assert.Equal("localSlice", ranked[0].Provider);
    }

    [Fact]
    public void SourceEvidenceBuilder_UsesRelationSourceLocationInsteadOfDeclarationFile()
    {
        var candidate = new InvestigationCandidate(
            Symbol("Repository.Save"),
            new IndexedEdge("caller", "Repository.Save", EdgeKind.Calls, "caller-file", 42)
            {
                StartColumn = 13,
                EndLine = 42,
                EndColumn = 28
            },
            1,
            "callees",
            CertaintyTier.Semantic,
            1,
            1,
            1)
        {
            EvidenceLocation = new InvestigationEvidenceLocation(
                "src/OrderService.cs",
                42,
                13,
                42,
                28,
                InvestigationEvidenceLocationOrigin.EdgeSource)
        };
        var directory = Directory.CreateTempSubdirectory("codemap-investigation-source-");
        try
        {
            File.WriteAllText(Path.Combine(directory.FullName, "src", "OrderService.cs"), "");
        }
        catch (DirectoryNotFoundException)
        {
            Directory.CreateDirectory(Path.Combine(directory.FullName, "src"));
            File.WriteAllLines(Path.Combine(directory.FullName, "src", "OrderService.cs"), Enumerable.Range(1, 50).Select(line => $"line {line}"));
        }

        try
        {
            var spans = new SourceEvidenceBuilder().BuildSpans(
                [candidate],
                SourceEvidenceMode.Minimal,
                200,
                new FileTextAccessor(directory.FullName));

            var span = Assert.Single(spans);
            Assert.Equal("src/OrderService.cs", span.File);
            Assert.DoesNotContain("Repository.cs", span.File);
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    private static IndexedSymbol Symbol(string id) => new(
        id, "project", "file", $"{id}.cs", NodeKind.Method, id, id, null, 1, 2, "public", "csharp");

    private static InvestigationCandidate RelationCandidate(string id, double confidence, int cost) =>
        new(
            Symbol(id),
            new IndexedEdge("source", id, EdgeKind.Calls, "file", 1) { Confidence = confidence },
            1,
            "callees",
            CertaintyTier.Semantic,
            confidence,
            0,
            cost);
}
