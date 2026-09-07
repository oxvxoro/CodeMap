using CodeMap.Core.Models;

namespace CodeMap.Core.Analysis;

public interface ILanguageAnalyzer
{
    string Language { get; }

    bool CanAnalyze(string filePath);

    Task<AnalysisResult> AnalyzeAsync(
        AnalysisContext context,
        CancellationToken cancellationToken);
}
