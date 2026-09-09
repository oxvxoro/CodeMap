using CodeMap.Core.Models;

namespace CodeMap.Engine.Application;

public sealed record QueryError(string Code, string Message);

public sealed record ApplicationResponse<T>(T? Value, QueryError? Error, bool Stale)
{
    public bool Succeeded => Error is null;

    public static ApplicationResponse<T> Success(T value, bool stale = false) => new(value, null, stale);

    public static ApplicationResponse<T> Failure(QueryError error, bool stale = false) => new(default, error, stale);
}

public sealed record FindRequest(string Query, string? Root = null, int MaxResults = 20);

public sealed record RelationsRequest(
    string Source,
    string Target,
    string? Root = null,
    string? EdgeKind = null,
    int MaxResults = 20,
    double MinConfidence = 0);

public sealed record ImpactRequest(
    string Query,
    string? Root = null,
    int Depth = 2,
    int MaxResults = 20,
    string Profile = "code",
    double MinConfidence = 0);

public sealed record FlowRequest(
    string Entry,
    string Kind = "all",
    int Depth = 4,
    string? Root = null,
    int MaxResults = 20,
    double MinConfidence = 0);

public sealed record StatusRequest(string Root, bool CheckFreshness = false);

public sealed record RefreshIndexRequest(string Root, bool Force = false);

public sealed record ContextRequest(
    string Task,
    string? Root = null,
    int MaxResults = 5,
    int TokenBudget = 500);

public sealed record ApplicationContextResult(
    IReadOnlyList<IndexedSymbol> Matches,
    RepoMap Map);

public sealed record ApplicationImpactResult(
    IndexedSymbol Symbol,
    IReadOnlyList<ImpactItem> Items,
    IReadOnlyDictionary<string, IndexedFile> Files);

public sealed record ApplicationFindResult(
    SymbolSearchResult Resolution,
    IReadOnlyDictionary<string, IReadOnlyList<IndexedSymbol>> Members)
{
    public IReadOnlyList<IndexedSymbol> Matches => Resolution.Matches;
    public bool IsAmbiguous => Resolution.IsAmbiguous;
}

public sealed record ApplicationFlowResult(
    IndexedSymbol Entry,
    IReadOnlyList<ImpactItem> Items,
    IReadOnlyDictionary<string, IndexedSymbol> Sources,
    IReadOnlyDictionary<string, IndexedFile> Files);
