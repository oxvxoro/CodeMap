namespace CodeMap.Core.Models;

public sealed record AdditionalLocation(string FilePath, SourceLocation Location);

public sealed class CodeNode
{
    public required string Id { get; init; }

    public required NodeKind Kind { get; init; }

    public required string Name { get; init; }

    public required string QualifiedName { get; init; }

    public string? FilePath { get; init; }

    public SourceLocation? SourceLocation { get; init; }

    public List<AdditionalLocation> AdditionalLocations { get; init; } = [];

    public required string Language { get; init; }

    public string? Signature { get; init; }

    public string? Visibility { get; init; }
}
