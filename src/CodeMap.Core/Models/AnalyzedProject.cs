namespace CodeMap.Core.Models;

public sealed record AnalyzedSourceFile(
    string FilePath,
    string RelativePath,
    string Content,
    string Language);

public sealed record AnalyzedProject(
    string ProjectName,
    string ProjectPath,
    IReadOnlyList<AnalyzedSourceFile> Files,
    AnalysisResult Result,
    AnalyzerCapabilityLevel CapabilityLevel = AnalyzerCapabilityLevel.Semantic);
