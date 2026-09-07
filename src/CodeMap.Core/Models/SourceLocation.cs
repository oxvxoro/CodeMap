namespace CodeMap.Core.Models;

public sealed class SourceLocation
{
    public int StartLine { get; init; }

    public int StartColumn { get; init; }

    public int EndLine { get; init; }

    public int EndColumn { get; init; }
}
