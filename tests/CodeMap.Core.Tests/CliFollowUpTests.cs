using System.Diagnostics;
using System.Text.Json;
using CodeMap.Storage;
using Microsoft.Data.Sqlite;

namespace CodeMap.Core.Tests;

[Collection("MsBuild")]
public sealed class CliFollowUpTests
{
    [Fact(Timeout = 90_000)]
    public async Task DiffJson_UsesChangedFilesAndCorrectImpactRoots()
    {
        var workingDirectory = CopyFixture("MultiProject");
        try
        {
            RunGit(workingDirectory, "init", "-q");
            RunGit(workingDirectory, "add", ".");
            RunGit(workingDirectory, "-c", "user.name=CodeMap Tests", "-c", "user.email=codemap@example.invalid", "commit", "-qm", "baseline");
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);

            await File.AppendAllTextAsync(Path.Combine(workingDirectory, "ProjB", "Greeter.cs"), Environment.NewLine + "// changed");
            var output = await CliProcess.RunAsync($"diff HEAD --root \"{workingDirectory}\" --json");

            Assert.True(output.ExitCode == 0, output.StdErr + output.StdOut);
            using var document = JsonDocument.Parse(output.StdOut);
            var changedFiles = document.RootElement.GetProperty("changedFiles").EnumerateArray()
                .Select(element => element.GetString())
                .ToArray();
            Assert.Contains("ProjB/Greeter.cs", changedFiles);

            var directIds = document.RootElement.GetProperty("matches").EnumerateArray()
                .Select(element => element.GetProperty("id").GetString())
                .ToHashSet(StringComparer.Ordinal);
            var relations = document.RootElement.GetProperty("relations").EnumerateArray().ToArray();
            Assert.NotEmpty(relations);
            foreach (var relation in relations)
                Assert.Contains(relation.GetProperty("targetId").GetString(), directIds);
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }

    [Fact(Timeout = 90_000)]
    public async Task DiffJson_AnalyzesAllChangedRootsButOnlyDisplaysMaxResultsMatches()
    {
        var workingDirectory = CopyFixture("MultiProject");
        try
        {
            const int symbolCount = 25;
            const int maxResults = 5;
            var manySymbolsPath = Path.Combine(workingDirectory, "ProjA", "ManySymbols.cs");
            await File.WriteAllTextAsync(manySymbolsPath, RenderManySymbolsSource(0));

            RunGit(workingDirectory, "init", "-q");
            RunGit(workingDirectory, "add", ".");
            RunGit(workingDirectory, "-c", "user.name=CodeMap Tests", "-c", "user.email=codemap@example.invalid", "commit", "-qm", "baseline");
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);

            await File.WriteAllTextAsync(manySymbolsPath, RenderManySymbolsSource(symbolCount));
            await new IncrementalCodeMapIndexer().UpdateAsync(workingDirectory, CancellationToken.None);

            var output = await CliProcess.RunAsync($"diff HEAD --root \"{workingDirectory}\" --max-results {maxResults} --json");

            Assert.True(output.ExitCode == 0, output.StdErr + output.StdOut);
            using var document = JsonDocument.Parse(output.StdOut);
            var matches = document.RootElement.GetProperty("matches").EnumerateArray().ToArray();
            Assert.True(matches.Length <= maxResults, $"expected matches to respect --max-results, got {matches.Length}");

            var textOutput = await CliProcess.RunAsync($"diff HEAD --root \"{workingDirectory}\" --max-results {maxResults}");
            Assert.True(textOutput.ExitCode == 0, textOutput.StdErr + textOutput.StdOut);
            var directSymbolsLine = textOutput.StdOut.Split(Environment.NewLine)
                .Single(line => line.StartsWith("direct symbols: ", StringComparison.Ordinal));
            var reportedCount = int.Parse(directSymbolsLine["direct symbols: ".Length..]);
            Assert.True(reportedCount >= symbolCount, $"expected analysis to see all {symbolCount} changed-root symbols, got {reportedCount}");
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }

    private static string RenderManySymbolsSource(int methodCount)
    {
        var methods = string.Join(Environment.NewLine, Enumerable.Range(0, methodCount)
            .Select(index => $"    public void Method{index}() {{ }}"));
        return $"namespace Fixture.ProjA;{Environment.NewLine}public class ManySymbols{Environment.NewLine}{{{Environment.NewLine}{methods}{Environment.NewLine}}}{Environment.NewLine}";
    }





    [Fact(Timeout = 90_000)]
    public async Task DiffJson_RiskIsIndependentOfMaxResults()
    {
        var workingDirectory = CopyFixture("MultiProject");
        try
        {
            const int callerCount = 12;
            var impactHosePath = Path.Combine(workingDirectory, "ProjA", "ImpactHose.cs");
            await File.WriteAllTextAsync(impactHosePath, RenderImpactHoseSource(callerCount));

            RunGit(workingDirectory, "init", "-q");
            RunGit(workingDirectory, "add", ".");
            RunGit(workingDirectory, "-c", "user.name=CodeMap Tests", "-c", "user.email=codemap@example.invalid", "commit", "-qm", "baseline");
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);

            await File.AppendAllTextAsync(Path.Combine(workingDirectory, "ProjB", "Greeter.cs"), Environment.NewLine + "// changed");

            var risks = new List<JsonElement>();
            foreach (var maxResults in new[] { 1, 5, 100 })
            {
                var output = await CliProcess.RunAsync($"diff HEAD --root \"{workingDirectory}\" --max-results {maxResults} --json");
                Assert.True(output.ExitCode == 0, output.StdErr + output.StdOut);
                using var document = JsonDocument.Parse(output.StdOut);
                var relations = document.RootElement.GetProperty("relations").EnumerateArray().ToArray();
                Assert.True(relations.Length <= maxResults, $"expected relations <= {maxResults}, got {relations.Length}");
                risks.Add(document.RootElement.GetProperty("risk").Clone());
            }

            AssertRiskEqual(risks[0], risks[1]);
            AssertRiskEqual(risks[0], risks[2]);
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }

    [Fact(Timeout = 90_000)]
    public async Task Diff_MinConfidenceFiltersDisplayedImpactWithoutChangingRisk()
    {
        var workingDirectory = CopyFixture("WebFixture");
        try
        {
            RunGit(workingDirectory, "init", "-q");
            RunGit(workingDirectory, "add", ".");
            RunGit(workingDirectory, "-c", "user.name=CodeMap Tests", "-c", "user.email=codemap@example.invalid", "commit", "-qm", "baseline");
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);

            await File.AppendAllTextAsync(Path.Combine(workingDirectory, "save.ts"), Environment.NewLine + "// changed");

            var unfiltered = await CliProcess.RunAsync($"diff HEAD --root \"{workingDirectory}\" --json");
            var filteredJson = await CliProcess.RunAsync($"diff HEAD --root \"{workingDirectory}\" --json --min-confidence=1");
            var filteredText = await CliProcess.RunAsync($"diff HEAD --root \"{workingDirectory}\" --min-confidence=1");

            Assert.Equal(0, unfiltered.ExitCode);
            Assert.Equal(0, filteredJson.ExitCode);
            Assert.Equal(0, filteredText.ExitCode);
            using var unfilteredDocument = JsonDocument.Parse(unfiltered.StdOut);
            using var filteredDocument = JsonDocument.Parse(filteredJson.StdOut);
            var unfilteredRelations = unfilteredDocument.RootElement.GetProperty("relations").EnumerateArray().ToArray();
            var filteredRelations = filteredDocument.RootElement.GetProperty("relations").EnumerateArray().ToArray();

            Assert.NotEmpty(unfilteredRelations);
            Assert.Empty(filteredRelations);
            AssertRiskEqual(unfilteredDocument.RootElement.GetProperty("risk"), filteredDocument.RootElement.GetProperty("risk"));
            Assert.DoesNotContain("<-", filteredText.StdOut);
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }

    private static string RenderImpactHoseSource(int callerCount)
    {
        var methods = string.Join(Environment.NewLine, Enumerable.Range(0, callerCount)
            .Select(index => $"    public void Caller{index}() {{ new Fixture.ProjB.Greeter().Greet(); }}"));
        return "using Fixture.ProjB;" + Environment.NewLine
            + Environment.NewLine
            + "namespace Fixture.ProjA;" + Environment.NewLine
            + Environment.NewLine
            + "public sealed class ImpactHose" + Environment.NewLine
            + "{" + Environment.NewLine
            + methods + Environment.NewLine
            + "}" + Environment.NewLine;
    }

    private static void AssertRiskEqual(JsonElement left, JsonElement right)
    {
        Assert.Equal(left.GetProperty("level").GetString(), right.GetProperty("level").GetString());
        Assert.Equal(left.GetProperty("publicApis").GetInt32(), right.GetProperty("publicApis").GetInt32());
        Assert.Equal(left.GetProperty("callers").GetInt32(), right.GetProperty("callers").GetInt32());
        Assert.Equal(left.GetProperty("crossProject").GetInt32(), right.GetProperty("crossProject").GetInt32());
        Assert.Equal(left.GetProperty("untested").GetInt32(), right.GetProperty("untested").GetInt32());
        Assert.Equal(left.GetProperty("weightedTotal").GetDouble(), right.GetProperty("weightedTotal").GetDouble());
    }



    [Fact(Timeout = 90_000)]
    public async Task DiffJson_V4Schema_DeletedTrackedFile_ReportsIncompleteAnalysisWithRealValues()
    {
        var workingDirectory = CopyFixture("MultiProject");
        try
        {
            RunGit(workingDirectory, "init", "-q");
            RunGit(workingDirectory, "add", ".");
            RunGit(workingDirectory, "-c", "user.name=CodeMap Tests", "-c", "user.email=codemap@example.invalid", "commit", "-qm", "baseline");
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);

            File.Delete(Path.Combine(workingDirectory, "ProjA", "Caller.cs"));
            var output = await CliProcess.RunAsync($"diff HEAD --root \"{workingDirectory}\" --json");

            Assert.True(output.ExitCode == 0, output.StdErr + output.StdOut);
            using var document = JsonDocument.Parse(output.StdOut);
            Assert.Equal(4, document.RootElement.GetProperty("version").GetInt32());
            Assert.False(document.RootElement.GetProperty("analysisComplete").GetBoolean());
            var unresolved = document.RootElement.GetProperty("unresolvedChangedFiles").EnumerateArray()
                .Select(element => element.GetString())
                .ToArray();
            Assert.Contains("ProjA/Caller.cs", unresolved);

            var changedFiles = document.RootElement.GetProperty("changedFiles").EnumerateArray()
                .Select(element => element.GetString())
                .ToArray();
            Assert.Contains("ProjA/Caller.cs", changedFiles);

            AssertMatchesDiffResponseSchema(document.RootElement, "cli-json-v4.schema.json");
            AssertDiffResponseDoesNotMatchSchema(document.RootElement, "cli-json-v2.schema.json");
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }

    [Fact(Timeout = 90_000)]
    public async Task DiffJson_V5Schema_DeletedTrackedFile_ReportsIncompleteAnalysis()
    {
        var workingDirectory = CopyFixture("MultiProject");
        try
        {
            RunGit(workingDirectory, "init", "-q");
            RunGit(workingDirectory, "add", ".");
            RunGit(workingDirectory, "-c", "user.name=CodeMap Tests", "-c", "user.email=codemap@example.invalid", "commit", "-qm", "baseline");
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);

            File.Delete(Path.Combine(workingDirectory, "ProjA", "Caller.cs"));
            var output = await CliProcess.RunAsync($"diff HEAD --root \"{workingDirectory}\" --json --evidence");

            Assert.True(output.ExitCode == 0, output.StdErr + output.StdOut);
            using var document = JsonDocument.Parse(output.StdOut);
            Assert.Equal(5, document.RootElement.GetProperty("version").GetInt32());
            Assert.False(document.RootElement.GetProperty("analysisComplete").GetBoolean());
            var unresolved = document.RootElement.GetProperty("unresolvedChangedFiles").EnumerateArray()
                .Select(element => element.GetString())
                .ToArray();
            Assert.Contains("ProjA/Caller.cs", unresolved);

            AssertMatchesDiffResponseSchema(document.RootElement, "cli-json-v5.schema.json");
            AssertDiffResponseDoesNotMatchSchema(document.RootElement, "cli-json-v3.schema.json");
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }

    [Fact]
    public void DiffJson_HistoricalV2Payload_MatchesHistoricalSchema()
    {
        const string historicalPayload = """
            {
              "version": 2,
              "base": "HEAD",
              "changedFiles": ["ProjA/Caller.cs"],
              "matches": [],
              "relations": [],
              "risk": { "level": "low", "publicApis": 0, "callers": 0, "crossProject": 0, "untested": 0, "weightedTotal": 0 },
              "stale": false
            }
            """;
        using var document = JsonDocument.Parse(historicalPayload);
        AssertMatchesDiffResponseSchema(document.RootElement, "cli-json-v2.schema.json");
    }

    [Fact]
    public void DiffJson_HistoricalV3Payload_MatchesHistoricalSchema()
    {
        const string historicalPayload = """
            {
              "version": 3,
              "base": "HEAD",
              "changedFiles": ["ProjA/Caller.cs"],
              "matches": [],
              "relations": [],
              "risk": { "level": "low", "publicApis": 0, "callers": 0, "crossProject": 0, "untested": 0, "weightedTotal": 0 },
              "stale": false
            }
            """;
        using var document = JsonDocument.Parse(historicalPayload);
        AssertMatchesDiffResponseSchema(document.RootElement, "cli-json-v3.schema.json");
    }



    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiffJson_PureModify_ReportsCompleteAnalysis(bool evidence)
    {
        var workingDirectory = CopyFixture("MultiProject");
        try
        {
            RunGit(workingDirectory, "init", "-q");
            RunGit(workingDirectory, "add", ".");
            RunGit(workingDirectory, "-c", "user.name=CodeMap Tests", "-c", "user.email=codemap@example.invalid", "commit", "-qm", "baseline");
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);

            await File.AppendAllTextAsync(Path.Combine(workingDirectory, "ProjB", "Greeter.cs"), Environment.NewLine + "// changed");
            var output = await CliProcess.RunAsync($"diff HEAD --root \"{workingDirectory}\" --json{(evidence ? " --evidence" : "")}");

            Assert.True(output.ExitCode == 0, output.StdErr + output.StdOut);
            using var document = JsonDocument.Parse(output.StdOut);
            Assert.Equal(evidence ? 5 : 4, document.RootElement.GetProperty("version").GetInt32());
            Assert.True(document.RootElement.GetProperty("analysisComplete").GetBoolean());
            Assert.Empty(document.RootElement.GetProperty("unresolvedChangedFiles").EnumerateArray());
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }







    private static void AssertMatchesSchemaDef(JsonElement response, string schemaFileName, string defName)
    {
        var testDir = AppContext.BaseDirectory;
        var solutionRoot = Path.GetFullPath(Path.Combine(testDir, "..", "..", "..", "..", ".."));
        using var schemaDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(solutionRoot, "docs", schemaFileName)));
        var responseSchema = schemaDocument.RootElement.GetProperty("$defs").GetProperty(defName);

        var required = responseSchema.GetProperty("required").EnumerateArray().Select(e => e.GetString()!).ToHashSet(StringComparer.Ordinal);
        foreach (var name in required)
            Assert.True(response.TryGetProperty(name, out _), $"{defName} is missing schema-required property '{name}'.");

        var declared = responseSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var property in response.EnumerateObject())
            Assert.True(declared.Contains(property.Name), $"{defName} has property '{property.Name}' not declared in the schema (additionalProperties: false).");
    }

    private static void AssertMatchesDiffResponseSchema(JsonElement response, string schemaFileName)
        => AssertMatchesSchemaDef(response, schemaFileName, "diffResponse");

    private static void AssertDoesNotMatchSchemaDef(JsonElement response, string schemaFileName, string defName)
    {
        var testDir = AppContext.BaseDirectory;
        var solutionRoot = Path.GetFullPath(Path.Combine(testDir, "..", "..", "..", "..", ".."));
        using var schemaDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(solutionRoot, "docs", schemaFileName)));
        var responseSchema = schemaDocument.RootElement.GetProperty("$defs").GetProperty(defName);
        var required = responseSchema.GetProperty("required").EnumerateArray().Select(e => e.GetString()!).ToHashSet(StringComparer.Ordinal);
        var missingRequired = required.Where(name => !response.TryGetProperty(name, out _)).ToArray();
        var declared = responseSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        var extraProperties = response.EnumerateObject().Select(p => p.Name).Where(name => !declared.Contains(name)).ToArray();
        Assert.True(missingRequired.Length > 0 || extraProperties.Length > 0,
            $"expected {defName} to violate {schemaFileName}, but all required properties were present and no extra properties were found.");
    }

    private static void AssertDiffResponseDoesNotMatchSchema(JsonElement response, string schemaFileName)
        => AssertDoesNotMatchSchemaDef(response, schemaFileName, "diffResponse");

    [Fact(Timeout = 90_000)]
    public async Task ArchitectureAndReportCommands_ReturnMachineEnvelopeAndRiskSection()
    {
        var workingDirectory = CopyFixture("MultiProject");
        var reportPath = Path.Combine(workingDirectory, "report.html");
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);

            var architecture = await CliProcess.RunAsync($"check architecture --root \"{workingDirectory}\" --json");
            Assert.Equal(1, architecture.ExitCode);
            using (var document = JsonDocument.Parse(architecture.StdOut))
                Assert.Equal(JsonValueKind.Array, document.RootElement.GetProperty("violations").ValueKind);

            var report = await CliProcess.RunAsync($"report --root \"{workingDirectory}\" --out \"{reportPath}\"");
            Assert.Equal(0, report.ExitCode);
            var html = await File.ReadAllTextAsync(reportPath);
            Assert.Contains("Risk:", html, StringComparison.Ordinal);
            Assert.Contains("CodeMap report", html, StringComparison.Ordinal);
            Assert.Contains("flowchart LR", html, StringComparison.Ordinal);
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }


















    [Fact(Timeout = 90_000)]
    public async Task ReportCommand_ProductionMethodCalledByTest_IsNotCountedUntested()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "codemap-report-untested-" + Guid.NewGuid());
        Directory.CreateDirectory(Path.Combine(workingDirectory, "tests"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(workingDirectory, "ProdLib.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(Path.Combine(workingDirectory, "Calculator.cs"),
                "namespace ProdLib;\n\npublic class Calculator\n{\n    public int Add(int a, int b) => a + b;\n}\n");
            await File.WriteAllTextAsync(Path.Combine(workingDirectory, "tests", "CalculatorTests.cs"), """
                namespace ProdLib.Tests;

                public class CalculatorTests
                {
                    public int InvokeAddTests()
                    {
                        var calculator = new ProdLib.Calculator();
                        return calculator.Add(1, 2);
                    }
                }
                """);

            var reportPath = Path.Combine(workingDirectory, "report.html");
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);

            var report = await CliProcess.RunAsync($"report --root \"{workingDirectory}\" --out \"{reportPath}\"");
            Assert.Equal(0, report.ExitCode);
            var html = await File.ReadAllTextAsync(reportPath);




            Assert.Matches(@"untested=[01]\b", html);
            Assert.DoesNotContain("untested=2", html, StringComparison.Ordinal);
            Assert.DoesNotContain("untested=3", html, StringComparison.Ordinal);
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }

    [Fact(Timeout = 90_000)]
    public async Task LspCommand_AnswersJsonLineRequests()
    {
        var workingDirectory = CopyFixture("MultiProject");
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);
            var request = JsonSerializer.Serialize(new { command = "find", query = "Greeter" }) + Environment.NewLine;

            var output = await CliProcess.RunWithInputAsync($"lsp --root \"{workingDirectory}\"", request);

            Assert.True(output.ExitCode == 0, output.StdErr + output.StdOut);
            using var response = JsonDocument.Parse(output.StdOut.Trim());
            Assert.True(response.RootElement.GetProperty("matches").GetArrayLength() > 0);
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }

    [Fact(Timeout = 90_000)]
    public async Task LspSymbolResolvingCommands_RejectAmbiguousMatches_AndPreserveUniqueResolution()
    {
        var workingDirectory = CopyFixture("CollidingProjectsWithReference");
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);

            var databasePath = Path.Combine(workingDirectory, ".codemap", "index.db");
            var graph = await new CodeMapQueryStore(databasePath).LoadAsync();
            var resolution = new CodeMapQueryService(graph).ResolveSymbol("Widget", callableOnly: false, maxResults: 20);
            Assert.True(resolution.IsAmbiguous);
            Assert.Equal(2, resolution.Matches.Count);

            using var process = StartLspProcess(workingDirectory);
            try
            {
                using var impact = await SendLspRequestAsync(process, new { command = "impact", query = "Widget" });
                AssertAmbiguous(impact, "Widget");

                using var flow = await SendLspRequestAsync(process, new { command = "flow", entry = "Widget" });
                AssertAmbiguous(flow, "Widget");

                using var sourceRelation = await SendLspRequestAsync(process, new
                {
                    command = "relation",
                    source = "Widget",
                    target = "CallB"
                });
                AssertAmbiguous(sourceRelation, "Widget");

                using var targetRelation = await SendLspRequestAsync(process, new
                {
                    command = "relation",
                    source = "CallB",
                    target = "Widget"
                });
                AssertAmbiguous(targetRelation, "Widget");

                using var uniqueImpact = await SendLspRequestAsync(process, new { command = "impact", query = "CallB" });
                Assert.True(uniqueImpact.RootElement.TryGetProperty("matches", out _), uniqueImpact.RootElement.ToString());
                Assert.False(uniqueImpact.RootElement.TryGetProperty("error", out _), uniqueImpact.RootElement.ToString());

                using var noMatchImpact = await SendLspRequestAsync(process, new { command = "impact", query = "MissingSymbol" });
                Assert.Equal(0, noMatchImpact.RootElement.GetProperty("matches").GetArrayLength());
                Assert.Equal(0, noMatchImpact.RootElement.GetProperty("relations").GetArrayLength());



                Assert.True(noMatchImpact.RootElement.TryGetProperty("stale", out var staleProperty), noMatchImpact.RootElement.ToString());
                Assert.Equal(JsonValueKind.False, staleProperty.ValueKind);
            }
            finally
            {
                process.StandardInput.Close();
                if (!process.WaitForExit(5000))
                    process.Kill(entireProcessTree: true);
            }
        }
        finally
        {
            CleanUp(workingDirectory);
        }

        static void AssertAmbiguous(JsonDocument document, string query)
        {
            Assert.Equal("ambiguous", document.RootElement.GetProperty("error").GetString());
            Assert.Equal(query, document.RootElement.GetProperty("query").GetString());
            Assert.True(document.RootElement.TryGetProperty("stale", out _));
        }
    }










    [Fact(Timeout = 90_000)]
    public async Task LspCommand_RepeatedFindQueries_CacheStaysCorrectAcrossAFileChange()
    {
        var workingDirectory = CopyFixture("MultiProject");
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);

            using var process = StartLspProcess(workingDirectory);
            try
            {
                var firstResponse = await SendLspRequestAsync(process, new { command = "find", query = "Greeter" });
                Assert.False(firstResponse.RootElement.GetProperty("stale").GetBoolean(), firstResponse.RootElement.ToString());

                await File.AppendAllTextAsync(Path.Combine(workingDirectory, "ProjB", "Greeter.cs"), Environment.NewLine + "// changed");



                var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
                JsonDocument? secondResponse = null;
                while (DateTimeOffset.UtcNow < deadline)
                {
                    secondResponse = await SendLspRequestAsync(process, new { command = "find", query = "Greeter" });
                    if (secondResponse.RootElement.GetProperty("stale").GetBoolean())
                        break;
                    await Task.Delay(200);
                }

                Assert.NotNull(secondResponse);
                Assert.True(secondResponse.RootElement.GetProperty("stale").GetBoolean(),
                    "Expected the file-system watcher to invalidate the cached freshness value after a tracked file changed." + Environment.NewLine + secondResponse.RootElement);
            }
            finally
            {
                process.StandardInput.Close();
                if (!process.WaitForExit(5000))
                    process.Kill(entireProcessTree: true);
            }
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }

    private static Process StartLspProcess(string workingDirectory, string command = "lsp")
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"exec \"{CliProcess.GetCliDllPath()}\" {command} --root \"{workingDirectory}\"",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        return Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start CLI process.");
    }

    private static async Task<JsonDocument> SendLspRequestAsync(Process process, object request)
    {
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request));
        await process.StandardInput.FlushAsync();
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));



        var buffer = new System.Text.StringBuilder();
        while (true)
        {
            var line = await process.StandardOutput.ReadLineAsync(timeoutCts.Token)
                ?? throw new InvalidOperationException("LSP process closed stdout before responding." + Environment.NewLine + await process.StandardError.ReadToEndAsync());
            buffer.AppendLine(line);
            var candidate = buffer.ToString();
            try
            {
                return JsonDocument.Parse(candidate);
            }
            catch (JsonException)
            {

            }
        }
    }

    [Fact(Timeout = 90_000)]
    public async Task McpCommand_ListsToolsOverStdio()
    {
        var workingDirectory = CopyFixture("MultiProject");
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);
            var initialize = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "initialize",
                @params = new
                {
                    protocolVersion = "2025-06-18",
                    capabilities = new { },
                    clientInfo = new { name = "CodeMap.Tests", version = "1.0" }
                }
            });
            var initialized = """{"jsonrpc":"2.0","method":"notifications/initialized"}""";
            var listTools = """{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}""";

            var output = await CliProcess.RunWithInputAsync(
                $"mcp --root \"{workingDirectory}\"",
                string.Join(Environment.NewLine, initialize, initialized, listTools) + Environment.NewLine,
                interMessageDelay: TimeSpan.FromMilliseconds(500));

            Assert.True(output.ExitCode == 0, output.StdErr + output.StdOut);
            Assert.Contains("find_symbol", output.StdOut, StringComparison.Ordinal);
            Assert.Contains("refresh_index", output.StdOut, StringComparison.Ordinal);
            Assert.Contains("explain_relation", output.StdOut, StringComparison.Ordinal);
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }








    [Fact(Timeout = 90_000)]
    public async Task McpRefreshIndex_InvalidatesCachedStaleness_SoNextFindSymbolSeesFreshIndex()
    {
        var workingDirectory = CopyFixture("MultiProject");
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);


            await File.AppendAllTextAsync(Path.Combine(workingDirectory, "ProjB", "Greeter.cs"), Environment.NewLine + "// changed");

            using var process = StartLspProcess(workingDirectory, command: "mcp");
            try
            {
                await SendMcpRequestAsync(process, 1, "initialize", new
                {
                    protocolVersion = "2025-06-18",
                    capabilities = new { },
                    clientInfo = new { name = "CodeMap.Tests", version = "1.0" }
                });
                await process.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
                await process.StandardInput.FlushAsync();






                var firstFind = await SendMcpToolCallAsync(process, 2, "find_symbol", new { query = "Greeter", root = workingDirectory });
                Assert.Contains("\"stale\":true", firstFind, StringComparison.Ordinal);

                var refresh = await SendMcpToolCallAsync(process, 3, "refresh_index", new { root = workingDirectory });
                Assert.Contains("Indexed", refresh, StringComparison.Ordinal);

                var secondFind = await SendMcpToolCallAsync(process, 4, "find_symbol", new { query = "Greeter", root = workingDirectory });
                Assert.Contains("\"stale\":false", secondFind, StringComparison.Ordinal);
            }
            finally
            {
                process.StandardInput.Close();
                if (!process.WaitForExit(5000))
                    process.Kill(entireProcessTree: true);
            }
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }

    private static async Task<JsonDocument> SendMcpRequestAsync(Process process, int id, string method, object @params)
    {
        var request = JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params });
        await process.StandardInput.WriteLineAsync(request);
        await process.StandardInput.FlushAsync();
        return await ReadMcpResponseAsync(process, id);
    }


    private static async Task<string> SendMcpToolCallAsync(Process process, int id, string toolName, object arguments)
    {
        using var response = await SendMcpRequestAsync(process, id, "tools/call", new { name = toolName, arguments });
        return response.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
    }

    private static async Task<JsonDocument> ReadMcpResponseAsync(Process process, int expectedId)
    {
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        while (true)
        {
            var line = await process.StandardOutput.ReadLineAsync(timeoutCts.Token)
                ?? throw new InvalidOperationException("MCP process closed stdout before responding." + Environment.NewLine + await process.StandardError.ReadToEndAsync());
            if (string.IsNullOrWhiteSpace(line))
                continue;
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }
            if (document.RootElement.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.Number && idElement.GetInt32() == expectedId)
                return document;
            document.Dispose();
        }
    }

    [Fact]
    public async Task FindJson_MatchesQueryResponseV4Schema()
    {
        var workingDirectory = CopyFixture("MultiProject");
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);
            var output = await CliProcess.RunAsync($"find Greeter --root \"{workingDirectory}\" --json");
            Assert.Equal(0, output.ExitCode);
            using var document = JsonDocument.Parse(output.StdOut);
            Assert.Equal(4, document.RootElement.GetProperty("version").GetInt32());
            AssertMatchesSchemaDef(document.RootElement, "cli-json-v4.schema.json", "queryResponse");
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }

    [Fact]
    public async Task EvidenceQueryErrors_MatchErrorResponseV5Schema()
    {
        var missingRoot = Path.Combine(Path.GetTempPath(), "codemap-missing-" + Guid.NewGuid());
        var output = await CliProcess.RunAsync(
            $"relation \"app.ts::bind\" \"save.ts::save\" --root \"{missingRoot}\" --json --evidence");

        Assert.Equal(1, output.ExitCode);
        using var document = JsonDocument.Parse(output.StdOut);
        Assert.Equal(5, document.RootElement.GetProperty("version").GetInt32());
        AssertMatchesSchemaDef(document.RootElement, "cli-json-v5.schema.json", "errorResponse");
    }

    [Fact(Timeout = 90_000)]
    public async Task CliRelationCommand_EmitsV5Evidence()
    {
        var workingDirectory = CopyFixture("WebFixture");
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);
            var output = await CliProcess.RunAsync($"callees \"app.ts::bind\" --root \"{workingDirectory}\" --json --evidence");
            Assert.Equal(0, output.ExitCode);
            using var document = JsonDocument.Parse(output.StdOut);
            Assert.Equal(5, document.RootElement.GetProperty("version").GetInt32());
            var relations = document.RootElement.GetProperty("relations").EnumerateArray().ToArray();
            Assert.NotEmpty(relations);
            var relation = relations[0];
            Assert.True(relation.TryGetProperty("evidence", out _));
            Assert.True(relation.TryGetProperty("location", out _));
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }

    [Fact(Timeout = 90_000)]
    public async Task Callees_MinConfidence_AppliesEquallyToTextAndJson()
    {
        var workingDirectory = CopyFixture("WebFixture");
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);
            var unfiltered = await CliProcess.RunAsync($"callees \"app.ts::bind\" --root \"{workingDirectory}\" --json");
            var filteredJson = await CliProcess.RunAsync($"callees \"app.ts::bind\" --root \"{workingDirectory}\" --json --min-confidence=1");
            var filteredText = await CliProcess.RunAsync($"callees \"app.ts::bind\" --root \"{workingDirectory}\" --min-confidence=1");

            Assert.Equal(0, unfiltered.ExitCode);
            Assert.Equal(0, filteredJson.ExitCode);
            Assert.Equal(0, filteredText.ExitCode);
            using var unfilteredDocument = JsonDocument.Parse(unfiltered.StdOut);
            using var filteredDocument = JsonDocument.Parse(filteredJson.StdOut);
            var unfilteredTargets = unfilteredDocument.RootElement.GetProperty("relations").EnumerateArray()
                .Select(relation => relation.GetProperty("target").GetString()!)
                .ToArray();
            var filteredTargets = filteredDocument.RootElement.GetProperty("relations").EnumerateArray()
                .Select(relation => relation.GetProperty("target").GetString()!)
                .ToArray();

            Assert.True(filteredTargets.Length < unfilteredTargets.Length);
            foreach (var target in filteredTargets)
                Assert.Contains(target, filteredText.StdOut);
            foreach (var target in unfilteredTargets.Except(filteredTargets))
                Assert.DoesNotContain(target, filteredText.StdOut);
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }

    [Fact(Timeout = 90_000)]
    public async Task CliRelationCommand_RejectsV4PairResponseWithoutEvidence()
    {
        var workingDirectory = CopyFixture("WebFixture");
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);
            var output = await CliProcess.RunAsync(
                $"relation \"app.ts::bind\" \"save.ts::save\" --root \"{workingDirectory}\" --json");

            Assert.Equal(1, output.ExitCode);
            using var document = JsonDocument.Parse(output.StdOut);
            Assert.Equal(4, document.RootElement.GetProperty("version").GetInt32());
            Assert.Equal("query_failed", document.RootElement.GetProperty("error").GetProperty("code").GetString());
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }

    [Fact]
    public async Task EvidenceQueryErrors_UseV5Envelope()
    {
        var missingRoot = Path.Combine(Path.GetTempPath(), "codemap-missing-" + Guid.NewGuid());
        var output = await CliProcess.RunAsync(
            $"relation \"app.ts::bind\" \"save.ts::save\" --root \"{missingRoot}\" --json --evidence");

        Assert.Equal(1, output.ExitCode);
        using var document = JsonDocument.Parse(output.StdOut);
        Assert.Equal(5, document.RootElement.GetProperty("version").GetInt32());
        Assert.Equal("index_not_found", document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("1.1")]
    [InlineData("-0.1")]
    public async Task MinConfidenceOutsideRange_IsRejectedBeforeQuery(string value)
    {
        var missingRoot = Path.Combine(Path.GetTempPath(), "codemap-missing-" + Guid.NewGuid());
        var output = await CliProcess.RunAsync(
            $"callers Greet --root \"{missingRoot}\" --json --evidence --min-confidence={value}");

        Assert.Equal(1, output.ExitCode);
        using var document = JsonDocument.Parse(output.StdOut);
        Assert.Equal(5, document.RootElement.GetProperty("version").GetInt32());
        Assert.Equal("query_failed", document.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Contains("finite number between 0 and 1", document.RootElement.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact(Timeout = 90_000)]
    public async Task ContextCommand_EmitsV5EvidenceWhenRequested()
    {
        var workingDirectory = CopyFixture("WebFixture");
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);
            var output = await CliProcess.RunAsync(
                $"context \"app.ts::bind\" --root \"{workingDirectory}\" --json --evidence");

            Assert.Equal(0, output.ExitCode);
            using var document = JsonDocument.Parse(output.StdOut);
            Assert.Equal(5, document.RootElement.GetProperty("version").GetInt32());
            var relations = document.RootElement.GetProperty("relations").EnumerateArray().ToArray();
            Assert.NotEmpty(relations);
            Assert.True(relations[0].TryGetProperty("evidence", out _));
            Assert.True(relations[0].TryGetProperty("location", out _));
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }

    [Fact(Timeout = 90_000)]
    public async Task LspRelationCommand_ReturnsEvidencePayload()
    {
        var workingDirectory = CopyFixture("WebFixture");
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);
            var request = JsonSerializer.Serialize(new
            {
                command = "relation",
                source = "app.ts::bind",
                target = "save.ts::save",
                minConfidence = 0
            }) + Environment.NewLine;
            var output = await CliProcess.RunWithInputAsync($"lsp --root \"{workingDirectory}\"", request);
            Assert.Equal(0, output.ExitCode);
            using var response = JsonDocument.Parse(output.StdOut.Trim());
            var relations = response.RootElement.GetProperty("relations").EnumerateArray().ToArray();
            Assert.NotEmpty(relations);
            Assert.True(relations[0].TryGetProperty("evidence", out _));
            Assert.True(relations[0].TryGetProperty("location", out _));
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }

    [Fact]
    public async Task LspRelationCommand_RejectsInvalidConfidence()
    {
        var missingRoot = Path.Combine(Path.GetTempPath(), "codemap-missing-" + Guid.NewGuid());
        var request = JsonSerializer.Serialize(new
        {
            command = "relation",
            source = "app.ts::bind",
            target = "save.ts::save",
            root = missingRoot,
            minConfidence = 1.1
        }) + Environment.NewLine;

        var output = await CliProcess.RunWithInputAsync("lsp", request);

        Assert.Equal(0, output.ExitCode);
        using var response = JsonDocument.Parse(output.StdOut.Trim());
        Assert.Equal("query_failed", response.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Contains("finite number between 0 and 1", response.RootElement.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact(Timeout = 90_000)]
    public async Task McpExplainRelation_ReturnsRelations()
    {
        var workingDirectory = CopyFixture("WebFixture");
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);
            var initialize = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "initialize",
                @params = new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "CodeMap.Tests", version = "1.0" } }
            });
            var initialized = """{"jsonrpc":"2.0","method":"notifications/initialized"}""";
            var call = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 2,
                method = "tools/call",
                @params = new
                {
                    name = "explain_relation",
                    arguments = new { source = "app.ts::bind", target = "save.ts::save", root = workingDirectory }
                }
            });
            var output = await CliProcess.RunWithInputAsync(
                $"mcp --root \"{workingDirectory}\"",
                string.Join(Environment.NewLine, initialize, initialized, call) + Environment.NewLine,
                interMessageDelay: TimeSpan.FromMilliseconds(500));
            Assert.Equal(0, output.ExitCode);
            Assert.True(output.StdOut.Contains("relations", StringComparison.Ordinal),
                output.StdOut + Environment.NewLine + output.StdErr);
            Assert.Contains("evidence", output.StdOut, StringComparison.Ordinal);
            Assert.Contains("location", output.StdOut, StringComparison.Ordinal);
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }

    [Fact]
    public async Task McpExplainRelation_RejectsInvalidConfidence()
    {
        var workingDirectory = CopyFixture("WebFixture");
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);
            var initialize = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "initialize",
                @params = new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "CodeMap.Tests", version = "1.0" } }
            });
            var initialized = """{"jsonrpc":"2.0","method":"notifications/initialized"}""";
            var call = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 2,
                method = "tools/call",
                @params = new
                {
                    name = "explain_relation",
                    arguments = new { source = "app.ts::bind", target = "save.ts::save", root = workingDirectory, minConfidence = 1.1 }
                }
            });

            var output = await CliProcess.RunWithInputAsync(
                $"mcp --root \"{workingDirectory}\"",
                string.Join(Environment.NewLine, initialize, initialized, call) + Environment.NewLine,
                interMessageDelay: TimeSpan.FromMilliseconds(500));

            Assert.Equal(0, output.ExitCode);
            Assert.Contains("query_failed", output.StdOut, StringComparison.Ordinal);
            Assert.Contains("finite number between 0 and 1", output.StdOut, StringComparison.Ordinal);
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }

    [Fact(Timeout = 90_000)]
    public async Task CliFlowCommand_HttpKind_TraversesRouteToImplementation()
    {
        var workingDirectory = CopyFixture("AspNetFixture");
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);
            var output = await CliProcess.RunAsync($"flow \"GET /orders/{{id}}\" --kind http --root \"{workingDirectory}\" --json --evidence");
            Assert.True(output.ExitCode == 0, output.StdErr + output.StdOut);

            using var document = JsonDocument.Parse(output.StdOut);
            Assert.Equal(5, document.RootElement.GetProperty("version").GetInt32());
            var relations = document.RootElement.GetProperty("relations").EnumerateArray().ToArray();
            Assert.NotEmpty(relations);
            Assert.Contains(relations, relation => relation.GetProperty("edgeKind").GetString() == "RoutesTo");
            Assert.All(relations, relation => Assert.True(relation.TryGetProperty("evidence", out _)));






            var relationsWithLocation = relations.Where(relation =>
                relation.TryGetProperty("location", out var location) && location.ValueKind != JsonValueKind.Null).ToArray();
            Assert.NotEmpty(relationsWithLocation);
            Assert.All(relationsWithLocation, relation =>
            {
                var file = relation.GetProperty("location").GetProperty("file").GetString();
                Assert.False(string.IsNullOrWhiteSpace(file));
                Assert.False(Path.IsPathRooted(file));
            });
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }

    [Fact(Timeout = 90_000)]
    public async Task CliFlowCommand_HttpKind_TraversesLambdaRouteToCallTarget()
    {
        var workingDirectory = CopyFixture("AspNetFixture");
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);
            var output = await CliProcess.RunAsync($"flow \"POST /orders\" --kind http --root \"{workingDirectory}\" --json --evidence");
            Assert.True(output.ExitCode == 0, output.StdErr + output.StdOut);

            using var document = JsonDocument.Parse(output.StdOut);
            Assert.Equal(5, document.RootElement.GetProperty("version").GetInt32());
            var relations = document.RootElement.GetProperty("relations").EnumerateArray().ToArray();
            Assert.NotEmpty(relations);



            Assert.Contains(relations, relation => relation.GetProperty("edgeKind").GetString() == "RoutesTo");
            Assert.Contains(relations, relation => relation.GetProperty("edgeKind").GetString() == "Calls");
            Assert.All(relations, relation => Assert.True(relation.TryGetProperty("evidence", out _)));
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }

    [Fact(Timeout = 90_000)]
    public async Task CliFlowCommand_UiKind_TraversesRazorComponentBindings()
    {
        var workingDirectory = CopyFixture("AspNetFixture");
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);
            var output = await CliProcess.RunAsync($"flow \"Components/OrderForm.razor\" --kind ui --root \"{workingDirectory}\" --json");
            Assert.True(output.ExitCode == 0, output.StdErr + output.StdOut);

            using var document = JsonDocument.Parse(output.StdOut);
            Assert.Equal(4, document.RootElement.GetProperty("version").GetInt32());
            var relations = document.RootElement.GetProperty("relations").EnumerateArray().ToArray();
            Assert.Contains(relations, relation => relation.GetProperty("edgeKind").GetString() == "Renders");
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }

    [Fact(Timeout = 90_000)]
    public async Task CliFlowCommand_RejectsInvalidKindAndDepth()
    {
        var workingDirectory = CopyFixture("AspNetFixture");
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);

            var badKind = await CliProcess.RunAsync($"flow \"GET /orders/{{id}}\" --kind bogus --root \"{workingDirectory}\" --json");
            Assert.Equal(1, badKind.ExitCode);

            var badDepth = await CliProcess.RunAsync($"flow \"GET /orders/{{id}}\" --depth 9 --root \"{workingDirectory}\" --json");
            Assert.Equal(1, badDepth.ExitCode);
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }

    [Fact(Timeout = 90_000)]
    public async Task CliImpactCommand_DefaultProfile_MatchesExplicitCodeProfile()
    {
        var workingDirectory = CopyFixture("AspNetFixture");
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);
            var query = "AspNetFixture.Services.IOrderService.GetOrder";

            var defaultOutput = await CliProcess.RunAsync($"impact \"{query}\" --root \"{workingDirectory}\" --json");
            var explicitOutput = await CliProcess.RunAsync($"impact \"{query}\" --root \"{workingDirectory}\" --profile code --json");
            Assert.True(defaultOutput.ExitCode == 0, defaultOutput.StdErr + defaultOutput.StdOut);
            Assert.True(explicitOutput.ExitCode == 0, explicitOutput.StdErr + explicitOutput.StdOut);

            using var defaultDocument = JsonDocument.Parse(defaultOutput.StdOut);
            using var explicitDocument = JsonDocument.Parse(explicitOutput.StdOut);
            var defaultTargets = defaultDocument.RootElement.GetProperty("relations").EnumerateArray()
                .Select(relation => relation.GetProperty("targetId").GetString()).ToArray();
            var explicitTargets = explicitDocument.RootElement.GetProperty("relations").EnumerateArray()
                .Select(relation => relation.GetProperty("targetId").GetString()).ToArray();
            Assert.Equal(explicitTargets, defaultTargets);
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }

    [Fact(Timeout = 90_000)]
    public async Task CliImpactCommand_AppProfile_ReachesRouteThroughLambdaHandler()
    {
        var workingDirectory = CopyFixture("AspNetFixture");
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);
            var query = "AspNetFixture.Services.IOrderService.GetOrder";

            var codeOutput = await CliProcess.RunAsync($"impact \"{query}\" --root \"{workingDirectory}\" --depth 4 --profile code --json");
            var appOutput = await CliProcess.RunAsync($"impact \"{query}\" --root \"{workingDirectory}\" --depth 4 --profile app --json");
            Assert.True(codeOutput.ExitCode == 0, codeOutput.StdErr + codeOutput.StdOut);
            Assert.True(appOutput.ExitCode == 0, appOutput.StdErr + appOutput.StdOut);

            using var codeDocument = JsonDocument.Parse(codeOutput.StdOut);
            using var appDocument = JsonDocument.Parse(appOutput.StdOut);
            var codeRelations = codeDocument.RootElement.GetProperty("relations").EnumerateArray().ToArray();
            var appRelations = appDocument.RootElement.GetProperty("relations").EnumerateArray().ToArray();



            Assert.DoesNotContain(codeRelations, relation => relation.GetProperty("edgeKind").GetString() == "RoutesTo");
            Assert.Contains(appRelations, relation => relation.GetProperty("edgeKind").GetString() == "RoutesTo");
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }

    [Fact(Timeout = 90_000)]
    public async Task CliImpactCommand_RejectsInvalidProfile()
    {
        var workingDirectory = CopyFixture("AspNetFixture");
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);

            var output = await CliProcess.RunAsync($"impact \"AspNetFixture.Services.IOrderService.GetOrder\" --root \"{workingDirectory}\" --profile bogus --json");
            Assert.Equal(1, output.ExitCode);
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }

    [Fact(Timeout = 90_000)]
    public async Task LspImpactCommand_AppProfile_ReachesRouteThroughLambdaHandler()
    {
        var workingDirectory = CopyFixture("AspNetFixture");
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);
            var request = JsonSerializer.Serialize(new { command = "impact", query = "AspNetFixture.Services.IOrderService.GetOrder", profile = "app" }) + Environment.NewLine;

            var output = await CliProcess.RunWithInputAsync($"lsp --root \"{workingDirectory}\"", request);

            Assert.True(output.ExitCode == 0, output.StdErr + output.StdOut);
            using var response = JsonDocument.Parse(output.StdOut.Trim());
            var relations = response.RootElement.GetProperty("relations").EnumerateArray().ToArray();
            Assert.Contains(relations, relation => relation.GetProperty("edgeKind").GetString() == "RoutesTo");
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }

    [Fact(Timeout = 90_000)]
    public async Task LspImpactCommand_RejectsInvalidProfile()
    {
        var workingDirectory = CopyFixture("AspNetFixture");
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);
            var request = JsonSerializer.Serialize(new { command = "impact", query = "AspNetFixture.Services.IOrderService.GetOrder", profile = "bogus" }) + Environment.NewLine;

            var output = await CliProcess.RunWithInputAsync($"lsp --root \"{workingDirectory}\"", request);

            Assert.True(output.ExitCode == 0, output.StdErr + output.StdOut);
            using var response = JsonDocument.Parse(output.StdOut.Trim());
            Assert.True(response.RootElement.TryGetProperty("error", out _));
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }

    [Fact(Timeout = 90_000)]
    public async Task McpGetImpactTool_AppProfile_IsCallableOverStdio()
    {
        var workingDirectory = CopyFixture("AspNetFixture");
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);
            var initialize = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "initialize",
                @params = new
                {
                    protocolVersion = "2025-06-18",
                    capabilities = new { },
                    clientInfo = new { name = "CodeMap.Tests", version = "1.0" }
                }
            });
            var initialized = """{"jsonrpc":"2.0","method":"notifications/initialized"}""";
            var listTools = """{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}""";
            var callTool = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 3,
                method = "tools/call",
                @params = new
                {
                    name = "get_impact",
                    arguments = new { query = "AspNetFixture.Services.IOrderService.GetOrder", profile = "app", root = workingDirectory }
                }
            });

            var output = await CliProcess.RunWithInputAsync(
                $"mcp --root \"{workingDirectory}\"",
                string.Join(Environment.NewLine, initialize, initialized, listTools, callTool) + Environment.NewLine,
                interMessageDelay: TimeSpan.FromMilliseconds(500));

            Assert.True(output.ExitCode == 0, output.StdErr + output.StdOut);
            Assert.Contains("get_impact", output.StdOut, StringComparison.OrdinalIgnoreCase);



            Assert.Contains("POST /orders", output.StdOut, StringComparison.Ordinal);
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }

    [Fact(Timeout = 90_000)]
    public async Task LspFlowCommand_AnswersJsonLineRequest()
    {
        var workingDirectory = CopyFixture("AspNetFixture");
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);
            var request = JsonSerializer.Serialize(new { command = "flow", entry = "GET /orders/{id}", kind = "http", depth = 4 }) + Environment.NewLine;

            var output = await CliProcess.RunWithInputAsync($"lsp --root \"{workingDirectory}\"", request);

            Assert.True(output.ExitCode == 0, output.StdErr + output.StdOut);
            using var response = JsonDocument.Parse(output.StdOut.Trim());
            Assert.True(response.RootElement.GetProperty("relations").GetArrayLength() > 0);
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }

    [Fact(Timeout = 90_000)]
    public async Task McpGetFlowTool_IsListedAndCallableOverStdio()
    {
        var workingDirectory = CopyFixture("AspNetFixture");
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);
            var initialize = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "initialize",
                @params = new
                {
                    protocolVersion = "2025-06-18",
                    capabilities = new { },
                    clientInfo = new { name = "CodeMap.Tests", version = "1.0" }
                }
            });
            var initialized = """{"jsonrpc":"2.0","method":"notifications/initialized"}""";
            var listTools = """{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}""";
            var callTool = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 3,
                method = "tools/call",
                @params = new
                {
                    name = "get_flow",
                    arguments = new { entry = "GET /orders/{id}", kind = "http", root = workingDirectory }
                }
            });

            var output = await CliProcess.RunWithInputAsync(
                $"mcp --root \"{workingDirectory}\"",
                string.Join(Environment.NewLine, initialize, initialized, listTools, callTool) + Environment.NewLine,
                interMessageDelay: TimeSpan.FromMilliseconds(500));

            Assert.True(output.ExitCode == 0, output.StdErr + output.StdOut);
            Assert.Contains("get_flow", output.StdOut, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("RoutesTo", output.StdOut, StringComparison.Ordinal);
        }
        finally
        {
            CleanUp(workingDirectory);
        }
    }

    [Fact(Timeout = 90_000)]
    public async Task GitChangedFileResolver_AcceptsWorktreeGitFile()
    {
        var repository = CopyFixture("MultiProject");
        var worktree = Path.Combine(Path.GetTempPath(), "codemap-worktree-" + Guid.NewGuid());
        try
        {
            RunGit(repository, "init", "-q");
            RunGit(repository, "add", ".");
            RunGit(repository, "-c", "user.name=CodeMap Tests", "-c", "user.email=codemap@example.invalid", "commit", "-qm", "baseline");
            RunGit(repository, "worktree", "add", "--detach", "-q", worktree, "HEAD");

            var changed = GitChangedFileResolver.ListChangedFiles(worktree, "HEAD");

            Assert.NotNull(changed);
            Assert.StartsWith("gitdir:", File.ReadAllText(Path.Combine(worktree, ".git")), StringComparison.OrdinalIgnoreCase);
            await Task.CompletedTask;
        }
        finally
        {
            try
            {
                if (Directory.Exists(worktree))
                    RunGit(repository, "worktree", "remove", "--force", worktree);
            }
            catch
            {

            }
            CleanUp(worktree);
            CleanUp(repository);
        }
    }

    private static void RunGit(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start git.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Task.WhenAll(stdout, stderr).GetAwaiter().GetResult();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git exited {process.ExitCode}: {stderr.Result}");
    }

    private static string CopyFixture(string fixtureName)
    {


        var source = fixtureName == "AspNetFixture"
            ? FixtureRestore.EnsureRestored(fixtureName)
            : Path.Combine(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..")), "tests", "Fixtures", fixtureName);
        var destination = Path.Combine(Path.GetTempPath(), $"codemap-follow-up-{fixtureName}-" + Guid.NewGuid());
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

    private static void CleanUp(string path)
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(path))
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            foreach (var directory in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories))
                File.SetAttributes(directory, FileAttributes.Normal);
            File.SetAttributes(path, FileAttributes.Normal);
            Directory.Delete(path, recursive: true);
        }
    }
}
