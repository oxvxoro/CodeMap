using CodeMap.Core.Analysis;
using CodeMap.Core.Models;

namespace CodeMap.CSharp.Analysis;

/// <summary>
/// Explicit entry point for the staged C# pipeline. The legacy analyzer remains
/// the implementation during the migration so IDs and semantic confidence do
/// not change while callers move to this boundary.
/// </summary>
public sealed class CSharpCompilationAnalyzer(CSharpLanguageAnalyzer? analyzer = null)
{
    private readonly CSharpLanguageAnalyzer _analyzer = analyzer ?? new CSharpLanguageAnalyzer();

    public Task<AnalysisResult> AnalyzeAsync(AnalysisContext context, CancellationToken cancellationToken = default) =>
        _analyzer.AnalyzeAsync(context, cancellationToken);

    public async Task<CSharpAnalysisStages> AnalyzeStagesAsync(AnalysisContext context, CancellationToken cancellationToken = default)
    {
        var result = await AnalyzeAsync(context, cancellationToken).ConfigureAwait(false);
        var declarations = DeclarationCollector.Collect(result);
        return new CSharpAnalysisStages(
            result,
            declarations,
            AnonymousFunctionCollector.Collect(result),
            SemanticRelationCollector.Collect(result),
            PublicSurfaceFingerprinter.Compute(declarations),
            ExternalRootCollector.Collect(result));
    }
}

public sealed record CSharpAnalysisStages(
    AnalysisResult Result,
    IReadOnlyList<CodeNode> Declarations,
    IReadOnlyList<CodeNode> AnonymousFunctions,
    IReadOnlyList<CodeEdge> SemanticRelations,
    string PublicSurfaceFingerprint,
    IReadOnlySet<string> ExternalRoots);
