namespace CodeMap.Storage.Queries;

/// <summary>
/// Backend-independent options used by query application services.
/// Existing <see cref="CodeMap.Storage.CodeMapQueryService"/> overloads remain
/// available while callers migrate to these contracts incrementally.
/// </summary>
public sealed record QueryOptions(
    int MaxResults = QueryLimits.DefaultMaxResults,
    int Depth = 1,
    double MinConfidence = QueryLimits.DefaultMinConfidence);

public sealed record RelationQuery(
    string SourceId,
    string TargetId,
    string? EdgeKind = null,
    QueryOptions? Options = null);

public sealed record TraversalQuery(
    string SymbolId,
    int Depth = 1,
    QueryOptions? Options = null);

public sealed record ImpactQuery(
    IReadOnlyList<string> RootIds,
    int Depth = 1,
    string Profile = "code",
    QueryOptions? Options = null);

public sealed record FlowQuery(
    string EntryId,
    string Kind = "all",
    int Depth = QueryLimits.FlowDefaultDepth,
    QueryOptions? Options = null);
