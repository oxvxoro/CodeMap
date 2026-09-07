using System.Text.Json;

namespace CodeMap.Core.Tests;

public sealed class ContractDocsTests
{
    private static string SolutionRoot =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    [Fact]
    public void AgentIntegrationDocs_Section10_UsesCurrentV4V5Contract()
    {
        var path = Path.Combine(SolutionRoot, "docs", "AGENT_INTEGRATION.md");
        var text = File.ReadAllText(path);
        var section10Start = text.IndexOf("## 10.", StringComparison.Ordinal);
        var section11Start = text.IndexOf("## 11.", StringComparison.Ordinal);
        Assert.True(section10Start >= 0 && section11Start > section10Start);

        var section10 = text[section10Start..section11Start];
        Assert.Contains("version 4 / version 5", section10, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("version 2 / version 3", section10, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"version\": 2", section10, StringComparison.Ordinal);
        Assert.DoesNotContain("\"version\": 3", section10, StringComparison.Ordinal);
        Assert.Contains("analysisComplete", section10, StringComparison.Ordinal);
        Assert.Contains("unresolvedChangedFiles", section10, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("cli-json-v2.schema.json")]
    [InlineData("cli-json-v3.schema.json")]
    public void HistoricalSchemaFiles_ArePrettyPrintedAndParseable(string fileName)
    {
        var path = Path.Combine(SolutionRoot, "docs", fileName);
        var text = File.ReadAllText(path);
        Assert.Contains('\n', text);
        Assert.EndsWith("\n", text);
        using var document = JsonDocument.Parse(text);
        Assert.Equal($"https://codemap.local/schemas/{fileName.Replace(".schema.json", ".json")}",
            document.RootElement.GetProperty("$id").GetString());
    }
}
