using CodeMap.Core.Contracts;
using CodeMap.Core.Models;
using CodeMap.Engine.Application;
using CodeMap.Engine.Application.Investigation;
using CodeMap.Core.Models.Investigation;
using CodeMap.CSharp;
using CodeMap.Engine.Application.Investigation.Providers;
using CodeMap.Storage;
using CodeMap.Storage.Queries;
using CodeMap.Core.Tests.Contracts;

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
    public async Task LocalSliceProvider_ReportsWindowTruncationAsPartial()
    {
        var root = Symbol("Process");
        var items = Enumerable.Range(1, 9)
            .Select(itemId => new SliceItem(
                itemId,
                SliceOperationKind.Invocation,
                $"value{itemId}",
                $"value{itemId}",
                new SourceLocation { StartLine = itemId, EndLine = itemId }))
            .ToArray();
        var slice = new SemanticSliceResult(
            root,
            new SliceScope(root.Id, root.DisplayName, "src/OrderService.cs", 1, 20),
            items,
            Array.Empty<SliceDependency>(),
            false);
        var provider = new LocalSliceProvider((_, _, _) => Task.FromResult(slice));

        var firstPage = await provider.CollectAsync(
            null!, root, new InvestigationOverrides(20, 1, 0, true, 200, "repo"),
            CancellationToken.None, offset: 0, window: 8);
        var secondPage = await provider.CollectAsync(
            null!, root, new InvestigationOverrides(20, 1, 0, true, 200, "repo"),
            CancellationToken.None, offset: 8, window: 8);

        Assert.Equal(8, firstPage.Candidates.Count);
        Assert.Equal(ProviderCoverageState.Partial, firstPage.Status.State);
        Assert.Equal("window", firstPage.Status.Reason);
        Assert.Single(secondPage.Candidates);
        Assert.Equal(ProviderCoverageState.Complete, secondPage.Status.State);
    }

    [Fact]
    public void PresentationMapper_PreservesV1MatchKindAndStructuralEstimate()
    {
        var root = Symbol("Process");
        var candidate = new InvestigationCandidate(
            Symbol("Validate"),
            new IndexedEdge(root.Id, "Validate", EdgeKind.Calls, null, null),
            1,
            "callees",
            CertaintyTier.Semantic,
            null,
            0,
            3)
        {
            EvidenceLocation = new InvestigationEvidenceLocation(
                "Validate.cs", 10, null, 12, null, InvestigationEvidenceLocationOrigin.SymbolDeclaration)
        };
        var selection = new InvestigationSelection([candidate], [], 7, false, null)
        {
            RequestedBudget = 100,
            Cost = new InvestigationCostReport(100, 0, 7, 11, 18, false, null)
        };
        var result = new InvestigationResult(
            new InvestigationResponse(1, "Process", InvestigationGoal.Debug, root, false, []),
            [candidate], selection, InvestigationCoverage.Empty, []);

        var response = InvestigationPresentationMapper.ToResponse(
            "Process", InvestigationGoal.Debug, result, stale: false);

        Assert.Equal("Method", response.Root!.Kind);
        Assert.Equal(7, response.Budget.Estimated);
        Assert.Equal(7, response.Budget.Structural);
        Assert.Equal(11, response.Budget.Source);
        Assert.Equal(18, response.Budget.TotalEstimated);
        Assert.Null(response.Items[0].Via!.Location);
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

    [Fact]
    public void SourceEvidenceBuilder_MergesOverlappingRangesIntoOneRead()
    {
        var first = CandidateAt("first", "src/File.cs", 10);
        var second = CandidateAt("second", "src/File.cs", 11);
        var accessor = new CountingAccessor();

        var spans = new SourceEvidenceBuilder().BuildSpans(
            [first, second], SourceEvidenceMode.Minimal, 200, accessor);

        var span = Assert.Single(spans);
        Assert.Equal("src/File.cs", span.File);
        Assert.Equal(8, span.StartLine);
        Assert.Equal(13, span.EndLine);
        Assert.Equal(1, accessor.ReadCount);
        Assert.Equal(["first", "second"], span.CandidateIds);
    }

    [Fact]
    public void SourceEvidenceBuilder_SkipsOverBudgetSpanAndContinues()
    {
        var large = CandidateAt("large", "a.cs", 10);
        var small = CandidateAt("small", "b.cs", 10);
        var accessor = new CountingAccessor { LargeText = new string('x', 400) };

        var spans = new SourceEvidenceBuilder().BuildSpans(
            [large, small], SourceEvidenceMode.Minimal, 20, accessor);

        var span = Assert.Single(spans);
        Assert.Equal("b.cs", span.File);
        Assert.Contains("a.cs", accessor.ReadPaths);
        Assert.Contains("b.cs", accessor.ReadPaths);
    }

    [Fact]
    public void SourceEvidenceBuilder_ReservesBudgetForFocusedEvidenceBeforeDeepEvidence()
    {
        var focused = InvestigationCandidate.FromLocalSlice(
            Symbol("focused"),
            new LocalSliceEvidence(
                "focused",
                1,
                SliceOperationKind.Condition,
                "value",
                "if (value)",
                "focused.cs",
                new SourceLocation { StartLine = 10, EndLine = 10 },
                []));
        var deep = new InvestigationCandidate(
            Symbol("deep"),
            null,
            2,
            "impact",
            CertaintyTier.Semantic,
            null,
            0,
            1)
        {
            EvidenceLocation = new InvestigationEvidenceLocation(
                "deep.cs", 10, null, 10, null, InvestigationEvidenceLocationOrigin.SymbolDeclaration)
        };
        var accessor = new CountingAccessor { LargeText = new string('x', 40) };

        var spans = new SourceEvidenceBuilder().BuildSpans(
            [focused, deep], SourceEvidenceMode.Minimal, 20, accessor);

        var span = Assert.Single(spans);
        Assert.Equal(["focused"], span.CandidateIds);
        Assert.Contains("deep.cs", accessor.ReadPaths);
    }

    [Fact]
    public void SourceEvidenceBuilder_ReadsOnlyCandidatesProvidedAsSelected()
    {
        var selected = CandidateAt("selected", "selected.cs", 10);
        var accessor = new CountingAccessor();

        var spans = new SourceEvidenceBuilder().BuildSpans(
            [selected], SourceEvidenceMode.Minimal, 200, accessor);

        Assert.Single(spans);
        Assert.Equal(["selected.cs"], accessor.ReadPaths);
        Assert.DoesNotContain("excluded.cs", accessor.ReadPaths);
    }

    [Fact]
    public void BudgetAllocator_ReportsAdditiveCostBreakdown()
    {
        var selection = new InvestigationBudgetAllocator().Allocate(
            [new InvestigationCandidate(Symbol("a"), null, 1, "members", CertaintyTier.Semantic, null, 0, 3)], 10);

        Assert.Equal(selection.Cost.Envelope + selection.Cost.Structural + selection.Cost.Source, selection.Cost.TotalEstimated);
        Assert.Equal(selection.EstimatedTokens, selection.Cost.Structural);
    }

    [Fact]
    public async Task Application_InvestigateDebug_UsesIndexedRootAndCoverage()
    {
        await using var fixture = await QueryParityFixture.CreateAsync("MultiProject");
        var application = new CodeMapApplication(graphReaderFactory: OpenReaderAsync);

        var response = await application.InvestigateAsync(new InvestigationRequest(
            "Fixture.ProjB.Greeter",
            InvestigationGoal.Debug,
            fixture.WorkingDirectory,
            TokenBudget: 200,
            MaxResults: 5,
            Depth: 1,
            SourceMode: "none"));

        Assert.True(response.Succeeded, response.Error?.Message);
        Assert.NotNull(response.Value);
        Assert.Equal("Greeter", response.Value!.Resolution.Root!.Name);
        Assert.NotEmpty(response.Value.Coverage.Providers);
        Assert.All(response.Value.Items, item => Assert.Contains(item.Provider, response.Value.Coverage.Providers.Select(status => status.Provider.ToString().ToLowerInvariant())));
    }

    private static async Task<ICodeMapGraphReader> OpenReaderAsync(string databasePath, CancellationToken cancellationToken)
    {
        var connection = await new CodeMapQueryStore(databasePath).OpenReadOnlyConnectionAsync(cancellationToken);
        return new SqliteCodeMapGraphReader(connection);
    }

    [Fact]
    public async Task Orchestrator_ReportsProvidersSkippedByEarlyStop()
    {
        var root = Symbol("root");
        var flow = new FixedProvider(InvestigationProviderKind.Flow, [Candidate("flow")]);
        var callees = new FixedProvider(InvestigationProviderKind.Callees, [Candidate("callee")]);
        var orchestrator = new InvestigationOrchestrator(
            new InvestigationRankingPolicy(),
            new InvestigationBudgetAllocator(),
            _ =>
            [
                new InvestigationProviderPlan(flow, ProviderPlan.For(flow.Kind)),
                new InvestigationProviderPlan(callees, ProviderPlan.For(callees.Kind))
            ]);

        var result = await orchestrator.RunAsync(
            InvestigationGoal.Trace,
            null!,
            root,
            new InvestigationOverrides(20, 1, 0, true, 200),
            CancellationToken.None);

        var skipped = Assert.Single(result.ProviderStatuses, status => status.Provider == InvestigationProviderKind.Callees);
        Assert.Equal(ProviderCoverageState.NotRun, skipped.State);
        Assert.Equal("early_stop", skipped.Reason);
    }

    [Fact]
    public async Task Orchestrator_ReportsProvidersSkippedByGlobalCandidateCap()
    {
        var root = Symbol("root");
        var first = new FixedProvider(InvestigationProviderKind.Callers, [Candidate("first"), Candidate("second")]);
        var second = new FixedProvider(InvestigationProviderKind.Impact, [Candidate("third")]);
        var orchestrator = new InvestigationOrchestrator(
            new InvestigationRankingPolicy(),
            new InvestigationBudgetAllocator(),
            _ =>
            [
                new InvestigationProviderPlan(first, ProviderPlan.For(first.Kind)),
                new InvestigationProviderPlan(second, ProviderPlan.For(second.Kind))
            ]);

        var result = await orchestrator.RunAsync(
            InvestigationGoal.Debug,
            null!,
            root,
            new InvestigationOverrides(1, 1, 0, true, 200),
            CancellationToken.None);

        var skipped = Assert.Single(result.ProviderStatuses, status => status.Provider == InvestigationProviderKind.Impact);
        Assert.Equal(ProviderCoverageState.NotRun, skipped.State);
        Assert.Equal("global_cap", skipped.Reason);
    }

    private static InvestigationCandidate CandidateAt(string id, string file, int line) =>
        new(Symbol(id), null, 1, "members", CertaintyTier.Semantic, null, 0, 1)
        {
            EvidenceLocation = new InvestigationEvidenceLocation(
                file, line, null, line, null, InvestigationEvidenceLocationOrigin.SymbolDeclaration)
        };

    private static InvestigationCandidate Candidate(string id) =>
        new(Symbol(id), new IndexedEdge("root", id, EdgeKind.Calls, "file", 1), 1,
            "flow", CertaintyTier.Semantic, 1, 0, 1);

    private sealed class FixedProvider(
        InvestigationProviderKind kind,
        IReadOnlyList<InvestigationCandidate> candidates) : IInvestigationProvider
    {
        public InvestigationProviderKind Kind => kind;

        public Task<InvestigationProviderResult> CollectAsync(
            ICodeMapGraphReader reader,
            IndexedSymbol root,
            InvestigationOverrides overrides,
            CancellationToken cancellationToken,
            int offset = 0,
            int? window = null) =>
            Task.FromResult(new InvestigationProviderResult(
                candidates,
                ProviderCoverageStatus.Complete(kind, candidates.Count)));
    }

    private sealed class CountingAccessor : IFileTextAccessor
    {
        public int ReadCount { get; private set; }
        public string LargeText { get; init; } = string.Empty;
        public List<string> ReadPaths { get; } = [];

        public string? Read(string relativePath, int startLine, int endLine)
        {
            ReadCount++;
            ReadPaths.Add(relativePath);
            return relativePath == "a.cs" && LargeText.Length > 0
                ? LargeText
                : string.Join(Environment.NewLine, Enumerable.Range(startLine, endLine - startLine + 1).Select(line => $"line {line}"));
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
