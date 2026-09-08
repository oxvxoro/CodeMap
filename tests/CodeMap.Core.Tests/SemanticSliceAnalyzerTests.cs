using CodeMap.CSharp;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CodeMap.Core.Tests;

public sealed class SemanticSliceAnalyzerTests
{
    [Fact]
    public void BackwardSlice_LoopCarriedAssignmentTerminatesAndKeepsDefinition()
    {
        const string source = """
            class C { int Run(int input) { var value = input; while (value < 3) value = value + 1; return value; } }
            """;
        var (model, method) = CreateMethod(source);
        var analysis = new CSharpSemanticSliceAnalyzer().Analyze(model, method,
            new SemanticSliceRequest(SliceDirection.Backward), CancellationToken.None);

        Assert.Contains(analysis.Items, item => item.Display.Contains("value = value + 1", StringComparison.Ordinal));
        Assert.Contains(analysis.Items, item => item.Display.Contains("var value = input", StringComparison.Ordinal));
        Assert.False(analysis.Truncated);
    }

    [Fact]
    public void BackwardSlice_InvocationArgumentTracksLocalDefinitionsWithoutCalleeBody()
    {
        var (model, method) = CreateMethod("""
            class C { void Run(int input) { var value = input + 1; Send(Normalize(value)); } }
            """);
        var result = new CSharpSemanticSliceAnalyzer().Analyze(model, method,
            new SemanticSliceRequest(SliceDirection.Backward, Line: 1), CancellationToken.None);

        Assert.Contains(result.Items, item => item.Kind == SliceOperationKind.Invocation);
        Assert.Contains(result.Items, item => item.Display.Contains("var value = input + 1", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Items, item => item.Display.Contains("=> x", StringComparison.Ordinal));
    }
    [Fact]
    public void BackwardSlice_UsesOnlyTheReachingAssignment()
    {
        var (model, method) = CreateMethod("""
            class Sample
            {
                int Calculate(int input)
                {
                    var x = input;
                    x = 10;
                    return x;
                }
            }
            """);

        var result = new CSharpSemanticSliceAnalyzer().Analyze(model, method,
            new SemanticSliceRequest(SliceDirection.Backward), CancellationToken.None);

        Assert.Contains(result.Items, item => item.Display.Contains("x = 10", StringComparison.Ordinal));
        Assert.Contains(result.Items, item => item.Kind == SliceOperationKind.Return);
        Assert.DoesNotContain(result.Items, item => item.Display.Contains("var x = input", StringComparison.Ordinal));
    }

    [Fact]
    public void ForwardSlice_FollowsParameterThroughAssignmentsToReturn()
    {
        var (model, method) = CreateMethod("""
            class Sample
            {
                int Calculate(int input)
                {
                    var a = input + 1;
                    var b = a * 2;
                    return b;
                }
            }
            """);

        var result = new CSharpSemanticSliceAnalyzer().Analyze(model, method,
            new SemanticSliceRequest(SliceDirection.Forward), CancellationToken.None);

        Assert.Contains(result.Items, item => item.Kind == SliceOperationKind.Parameter && item.Symbol == "input");
        Assert.Contains(result.Items, item => item.Display.Contains("var a = input + 1", StringComparison.Ordinal));
        Assert.Contains(result.Items, item => item.Display.Contains("var b = a * 2", StringComparison.Ordinal));
        Assert.Contains(result.Items, item => item.Kind == SliceOperationKind.Return);
    }

    [Fact]
    public void LocalFunctionSlice_DoesNotInlineItsParentBody()
    {
        const string source = """
            class Sample
            {
                void Handle(string input)
                {
                    void Save(string value) { var copy = value; }
                    Save(input);
                }
            }
            """;
        var tree = CSharpSyntaxTree.ParseText(source, path: "Sample.cs");
        var compilation = CSharpCompilation.Create("Fixture", [tree], CSharpLanguageAnalyzer.CreateDefaultReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var model = compilation.GetSemanticModel(tree);
        var declaration = tree.GetRoot().DescendantNodes().OfType<LocalFunctionStatementSyntax>().Single();
        var method = (IMethodSymbol)model.GetDeclaredSymbol(declaration)!;

        var result = new CSharpSemanticSliceAnalyzer().Analyze(model, method,
            new SemanticSliceRequest(SliceDirection.Forward), CancellationToken.None);

        Assert.Contains(result.Items, item => item.Symbol == "value");
        Assert.Contains(result.Items, item => item.Display.Contains("var copy = value", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Items, item => item.Display.Contains("Save(input)", StringComparison.Ordinal));
    }

    [Fact]
    public void BackwardSlice_IncludesAlternativeBranchDefinitionAndCondition()
    {
        var (model, method) = CreateMethod("""
            class Sample
            {
                int Calculate(int input, bool flag)
                {
                    var x = input;
                    if (flag)
                        x = 10;
                    return x;
                }
            }
            """);

        var result = new CSharpSemanticSliceAnalyzer().Analyze(model, method,
            new SemanticSliceRequest(SliceDirection.Backward), CancellationToken.None);

        Assert.Contains(result.Items, item => item.Display.Contains("var x = input", StringComparison.Ordinal));
        Assert.Contains(result.Items, item => item.Display == "flag");
        Assert.Contains(result.Items, item => item.Display.Contains("x = 10", StringComparison.Ordinal));
    }

    private static (SemanticModel Model, IMethodSymbol Method) CreateMethod(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source, path: "Sample.cs");
        var compilation = CSharpCompilation.Create("Fixture", [tree], CSharpLanguageAnalyzer.CreateDefaultReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var model = compilation.GetSemanticModel(tree);
        var declaration = tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single();
        return (model, (IMethodSymbol)model.GetDeclaredSymbol(declaration)!);
    }
}
