namespace CodeMap.Core.Analysis;

public sealed class AnalysisContext
{
    /// <summary>분석 대상 프로젝트 이름.</summary>
    public required string ProjectName { get; init; }

    /// <summary>분석할 파일의 전체 경로.</summary>
    public required string FilePath { get; init; }

    /// <summary>파일 내용. null이면 파일에서 직접 읽는다.</summary>
    public string? Content { get; init; }


    /// <summary>동일한 의미 분석에 함께 사용할 추가 소스 파일.</summary>
    public IReadOnlyDictionary<string, string>? SourceFiles { get; init; }


    /// <summary>소스의 상대 경로를 계산할 기준 디렉터리.</summary>
    public string? RootDirectory { get; init; }
}
