using CodeMap.Core.Analysis;
using CodeMap.Core.Models;
using CodeMap.Storage;
using CodeMap.Web;
using Microsoft.Data.Sqlite;

namespace CodeMap.Core.Tests;

public sealed class WebAnalyzerTests
{
    [Fact]
    public async Task ExtractsWebSymbolsAndCrossLanguageRelations()
    {
        var analyzer = new WebLanguageAnalyzer();
        var root = Path.Combine(Path.GetTempPath(), "codemap-web-fixture");
        var files = new Dictionary<string, string>
        {
            ["index.html"] = "<button id=\"saveBtn\" class=\"primary\">Save</button>",
            ["site.css"] = "#saveBtn { color: red; } .primary { font-weight: bold; }",
            ["app.ts"] = "import { save } from './save'; function bind() { document.querySelector('#saveBtn'); save(); }",
            ["save.ts"] = "export function save() { return true; }"
        };

        var result = await analyzer.AnalyzeAsync(new AnalysisContext
        {
            ProjectName = "Fixture.Web",
            FilePath = Path.Combine(root, "app.ts"),
            Content = files["app.ts"],
            SourceFiles = files,
            RootDirectory = root
        }, CancellationToken.None);

        Assert.Contains(result.Nodes, node => node.Kind == NodeKind.HtmlElement && node.QualifiedName.EndsWith("/#saveBtn", StringComparison.Ordinal));
        Assert.Contains(result.Nodes, node => node.Kind == NodeKind.CssSelector && node.Name == "#saveBtn");
        Assert.Contains(result.Nodes, node => node.Kind == NodeKind.Function && node.Name == "save");
        Assert.Contains(result.Edges, edge => edge.Kind == EdgeKind.Imports);
        Assert.Contains(result.Edges, edge => edge.Kind == EdgeKind.Calls);
        Assert.Contains(result.Edges, edge => edge.Kind == EdgeKind.UsesElement);
        Assert.Contains(result.Edges, edge => edge.Kind == EdgeKind.UsesCss);
        Assert.All(result.Edges.Where(edge => edge.Kind == EdgeKind.UsesCss), edge => Assert.Equal(EdgeResolutionKind.Syntactic, edge.ResolutionKind));
        Assert.All(result.Edges.Where(edge => edge.Kind == EdgeKind.Imports), edge => Assert.Equal(EdgeResolutionKind.Heuristic, edge.ResolutionKind));
        Assert.All(result.Edges.Where(edge => edge.Kind == EdgeKind.Calls), edge => Assert.Equal(EdgeResolutionKind.Syntactic, edge.ResolutionKind));
        Assert.All(result.Edges.Where(edge => edge.Kind == EdgeKind.UsesElement), edge => Assert.Equal(EdgeResolutionKind.Heuristic, edge.ResolutionKind));
        Assert.All(result.Edges.Where(edge => edge.Kind == EdgeKind.UsesCss), edge => Assert.Equal(0.85, edge.Confidence));
        Assert.All(result.Edges.Where(edge => edge.Kind is EdgeKind.Imports or EdgeKind.UsesElement), edge => Assert.Equal(0.60, edge.Confidence));
        Assert.Contains(result.Edges, edge => edge.Kind == EdgeKind.Calls && edge.Confidence == 0.90);
    }

    [Fact]
    public async Task ExtractsCommonJsRequireAsImport()
    {
        var result = await AnalyzeScripts(new Dictionary<string, string>
        {
            ["save.js"] = "function save() { return true; }",
            ["app.js"] = "const save = require('./save'); function bind() { save(); }"
        });

        Assert.Contains(result.Edges, edge =>
            edge.Kind == EdgeKind.Imports
            && edge.SourceId.Contains("app.js", StringComparison.Ordinal)
            && edge.TargetId.Contains("save.js", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Nodes, node => node.Name == "require");
        Assert.DoesNotContain(result.Edges, edge => edge.Kind == EdgeKind.Calls && result.Nodes.Any(node => node.Id == edge.TargetId && node.Name == "require"));
    }

    [Fact]
    public async Task ExtractsCallsFromArrowAndFunctionExpressions()
    {
        var result = await AnalyzeScripts(new Dictionary<string, string>
        {
            ["helpers.js"] = "function helper() { return true; }",
            ["app.js"] = """
                const save = () => helper();
                const load = function() { helper(); };
                """
        });

        var helper = Assert.Single(result.Nodes, node => node.Kind == NodeKind.Function && node.Name == "helper");
        var save = Assert.Single(result.Nodes, node => node.Kind == NodeKind.Function && node.Name == "save");
        var load = Assert.Single(result.Nodes, node => node.Kind == NodeKind.Function && node.Name == "load");
        Assert.Contains(result.Edges, edge => edge.Kind == EdgeKind.Calls && edge.SourceId == save.Id && edge.TargetId == helper.Id);
        Assert.Contains(result.Edges, edge => edge.Kind == EdgeKind.Calls && edge.SourceId == load.Id && edge.TargetId == helper.Id);
    }

    [Fact]
    public async Task RecordsClassMethodsAsMethodNodes()
    {
        var result = await AnalyzeScripts(new Dictionary<string, string>
        {
            ["widget.js"] = """
                function helper() { return true; }
                class Widget {
                  save() { helper(); }
                }
                """
        });

        var method = Assert.Single(result.Nodes, node => node.Name == "save");
        Assert.Equal(NodeKind.Method, method.Kind);
        var helper = Assert.Single(result.Nodes, node => node.Name == "helper");
        Assert.Contains(result.Edges, edge => edge.Kind == EdgeKind.Calls && edge.SourceId == method.Id && edge.TargetId == helper.Id);
    }

    [Fact]
    public async Task ResolvesTsconfigPathAliasImports()
    {
        var root = Path.Combine(Path.GetTempPath(), "codemap-web-alias-" + Guid.NewGuid());
        var files = new Dictionary<string, string>
        {
            ["tsconfig.json"] = """
                { "compilerOptions": { "baseUrl": ".", "paths": { "@lib/*": ["lib/*"] } } }
                """,
            ["lib/helper.ts"] = "export function helper() { return true; }",
            ["alias.ts"] = "import { helper } from '@lib/helper'; export function runAlias() { helper(); }"
        };
        var result = await AnalyzeScriptsAt(root, files);
        Assert.Contains(result.Edges, edge =>
            edge.Kind == EdgeKind.Imports
            && edge.SourceId.Contains("alias.ts", StringComparison.Ordinal)
            && edge.TargetId.Contains("helper.ts", StringComparison.Ordinal));
        Assert.Contains(result.Edges, edge => edge.Kind == EdgeKind.Calls && edge.TargetId.Contains("helper", StringComparison.Ordinal));
    }





    [Fact]
    public async Task ResolvesTsconfigPathAliasImports_WithJsoncCommentsAndTrailingComma()
    {
        var root = Path.Combine(Path.GetTempPath(), "codemap-web-alias-jsonc-" + Guid.NewGuid());
        var files = new Dictionary<string, string>
        {
            ["tsconfig.json"] = """
                {
                  // line comment before compilerOptions
                  "compilerOptions": {
                    "baseUrl": ".", /* block comment */
                    "paths": {
                      "@lib/*": ["lib/*"],
                    },
                  },
                }
                """,
            ["lib/helper.ts"] = "export function helper() { return true; }",
            ["alias.ts"] = "import { helper } from '@lib/helper'; export function runAlias() { helper(); }"
        };
        var result = await AnalyzeScriptsAt(root, files);
        Assert.Contains(result.Edges, edge =>
            edge.Kind == EdgeKind.Imports
            && edge.SourceId.Contains("alias.ts", StringComparison.Ordinal)
            && edge.TargetId.Contains("helper.ts", StringComparison.Ordinal));
        Assert.Contains(result.Edges, edge => edge.Kind == EdgeKind.Calls && edge.TargetId.Contains("helper", StringComparison.Ordinal));
    }



    [Fact]
    public async Task MalformedTsconfig_DegradesToEmptyAliasMapWithoutThrowing()
    {
        var root = Path.Combine(Path.GetTempPath(), "codemap-web-alias-malformed-" + Guid.NewGuid());
        var files = new Dictionary<string, string>
        {
            ["tsconfig.json"] = "{ this is not valid json at all",
            ["lib/helper.ts"] = "export function helper() { return true; }",
            ["alias.ts"] = "import { helper } from '@lib/helper'; export function runAlias() { helper(); }"
        };
        var result = await AnalyzeScriptsAt(root, files);
        Assert.DoesNotContain(result.Edges, edge =>
            edge.Kind == EdgeKind.Imports
            && edge.SourceId.Contains("alias.ts", StringComparison.Ordinal)
            && edge.TargetId.Contains("helper.ts", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ResolvesJsxComponentCalls()
    {
        var root = Path.Combine(Path.GetTempPath(), "codemap-web-jsx-" + Guid.NewGuid());
        var files = new Dictionary<string, string>
        {
            ["Button.tsx"] = "export function Button() { return null; }",
            ["page.tsx"] = """
                import { Button } from './Button';
                export function Page() {
                  return <Button />;
                }
                """
        };
        var result = await AnalyzeScriptsAt(root, files);
        Assert.Contains(result.Edges, edge =>
            edge.Kind == EdgeKind.Calls
            && edge.SourceId.Contains("page.tsx", StringComparison.Ordinal)
            && edge.TargetId.Contains("Button", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ResolvesDefaultExportImportBindings()
    {
        var result = await AnalyzeScriptsAt(Path.Combine(Path.GetTempPath(), "codemap-web-default-" + Guid.NewGuid()), new Dictionary<string, string>
        {
            ["default-export.ts"] = "export default function save() { return true; }",
            ["default-import.ts"] = "import save from './default-export'; export function runDefault() { save(); }"
        });
        Assert.Contains(result.Edges, edge => edge.Kind == EdgeKind.Calls && edge.Confidence == 0.90 && edge.ResolutionKind == EdgeResolutionKind.Syntactic);
    }

    [Fact]
    public async Task ResolvesNamespaceMemberCalls()
    {
        var result = await AnalyzeScriptsAt(Path.Combine(Path.GetTempPath(), "codemap-web-ns-" + Guid.NewGuid()), new Dictionary<string, string>
        {
            ["lib/math.ts"] = "export function add(a: number, b: number) { return a + b; }",
            ["namespace-call.ts"] = "import * as math from './lib/math'; export function compute() { math.add(1, 2); }"
        });
        Assert.Contains(result.Edges, edge => edge.Kind == EdgeKind.Calls && edge.TargetId.Contains("add", StringComparison.Ordinal) && edge.Confidence == 0.90);
    }

    [Fact]
    public void ScriptAstAnalyzer_ExtractsNestedMemberQualifier()
    {
        var analysis = WebScriptAstAnalyzer.Analyze(
            "import * as api from './api'; function load() { api.users.list(); }",
            "typescript",
            "sample.ts");

        var call = Assert.Single(analysis.Calls, item => item.Name == "list");
        Assert.Equal("api.users", call.Qualifier);
    }

    [Fact]
    public async Task IndexesStaticHttpRequestLiteralsWithoutInventingEdges()
    {
        var result = await AnalyzeScripts(new Dictionary<string, string>
        {
            ["app.ts"] = "export function load() { fetch('/api/users'); client.get(`https://example.test/items`); fetch(`/api/${id}`); }"
        });

        var routes = result.Nodes.Where(node => node.Kind == NodeKind.Route).ToArray();
        Assert.Equal(2, routes.Length);
        Assert.Contains(routes, node => node.Name == "/api/users");
        Assert.Contains(routes, node => node.Name == "https://example.test/items");
        Assert.DoesNotContain(result.Edges, edge => edge.TargetId.Contains("http:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ResolvesReexportChainWithFixedConfidence()
    {
        var result = await AnalyzeScriptsAt(Path.Combine(Path.GetTempPath(), "codemap-web-reexport-" + Guid.NewGuid()), new Dictionary<string, string>
        {
            ["lib/helper.ts"] = "export function helper() { return true; }",
            ["barrel.ts"] = "export { helper } from './lib/helper';",
            ["reexport-consumer.ts"] = "import { helper } from './barrel'; export function consume() { helper(); }"
        });
        Assert.Contains(result.Edges, edge => edge.Kind == EdgeKind.Calls && edge.Confidence == 0.85);
    }

    [Fact]
    public void ScriptAstAnalyzer_ExtractsImportBindingKinds()
    {
        var analysis = WebScriptAstAnalyzer.Analyze(
            """
            import def, { foo as local } from './mod';
            import * as ns from './ns';
            import './side-effect';
            """,
            "typescript",
            "sample.ts");
        Assert.Contains(analysis.Imports[0].Bindings, binding => binding is { LocalName: "def", ImportedName: "default", Kind: WebScriptAstAnalyzer.ScriptImportBindingKind.Default });
        Assert.Contains(analysis.Imports[0].Bindings, binding => binding is { LocalName: "local", ImportedName: "foo", Kind: WebScriptAstAnalyzer.ScriptImportBindingKind.Named });
        Assert.Contains(analysis.Imports[1].Bindings, binding => binding.Kind == WebScriptAstAnalyzer.ScriptImportBindingKind.Namespace);
        Assert.Equal(WebScriptAstAnalyzer.ScriptImportBindingKind.SideEffect, analysis.Imports[2].Bindings[0].Kind);
    }

    [Fact]
    public async Task DoesNotResolveNonExportedSymbolsThroughImports()
    {
        var result = await AnalyzeScriptsAt(Path.Combine(Path.GetTempPath(), "codemap-web-private-" + Guid.NewGuid()), new Dictionary<string, string>
        {
            ["module.ts"] = "function hidden() { return true; }",
            ["consumer.ts"] = "import { hidden } from './module'; export function consume() { hidden(); }"
        });

        Assert.DoesNotContain(result.Edges, edge =>
            edge.Kind == EdgeKind.Calls
            && edge.SourceId.Contains("consumer.ts", StringComparison.Ordinal)
            && edge.TargetId.Contains("hidden", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PreservesExportAliasDirection()
    {
        var result = await AnalyzeScriptsAt(Path.Combine(Path.GetTempPath(), "codemap-web-export-alias-" + Guid.NewGuid()), new Dictionary<string, string>
        {
            ["module.ts"] = "function foo() { return true; } export { foo as bar };",
            ["consumer.ts"] = "import { bar } from './module'; export function consume() { bar(); }"
        });

        Assert.Contains(result.Edges, edge =>
            edge.Kind == EdgeKind.Calls
            && edge.SourceId.Contains("consumer.ts", StringComparison.Ordinal)
            && edge.TargetId.Contains("module.ts::foo", StringComparison.Ordinal)
            && edge.Confidence == 0.90);
    }

    private static async Task<AnalysisResult> AnalyzeScriptsAt(string root, Dictionary<string, string> files)
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
        Directory.CreateDirectory(root);
        foreach (var (relative, content) in files)
        {
            var fullPath = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await File.WriteAllTextAsync(fullPath, content);
        }
        var sourceFiles = files.Keys.ToDictionary(
            relative => Path.GetFullPath(Path.Combine(root, relative)),
            relative => files[relative],
            StringComparer.OrdinalIgnoreCase);
        var first = sourceFiles.Keys.First();
        return await new WebLanguageAnalyzer().AnalyzeAsync(new AnalysisContext
        {
            ProjectName = "Fixture.Web",
            FilePath = first,
            Content = sourceFiles[first],
            SourceFiles = sourceFiles,
            RootDirectory = root
        }, CancellationToken.None);
    }

    private static Task<AnalysisResult> AnalyzeScripts(Dictionary<string, string> files)
    {
        var root = Path.Combine(Path.GetTempPath(), "codemap-web-" + Guid.NewGuid());
        var first = files.Keys.First();
        return new WebLanguageAnalyzer().AnalyzeAsync(new AnalysisContext
        {
            ProjectName = "Fixture.Web",
            FilePath = Path.Combine(root, first),
            Content = files[first],
            SourceFiles = files,
            RootDirectory = root
        }, CancellationToken.None);
    }

    [Fact]
    public async Task IndexingPersistsResolutionKindsAndConfidence()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), "codemap-web-resolution-" + Guid.NewGuid());
        CopyFixture(GetFixtureDirectory(), workingDirectory);
        try
        {
            await new IncrementalCodeMapIndexer().IndexAsync(workingDirectory, force: true, CancellationToken.None);

            var databasePath = Path.Combine(workingDirectory, ".codemap", "index.db");
            await using var connection = new SqliteConnection($"Data Source={databasePath}");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT kind, resolution_kind, confidence FROM edges";
            await using var reader = await command.ExecuteReaderAsync();
            var rows = new List<(string Kind, string ResolutionKind, double? Confidence)>();
            while (await reader.ReadAsync())
                rows.Add((reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetDouble(2)));

            Assert.Contains(rows, row => row is { Kind: "UsesCss", ResolutionKind: "syntactic", Confidence: 0.85 });
            Assert.Contains(rows, row => row is { Kind: "Calls", ResolutionKind: "syntactic", Confidence: 0.90 });
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(workingDirectory))
                Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task WebWorkspaceIndexer_AnalyzeAsync_ThrowsBeforeEnumeratingWhenAlreadyCancelled()
    {
        var root = Path.Combine(Path.GetTempPath(), "codemap-web-missing-" + Guid.NewGuid());
        using var cts = new CancellationTokenSource();
        cts.Cancel();




        await Assert.ThrowsAsync<OperationCanceledException>(
            () => new WebWorkspaceIndexer().AnalyzeAsync(root, cts.Token));
    }

    [Fact]
    public async Task WebWorkspaceIndexer_AnalyzeAsync_NonCancelled_ReturnsAnalyzedProject()
    {
        var root = Path.Combine(Path.GetTempPath(), "codemap-web-indexer-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "save.js"), "function save() { return true; }");

            var project = await new WebWorkspaceIndexer().AnalyzeAsync(root, CancellationToken.None);

            Assert.NotNull(project);
            Assert.Contains(project!.Result.Nodes, node => node.Kind == NodeKind.Function && node.Name == "save");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string GetFixtureDirectory()
    {
        var testDir = AppContext.BaseDirectory;
        var solutionRoot = Path.GetFullPath(Path.Combine(testDir, "..", "..", "..", "..", ".."));
        return Path.Combine(solutionRoot, "tests", "Fixtures", "WebFixture");
    }

    private static void CopyFixture(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*.*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var targetPath = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.Copy(file, targetPath, overwrite: true);
        }
    }
}
