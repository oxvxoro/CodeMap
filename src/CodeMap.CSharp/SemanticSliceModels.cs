using CodeMap.Core.Models;

namespace CodeMap.CSharp;

public enum SliceDirection
{
    Backward,
    Forward
}

public enum SliceOperationKind
{
    Parameter,
    Declaration,
    Assignment,
    Invocation,
    Return,
    Condition
}

public enum SliceDependencyKind
{
    Definition,
    Use,
    Assignment,
    Argument,
    Return,
    Condition,
    Capture
}

public sealed record SemanticSliceRequest(
    SliceDirection Direction = SliceDirection.Backward,
    int? Line = null,
    int? Column = null,
    int MaxResults = 80,
    string Query = "",
    bool IncludeSource = false);

public sealed record SliceItem(
    int Id,
    SliceOperationKind Kind,
    string? Symbol,
    string Display,
    SourceLocation Location);

public sealed record SliceDependency(int Source, int Target, SliceDependencyKind Kind);

public sealed record SemanticSliceAnalysis(
    IReadOnlyList<SliceItem> Items,
    IReadOnlyList<SliceDependency> Dependencies,
    bool Truncated);
