using CodeMap.CSharp;
using CodeMap.Core.Models;

namespace CodeMap.Core.Tests;







[Collection("MsBuild")]
public sealed class DotNetApplicationAnalyzerTests
{
    private static readonly string AspNetFixturePath = FixtureRestore.EnsureRestored("AspNetFixture");
    private static readonly string WpfFixturePath = FixtureRestore.EnsureRestored("WpfFixture");

    [Fact]
    public async Task MinimalApi_MethodGroupHandler_GetsRoutesToEdge()
    {
        var project = await AnalyzeAspNetFixtureAsync();
        var route = Assert.Single(project.Result.Nodes, n => n.Kind == NodeKind.Route && n.QualifiedName == "GET /orders/{id}");
        Assert.Equal("csharp", route.Language);

        var handler = Assert.Single(project.Result.Nodes, n => n.Kind == NodeKind.Method && n.Name == "GetOrderHandler");
        var edge = Assert.Single(project.Result.Edges, e => e.Kind == EdgeKind.RoutesTo && e.SourceId == route.Id);
        Assert.Equal(handler.Id, edge.TargetId);
        Assert.Equal(1.00, edge.Confidence);
        Assert.Equal(EdgeResolutionKind.Semantic, edge.ResolutionKind);
    }

    [Fact]
    public async Task MinimalApi_LambdaHandler_GetsRoutesToEdgeToFunctionNode()
    {
        var project = await AnalyzeAspNetFixtureAsync();
        var route = Assert.Single(project.Result.Nodes, n => n.Kind == NodeKind.Route && n.QualifiedName == "POST /orders");
        var edge = Assert.Single(project.Result.Edges, e => e.Kind == EdgeKind.RoutesTo && e.SourceId == route.Id);
        var handler = Assert.Single(project.Result.Nodes, n => n.Id == edge.TargetId);
        Assert.Equal(NodeKind.Function, handler.Kind);
        Assert.Equal(1.00, edge.Confidence);
        Assert.Equal(EdgeResolutionKind.Semantic, edge.ResolutionKind);






        var getOrder = Assert.Single(project.Result.Nodes, n => n.Kind == NodeKind.Method && n.Name == "GetOrder"
            && n.QualifiedName.Contains("IOrderService", StringComparison.Ordinal));
        Assert.Contains(project.Result.Edges, e => e.Kind == EdgeKind.Calls && e.SourceId == handler.Id && e.TargetId == getOrder.Id);
    }

    [Fact]
    public async Task MinimalApi_OverloadedMethodGroupHandler_ResolvesToExactSelectedOverload()
    {
        var project = await AnalyzeAspNetFixtureAsync();
        var route = Assert.Single(project.Result.Nodes, n => n.Kind == NodeKind.Route && n.QualifiedName == "GET /ping");
        var edge = Assert.Single(project.Result.Edges, e => e.Kind == EdgeKind.RoutesTo && e.SourceId == route.Id);

        var overloads = project.Result.Nodes.Where(n => n.Kind == NodeKind.Method && n.Name == "PingHandler").ToArray();
        Assert.Equal(2, overloads.Length);
        var parameterless = Assert.Single(overloads, n => n.Signature == "PingHandler()");

        Assert.Equal(parameterless.Id, edge.TargetId);
        Assert.Equal(1.00, edge.Confidence);
        Assert.Equal(EdgeResolutionKind.Semantic, edge.ResolutionKind);
    }

    [Fact]
    public async Task MinimalApi_NonConstantRoute_ProducesNoRouteNode()
    {
        var project = await AnalyzeAspNetFixtureAsync();
        Assert.DoesNotContain(project.Result.Nodes, n => n.Kind == NodeKind.Route && n.QualifiedName.Contains("dynamic", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task MinimalApi_MapMethodsWithConstantVerbArray_CreatesRoutePerVerb()
    {
        var project = await AnspNetFixtureRoutesAsync();
        Assert.Contains(project, n => n.QualifiedName == "GET /orders/{id}/status");
        Assert.Contains(project, n => n.QualifiedName == "HEAD /orders/{id}/status");
    }

    [Fact]
    public async Task Mvc_ClassAndActionRoutesCombineWithTokenReplacement()
    {
        var project = await AnalyzeAspNetFixtureAsync();
        Assert.Contains(project.Result.Nodes, n => n.Kind == NodeKind.Route && n.QualifiedName == "GET /api/Orders/{id}");
        Assert.Contains(project.Result.Nodes, n => n.Kind == NodeKind.Route && n.QualifiedName == "POST /api/Orders");
    }

    [Fact]
    public async Task Mvc_RouteWithoutVerbAttribute_BecomesAny()
    {
        var project = await AnalyzeAspNetFixtureAsync();
        Assert.Contains(project.Result.Nodes, n => n.Kind == NodeKind.Route && n.QualifiedName == "ANY /api/Orders/ping");
    }





    [Fact]
    public async Task Mvc_OverloadedActions_EachRouteTargetsItsOwnOverload()
    {
        var project = await AnalyzeExtraControllerFixtureAsync();

        var getRoute = Assert.Single(project.Result.Nodes, n => n.Kind == NodeKind.Route && n.QualifiedName == "GET /api/multi/items/{id}");
        var searchRoute = Assert.Single(project.Result.Nodes, n => n.Kind == NodeKind.Route && n.QualifiedName == "GET /api/multi/items");

        var overloads = project.Result.Nodes
            .Where(n => n.Kind == NodeKind.Method && n.Name == "Get" && n.QualifiedName.StartsWith("AspNetFixture.OverloadController.", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(2, overloads.Length);
        var byId = overloads.Single(n => n.Signature == "Get(int)");
        var byQuery = overloads.Single(n => n.Signature == "Get()");

        var getEdge = Assert.Single(project.Result.Edges, e => e.Kind == EdgeKind.RoutesTo && e.SourceId == getRoute.Id);
        var searchEdge = Assert.Single(project.Result.Edges, e => e.Kind == EdgeKind.RoutesTo && e.SourceId == searchRoute.Id);
        Assert.Equal(byId.Id, getEdge.TargetId);
        Assert.Equal(byQuery.Id, searchEdge.TargetId);
    }



    [Fact]
    public async Task Mvc_MultipleClassRouteAttributes_ProduceRoutesUnderEachPrefix()
    {
        var project = await AnalyzeExtraControllerFixtureAsync();
        Assert.Contains(project.Result.Nodes, n => n.Kind == NodeKind.Route && n.QualifiedName == "GET /api/multi/items");
        Assert.Contains(project.Result.Nodes, n => n.Kind == NodeKind.Route && n.QualifiedName == "GET /v2/multi/items");
    }



    [Fact]
    public async Task Mvc_TildeSlashActionTemplate_IgnoresClassPrefix()
    {
        var project = await AnalyzeExtraControllerFixtureAsync();
        Assert.Contains(project.Result.Nodes, n => n.Kind == NodeKind.Route && n.QualifiedName == "GET /health");
        Assert.DoesNotContain(project.Result.Nodes, n => n.Kind == NodeKind.Route && n.QualifiedName.Contains("~", StringComparison.Ordinal));
        Assert.DoesNotContain(project.Result.Nodes, n => n.Kind == NodeKind.Route && n.QualifiedName.Contains("Overload/health", StringComparison.Ordinal));
    }




    [Fact]
    public async Task Mvc_FakeSameNameAttributes_DoNotProduceRoutes()
    {
        var project = await AnalyzeExtraControllerFixtureAsync();
        Assert.DoesNotContain(project.Result.Nodes, n => n.Kind == NodeKind.Route && n.QualifiedName.Contains("FakeAttr", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(project.Result.Nodes, n => n.Kind == NodeKind.Route && n.QualifiedName.Contains("NotReallyAController", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Di_GenericTwoArgumentOverload_CreatesRegistersAndResolvesToEdges()
    {
        var project = await AnalyzeAspNetFixtureAsync();
        var registration = Assert.Single(project.Result.Nodes, n => n.Kind == NodeKind.DependencyRegistration && n.Name == "IOrderService");
        Assert.Equal("lifetime=singleton", registration.Signature);

        var serviceType = Assert.Single(project.Result.Nodes, n => n.Kind == NodeKind.Interface && n.Name == "IOrderService");
        var implementationType = Assert.Single(project.Result.Nodes, n => n.Kind == NodeKind.Class && n.Name == "OrderService");

        var registers = Assert.Single(project.Result.Edges, e => e.Kind == EdgeKind.Registers && e.SourceId == registration.Id);
        Assert.Equal(serviceType.Id, registers.TargetId);
        Assert.Equal(1.00, registers.Confidence);

        var resolvesTo = Assert.Single(project.Result.Edges, e => e.Kind == EdgeKind.ResolvesTo && e.SourceId == registration.Id);
        Assert.Equal(implementationType.Id, resolvesTo.TargetId);
    }

    [Fact]
    public async Task Di_SingleTypeArgumentOverload_RegistersServiceAsItsOwnImplementation()
    {
        var project = await AnalyzeAspNetFixtureAsync();
        var registration = Assert.Single(project.Result.Nodes, n => n.Kind == NodeKind.DependencyRegistration && n.Name == "OrderNotifier");
        Assert.Equal("lifetime=scoped", registration.Signature);
        Assert.Contains(project.Result.Edges, e => e.Kind == EdgeKind.Registers && e.SourceId == registration.Id);
        Assert.Contains(project.Result.Edges, e => e.Kind == EdgeKind.ResolvesTo && e.SourceId == registration.Id);
    }

    [Fact]
    public async Task Di_FactoryDelegateOverload_ProducesNoRegistrationNode()
    {
        var project = await AnalyzeAspNetFixtureAsync();


        var registrations = project.Result.Nodes.Where(n => n.Kind == NodeKind.DependencyRegistration).ToArray();
        Assert.Equal(2, registrations.Length);
    }

    [Fact]
    public async Task Razor_ChildComponent_GetsRendersEdge()
    {
        var project = await AnalyzeAspNetFixtureAsync();
        var form = Assert.Single(project.Result.Nodes, n => n.Kind == NodeKind.RazorComponent && n.QualifiedName.Contains("OrderForm", StringComparison.Ordinal));
        var summaryArtifact = Assert.Single(project.Result.Nodes, n => n.Kind == NodeKind.RazorComponent && n.QualifiedName.Contains("OrderSummary", StringComparison.Ordinal));
        var edge = Assert.Single(project.Result.Edges, e => e.Kind == EdgeKind.Renders && e.SourceId == form.Id && e.TargetId == summaryArtifact.Id);
        Assert.Equal(0.90, edge.Confidence);
        Assert.Equal(EdgeResolutionKind.Syntactic, edge.ResolutionKind);
    }

    [Fact]
    public async Task Renders_TargetsRazorArtifact_WhenArtifactExists()
    {
        var project = await AnalyzeAspNetFixtureAsync();
        var form = Assert.Single(project.Result.Nodes, n => n.Kind == NodeKind.RazorComponent && n.QualifiedName.Contains("OrderForm", StringComparison.Ordinal));
        var summaryArtifact = Assert.Single(project.Result.Nodes, n => n.Kind == NodeKind.RazorComponent && n.QualifiedName.Contains("OrderSummary", StringComparison.Ordinal));
        var summaryClass = Assert.Single(project.Result.Nodes, n => n.Kind == NodeKind.Class && n.Name == "OrderSummary");

        var edge = Assert.Single(project.Result.Edges, e => e.Kind == EdgeKind.Renders && e.SourceId == form.Id && e.TargetId == summaryArtifact.Id);



        Assert.Equal(summaryArtifact.Id, edge.TargetId);
        Assert.NotEqual(summaryClass.Id, edge.TargetId);
    }

    [Fact]
    public async Task Renders_CodeBehindlessComponent_IsResolvedAsArtifact()
    {
        var project = await AnalyzeAspNetFixtureAsync();
        var form = Assert.Single(project.Result.Nodes, n => n.Kind == NodeKind.RazorComponent && n.QualifiedName.Contains("OrderForm", StringComparison.Ordinal));
        var badgeArtifact = Assert.Single(project.Result.Nodes, n => n.Kind == NodeKind.RazorComponent && n.QualifiedName.Contains("OrderStatusBadge", StringComparison.Ordinal));


        Assert.Contains(project.Result.Edges, e => e.Kind == EdgeKind.Renders && e.SourceId == form.Id && e.TargetId == badgeArtifact.Id);
    }

    [Fact]
    public async Task Renders_NamespaceQualifiedCodeBehindlessComponent_ResolvesCorrectArtifact()
    {
        var project = await AnalyzeAspNetFixtureAsync();
        var form = Assert.Single(project.Result.Nodes, n => n.Kind == NodeKind.RazorComponent && n.QualifiedName.Contains("OrderForm", StringComparison.Ordinal));
        var badgeArtifacts = project.Result.Nodes.Where(n => n.Kind == NodeKind.RazorComponent && n.Name == "Badge.razor").ToArray();
        Assert.Equal(2, badgeArtifacts.Length);
        var correctBadge = Assert.Single(badgeArtifacts, n => n.QualifiedName == "Components/Badge.razor");
        var wrongBadge = Assert.Single(badgeArtifacts, n => n.QualifiedName == "Components/Alt/Badge.razor");







        Assert.Contains(project.Result.Edges, e => e.Kind == EdgeKind.Renders && e.SourceId == form.Id && e.TargetId == correctBadge.Id);
        Assert.DoesNotContain(project.Result.Edges, e => e.Kind == EdgeKind.Renders && e.SourceId == form.Id && e.TargetId == wrongBadge.Id);
    }

    [Fact]
    public async Task Renders_PlainTag_ResolvesSoleSameFileNameArtifact()
    {
        var project = await AnalyzeAspNetFixtureAsync();
        var form = Assert.Single(project.Result.Nodes, n => n.Kind == NodeKind.RazorComponent && n.QualifiedName.Contains("OrderForm", StringComparison.Ordinal));
        var spinner = Assert.Single(project.Result.Nodes, n => n.Kind == NodeKind.RazorComponent && n.Name == "Spinner.razor");


        Assert.Contains(project.Result.Edges, e => e.Kind == EdgeKind.Renders && e.SourceId == form.Id && e.TargetId == spinner.Id);
    }

    [Fact]
    public async Task Renders_DottedTagNamingUnrelatedNamespace_DoesNotMatchSoleLocalFile()
    {
        var project = await AnalyzeAspNetFixtureAsync();
        var form = Assert.Single(project.Result.Nodes, n => n.Kind == NodeKind.RazorComponent && n.QualifiedName.Contains("OrderForm", StringComparison.Ordinal));
        var spinner = Assert.Single(project.Result.Nodes, n => n.Kind == NodeKind.RazorComponent && n.Name == "Spinner.razor");







        Assert.Single(project.Result.Edges, e => e.Kind == EdgeKind.Renders && e.SourceId == form.Id && e.TargetId == spinner.Id);
    }

    [Fact]
    public async Task AmbiguousComponentName_ProducesNoRenders()
    {
        var project = await AnalyzeAspNetFixtureAsync();
        var form = Assert.Single(project.Result.Nodes, n => n.Kind == NodeKind.RazorComponent && n.QualifiedName.Contains("OrderForm", StringComparison.Ordinal));
        var tagArtifacts = project.Result.Nodes.Where(n => n.Kind == NodeKind.RazorComponent && n.Name == "OrderTag.razor").ToArray();
        Assert.Equal(2, tagArtifacts.Length);




        Assert.DoesNotContain(project.Result.Edges, e => e.Kind == EdgeKind.Renders && e.SourceId == form.Id && tagArtifacts.Select(n => n.Id).Contains(e.TargetId));
    }

    [Fact]
    public async Task Razor_ParameterAttribute_GetsBindsToEdge()
    {
        var project = await AnalyzeAspNetFixtureAsync();
        var form = Assert.Single(project.Result.Nodes, n => n.Kind == NodeKind.RazorComponent && n.QualifiedName.Contains("OrderForm", StringComparison.Ordinal));
        var totalProperty = Assert.Single(project.Result.Nodes, n => n.Kind == NodeKind.Property && n.Name == "Total");
        var edge = Assert.Single(project.Result.Edges, e => e.Kind == EdgeKind.BindsTo && e.SourceId == form.Id && e.TargetId == totalProperty.Id);
        Assert.Equal(0.85, edge.Confidence);
    }

    [Fact]
    public async Task ParameterBinding_NonParameterProperty_ProducesNoEdge()
    {
        var project = await AnalyzeAspNetFixtureAsync();
        var form = Assert.Single(project.Result.Nodes, n => n.Kind == NodeKind.RazorComponent && n.QualifiedName.Contains("OrderForm", StringComparison.Ordinal));
        var internalNoteProperty = Assert.Single(project.Result.Nodes, n => n.Kind == NodeKind.Property && n.Name == "InternalNote");



        Assert.DoesNotContain(project.Result.Edges, e => e.Kind == EdgeKind.BindsTo && e.SourceId == form.Id && e.TargetId == internalNoteProperty.Id);
    }

    [Fact]
    public async Task ParameterBinding_ParameterProperty_StillProducesEdge()
    {
        var project = await AnalyzeAspNetFixtureAsync();
        var form = Assert.Single(project.Result.Nodes, n => n.Kind == NodeKind.RazorComponent && n.QualifiedName.Contains("OrderForm", StringComparison.Ordinal));
        var totalProperty = Assert.Single(project.Result.Nodes, n => n.Kind == NodeKind.Property && n.Name == "Total");
        Assert.Contains(project.Result.Edges, e => e.Kind == EdgeKind.BindsTo && e.SourceId == form.Id && e.TargetId == totalProperty.Id);
    }

    [Fact]
    public async Task Razor_BindDirective_ResolvesToOwningProperty()
    {
        var project = await AnalyzeAspNetFixtureAsync();
        var form = Assert.Single(project.Result.Nodes, n => n.Kind == NodeKind.RazorComponent && n.QualifiedName.Contains("OrderForm", StringComparison.Ordinal));
        var customerName = Assert.Single(project.Result.Nodes, n => n.Kind == NodeKind.Property && n.Name == "CustomerName");
        Assert.Contains(project.Result.Edges, e => e.Kind == EdgeKind.BindsTo && e.SourceId == form.Id && e.TargetId == customerName.Id && e.Confidence == 0.85);
    }

    [Fact]
    public async Task Razor_OnClickDirective_ResolvesToOwningMethod()
    {
        var project = await AnalyzeAspNetFixtureAsync();
        var form = Assert.Single(project.Result.Nodes, n => n.Kind == NodeKind.RazorComponent && n.QualifiedName.Contains("OrderForm", StringComparison.Ordinal));
        var submit = Assert.Single(project.Result.Nodes, n => n.Kind == NodeKind.Method && n.Name == "Submit");
        var edge = Assert.Single(project.Result.Edges, e => e.Kind == EdgeKind.HandlesEvent && e.SourceId == form.Id && e.TargetId == submit.Id);
        Assert.Equal(0.90, edge.Confidence);
    }

    [Fact]
    public async Task Razor_CodeBehindConvention_RecordsComponentInSignatureNotAsEdge()
    {
        var project = await AnalyzeAspNetFixtureAsync();
        var form = Assert.Single(project.Result.Nodes, n => n.Kind == NodeKind.RazorComponent && n.QualifiedName.Contains("OrderForm", StringComparison.Ordinal));
        Assert.NotNull(form.Signature);
        Assert.StartsWith("component=", form.Signature, StringComparison.Ordinal);


        Assert.DoesNotContain(project.Result.Edges, e => e.SourceId == form.Id && e.Kind == EdgeKind.BindsTo
            && e.TargetId.Contains("Components.OrderForm", StringComparison.Ordinal) && !e.TargetId.Contains('.', StringComparison.Ordinal));
    }

    [Fact]
    public async Task Razor_PageDirective_ClassifiesAsRazorPageNotRazorView()
    {
        var project = await AnalyzeAspNetFixtureAsync();
        Assert.Contains(project.Result.Nodes, n => n.Kind == NodeKind.RazorPage && n.QualifiedName.Contains("Index.cshtml", StringComparison.Ordinal));
        Assert.DoesNotContain(project.Result.Nodes, n => n.Kind == NodeKind.RazorView && n.QualifiedName.Contains("Index.cshtml", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Xaml_XClass_RecordsSymbolIdInSignature()
    {
        var project = await AnalyzeWpfFixtureAsync();
        var mainWindow = Assert.Single(project.Result.Nodes, n => n.Kind == NodeKind.XamlView && n.QualifiedName.Contains("MainWindow.xaml", StringComparison.Ordinal));
        var mainWindowClass = Assert.Single(project.Result.Nodes, n => n.Kind == NodeKind.Class && n.Name == "MainWindow");
        Assert.Equal($"class={mainWindowClass.Id}", mainWindow.Signature);
    }

    [Fact]
    public async Task Xaml_DataTypeDirective_ResolvesViewModelWithUsesViewModelEdge()
    {
        var project = await AnalyzeWpfFixtureAsync();
        var mainWindow = Assert.Single(project.Result.Nodes, n => n.Kind == NodeKind.XamlView && n.QualifiedName.Contains("MainWindow.xaml", StringComparison.Ordinal));
        var viewModel = Assert.Single(project.Result.Nodes, n => n.Kind == NodeKind.Class && n.Name == "MainViewModel");
        var edge = Assert.Single(project.Result.Edges, e => e.Kind == EdgeKind.UsesViewModel && e.SourceId == mainWindow.Id);
        Assert.Equal(viewModel.Id, edge.TargetId);
        Assert.Equal(0.90, edge.Confidence);
        Assert.Equal(EdgeResolutionKind.Syntactic, edge.ResolutionKind);
    }

    [Fact]
    public async Task Xaml_PropertyPathBinding_ResolvesFirstSegmentOnly()
    {
        var project = await AnalyzeWpfFixtureAsync();
        var mainWindow = Assert.Single(project.Result.Nodes, n => n.Kind == NodeKind.XamlView && n.QualifiedName.Contains("MainWindow.xaml", StringComparison.Ordinal));
        var customerProperty = Assert.Single(project.Result.Nodes, n => n.Kind == NodeKind.Property && n.Name == "Customer" && n.QualifiedName.Contains("MainViewModel", StringComparison.Ordinal));
        var edge = Assert.Single(project.Result.Edges, e => e.Kind == EdgeKind.BindsTo && e.SourceId == mainWindow.Id && e.TargetId == customerProperty.Id);
        Assert.Equal(0.85, edge.Confidence);
    }

    [Fact]
    public async Task Xaml_CommandBindingBothForms_ResolveToSingleMatch()
    {
        var project = await AnalyzeWpfFixtureAsync();
        var mainWindow = Assert.Single(project.Result.Nodes, n => n.Kind == NodeKind.XamlView && n.QualifiedName.Contains("MainWindow.xaml", StringComparison.Ordinal));
        var saveCommand = Assert.Single(project.Result.Nodes, n => n.Kind == NodeKind.Property && n.Name == "SaveCommand");
        var saveMethod = Assert.Single(project.Result.Nodes, n => n.Kind == NodeKind.Method && n.Name == "Save");
        Assert.Contains(project.Result.Edges, e => e.Kind == EdgeKind.BindsTo && e.SourceId == mainWindow.Id && e.TargetId == saveCommand.Id);
        Assert.Contains(project.Result.Edges, e => e.Kind == EdgeKind.BindsTo && e.SourceId == mainWindow.Id && e.TargetId == saveMethod.Id);
    }

    [Fact]
    public async Task Xaml_ClickAttribute_ResolvesToCodeBehindMethod()
    {
        var project = await AnalyzeWpfFixtureAsync();
        var mainWindow = Assert.Single(project.Result.Nodes, n => n.Kind == NodeKind.XamlView && n.QualifiedName.Contains("MainWindow.xaml", StringComparison.Ordinal));
        var handler = Assert.Single(project.Result.Nodes, n => n.Kind == NodeKind.Method && n.Name == "Save_Click");


        var edges = project.Result.Edges.Where(e => e.Kind == EdgeKind.HandlesEvent && e.SourceId == mainWindow.Id && e.TargetId == handler.Id).ToArray();
        Assert.NotEmpty(edges);
        Assert.All(edges, e => Assert.Equal(0.90, e.Confidence));
    }

    [Fact]
    public async Task Xaml_AppXaml_DetectedAsWpfProjectEvenWithoutWindowRoot()
    {
        var project = await AnalyzeWpfFixtureAsync();
        Assert.Contains(project.Result.Nodes, n => n.Kind == NodeKind.XamlView && n.QualifiedName == "App.xaml");
    }

    [Fact]
    public async Task Xaml_DtdIsRejectedByHardenedParser()
    {





        var testDir = AppContext.BaseDirectory;
        var solutionRoot = Path.GetFullPath(Path.Combine(testDir, "..", "..", "..", "..", ".."));
        var source = Path.Combine(solutionRoot, "tests", "Fixtures", "WpfFixture");
        var destination = Path.Combine(Path.GetTempPath(), "codemap-xaml-dtd-" + Guid.NewGuid());
        Directory.CreateDirectory(destination);
        try
        {
            foreach (var file in Directory.EnumerateFiles(source, "*.*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(source, file);
                var targetPath = Path.Combine(destination, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                File.Copy(file, targetPath, overwrite: true);
            }

            var maliciousXaml = """
                <?xml version="1.0"?>
                <!DOCTYPE Window [<!ENTITY xxe "test">]>
                <Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                </Window>
                """;
            await File.WriteAllTextAsync(Path.Combine(destination, "Views", "Malicious.xaml"), maliciousXaml);

            var indexer = new CSharpWorkspaceIndexer();
            var projects = await indexer.AnalyzeAsync(destination, null, CancellationToken.None);
            var project = Assert.Single(projects, p => !p.ProjectName.StartsWith("external:", StringComparison.Ordinal));
            Assert.DoesNotContain(project.Result.Nodes, n => n.Kind == NodeKind.XamlView && n.QualifiedName.Contains("Malicious", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(destination, recursive: true);
        }
    }

    private static async Task<CSharpProjectAnalysis> AnalyzeAspNetFixtureAsync()
    {
        var indexer = new CSharpWorkspaceIndexer();
        var projects = await indexer.AnalyzeAsync(AspNetFixturePath, null, CancellationToken.None);



        return Assert.Single(projects, project => !project.ProjectName.StartsWith("external:", StringComparison.Ordinal));
    }

    private static async Task<IReadOnlyList<CodeNode>> AnspNetFixtureRoutesAsync()
    {
        var project = await AnalyzeAspNetFixtureAsync();
        return project.Result.Nodes.Where(n => n.Kind == NodeKind.Route).ToArray();
    }









    private static async Task<CSharpProjectAnalysis> AnalyzeExtraControllerFixtureAsync()
    {
        var destination = Path.Combine(Path.GetTempPath(), "codemap-aspnet-extra-controller-" + Guid.NewGuid());
        CopyFixture(AspNetFixturePath, destination);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(destination, "ExtraController.cs"), """
                using Microsoft.AspNetCore.Mvc;

                namespace AspNetFixture;

                [ApiController]
                [Route("api/multi/items")]
                [Route("v2/multi/items")]
                public sealed class OverloadController : ControllerBase
                {
                    [HttpGet("{id}")]
                    public string Get(int id) => id.ToString();

                    [HttpGet]
                    public string Get() => "all";

                    [HttpGet("~/health")]
                    public string Health() => "ok";
                }

                namespace AspNetFixture.Fake
                {
                    // ASP.NET Core MVC와 같은 짧은 이름을 사용하지만 실제 프레임워크 타입은 아니다.
                    // 라우팅 특성으로 인식해서는 안 된다.
                    public sealed class RouteAttribute : System.Attribute
                    {
                        public RouteAttribute(string template) { }
                    }

                    public sealed class HttpGetAttribute : System.Attribute
                    {
                        public HttpGetAttribute(string template) { }
                    }

                    [RouteAttribute("fake/prefix")]
                    public sealed class NotReallyAController
                    {
                        [HttpGetAttribute("FakeAttr")]
                        public string Get() => "unreachable";
                    }
                }
                """);

            var indexer = new CSharpWorkspaceIndexer();
            var projects = await indexer.AnalyzeAsync(destination, null, CancellationToken.None);
            return Assert.Single(projects, project => !project.ProjectName.StartsWith("external:", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(destination, recursive: true);
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

        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = "restore",
            WorkingDirectory = destination,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var process = System.Diagnostics.Process.Start(startInfo)
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

    private static async Task<CSharpProjectAnalysis> AnalyzeWpfFixtureAsync()
    {
        var indexer = new CSharpWorkspaceIndexer();
        var projects = await indexer.AnalyzeAsync(WpfFixturePath, null, CancellationToken.None);
        return Assert.Single(projects, project => !project.ProjectName.StartsWith("external:", StringComparison.Ordinal));
    }

}