using CodeMap.Core.Models;
using CodeMap.Engine.Application;
using CodeMap.Engine.Concurrency;
using CodeMap.Engine.Indexing;
using CodeMap.Storage.Migrations;
using Microsoft.Data.Sqlite;

namespace CodeMap.Core.Tests;

public sealed class EngineContractTests
{
    [Fact]
    public void ReadinessPolicy_AllowsStaleAndRejectsPartialStates()
    {
        Assert.True(IndexReadinessPolicy.Evaluate(IndexLifecycleState.Stale).CanQuery);
        Assert.True(IndexReadinessPolicy.Evaluate(IndexLifecycleState.Stale).Stale);
        Assert.False(IndexReadinessPolicy.Evaluate(IndexLifecycleState.Building).CanQuery);
        Assert.Equal("index_building", IndexReadinessPolicy.Evaluate(IndexLifecycleState.Building).Error!.Code);
        Assert.True(IndexReadinessPolicy.Evaluate(IndexLifecycleState.Updating, previousReadySnapshotAvailable: true).CanQuery);
    }

    [Fact]
    public void PropagationPlanner_UsesReverseGraphAndFingerprintSkip()
    {
        var graph = ProjectDependencyGraph.Create(new Dictionary<string, IReadOnlyCollection<string>>
        {
            ["A"] = [],
            ["B"] = ["A"],
            ["C"] = ["B"]
        });

        var unchanged = PublicSurfacePropagation.Plan(
            new HashSet<string>(["A"], StringComparer.Ordinal), graph,
            new Dictionary<string, string?> { ["A"] = "same" },
            new Dictionary<string, string?> { ["A"] = "same" });
        Assert.Equal(["A"], unchanged.ProjectsToAnalyze.OrderBy(value => value));
        Assert.Contains("B", unchanged.SkippedDependents.Keys);

        var changed = PublicSurfacePropagation.Plan(
            new HashSet<string>(["A"], StringComparer.Ordinal), graph,
            new Dictionary<string, string?> { ["A"] = "old" },
            new Dictionary<string, string?> { ["A"] = "new" });
        Assert.Equal(["A", "B", "C"], changed.ProjectsToAnalyze.OrderBy(value => value));
    }

    [Fact]
    public async Task KeyedLock_SerializesOnlyTheSameKey()
    {
        var keyed = new KeyedAsyncLock<string>();
        var first = await keyed.AcquireAsync("one");
        var blocked = keyed.AcquireAsync("one").AsTask();
        var independent = await keyed.AcquireAsync("two");
        Assert.False(blocked.IsCompleted);
        await independent.DisposeAsync();
        await first.DisposeAsync();
        await using var second = await blocked;
    }

    [Fact]
    public async Task MigrationLedger_IsIdempotent()
    {
        var path = Path.Combine(Path.GetTempPath(), "codemap-migration-" + Guid.NewGuid() + ".db");
        try
        {
            await using var connection = new SqliteConnection($"Data Source={path}");
            await connection.OpenAsync();
            var migrator = new CodeMapMigrator();
            await migrator.EnsureAsync(connection);
            await migrator.EnsureAsync(connection);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM codemap_migrations";
            Assert.Equal(1L, await command.ExecuteScalarAsync());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void DirtyProjectPlanner_ComposesChangesAndPropagation()
    {
        var graph = ProjectDependencyGraph.Create(new Dictionary<string, IReadOnlyCollection<string>>
        {
            ["A"] = [], ["B"] = ["A"], ["C"] = ["B"]
        });
        var changes = new RepositoryChangeSet(
            new HashSet<string>(["A"], StringComparer.Ordinal),
            new HashSet<string>(["Old"], StringComparer.Ordinal), 1, 0, 0);

        var plan = new DirtyProjectPlanner().Plan(changes, graph,
            new Dictionary<string, string?> { ["A"] = "old" },
            new Dictionary<string, string?> { ["A"] = "new" });

        Assert.Equal(["A", "B", "C"], plan.ProjectsToAnalyze.OrderBy(value => value));
        Assert.Equal(["Old"], plan.RemovedProjects);
    }

    [Fact]
    public async Task AnalysisCoordinator_IsDeterministicAndDoesNotPersist()
    {
        var seen = new List<string>();
        var coordinator = new AnalysisCoordinator((item, _) =>
        {
            seen.Add(item.ProjectName);
            return Task.FromResult(new AnalysisResult());
        });
        var files = Array.Empty<AnalyzedSourceFile>();
        var result = await coordinator.AnalyzeAsync([
            new AnalysisWorkItem("B", "b", files),
            new AnalysisWorkItem("A", "a", files)]);

        Assert.Equal(["A", "B"], seen);
        Assert.Equal(["A", "B"], result.Select(item => item.ProjectName));
    }

    [Fact]
    public void ExternalAssemblyPlanner_ReportsOrphansAfterNormalization()
    {
        var oldPath = Path.Combine(Path.GetTempPath(), "codemap-old.dll");
        var newPath = Path.Combine(Path.GetTempPath(), "codemap-new.dll");
        var plan = ExternalAssemblyPlanner.Plan([oldPath, newPath], [newPath]);
        Assert.Contains(Path.GetFullPath(newPath), plan.RequiredAssemblyPaths);
        Assert.Contains(Path.GetFullPath(oldPath), plan.OrphanedAssemblyPaths);
    }

    [Fact]
    public void ScipProviderCoordinator_ClassifiesChangesByNameAndHash()
    {
        var plan = ScipProviderCoordinator.Reconcile(
            [new ScipProviderArtifact("same", "same.scip", "1"), new ScipProviderArtifact("removed", "r.scip", "1")],
            [new ScipProviderArtifact("same", "same.scip", "1"), new ScipProviderArtifact("new", "n.scip", "1")]);
        Assert.Single(plan.Unchanged);
        Assert.Single(plan.Added);
        Assert.Single(plan.Removed);
    }

    [Fact]
    public async Task IndexingWorkflow_CommitsOnlyPlannedProjects()
    {
        IndexCommitRequest? committed = null;
        var workflow = new IndexingWorkflow(
            new DirtyProjectPlanner(),
            new AnalysisCoordinator((_, _) => Task.FromResult(new AnalysisResult())),
            new IndexCommitter((request, _) =>
            {
                committed = request;
                return Task.CompletedTask;
            }));
        var graph = ProjectDependencyGraph.Create(new Dictionary<string, IReadOnlyCollection<string>> { ["A"] = [], ["B"] = [] });
        var changes = new RepositoryChangeSet(new HashSet<string>(["A"], StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal), 0, 1, 0);
        var files = Array.Empty<AnalyzedSourceFile>();

        var result = await workflow.RunAsync(changes, graph, new Dictionary<string, string?>(), new Dictionary<string, string?>(),
            [new AnalysisWorkItem("A", "a", files), new AnalysisWorkItem("B", "b", files)]);

        Assert.Single(result.AnalyzedProjects);
        Assert.NotNull(committed);
        Assert.Single(committed!.Projects);
        Assert.Contains(committed.Projects, project => project.ProjectName == "A");
    }
}
