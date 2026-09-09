using CodeMap.Core.Models;
using CodeMap.Engine.Application;
using CodeMap.Engine.Application.Investigation;
using CodeMap.Core.Models.Investigation;

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

    private static IndexedSymbol Symbol(string id) => new(
        id, "project", "file", $"{id}.cs", NodeKind.Method, id, id, null, 1, 2, "public", "csharp");
}
