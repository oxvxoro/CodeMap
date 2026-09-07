namespace CodeMap.Core.Models;

public sealed class CodeEdge
{
    public required string SourceId { get; init; }

    public required string TargetId { get; init; }

    public required EdgeKind Kind { get; init; }

    public EdgeResolutionKind ResolutionKind { get; init; } = EdgeResolutionKind.Semantic;

    public double? Confidence { get; init; }

    public SourceLocation? SourceLocation { get; init; }







    /// <summary>
    /// 저장 시 프로젝트 간 동일 심볼 이름을 구분하기 위한 임시 대상 프로젝트 힌트.
    /// 논리적 엣지 식별자에는 포함하지 않으며 저장하거나 CLI/MCP로 노출하지 않는다.
    /// </summary>
    public string? TargetProject { get; init; }
}
