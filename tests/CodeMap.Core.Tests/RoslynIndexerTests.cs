using CodeMap.CSharp;
using CodeMap.Core.Analysis;
using CodeMap.Core.Models;

namespace CodeMap.Core.Tests;

public sealed class RoslynIndexerTests
{
    [Fact]
    public void ResolveInput_DirectoryFindsUniqueNestedSolution()
    {
        var root = Path.Combine(Path.GetTempPath(), "codemap-resolve-input-" + Guid.NewGuid());
        var solutionPath = Path.Combine(root, "nested", "Nested.slnx");
        Directory.CreateDirectory(Path.GetDirectoryName(solutionPath)!);
        File.WriteAllText(solutionPath, "<Solution />");
        try
        {
            Assert.Equal(solutionPath, CSharpWorkspaceIndexer.ResolveInput(root));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SemanticCompilation_ResolvesCallsAndImplementations()
    {
        var analyzer = new CSharpLanguageAnalyzer();
        var sources = new Dictionary<string, string>
        {
            ["ITestService.cs"] = "namespace Fixture; public interface ITestService { System.Threading.Tasks.Task RunAsync(); }",
            ["Repository.cs"] = "namespace Fixture; public class Repository { public void Save() { } }",
            ["TestService.cs"] = "namespace Fixture; public class TestService : ITestService { private readonly Repository _repository = new(); public System.Threading.Tasks.Task RunAsync() { _repository.Save(); return System.Threading.Tasks.Task.CompletedTask; } }",
            ["Controller.cs"] = "namespace Fixture; public class Controller { private readonly ITestService _service; public Controller(ITestService service) { _service = service; } public System.Threading.Tasks.Task Handle() => _service.RunAsync(); }"
        };

        var result = await analyzer.AnalyzeAsync(new AnalysisContext
        {
            ProjectName = "Fixture",
            FilePath = "Controller.cs",
            Content = sources["Controller.cs"],
            SourceFiles = sources,
            RootDirectory = Directory.GetCurrentDirectory()
        }, CancellationToken.None);

        var controllerHandle = result.Nodes.Single(node => node.Name == "Handle");
        var interfaceRun = result.Nodes.Single(node => node.Name == "RunAsync" && node.QualifiedName.Contains("ITestService", StringComparison.Ordinal));
        var serviceRun = result.Nodes.Single(node => node.Name == "RunAsync" && node.QualifiedName.Contains("Fixture.TestService.", StringComparison.Ordinal));
        var repositorySave = result.Nodes.Single(node => node.Name == "Save");

        Assert.Contains(result.Edges, edge => edge.Kind == EdgeKind.Calls && edge.SourceId == controllerHandle.Id && edge.TargetId == interfaceRun.Id);
        Assert.Contains(result.Edges, edge => edge.Kind == EdgeKind.Calls && edge.SourceId == serviceRun.Id && edge.TargetId == repositorySave.Id);
        Assert.Contains(result.Edges, edge => edge.Kind == EdgeKind.ImplementedBy && edge.SourceId == interfaceRun.Id && edge.TargetId == serviceRun.Id);
        Assert.Contains(result.Edges, edge => edge.Kind == EdgeKind.Implements && edge.SourceId.Contains("TestService", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NamedLocalFunction_IsRegisteredAndAttributesCallsToItself()
    {
        var analyzer = new CSharpLanguageAnalyzer();
        const string source = """
            namespace Fixture;
            public class Repository { public void Save() { } }
            public class Handler
            {
                private readonly Repository repository = new();
                public void Handle()
                {
                    SaveLocal();
                    void SaveLocal() => repository.Save();
                }
            }
            """;

        var result = await analyzer.AnalyzeAsync(new AnalysisContext
        {
            ProjectName = "Fixture",
            FilePath = "Handler.cs",
            Content = source,
            RootDirectory = Directory.GetCurrentDirectory()
        }, CancellationToken.None);

        var handle = Assert.Single(result.Nodes, n => n.Name == "Handle" && n.Kind == NodeKind.Method);
        var saveLocal = Assert.Single(result.Nodes, n => n.Name == "SaveLocal");
        var repositorySave = Assert.Single(result.Nodes, n => n.Name == "Save");

        Assert.Equal(NodeKind.Function, saveLocal.Kind);
        Assert.Contains(result.Edges, e => e.Kind == EdgeKind.Contains && e.SourceId == handle.Id && e.TargetId == saveLocal.Id);
        Assert.Contains(result.Edges, e => e.Kind == EdgeKind.Calls && e.SourceId == handle.Id && e.TargetId == saveLocal.Id);
        Assert.Contains(result.Edges, e => e.Kind == EdgeKind.Calls && e.SourceId == saveLocal.Id && e.TargetId == repositorySave.Id);
        Assert.DoesNotContain(result.Edges, e => e.Kind == EdgeKind.Calls && e.SourceId == handle.Id && e.TargetId == repositorySave.Id);
    }

    [Fact]
    public async Task LambdaBody_IsRegisteredAndAttributesCallsToItself()
    {
        var analyzer = new CSharpLanguageAnalyzer();
        const string source = """
            namespace Fixture;
            public class Repository { public void Save() { } }
            public class Handler
            {
                private readonly Repository repository = new();
                public void Handle()
                {
                    System.Action action = () => repository.Save();
                }
            }
            """;

        var result = await analyzer.AnalyzeAsync(new AnalysisContext
        {
            ProjectName = "Fixture",
            FilePath = "Handler.cs",
            Content = source,
            RootDirectory = Directory.GetCurrentDirectory()
        }, CancellationToken.None);

        var handle = Assert.Single(result.Nodes, n => n.Name == "Handle" && n.Kind == NodeKind.Method);
        var repositorySave = Assert.Single(result.Nodes, n => n.Name == "Save");
        var lambda = Assert.Single(result.Nodes, n => n.Kind == NodeKind.Function && n.QualifiedName.Contains("<lambda>", StringComparison.Ordinal));

        Assert.Contains(result.Edges, e => e.Kind == EdgeKind.Contains && e.SourceId == handle.Id && e.TargetId == lambda.Id);
        Assert.Contains(result.Edges, e => e.Kind == EdgeKind.Calls && e.SourceId == lambda.Id && e.TargetId == repositorySave.Id);
        Assert.DoesNotContain(result.Edges, e => e.Kind == EdgeKind.Calls && e.SourceId == handle.Id && e.TargetId == repositorySave.Id);
    }

    [Fact]
    public async Task LocalFunctionsWithSameName_InDifferentMethods_ResolveToDistinctNodes()
    {
        var analyzer = new CSharpLanguageAnalyzer();
        const string source = """
            namespace Fixture;
            public class Handler
            {
                public void HandleA()
                {
                    Validate();
                    void Validate() { }
                }
                public void HandleB()
                {
                    Validate();
                    void Validate() { }
                }
            }
            """;

        var result = await analyzer.AnalyzeAsync(new AnalysisContext
        {
            ProjectName = "Fixture",
            FilePath = "Handler.cs",
            Content = source,
            RootDirectory = Directory.GetCurrentDirectory()
        }, CancellationToken.None);

        var handleA = Assert.Single(result.Nodes, n => n.Name == "HandleA");
        var handleB = Assert.Single(result.Nodes, n => n.Name == "HandleB");
        var validateNodes = result.Nodes.Where(n => n.Name == "Validate").ToArray();

        Assert.Equal(2, validateNodes.Length);
        Assert.NotEqual(validateNodes[0].Id, validateNodes[1].Id);
        Assert.NotEqual(validateNodes[0].QualifiedName, validateNodes[1].QualifiedName);

        foreach (var validate in validateNodes)
            Assert.Contains(result.Edges, e => e.Kind == EdgeKind.Calls && e.TargetId == validate.Id
                && (e.SourceId == handleA.Id || e.SourceId == handleB.Id));

        Assert.Contains(result.Edges, e => e.Kind == EdgeKind.Calls && e.SourceId == handleA.Id && e.TargetId == validateNodes.Single(n => n.QualifiedName.Contains("HandleA", StringComparison.Ordinal)).Id);
        Assert.Contains(result.Edges, e => e.Kind == EdgeKind.Calls && e.SourceId == handleB.Id && e.TargetId == validateNodes.Single(n => n.QualifiedName.Contains("HandleB", StringComparison.Ordinal)).Id);
    }

    [Fact]
    public async Task LocalFunctionsWithSameName_InOverloadedParentMethods_ResolveToDistinctNodes()
    {
        var analyzer = new CSharpLanguageAnalyzer();
        const string source = """
            namespace Fixture;
            public class Handler
            {
                public void Handle(int id)
                {
                    Validate();
                    void Validate() { }
                }
                public void Handle(string id)
                {
                    Validate();
                    void Validate() { }
                }
            }
            """;

        var result = await analyzer.AnalyzeAsync(new AnalysisContext
        {
            ProjectName = "Fixture",
            FilePath = "Handler.cs",
            Content = source,
            RootDirectory = Directory.GetCurrentDirectory()
        }, CancellationToken.None);

        var handleInt = Assert.Single(result.Nodes, n => n.Name == "Handle" && n.Signature == "Handle(int)");
        var handleString = Assert.Single(result.Nodes, n => n.Name == "Handle" && n.Signature == "Handle(string)");
        var validateNodes = result.Nodes.Where(n => n.Name == "Validate").ToArray();

        Assert.Equal(2, validateNodes.Length);
        Assert.NotEqual(validateNodes[0].Id, validateNodes[1].Id);
        Assert.NotEqual(validateNodes[0].QualifiedName, validateNodes[1].QualifiedName);

        Assert.Contains(result.Edges, e => e.Kind == EdgeKind.Calls && e.SourceId == handleInt.Id
            && e.TargetId == validateNodes.Single(n => n.QualifiedName.Contains("(int)", StringComparison.Ordinal)).Id);
        Assert.Contains(result.Edges, e => e.Kind == EdgeKind.Calls && e.SourceId == handleString.Id
            && e.TargetId == validateNodes.Single(n => n.QualifiedName.Contains("(string)", StringComparison.Ordinal)).Id);
    }








    [Fact]
    public async Task MergedRelationScan_HandlesAllSyntaxCategoriesInOneMethod()
    {
        var analyzer = new CSharpLanguageAnalyzer();
        const string source = """
            using System;
            namespace Fixture;

            [AttributeUsage(AttributeTargets.Method)]
            public class MarkerAttribute : Attribute { }

            public class Repository
            {
                public Repository() { }
                public void Save() { }
            }

            public class Container<T> { }

            public class Handler
            {
                [Marker]
                public void Handle()
                {
                    var repository = new Repository();
                    repository.Save();
                    repository.Save();
                    Container<Repository> container = new();
                    var type = typeof(Repository);
                    Action action = () => repository.Save();
                }
            }
            """;

        var result = await analyzer.AnalyzeAsync(new AnalysisContext
        {
            ProjectName = "Fixture",
            FilePath = "Handler.cs",
            Content = source,
            RootDirectory = Directory.GetCurrentDirectory()
        }, CancellationToken.None);

        var handle = Assert.Single(result.Nodes, n => n.Name == "Handle" && n.Kind == NodeKind.Method);
        var repositoryType = Assert.Single(result.Nodes, n => n.Name == "Repository");
        var repositoryCtor = Assert.Single(result.Nodes, n => n.Kind == NodeKind.Constructor && n.QualifiedName.Contains("Repository", StringComparison.Ordinal));
        var save = Assert.Single(result.Nodes, n => n.Name == "Save");
        var containerType = Assert.Single(result.Nodes, n => n.Name == "Container");
        var lambda = Assert.Single(result.Nodes, n => n.Kind == NodeKind.Function && n.QualifiedName.Contains("<lambda>", StringComparison.Ordinal));



        Assert.Contains(result.Edges, e => e.Kind == EdgeKind.Constructs && e.SourceId == handle.Id && e.TargetId == repositoryCtor.Id);
        Assert.Contains(result.Edges, e => e.Kind == EdgeKind.References && e.SourceId == handle.Id && e.TargetId == repositoryType.Id);



        var directCalls = result.Edges.Where(e => e.Kind == EdgeKind.Calls && e.SourceId == handle.Id && e.TargetId == save.Id).ToArray();
        Assert.Equal(2, directCalls.Length);
        Assert.NotEqual(directCalls[0].SourceLocation!.StartLine, directCalls[1].SourceLocation!.StartLine);




        Assert.Contains(result.Edges, e => e.Kind == EdgeKind.References && e.SourceId == handle.Id && e.TargetId == containerType.Id);





        var repositoryReferences = result.Edges
            .Where(e => e.Kind == EdgeKind.References && e.SourceId == handle.Id && e.TargetId == repositoryType.Id)
            .ToArray();
        Assert.True(repositoryReferences.Length >= 3,
            $"Expected at least 3 References edges to Repository (new/generic-arg/typeof), found {repositoryReferences.Length}.");
        Assert.Equal(repositoryReferences.Length, repositoryReferences.Select(e => (e.SourceLocation!.StartLine, e.SourceLocation!.StartColumn)).Distinct().Count());








        var markerType = Assert.Single(result.Nodes, n => n.Name == "MarkerAttribute");
        Assert.Contains(result.Edges, e => e.Kind == EdgeKind.References && e.SourceId == markerType.Id && e.TargetId.Contains("AttributeTargets", StringComparison.Ordinal));



        Assert.Contains(result.Edges, e => e.Kind == EdgeKind.Calls && e.SourceId == lambda.Id && e.TargetId == save.Id);
        Assert.Equal(3, result.Edges.Count(e => e.Kind == EdgeKind.Calls && e.TargetId == save.Id));
    }
}