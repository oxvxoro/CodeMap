using CodeMap.Storage;
using Microsoft.Data.Sqlite;

namespace CodeMap.Core.Tests;












[Collection("MsBuild")]
public sealed class PublicSurfaceFingerprintTests
{
    [Fact]
    public async Task UpdateAsync_ImplementationOnlyBodyChange_DoesNotExpandToDependentProject()
    {
        var workingDirectory = CopyFixtureToTempDirectory();
        try
        {
            var indexer = new IncrementalCodeMapIndexer();
            await indexer.IndexAsync(workingDirectory, force: true, CancellationToken.None);



            var greeterPath = Path.Combine(workingDirectory, "ProjB", "Greeter.cs");
            await File.WriteAllTextAsync(greeterPath, """
                namespace Fixture.ProjB;

                public interface IGreeter
                {
                    string GetGreeting();
                }

                public class Greeter : IGreeter
                {
                    public string Greet() => GetGreeting();

                    public string GetGreeting() => "hello there";
                }

                public class AlternateGreeter : IGreeter
                {
                    public string GetGreeting() => "alternate";
                }
                """);

            var updated = await indexer.UpdateAsync(workingDirectory, CancellationToken.None);
            Assert.Contains("ProjB", updated.AnalyzedProjects);
            Assert.DoesNotContain("ProjA", updated.AnalyzedProjects);

            var databasePath = Path.Combine(workingDirectory, ".codemap", "index.db");
            var graph = await new CodeMapQueryStore(databasePath).LoadAsync();
            var getGreeting = Assert.Single(graph.Symbols, s => s.QualifiedName == "Fixture.ProjB.Greeter.GetGreeting");
            Assert.NotNull(getGreeting);
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }

    [Fact]
    public async Task UpdateAsync_PrivateMemberAdded_DoesNotExpandToDependentProject()
    {
        var workingDirectory = CopyFixtureToTempDirectory();
        try
        {
            var indexer = new IncrementalCodeMapIndexer();
            await indexer.IndexAsync(workingDirectory, force: true, CancellationToken.None);


            var greeterPath = Path.Combine(workingDirectory, "ProjB", "Greeter.cs");
            await File.WriteAllTextAsync(greeterPath, """
                namespace Fixture.ProjB;

                public interface IGreeter
                {
                    string GetGreeting();
                }

                public class Greeter : IGreeter
                {
                    private readonly string _prefix = "hi: ";

                    public string Greet() => GetGreeting();

                    public string GetGreeting() => "hi";

                    private string BuildPrefixed(string value) => _prefix + value;
                }

                public class AlternateGreeter : IGreeter
                {
                    public string GetGreeting() => "alternate";
                }
                """);

            var updated = await indexer.UpdateAsync(workingDirectory, CancellationToken.None);
            Assert.Contains("ProjB", updated.AnalyzedProjects);
            Assert.DoesNotContain("ProjA", updated.AnalyzedProjects);
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }

    [Fact]
    public async Task UpdateAsync_PublicMemberAdded_ExpandsToDependentProject()
    {
        var workingDirectory = CopyFixtureToTempDirectory();
        try
        {
            var indexer = new IncrementalCodeMapIndexer();
            await indexer.IndexAsync(workingDirectory, force: true, CancellationToken.None);



            var greeterPath = Path.Combine(workingDirectory, "ProjB", "Greeter.cs");
            await File.WriteAllTextAsync(greeterPath, """
                namespace Fixture.ProjB;

                public interface IGreeter
                {
                    string GetGreeting();
                }

                public class Greeter : IGreeter
                {
                    public string Greet() => GetGreeting();

                    public string GetGreeting() => "hi";

                    public string Farewell() => "bye";
                }

                public class AlternateGreeter : IGreeter
                {
                    public string GetGreeting() => "alternate";
                }
                """);

            var updated = await indexer.UpdateAsync(workingDirectory, CancellationToken.None);
            Assert.Contains("ProjB", updated.AnalyzedProjects);
            Assert.Contains("ProjA", updated.AnalyzedProjects);
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }

    [Fact]
    public async Task UpdateAsync_PublicMethodSignatureChange_ExpandsToDependentProject()
    {
        var workingDirectory = CopyFixtureToTempDirectory();
        try
        {
            var indexer = new IncrementalCodeMapIndexer();
            await indexer.IndexAsync(workingDirectory, force: true, CancellationToken.None);



            var greeterPath = Path.Combine(workingDirectory, "ProjB", "Greeter.cs");
            await File.WriteAllTextAsync(greeterPath, """
                namespace Fixture.ProjB;

                public interface IGreeter
                {
                    string GetGreeting();
                }

                public class Greeter : IGreeter
                {
                    public string Greet(string suffix = "") => GetGreeting() + suffix;

                    public string GetGreeting() => "hi";
                }

                public class AlternateGreeter : IGreeter
                {
                    public string GetGreeting() => "alternate";
                }
                """);

            var updated = await indexer.UpdateAsync(workingDirectory, CancellationToken.None);
            Assert.Contains("ProjB", updated.AnalyzedProjects);
            Assert.Contains("ProjA", updated.AnalyzedProjects);
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }








    [Fact]
    public async Task UpdateAsync_PublicMethodReturnTypeChange_ExpandsToDependentProject()
    {
        var workingDirectory = CopyFixtureToTempDirectory();
        try
        {
            var indexer = new IncrementalCodeMapIndexer();
            await indexer.IndexAsync(workingDirectory, force: true, CancellationToken.None);

            var greeterPath = Path.Combine(workingDirectory, "ProjB", "Greeter.cs");
            await File.WriteAllTextAsync(greeterPath, """
                namespace Fixture.ProjB;

                public interface IGreeter
                {
                    string GetGreeting();
                }

                public class Greeter : IGreeter
                {
                    public string Greet() => GetGreeting();

                    public string GetGreeting() => "hi";

                    public int Rank() => 1;
                }

                public class AlternateGreeter : IGreeter
                {
                    public string GetGreeting() => "alternate";
                }
                """);
            await indexer.UpdateAsync(workingDirectory, CancellationToken.None);


            await File.WriteAllTextAsync(greeterPath, """
                namespace Fixture.ProjB;

                public interface IGreeter
                {
                    string GetGreeting();
                }

                public class Greeter : IGreeter
                {
                    public string Greet() => GetGreeting();

                    public string GetGreeting() => "hi";

                    public string Rank() => "first";
                }

                public class AlternateGreeter : IGreeter
                {
                    public string GetGreeting() => "alternate";
                }
                """);

            var updated = await indexer.UpdateAsync(workingDirectory, CancellationToken.None);
            Assert.Contains("ProjB", updated.AnalyzedProjects);
            Assert.Contains("ProjA", updated.AnalyzedProjects);
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }






    [Fact]
    public async Task UpdateAsync_PublicPropertyTypeChange_ExpandsToDependentProject()
    {
        var workingDirectory = CopyFixtureToTempDirectory();
        try
        {
            var indexer = new IncrementalCodeMapIndexer();
            await indexer.IndexAsync(workingDirectory, force: true, CancellationToken.None);

            var greeterPath = Path.Combine(workingDirectory, "ProjB", "Greeter.cs");
            await File.WriteAllTextAsync(greeterPath, """
                namespace Fixture.ProjB;

                public interface IGreeter
                {
                    string GetGreeting();
                }

                public class Greeter : IGreeter
                {
                    public string Greet() => GetGreeting();

                    public string GetGreeting() => "hi";

                    public int Score { get; set; }
                }

                public class AlternateGreeter : IGreeter
                {
                    public string GetGreeting() => "alternate";
                }
                """);
            await indexer.UpdateAsync(workingDirectory, CancellationToken.None);


            await File.WriteAllTextAsync(greeterPath, """
                namespace Fixture.ProjB;

                public interface IGreeter
                {
                    string GetGreeting();
                }

                public class Greeter : IGreeter
                {
                    public string Greet() => GetGreeting();

                    public string GetGreeting() => "hi";

                    public double Score { get; set; }
                }

                public class AlternateGreeter : IGreeter
                {
                    public string GetGreeting() => "alternate";
                }
                """);

            var updated = await indexer.UpdateAsync(workingDirectory, CancellationToken.None);
            Assert.Contains("ProjB", updated.AnalyzedProjects);
            Assert.Contains("ProjA", updated.AnalyzedProjects);
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }







    [Fact]
    public async Task UpdateAsync_PublicToProtectedAccessibilityChange_ExpandsToDependentProject()
    {
        var workingDirectory = CopyFixtureToTempDirectory();
        try
        {
            var indexer = new IncrementalCodeMapIndexer();
            await indexer.IndexAsync(workingDirectory, force: true, CancellationToken.None);

            var greeterPath = Path.Combine(workingDirectory, "ProjB", "Greeter.cs");
            await File.WriteAllTextAsync(greeterPath, """
                namespace Fixture.ProjB;

                public interface IGreeter
                {
                    string GetGreeting();
                }

                public class Greeter : IGreeter
                {
                    public string Greet() => GetGreeting();

                    public string GetGreeting() => "hi";

                    public string Rank() => "first";
                }

                public class AlternateGreeter : IGreeter
                {
                    public string GetGreeting() => "alternate";
                }
                """);
            await indexer.UpdateAsync(workingDirectory, CancellationToken.None);



            await File.WriteAllTextAsync(greeterPath, """
                namespace Fixture.ProjB;

                public interface IGreeter
                {
                    string GetGreeting();
                }

                public class Greeter : IGreeter
                {
                    public string Greet() => GetGreeting();

                    public string GetGreeting() => "hi";

                    protected string Rank() => "first";
                }

                public class AlternateGreeter : IGreeter
                {
                    public string GetGreeting() => "alternate";
                }
                """);

            var updated = await indexer.UpdateAsync(workingDirectory, CancellationToken.None);
            Assert.Contains("ProjB", updated.AnalyzedProjects);
            Assert.Contains("ProjA", updated.AnalyzedProjects);
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }








    [Fact]
    public async Task UpdateAsync_PublicTypeGenericConstraintChange_ExpandsToDependentProject()
    {
        var workingDirectory = CopyFixtureToTempDirectory();
        try
        {
            var indexer = new IncrementalCodeMapIndexer();
            await indexer.IndexAsync(workingDirectory, force: true, CancellationToken.None);

            var greeterPath = Path.Combine(workingDirectory, "ProjB", "Greeter.cs");
            await File.WriteAllTextAsync(greeterPath, """
                namespace Fixture.ProjB;

                public interface IGreeter
                {
                    string GetGreeting();
                }

                public class Greeter : IGreeter
                {
                    public string Greet() => GetGreeting();

                    public string GetGreeting() => "hi";
                }

                public class AlternateGreeter : IGreeter
                {
                    public string GetGreeting() => "alternate";
                }

                public class Box<T> where T : class
                {
                }
                """);
            await indexer.UpdateAsync(workingDirectory, CancellationToken.None);


            await File.WriteAllTextAsync(greeterPath, """
                namespace Fixture.ProjB;

                public interface IGreeter
                {
                    string GetGreeting();
                }

                public class Greeter : IGreeter
                {
                    public string Greet() => GetGreeting();

                    public string GetGreeting() => "hi";
                }

                public class AlternateGreeter : IGreeter
                {
                    public string GetGreeting() => "alternate";
                }

                public class Box<T> where T : struct
                {
                }
                """);

            var updated = await indexer.UpdateAsync(workingDirectory, CancellationToken.None);
            Assert.Contains("ProjB", updated.AnalyzedProjects);
            Assert.Contains("ProjA", updated.AnalyzedProjects);
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }







    [Fact]
    public async Task UpdateAsync_PublicMethodParameterRefModifierChange_ExpandsToDependentProject()
    {
        var workingDirectory = CopyFixtureToTempDirectory();
        try
        {
            var indexer = new IncrementalCodeMapIndexer();
            await indexer.IndexAsync(workingDirectory, force: true, CancellationToken.None);

            var greeterPath = Path.Combine(workingDirectory, "ProjB", "Greeter.cs");
            await File.WriteAllTextAsync(greeterPath, """
                namespace Fixture.ProjB;

                public interface IGreeter
                {
                    string GetGreeting();
                }

                public class Greeter : IGreeter
                {
                    public string Greet() => GetGreeting();

                    public string GetGreeting() => "hi";

                    public void Apply(int value) { }
                }

                public class AlternateGreeter : IGreeter
                {
                    public string GetGreeting() => "alternate";
                }
                """);
            await indexer.UpdateAsync(workingDirectory, CancellationToken.None);



            await File.WriteAllTextAsync(greeterPath, """
                namespace Fixture.ProjB;

                public interface IGreeter
                {
                    string GetGreeting();
                }

                public class Greeter : IGreeter
                {
                    public string Greet() => GetGreeting();

                    public string GetGreeting() => "hi";

                    public void Apply(ref int value) { }
                }

                public class AlternateGreeter : IGreeter
                {
                    public string GetGreeting() => "alternate";
                }
                """);

            var updated = await indexer.UpdateAsync(workingDirectory, CancellationToken.None);
            Assert.Contains("ProjB", updated.AnalyzedProjects);
            Assert.Contains("ProjA", updated.AnalyzedProjects);
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }








    [Fact]
    public async Task UpdateAsync_PublicPropertySetterAccessibilityChange_ExpandsToDependentProject()
    {
        var workingDirectory = CopyFixtureToTempDirectory();
        try
        {
            var indexer = new IncrementalCodeMapIndexer();
            await indexer.IndexAsync(workingDirectory, force: true, CancellationToken.None);

            var greeterPath = Path.Combine(workingDirectory, "ProjB", "Greeter.cs");
            await File.WriteAllTextAsync(greeterPath, """
                namespace Fixture.ProjB;

                public interface IGreeter
                {
                    string GetGreeting();
                }

                public class Greeter : IGreeter
                {
                    public string Greet() => GetGreeting();

                    public string GetGreeting() => "hi";

                    public int Score { get; set; }
                }

                public class AlternateGreeter : IGreeter
                {
                    public string GetGreeting() => "alternate";
                }
                """);
            await indexer.UpdateAsync(workingDirectory, CancellationToken.None);



            await File.WriteAllTextAsync(greeterPath, """
                namespace Fixture.ProjB;

                public interface IGreeter
                {
                    string GetGreeting();
                }

                public class Greeter : IGreeter
                {
                    public string Greet() => GetGreeting();

                    public string GetGreeting() => "hi";

                    public int Score { get; private set; }
                }

                public class AlternateGreeter : IGreeter
                {
                    public string GetGreeting() => "alternate";
                }
                """);

            var updated = await indexer.UpdateAsync(workingDirectory, CancellationToken.None);
            Assert.Contains("ProjB", updated.AnalyzedProjects);
            Assert.Contains("ProjA", updated.AnalyzedProjects);
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }







    [Fact]
    public async Task UpdateAsync_PublicDelegateInvokeSignatureChange_ExpandsToDependentProject()
    {
        var workingDirectory = CopyFixtureToTempDirectory();
        try
        {
            var indexer = new IncrementalCodeMapIndexer();
            await indexer.IndexAsync(workingDirectory, force: true, CancellationToken.None);

            var greeterPath = Path.Combine(workingDirectory, "ProjB", "Greeter.cs");
            await File.WriteAllTextAsync(greeterPath, """
                namespace Fixture.ProjB;

                public interface IGreeter
                {
                    string GetGreeting();
                }

                public class Greeter : IGreeter
                {
                    public string Greet() => GetGreeting();

                    public string GetGreeting() => "hi";
                }

                public class AlternateGreeter : IGreeter
                {
                    public string GetGreeting() => "alternate";
                }

                public delegate void Handler(int value);
                """);
            await indexer.UpdateAsync(workingDirectory, CancellationToken.None);


            await File.WriteAllTextAsync(greeterPath, """
                namespace Fixture.ProjB;

                public interface IGreeter
                {
                    string GetGreeting();
                }

                public class Greeter : IGreeter
                {
                    public string Greet() => GetGreeting();

                    public string GetGreeting() => "hi";
                }

                public class AlternateGreeter : IGreeter
                {
                    public string GetGreeting() => "alternate";
                }

                public delegate void Handler(string value);
                """);

            var updated = await indexer.UpdateAsync(workingDirectory, CancellationToken.None);
            Assert.Contains("ProjB", updated.AnalyzedProjects);
            Assert.Contains("ProjA", updated.AnalyzedProjects);
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }







    [Fact]
    public async Task UpdateAsync_PublicEnumUnderlyingTypeChange_ExpandsToDependentProject()
    {
        var workingDirectory = CopyFixtureToTempDirectory();
        try
        {
            var indexer = new IncrementalCodeMapIndexer();
            await indexer.IndexAsync(workingDirectory, force: true, CancellationToken.None);

            var greeterPath = Path.Combine(workingDirectory, "ProjB", "Greeter.cs");
            await File.WriteAllTextAsync(greeterPath, """
                namespace Fixture.ProjB;

                public interface IGreeter
                {
                    string GetGreeting();
                }

                public class Greeter : IGreeter
                {
                    public string Greet() => GetGreeting();

                    public string GetGreeting() => "hi";
                }

                public class AlternateGreeter : IGreeter
                {
                    public string GetGreeting() => "alternate";
                }

                public enum Level : byte
                {
                    Low,
                    High
                }
                """);
            await indexer.UpdateAsync(workingDirectory, CancellationToken.None);


            await File.WriteAllTextAsync(greeterPath, """
                namespace Fixture.ProjB;

                public interface IGreeter
                {
                    string GetGreeting();
                }

                public class Greeter : IGreeter
                {
                    public string Greet() => GetGreeting();

                    public string GetGreeting() => "hi";
                }

                public class AlternateGreeter : IGreeter
                {
                    public string GetGreeting() => "alternate";
                }

                public enum Level : int
                {
                    Low,
                    High
                }
                """);

            var updated = await indexer.UpdateAsync(workingDirectory, CancellationToken.None);
            Assert.Contains("ProjB", updated.AnalyzedProjects);
            Assert.Contains("ProjA", updated.AnalyzedProjects);
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }







    [Fact]
    public async Task UpdateAsync_PublicMethodStaticModifierChange_ExpandsToDependentProject()
    {
        var workingDirectory = CopyFixtureToTempDirectory();
        try
        {
            var indexer = new IncrementalCodeMapIndexer();
            await indexer.IndexAsync(workingDirectory, force: true, CancellationToken.None);

            var greeterPath = Path.Combine(workingDirectory, "ProjB", "Greeter.cs");
            await File.WriteAllTextAsync(greeterPath, """
                namespace Fixture.ProjB;

                public interface IGreeter
                {
                    string GetGreeting();
                }

                public class Greeter : IGreeter
                {
                    public string Greet() => GetGreeting();

                    public string GetGreeting() => "hi";

                    public string Rank() => "first";
                }

                public class AlternateGreeter : IGreeter
                {
                    public string GetGreeting() => "alternate";
                }
                """);
            await indexer.UpdateAsync(workingDirectory, CancellationToken.None);



            await File.WriteAllTextAsync(greeterPath, """
                namespace Fixture.ProjB;

                public interface IGreeter
                {
                    string GetGreeting();
                }

                public class Greeter : IGreeter
                {
                    public string Greet() => GetGreeting();

                    public string GetGreeting() => "hi";

                    public static string Rank() => "first";
                }

                public class AlternateGreeter : IGreeter
                {
                    public string GetGreeting() => "alternate";
                }
                """);

            var updated = await indexer.UpdateAsync(workingDirectory, CancellationToken.None);
            Assert.Contains("ProjB", updated.AnalyzedProjects);
            Assert.Contains("ProjA", updated.AnalyzedProjects);
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }








    [Fact]
    public async Task UpdateAsync_PublicGenericInterfaceVarianceChange_ExpandsToDependentProject()
    {
        var workingDirectory = CopyFixtureToTempDirectory();
        try
        {
            var indexer = new IncrementalCodeMapIndexer();
            await indexer.IndexAsync(workingDirectory, force: true, CancellationToken.None);

            var greeterPath = Path.Combine(workingDirectory, "ProjB", "Greeter.cs");
            await File.WriteAllTextAsync(greeterPath, """
                namespace Fixture.ProjB;

                public interface IGreeter
                {
                    string GetGreeting();
                }

                public class Greeter : IGreeter
                {
                    public string Greet() => GetGreeting();

                    public string GetGreeting() => "hi";
                }

                public class AlternateGreeter : IGreeter
                {
                    public string GetGreeting() => "alternate";
                }

                public interface IProducer<out T>
                {
                    T Produce();
                }
                """);
            await indexer.UpdateAsync(workingDirectory, CancellationToken.None);


            await File.WriteAllTextAsync(greeterPath, """
                namespace Fixture.ProjB;

                public interface IGreeter
                {
                    string GetGreeting();
                }

                public class Greeter : IGreeter
                {
                    public string Greet() => GetGreeting();

                    public string GetGreeting() => "hi";
                }

                public class AlternateGreeter : IGreeter
                {
                    public string GetGreeting() => "alternate";
                }

                public interface IProducer<T>
                {
                    T Produce();
                }
                """);

            var updated = await indexer.UpdateAsync(workingDirectory, CancellationToken.None);
            Assert.Contains("ProjB", updated.AnalyzedProjects);
            Assert.Contains("ProjA", updated.AnalyzedProjects);
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }







    [Fact]
    public async Task UpdateAsync_PublicGenericDelegateVarianceChange_ExpandsToDependentProject()
    {
        var workingDirectory = CopyFixtureToTempDirectory();
        try
        {
            var indexer = new IncrementalCodeMapIndexer();
            await indexer.IndexAsync(workingDirectory, force: true, CancellationToken.None);

            var greeterPath = Path.Combine(workingDirectory, "ProjB", "Greeter.cs");
            await File.WriteAllTextAsync(greeterPath, """
                namespace Fixture.ProjB;

                public interface IGreeter
                {
                    string GetGreeting();
                }

                public class Greeter : IGreeter
                {
                    public string Greet() => GetGreeting();

                    public string GetGreeting() => "hi";
                }

                public class AlternateGreeter : IGreeter
                {
                    public string GetGreeting() => "alternate";
                }

                public delegate T Factory<out T>();
                """);
            await indexer.UpdateAsync(workingDirectory, CancellationToken.None);


            await File.WriteAllTextAsync(greeterPath, """
                namespace Fixture.ProjB;

                public interface IGreeter
                {
                    string GetGreeting();
                }

                public class Greeter : IGreeter
                {
                    public string Greet() => GetGreeting();

                    public string GetGreeting() => "hi";
                }

                public class AlternateGreeter : IGreeter
                {
                    public string GetGreeting() => "alternate";
                }

                public delegate T Factory<T>();
                """);

            var updated = await indexer.UpdateAsync(workingDirectory, CancellationToken.None);
            Assert.Contains("ProjB", updated.AnalyzedProjects);
            Assert.Contains("ProjA", updated.AnalyzedProjects);
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }

    [Fact]
    public async Task UpdateAsync_BaseTypeInterfaceChange_ExpandsToDependentProject()
    {
        var workingDirectory = CopyFixtureToTempDirectory();
        try
        {
            var indexer = new IncrementalCodeMapIndexer();
            await indexer.IndexAsync(workingDirectory, force: true, CancellationToken.None);



            var greeterPath = Path.Combine(workingDirectory, "ProjB", "Greeter.cs");
            await File.WriteAllTextAsync(greeterPath, """
                namespace Fixture.ProjB;

                public interface IGreeter
                {
                    string GetGreeting();
                }

                public class Greeter
                {
                    public string Greet() => GetGreeting();

                    public string GetGreeting() => "hi";
                }

                public class AlternateGreeter : IGreeter
                {
                    public string GetGreeting() => "alternate";
                }
                """);

            var updated = await indexer.UpdateAsync(workingDirectory, CancellationToken.None);
            Assert.Contains("ProjB", updated.AnalyzedProjects);
            Assert.Contains("ProjA", updated.AnalyzedProjects);
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }

    [Fact]
    public async Task UpdateAsync_ProjectReferenceChange_ConservativelyInvalidatesDependents()
    {
        var workingDirectory = CopyFixtureToTempDirectory();
        try
        {
            var indexer = new IncrementalCodeMapIndexer();
            await indexer.IndexAsync(workingDirectory, force: true, CancellationToken.None);




            var projAPath = Path.Combine(workingDirectory, "ProjA", "ProjA.csproj");
            var original = await File.ReadAllTextAsync(projAPath);
            await File.WriteAllTextAsync(projAPath, original.Replace("<Nullable>enable</Nullable>", "<Nullable>enable</Nullable><LangVersion>latest</LangVersion>"));

            var updated = await indexer.UpdateAsync(workingDirectory, CancellationToken.None);
            Assert.Contains("ProjA", updated.AnalyzedProjects);
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }







    [Fact]
    public async Task UpdateAsync_MissingStoredFingerprint_FallsBackToExpandingDependents()
    {
        var workingDirectory = CopyFixtureToTempDirectory();
        try
        {
            var indexer = new IncrementalCodeMapIndexer();
            await indexer.IndexAsync(workingDirectory, force: true, CancellationToken.None);

            var statePath = Path.Combine(workingDirectory, ".codemap", "state.json");
            var stateJson = await File.ReadAllTextAsync(statePath);
            using (var document = System.Text.Json.JsonDocument.Parse(stateJson))
            {
                Assert.Contains("publicSurfaceFingerprint", stateJson, StringComparison.OrdinalIgnoreCase);
            }

            var strippedJson = System.Text.RegularExpressions.Regex.Replace(
                stateJson, "\"publicSurfaceFingerprint\"\\s*:\\s*\"[^\"]*\",?\\s*", "");
            await File.WriteAllTextAsync(statePath, strippedJson);

            var greeterPath = Path.Combine(workingDirectory, "ProjB", "Greeter.cs");
            await File.WriteAllTextAsync(greeterPath, """
                namespace Fixture.ProjB;

                public interface IGreeter
                {
                    string GetGreeting();
                }

                public class Greeter : IGreeter
                {
                    public string Greet() => GetGreeting();

                    public string GetGreeting() => "hi there, changed body only";
                }

                public class AlternateGreeter : IGreeter
                {
                    public string GetGreeting() => "alternate";
                }
                """);

            var updated = await indexer.UpdateAsync(workingDirectory, CancellationToken.None);
            Assert.Contains("ProjB", updated.AnalyzedProjects);
            Assert.Contains("ProjA", updated.AnalyzedProjects);
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }






    [Fact]
    public async Task UpdateAsync_AfterExpandingToDependendent_CrossProjectEdgesRemainCorrect()
    {
        var workingDirectory = CopyFixtureToTempDirectory();
        try
        {
            var indexer = new IncrementalCodeMapIndexer();
            await indexer.IndexAsync(workingDirectory, force: true, CancellationToken.None);

            var greeterPath = Path.Combine(workingDirectory, "ProjB", "Greeter.cs");
            await File.WriteAllTextAsync(greeterPath, """
                namespace Fixture.ProjB;

                public interface IGreeter
                {
                    string GetGreeting();
                }

                public class Greeter : IGreeter
                {
                    public string Greet() => GetGreeting();

                    public string GetGreeting() => "hi";

                    public string Farewell() => "bye";
                }

                public class AlternateGreeter : IGreeter
                {
                    public string GetGreeting() => "alternate";
                }
                """);

            await indexer.UpdateAsync(workingDirectory, CancellationToken.None);

            var databasePath = Path.Combine(workingDirectory, ".codemap", "index.db");
            var graph = await new CodeMapQueryStore(databasePath).LoadAsync();
            var service = new CodeMapQueryService(graph);
            var greet = Assert.Single(service.Find("Greet", maxResults: 10));
            Assert.Contains(service.ReferencedBy(greet, maxResults: 10), symbol => symbol.Name == "Call");
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }








    [Fact]
    public async Task UpdateAsync_AttributeAddedToPublicMethod_ExpandsToDependentProject()
    {
        var workingDirectory = CopyFixtureToTempDirectory();
        try
        {
            var indexer = new IncrementalCodeMapIndexer();
            await indexer.IndexAsync(workingDirectory, force: true, CancellationToken.None);

            var greeterPath = Path.Combine(workingDirectory, "ProjB", "Greeter.cs");
            await File.WriteAllTextAsync(greeterPath, """
                namespace Fixture.ProjB;

                public interface IGreeter
                {
                    string GetGreeting();
                }

                public class Greeter : IGreeter
                {
                    public string Greet() => GetGreeting();

                    [System.Obsolete("use GetGreeting2 instead")]
                    public string GetGreeting() => "hi";
                }

                public class AlternateGreeter : IGreeter
                {
                    public string GetGreeting() => "alternate";
                }
                """);

            var updated = await indexer.UpdateAsync(workingDirectory, CancellationToken.None);
            Assert.Contains("ProjB", updated.AnalyzedProjects);
            Assert.Contains("ProjA", updated.AnalyzedProjects);
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }






    [Fact]
    public async Task UpdateAsync_AttributeConstructorArgumentChanged_ExpandsToDependentProject()
    {
        var workingDirectory = CopyFixtureToTempDirectory();
        try
        {
            var indexer = new IncrementalCodeMapIndexer();
            var initialGreeterPath = Path.Combine(workingDirectory, "ProjB", "Greeter.cs");
            await File.WriteAllTextAsync(initialGreeterPath, """
                namespace Fixture.ProjB;

                public interface IGreeter
                {
                    string GetGreeting();
                }

                public class Greeter : IGreeter
                {
                    public string Greet() => GetGreeting();

                    [System.Obsolete("v1")]
                    public string GetGreeting() => "hi";
                }

                public class AlternateGreeter : IGreeter
                {
                    public string GetGreeting() => "alternate";
                }
                """);
            await indexer.IndexAsync(workingDirectory, force: true, CancellationToken.None);

            await File.WriteAllTextAsync(initialGreeterPath, """
                namespace Fixture.ProjB;

                public interface IGreeter
                {
                    string GetGreeting();
                }

                public class Greeter : IGreeter
                {
                    public string Greet() => GetGreeting();

                    [System.Obsolete("v2")]
                    public string GetGreeting() => "hi";
                }

                public class AlternateGreeter : IGreeter
                {
                    public string GetGreeting() => "alternate";
                }
                """);

            var updated = await indexer.UpdateAsync(workingDirectory, CancellationToken.None);
            Assert.Contains("ProjB", updated.AnalyzedProjects);
            Assert.Contains("ProjA", updated.AnalyzedProjects);
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }








    [Fact]
    public async Task UpdateAsync_AttributeRemovedFromPublicMethod_ExpandsToDependentProject()
    {
        var workingDirectory = CopyFixtureToTempDirectory();
        try
        {
            var indexer = new IncrementalCodeMapIndexer();
            var greeterPath = Path.Combine(workingDirectory, "ProjB", "Greeter.cs");
            await File.WriteAllTextAsync(greeterPath, """
                namespace Fixture.ProjB;

                public interface IGreeter
                {
                    string GetGreeting();
                }

                public class Greeter : IGreeter
                {
                    public string Greet() => GetGreeting();

                    [System.Obsolete("use GetGreeting2 instead")]
                    public string GetGreeting() => "hi";
                }

                public class AlternateGreeter : IGreeter
                {
                    public string GetGreeting() => "alternate";
                }
                """);
            await indexer.IndexAsync(workingDirectory, force: true, CancellationToken.None);

            await File.WriteAllTextAsync(greeterPath, """
                namespace Fixture.ProjB;

                public interface IGreeter
                {
                    string GetGreeting();
                }

                public class Greeter : IGreeter
                {
                    public string Greet() => GetGreeting();

                    public string GetGreeting() => "hi";
                }

                public class AlternateGreeter : IGreeter
                {
                    public string GetGreeting() => "alternate";
                }
                """);

            var updated = await indexer.UpdateAsync(workingDirectory, CancellationToken.None);
            Assert.Contains("ProjB", updated.AnalyzedProjects);
            Assert.Contains("ProjA", updated.AnalyzedProjects);
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }











    [Fact]
    public async Task UpdateAsync_ProjectGraphRefresh_ImplementationOnlyChange_DoesNotExpandToDependentProject()
    {
        var workingDirectory = CopyFixtureToTempDirectory();
        try
        {
            var indexer = new IncrementalCodeMapIndexer();
            await indexer.IndexAsync(workingDirectory, force: true, CancellationToken.None);

            var projBCsprojPath = Path.Combine(workingDirectory, "ProjB", "ProjB.csproj");
            var originalCsproj = await File.ReadAllTextAsync(projBCsprojPath);
            await File.WriteAllTextAsync(projBCsprojPath, originalCsproj.Replace(
                "<Nullable>enable</Nullable>",
                "<Nullable>enable</Nullable>\n  <!-- force ProjectFileHash change without touching ProjectReference -->"));

            var greeterPath = Path.Combine(workingDirectory, "ProjB", "Greeter.cs");
            await File.WriteAllTextAsync(greeterPath, """
                namespace Fixture.ProjB;

                public interface IGreeter
                {
                    string GetGreeting();
                }

                public class Greeter : IGreeter
                {
                    public string Greet() => GetGreeting();

                    public string GetGreeting() => "hello there";
                }

                public class AlternateGreeter : IGreeter
                {
                    public string GetGreeting() => "alternate";
                }
                """);

            var updated = await indexer.UpdateAsync(workingDirectory, CancellationToken.None);
            Assert.Contains("ProjB", updated.AnalyzedProjects);
            Assert.DoesNotContain("ProjA", updated.AnalyzedProjects);

            var databasePath = Path.Combine(workingDirectory, ".codemap", "index.db");
            var graph = await new CodeMapQueryStore(databasePath).LoadAsync();
            Assert.Single(graph.Symbols, s => s.QualifiedName == "Fixture.ProjB.Greeter.GetGreeting");
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }







    [Fact]
    public async Task UpdateAsync_ProjectGraphRefresh_PublicSurfaceChange_ExpandsToDependentProject()
    {
        var workingDirectory = CopyFixtureToTempDirectory();
        try
        {
            var indexer = new IncrementalCodeMapIndexer();
            await indexer.IndexAsync(workingDirectory, force: true, CancellationToken.None);

            var projBCsprojPath = Path.Combine(workingDirectory, "ProjB", "ProjB.csproj");
            var originalCsproj = await File.ReadAllTextAsync(projBCsprojPath);
            await File.WriteAllTextAsync(projBCsprojPath, originalCsproj.Replace(
                "<Nullable>enable</Nullable>",
                "<Nullable>enable</Nullable>\n  <!-- force ProjectFileHash change without touching ProjectReference -->"));

            var greeterPath = Path.Combine(workingDirectory, "ProjB", "Greeter.cs");
            await File.WriteAllTextAsync(greeterPath, """
                namespace Fixture.ProjB;

                public interface IGreeter
                {
                    string GetGreeting();
                    string GetGreeting2();
                }

                public class Greeter : IGreeter
                {
                    public string Greet() => GetGreeting();

                    public string GetGreeting() => "hi";

                    public string GetGreeting2() => "hi again";
                }

                public class AlternateGreeter : IGreeter
                {
                    public string GetGreeting() => "alternate";

                    public string GetGreeting2() => "alternate again";
                }
                """);

            var updated = await indexer.UpdateAsync(workingDirectory, CancellationToken.None);
            Assert.Contains("ProjB", updated.AnalyzedProjects);
            Assert.Contains("ProjA", updated.AnalyzedProjects);
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }

    private static string CopyFixtureToTempDirectory()
    {
        var testDir = AppContext.BaseDirectory;
        var solutionRoot = Path.GetFullPath(Path.Combine(testDir, "..", "..", "..", "..", ".."));
        var source = Path.Combine(solutionRoot, "tests", "Fixtures", "MultiProject");
        var destination = Path.Combine(Path.GetTempPath(), "codemap-fingerprint-" + Guid.NewGuid());
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