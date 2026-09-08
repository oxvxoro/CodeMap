namespace CodeMap.Scip;

public enum ScipSymbolKind
{
    Unknown,
    Namespace,
    Class,
    Interface,
    Struct,
    Enum,
    Method,
    Constructor,
    Function,
    Property,
    Field,
    Event,
    Local,
    Parameter,
    TypeParameter
}

public sealed record ScipRange(int StartLine, int StartColumn, int EndLine, int EndColumn);

public sealed record ScipOccurrence(
    string Symbol,
    ScipRange Range,
    bool IsDefinition,
    string? EnclosingSymbol = null,
    ScipRange? EnclosingRange = null);

public sealed record ScipRelationship(string Symbol, bool IsReference, bool IsImplementation, bool IsTypeDefinition, bool IsDefinition);

public sealed record ScipSymbolInformation(
    string Symbol,
    string DisplayName,
    ScipSymbolKind Kind,
    IReadOnlyList<ScipRelationship>? Relationships = null);

public sealed record ScipDocument(
    string RelativePath,
    string Language,
    IReadOnlyList<ScipOccurrence> Occurrences,
    IReadOnlyList<ScipSymbolInformation> Symbols);

public sealed record ScipIndex(IReadOnlyList<ScipDocument> Documents);
