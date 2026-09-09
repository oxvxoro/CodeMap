using CodeMap.Core.Analysis;
using CodeMap.Core.Models;

namespace CodeMap.Web;

/// <summary>
/// Web analysis composition boundary. HTML/CSS/script parsing and relation
/// resolution remain behavior-compatible with WebLanguageAnalyzer while
/// adapters gain a single session entry point.
/// </summary>
public sealed class WebAnalysisCoordinator(WebLanguageAnalyzer? analyzer = null)
{
    private readonly WebLanguageAnalyzer _analyzer = analyzer ?? new WebLanguageAnalyzer();

    public Task<AnalysisResult> AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken = default) =>
        _analyzer.AnalyzeAsync(context, cancellationToken);

    public async Task<WebAnalysisStages> AnalyzeStagesAsync(AnalysisContext context, CancellationToken cancellationToken = default)
    {
        var result = await AnalyzeAsync(context, cancellationToken).ConfigureAwait(false);
        var files = (context.SourceFiles?.Keys ?? Array.Empty<string>()).ToArray();
        return new WebAnalysisStages(
            result,
            files.Where(HtmlDocumentAnalyzer.Supports).ToArray(),
            files.Where(CssDocumentAnalyzer.Supports).ToArray(),
            files.Where(ScriptDocumentAnalyzer.Supports).ToArray(),
            WebRelationBuilder.SemanticEdges(result));
    }
}

public sealed record WebAnalysisStages(
    AnalysisResult Result,
    IReadOnlyList<string> HtmlFiles,
    IReadOnlyList<string> CssFiles,
    IReadOnlyList<string> ScriptFiles,
    IReadOnlyList<CodeEdge> Relations);
