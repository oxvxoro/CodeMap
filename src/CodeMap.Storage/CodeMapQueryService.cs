using CodeMap.Core.Models;
using Microsoft.Data.Sqlite;

namespace CodeMap.Storage;

public sealed record SymbolSearchResult(IReadOnlyList<IndexedSymbol> Matches, bool IsAmbiguous = false);


public sealed partial class CodeMapQueryService : IAsyncDisposable
{
    private static readonly HashSet<EdgeKind> ReferenceKinds =
    [EdgeKind.References, EdgeKind.Calls, EdgeKind.Constructs, EdgeKind.UsesType, EdgeKind.Implements, EdgeKind.Inherits];

    private static readonly CodeMapSnapshot EmptySnapshot = new()
    {
        Files = [],
        Symbols = [],
        Edges = []
    };

    private readonly CodeMapSnapshot _graph;
    private readonly Dictionary<string, IndexedSymbol> _byId;
    private readonly ILookup<string, IndexedEdge> _bySource;
    private readonly ILookup<string, IndexedEdge> _byTarget;
    private readonly SqliteConnection? _connection;
    private readonly bool _ownsConnection;
    private IReadOnlyList<IndexedFile>? _filesCache;

    public CodeMapQueryService(CodeMapSnapshot graph)
        : this(graph, null, ownsConnection: false)
    {
    }

    public CodeMapQueryService(CodeMapSnapshot graph, SqliteConnection? connection)
        : this(graph, connection, ownsConnection: connection is not null)
    {
    }

    public CodeMapQueryService(SqliteConnection connection)
        : this(EmptySnapshot, connection, ownsConnection: true)
    {
    }

    private CodeMapQueryService(CodeMapSnapshot graph, SqliteConnection? connection, bool ownsConnection)
    {
        ArgumentNullException.ThrowIfNull(graph);
        _graph = graph;
        _byId = graph.Symbols.ToDictionary(symbol => symbol.Id, StringComparer.Ordinal);
        _bySource = graph.Edges.ToLookup(edge => edge.SourceId, StringComparer.Ordinal);
        _byTarget = graph.Edges.ToLookup(edge => edge.TargetId, StringComparer.Ordinal);
        _connection = connection;
        _ownsConnection = ownsConnection;
    }

    public IReadOnlyList<IndexedSymbol> Find(string query, int maxResults)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        if (_connection is not null)
            return RankFindResults(QueryFindCandidates(query), query, maxResults);

        var ranked = _graph.Symbols
            .Select(symbol => (Symbol: symbol, Score: FindScore(symbol, query)))
            .Where(item => item.Score >= 0)


            .GroupBy(item => item.Score / 100)
            .OrderByDescending(group => group.Key)
            .FirstOrDefault()?
            .ToArray() ?? Array.Empty<(IndexedSymbol Symbol, int Score)>();
        return ranked
            .OrderByDescending(item => item.Score)



            .ThenBy(item => SqliteCodeMapStore.IsExternalProject(item.Symbol.Project))
            .ThenBy(item => item.Symbol.QualifiedName, StringComparer.Ordinal)
            .ThenBy(item => item.Symbol.Signature ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(item => item.Symbol.Id, StringComparer.Ordinal)
            .Take(Math.Max(1, maxResults))
            .Select(item => item.Symbol)
            .ToArray();
    }

    public IndexedSymbol? FindById(string id) =>
        _byId.GetValueOrDefault(id) ?? (_connection is null ? null : QuerySymbolById(id));







    public IReadOnlyDictionary<string, IndexedSymbol> FindByIds(IEnumerable<string> ids)
    {
        var distinctIds = ids.Distinct(StringComparer.Ordinal).ToArray();
        if (_connection is null)
            return distinctIds
                .Select(id => _byId.GetValueOrDefault(id))
                .Where(symbol => symbol is not null)
                .ToDictionary(symbol => symbol!.Id, symbol => symbol!, StringComparer.Ordinal);

        var missing = distinctIds.Where(id => !_byId.ContainsKey(id)).ToArray();
        var resolved = missing.Length > 0
            ? QuerySymbolsByIds(missing)
            : new Dictionary<string, IndexedSymbol>(StringComparer.Ordinal);
        return distinctIds
            .Select(id => _byId.GetValueOrDefault(id) ?? resolved.GetValueOrDefault(id))
            .Where(symbol => symbol is not null)
            .ToDictionary(symbol => symbol!.Id, symbol => symbol!, StringComparer.Ordinal);
    }

    public IReadOnlyList<IndexedFile> Files()
    {
        if (_connection is not null)
        {
            if (_filesCache is not null)
                return _filesCache;
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT id, project, relative_path, language FROM files ORDER BY project, relative_path";
            using var reader = command.ExecuteReader();
            var files = new List<IndexedFile>();
            while (reader.Read())
                files.Add(new IndexedFile(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
            return _filesCache = files;
        }
        return _graph.Files;
    }











    public IReadOnlyDictionary<string, IndexedFile> FindFilesByIds(IEnumerable<string> ids)
    {
        var distinctIds = ids.Distinct(StringComparer.Ordinal).ToArray();
        if (distinctIds.Length == 0)
            return new Dictionary<string, IndexedFile>(StringComparer.Ordinal);

        if (_connection is null)
        {
            var byId = new HashSet<string>(distinctIds, StringComparer.Ordinal);
            return _graph.Files
                .Where(file => byId.Contains(file.Id))
                .ToDictionary(file => file.Id, file => file, StringComparer.Ordinal);
        }

        return QueryFilesByIds(distinctIds);
    }

    public IReadOnlyList<IndexedSymbol> SymbolsInFiles(IReadOnlyCollection<string> relativePaths, int maxResults = 500)
    {
        var normalized = relativePaths
            .Select(CodeMapPath.Normalize)
            .Where(path => path.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (normalized.Count == 0)
            return [];

        if (_connection is not null)
            return QuerySymbolsInFiles(normalized, maxResults);

        return _graph.Symbols
            .Where(symbol => MatchesChangedFilePath(normalized, symbol))
            .Where(symbol => symbol.Kind is not (NodeKind.File or NodeKind.Namespace))
            .OrderBy(symbol => symbol.QualifiedName, StringComparer.Ordinal)
            .ThenBy(symbol => symbol.Id, StringComparer.Ordinal)
            .Take(Math.Max(1, maxResults))
            .ToArray();
    }

    public IReadOnlyList<ImpactItem> ImpactUnion(IEnumerable<IndexedSymbol> roots, int depth, int maxResults) =>
        ImpactUnion(roots, depth, maxResults, "code");

    public IReadOnlyList<ImpactItem> ImpactUnion(IEnumerable<IndexedSymbol> roots, int depth, int maxResults, string profile)
    {
        var materializedRoots = roots as IReadOnlyList<IndexedSymbol> ?? roots.ToArray();
        var merged = new Dictionary<string, ImpactItem>(StringComparer.Ordinal);

        if (_connection is not null && materializedRoots.Count > 1)
        {
            var itemsByRoot = new Dictionary<string, List<ImpactItem>>(StringComparer.Ordinal);



            var distinctRootIds = materializedRoots.Select(root => root.Id).Distinct(StringComparer.Ordinal);
            const int parametersPerRoot = 2;
            const int fixedParameterCount = 2;
            var rootsPerSqliteBatch = Math.Max(1, (SqliteVariableChunkSize - fixedParameterCount) / parametersPerRoot);
            foreach (var chunk in distinctRootIds.Chunk(rootsPerSqliteBatch))
            {
                foreach (var item in QueryImpactUnion(chunk, depth, maxResults, profile))
                {
                    if (!itemsByRoot.TryGetValue(item.RootId!, out var list))
                        itemsByRoot[item.RootId!] = list = [];
                    list.Add(item);
                }
            }




            foreach (var root in materializedRoots)
            {
                if (!itemsByRoot.TryGetValue(root.Id, out var items))
                    continue;
                foreach (var item in items)
                {
                    if (!merged.TryGetValue(item.Symbol.Id, out var existing) || item.Depth < existing.Depth)
                        merged[item.Symbol.Id] = item;
                }
            }
        }
        else
        {
            foreach (var root in materializedRoots)
            {
                foreach (var item in Impact(root, depth, maxResults, profile))
                {
                    var rootedItem = item with { RootId = root.Id };
                    if (!merged.TryGetValue(rootedItem.Symbol.Id, out var existing) || rootedItem.Depth < existing.Depth)
                        merged[rootedItem.Symbol.Id] = rootedItem;
                }
            }
        }

        return merged.Values
            .OrderBy(item => item.Depth)
            .ThenBy(item => item.Symbol.QualifiedName, StringComparer.Ordinal)
            .ThenBy(item => item.Symbol.Id, StringComparer.Ordinal)
            .Take(Math.Max(1, maxResults))
            .ToArray();
    }













    public SymbolSearchResult ResolveSymbol(string query, bool callableOnly, int maxResults)
    {
        if (_connection is not null)
        {
            var exactMatches = QueryExactSymbols(query, callableOnly);
            var deduped = PreferSourceOverExternal(exactMatches, query);
            var resolved = deduped.Length > 0
                ? deduped
                : Find(query, maxResults).Where(symbol => !callableOnly || symbol.Kind is NodeKind.Method or NodeKind.Constructor or NodeKind.Function).ToArray();
            return new SymbolSearchResult(resolved, resolved.Length > 1);
        }

        var exactInMemory = _graph.Symbols
            .Where(symbol => !callableOnly || symbol.Kind is NodeKind.Method or NodeKind.Constructor or NodeKind.Function)
            .Where(symbol => string.Equals(symbol.QualifiedName, query, StringComparison.OrdinalIgnoreCase)
                || string.Equals(symbol.DisplayName, query, StringComparison.OrdinalIgnoreCase)
                || string.Equals(symbol.Name, query, StringComparison.OrdinalIgnoreCase)
                || string.Equals(symbol.Id, query, StringComparison.OrdinalIgnoreCase)
                || string.Equals(symbol.QualifiedName + (symbol.Signature ?? string.Empty), query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(symbol => symbol.DisplayName, StringComparer.Ordinal)
            .ThenBy(symbol => symbol.Id, StringComparer.Ordinal)
            .ToArray();
        var dedupedInMemory = PreferSourceOverExternal(exactInMemory, query);
        var matches = dedupedInMemory.Length > 0
            ? dedupedInMemory
            : Find(query, maxResults).Where(symbol => !callableOnly || symbol.Kind is NodeKind.Method or NodeKind.Constructor or NodeKind.Function).ToArray();
        return new SymbolSearchResult(matches, matches.Length > 1);
    }








    private static IndexedSymbol[] PreferSourceOverExternal(IReadOnlyList<IndexedSymbol> matches, string query)
    {
        if (matches.Count <= 1)
            return matches as IndexedSymbol[] ?? matches.ToArray();
        if (matches.Any(symbol => string.Equals(symbol.Id, query, StringComparison.OrdinalIgnoreCase)))
            return matches as IndexedSymbol[] ?? matches.ToArray();
        var sourceOnly = matches.Where(symbol => !SqliteCodeMapStore.IsExternalProject(symbol.Project)).ToArray();
        return sourceOnly.Length > 0 ? sourceOnly : (matches as IndexedSymbol[] ?? matches.ToArray());
    }

    public SymbolSearchResult ResolveCallable(string query)
    {
        if (_connection is not null)
        {
            var exactMatches = QueryExactSymbols(query, callableOnly: true);
            return new SymbolSearchResult(exactMatches, exactMatches.Count > 1);
        }

        var exactInMemory = _graph.Symbols
            .Where(symbol => symbol.Kind is NodeKind.Method or NodeKind.Constructor or NodeKind.Function)
            .Where(symbol => string.Equals(symbol.QualifiedName, query, StringComparison.OrdinalIgnoreCase)
                || string.Equals(symbol.DisplayName, query, StringComparison.OrdinalIgnoreCase)
                || string.Equals(symbol.Id, query, StringComparison.OrdinalIgnoreCase)
                || string.Equals(symbol.QualifiedName + (symbol.Signature ?? string.Empty), query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(symbol => symbol.DisplayName, StringComparer.Ordinal)
            .ThenBy(symbol => symbol.Id, StringComparer.Ordinal)
            .ToArray();
        return new SymbolSearchResult(exactInMemory, exactInMemory.Length > 1);
    }

    public IReadOnlyList<IndexedEdge> Outgoing(string symbolId, params EdgeKind[] kinds) =>
        FilterEdges(_bySource[symbolId], kinds);

    public IReadOnlyList<IndexedEdge> Incoming(string symbolId, params EdgeKind[] kinds) =>
        FilterEdges(_byTarget[symbolId], kinds);

    public IReadOnlyList<IndexedSymbol> Members(IndexedSymbol container, int maxResults = 8)
    {
        if (_connection is not null)
            return QueryRelatedSymbols(
                """
                SELECT s.id, s.file_id, f.project, f.relative_path, s.kind, s.name,
                       s.qualified_name, s.signature, s.start_line, s.end_line, s.visibility, s.language
                FROM edges e
                JOIN symbols s ON s.id = e.target_id
                JOIN files f ON f.id = s.file_id
                WHERE e.source_id = $symbolId
                  AND e.kind = 'Contains'
                  AND s.kind IN ('Method', 'Constructor', 'Property', 'Field', 'Event')
                ORDER BY COALESCE(s.start_line, 2147483647), s.name, s.id
                LIMIT $maxResults
                """,
                container.Id,
                maxResults);

        return _bySource[container.Id]
            .Where(edge => edge.Kind == EdgeKind.Contains)
            .Select(edge => _byId.GetValueOrDefault(edge.TargetId))
            .Where(symbol => symbol is not null)
            .Select(symbol => symbol!)
            .Where(symbol => symbol.Kind is NodeKind.Method or NodeKind.Constructor or NodeKind.Property or NodeKind.Field or NodeKind.Event)
            .OrderBy(symbol => symbol.StartLine ?? int.MaxValue)
            .ThenBy(symbol => symbol.Name, StringComparer.Ordinal)
            .Take(maxResults)
            .ToArray();
    }

    public IReadOnlyList<ImpactItem> Impact(IndexedSymbol root, int depth, int maxResults) =>
        Impact(root, depth, maxResults, "code");

    public static bool IsValidImpactProfile(string profile) => profile is "code" or "app";

    private static HashSet<EdgeKind> ResolveImpactEdgeKinds(string profile) => profile switch
    {
        "code" => ReferenceKinds,
        "app" => new HashSet<EdgeKind>(ReferenceKinds.Concat(ResolveFlowEdgeKinds("all"))),
        _ => throw new ArgumentException($"Unsupported impact profile '{profile}'. Use code or app.", nameof(profile))
    };








    public IReadOnlyList<ImpactItem> Impact(IndexedSymbol root, int depth, int maxResults, string profile)
    {
        var edgeKinds = ResolveImpactEdgeKinds(profile);
        if (_connection is not null)
            return QueryImpact(root.Id, depth, maxResults, profile);

        var reverse = _graph.Edges
            .Where(edge => edgeKinds.Contains(edge.Kind))
            .Where(edge => IsDiSelectedOrUnregisteredImplementation(edge))
            .GroupBy(edge => edge.TargetId)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var result = new List<ImpactItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { root.Id };
        var frontier = new[] { root.Id };
        for (var currentDepth = 1; currentDepth <= Math.Max(0, depth) && frontier.Length > 0; currentDepth++)
        {
            var next = new List<string>();
            foreach (var targetId in frontier)
            {
                if (!reverse.TryGetValue(targetId, out var incoming))
                    continue;
                foreach (var edge in incoming.OrderBy(edge => edge.SourceId, StringComparer.Ordinal).ThenBy(edge => edge.Kind))
                {
                    if (!seen.Add(edge.SourceId) || !_byId.TryGetValue(edge.SourceId, out var source))
                        continue;
                    result.Add(new ImpactItem(source, edge, currentDepth, root.Id));
                    next.Add(source.Id);
                    if (result.Count >= Math.Max(1, maxResults))
                        return result;
                }
            }
            frontier = next.Distinct(StringComparer.Ordinal).ToArray();
        }
        return result;
    }

    public IReadOnlyList<IndexedSymbol> Implementations(IndexedSymbol symbol, int maxResults) =>
        ImplementationRelations(symbol, maxResults).Select(relation => relation.Symbol).DistinctBy(symbol => symbol.Id).ToArray();

    public IReadOnlyList<IndexedRelation> ImplementationRelations(IndexedSymbol symbol, int maxResults)
    {
        if (_connection is not null)
            return QueryEdgeRelations(
                """
                SELECT id, file_id, project, relative_path, kind, name,
                       qualified_name, signature, start_line, end_line, visibility, language,
                       source_id, target_id, edge_kind, source_file_id, line, resolution_kind, confidence,
                       start_column, end_line_via, end_column
                FROM (
                SELECT s.id, s.file_id, f.project, f.relative_path, s.kind, s.name,
                       s.qualified_name, s.signature, s.start_line, s.end_line, s.visibility, s.language,
                       e.source_id, e.target_id, e.kind AS edge_kind, e.source_file_id, e.line, e.resolution_kind, e.confidence,
                       e.start_column, e.end_line AS end_line_via, e.end_column
                FROM edges e JOIN symbols s ON s.id = e.target_id JOIN files f ON f.id = s.file_id
                WHERE e.source_id = $symbolId AND e.kind = 'ImplementedBy'
                UNION ALL
                SELECT s.id, s.file_id, f.project, f.relative_path, s.kind, s.name,
                       s.qualified_name, s.signature, s.start_line, s.end_line, s.visibility, s.language,
                       e.source_id, e.target_id, e.kind AS edge_kind, e.source_file_id, e.line, e.resolution_kind, e.confidence,
                       e.start_column, e.end_line AS end_line_via, e.end_column
                FROM edges e JOIN symbols s ON s.id = e.source_id JOIN files f ON f.id = s.file_id
                WHERE e.target_id = $symbolId AND e.kind = 'Implements'
                )
                ORDER BY project, qualified_name, COALESCE(signature, ''), id
                LIMIT $maxResults
                """,
                symbol.Id,
                maxResults);

        return _bySource[symbol.Id]
            .Where(edge => edge.Kind == EdgeKind.ImplementedBy)
            .Select(edge => TryRelation(edge.TargetId, edge))
            .Concat(_byTarget[symbol.Id]
                .Where(edge => edge.Kind == EdgeKind.Implements)
                .Select(edge => TryRelation(edge.SourceId, edge)))
            .Where(relation => relation is not null)
            .Select(relation => relation!)
            .DistinctBy(relation => relation.Symbol.Id)
            .OrderBy(relation => relation.Symbol.Project, StringComparer.Ordinal)
            .ThenBy(relation => relation.Symbol.QualifiedName, StringComparer.Ordinal)
            .ThenBy(relation => relation.Symbol.Signature ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(relation => relation.Symbol.Id, StringComparer.Ordinal)
            .Take(Math.Max(1, maxResults))
            .ToArray();
    }

    public IReadOnlyList<IndexedSymbol> Callees(IndexedSymbol root, int depth, int maxResults) =>
        CalleeRelations(root, depth, maxResults).Select(relation => relation.Symbol).ToArray();

    public IReadOnlyList<IndexedRelation> CalleeRelations(IndexedSymbol root, int depth, int maxResults)
    {
        if (_connection is not null)
            return QueryCalleeRelations(root.Id, depth, maxResults);

        var result = new List<IndexedRelation>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { root.Id };
        var frontier = new[] { root.Id };
        for (var level = 0; level < Math.Max(1, depth) && frontier.Length > 0; level++)
        {
            var next = new List<string>();
            foreach (var sourceId in frontier)
            {
                foreach (var edge in Outgoing(sourceId, EdgeKind.Calls)
                    .OrderBy(edge => edge.TargetId, StringComparer.Ordinal))
                {
                    var relation = TryRelation(edge.TargetId, edge);
                    if (relation is null || !seen.Add(relation.Symbol.Id))
                        continue;
                    result.Add(relation);
                    next.Add(relation.Symbol.Id);
                    if (result.Count >= Math.Max(1, maxResults))
                        return result;
                }
            }
            frontier = next.Distinct(StringComparer.Ordinal).ToArray();
        }
        return result;
    }

    public IReadOnlyList<IndexedSymbol> ReferencedBy(IndexedSymbol symbol, int maxResults) =>
        ReferencedByRelations(symbol, maxResults).Select(relation => relation.Symbol).DistinctBy(symbol => symbol.Id).ToArray();

    public IReadOnlyList<IndexedRelation> ReferencedByRelations(IndexedSymbol symbol, int maxResults)
    {
        if (_connection is not null)
            return QueryEdgeRelations(
                """
                SELECT s.id, s.file_id, f.project, f.relative_path, s.kind, s.name,
                       s.qualified_name, s.signature, s.start_line, s.end_line, s.visibility, s.language,
                       e.source_id, e.target_id, e.kind, e.source_file_id, e.line, e.resolution_kind, e.confidence,
                       e.start_column, e.end_line, e.end_column
                FROM edges e JOIN symbols s ON s.id = e.source_id JOIN files f ON f.id = s.file_id
                WHERE e.target_id = $symbolId AND e.kind IN ('References', 'Calls', 'Constructs', 'UsesType', 'Implements', 'Inherits')
                ORDER BY f.project, s.qualified_name, COALESCE(s.signature, ''), s.id, e.kind
                LIMIT $maxResults
                """,
                symbol.Id,
                maxResults);

        return _byTarget[symbol.Id]
            .Where(edge => ReferenceKinds.Contains(edge.Kind))
            .Select(edge => TryRelation(edge.SourceId, edge))
            .Where(relation => relation is not null)
            .Select(relation => relation!)
            .DistinctBy(relation => relation.Symbol.Id)
            .OrderBy(relation => relation.Symbol.Project, StringComparer.Ordinal)
            .ThenBy(relation => relation.Symbol.QualifiedName, StringComparer.Ordinal)
            .ThenBy(relation => relation.Symbol.Signature ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(relation => relation.Symbol.Id, StringComparer.Ordinal)
            .Take(Math.Max(1, maxResults))
            .ToArray();
    }

    public IReadOnlyList<IndexedSymbol> Callers(IndexedSymbol symbol, int maxResults) =>
        CallerRelations(symbol, maxResults).Select(relation => relation.Symbol).ToArray();

    public IReadOnlyList<IndexedRelation> CallerRelations(IndexedSymbol symbol, int maxResults)
    {
        if (_connection is not null)
            return QueryEdgeRelations(
                """
                SELECT s.id, s.file_id, f.project, f.relative_path, s.kind, s.name,
                       s.qualified_name, s.signature, s.start_line, s.end_line, s.visibility, s.language,
                       e.source_id, e.target_id, e.kind, e.source_file_id, e.line, e.resolution_kind, e.confidence,
                       e.start_column, e.end_line, e.end_column
                FROM edges e JOIN symbols s ON s.id = e.source_id JOIN files f ON f.id = s.file_id
                WHERE e.target_id = $symbolId AND e.kind = 'Calls'
                ORDER BY f.project, s.qualified_name, COALESCE(s.signature, ''), s.id
                LIMIT $maxResults
                """,
                symbol.Id,
                maxResults);

        return _byTarget[symbol.Id]
            .Where(edge => edge.Kind == EdgeKind.Calls)
            .Select(edge => TryRelation(edge.SourceId, edge))
            .Where(relation => relation is not null)
            .Select(relation => relation!)
            .OrderBy(relation => relation.Symbol.Project, StringComparer.Ordinal)
            .ThenBy(relation => relation.Symbol.QualifiedName, StringComparer.Ordinal)
            .ThenBy(relation => relation.Symbol.Signature ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(relation => relation.Symbol.Id, StringComparer.Ordinal)
            .Take(Math.Max(1, maxResults))
            .ToArray();
    }

    private static readonly HashSet<EdgeKind> FlowHttpKinds =
    [
        EdgeKind.RoutesTo, EdgeKind.Registers, EdgeKind.ResolvesTo,
        EdgeKind.Calls, EdgeKind.UsesType, EdgeKind.Implements, EdgeKind.ImplementedBy
    ];

    private static readonly HashSet<EdgeKind> FlowUiKinds =
    [
        EdgeKind.Renders, EdgeKind.BindsTo, EdgeKind.HandlesEvent, EdgeKind.UsesViewModel,
        EdgeKind.Calls, EdgeKind.UsesType
    ];

    public const int FlowMinDepth = 1;
    public const int FlowMaxDepth = 8;
    public const int FlowDefaultDepth = 4;

    public static bool IsValidFlowKind(string kind) => kind is "http" or "ui" or "all";

    public static bool IsValidFlowDepth(int depth) => depth is >= FlowMinDepth and <= FlowMaxDepth;

    private static HashSet<EdgeKind> ResolveFlowEdgeKinds(string kind) => kind switch
    {
        "http" => FlowHttpKinds,
        "ui" => FlowUiKinds,
        "all" => new HashSet<EdgeKind>(FlowHttpKinds.Concat(FlowUiKinds)),
        _ => throw new ArgumentException($"Unsupported flow kind '{kind}'. Use http, ui, or all.", nameof(kind))
    };








    public IReadOnlyList<ImpactItem> Flow(IndexedSymbol entry, string kind, int depth, int maxResults, double minConfidence)
    {
        var edgeKinds = ResolveFlowEdgeKinds(kind);
        var clampedDepth = Math.Clamp(depth, FlowMinDepth, FlowMaxDepth);

        if (_connection is not null)
            return QueryFlow(entry.Id, edgeKinds, clampedDepth, maxResults, minConfidence);

        var result = new List<ImpactItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { entry.Id };
        var frontier = new[] { entry.Id };
        for (var level = 1; level <= clampedDepth && frontier.Length > 0; level++)
        {
            var next = new List<string>();
            foreach (var sourceId in frontier)
            {
                foreach (var edge in _bySource[sourceId]
                    .Where(edge => edgeKinds.Contains(edge.Kind))
                    .Where(edge => EffectiveConfidence(edge) >= minConfidence)
                    .Where(edge => IsDiSelectedOrUnregisteredImplementation(edge))
                    .OrderBy(edge => edge.Kind).ThenBy(edge => edge.TargetId, StringComparer.Ordinal))
                {
                    if (!_byId.TryGetValue(edge.TargetId, out var targetSymbol) || !seen.Add(targetSymbol.Id))
                        continue;
                    result.Add(new ImpactItem(targetSymbol, edge, level, entry.Id));
                    next.Add(targetSymbol.Id);
                    if (result.Count >= Math.Max(1, maxResults))
                        return result;
                }
            }
            frontier = next.Distinct(StringComparer.Ordinal).ToArray();
        }
        return result;
    }

    private static double EffectiveConfidence(IndexedEdge edge) => edge.Confidence ?? 1.0;












    private bool IsDiSelectedOrUnregisteredImplementation(IndexedEdge edge)
    {
        if (edge.Kind != EdgeKind.ImplementedBy)
            return true;

        var registrations = _byTarget[edge.SourceId].Where(e => e.Kind == EdgeKind.Registers).ToArray();
        if (registrations.Length != 1)
            return true;

        var resolvesTo = _bySource[registrations[0].SourceId].FirstOrDefault(e => e.Kind == EdgeKind.ResolvesTo);
        if (resolvesTo is null)
            return true;

        return edge.TargetId == resolvesTo.TargetId;
    }














    private IReadOnlyList<ImpactItem> QueryFlow(string rootId, HashSet<EdgeKind> edgeKinds, int maxDepth, int maxResults, double minConfidence)
    {
        var kindList = string.Join(",", edgeKinds.Select(k => $"'{k}'"));
        using var command = _connection!.CreateCommand();
        command.CommandText = $"""
            WITH RECURSIVE flow(id, depth) AS (
                SELECT $rootId, 0
                UNION
                SELECT e.target_id, f.depth + 1
                FROM edges e JOIN flow f ON e.source_id = f.id
                WHERE e.kind IN ({kindList})
                  AND (e.confidence IS NULL OR e.confidence >= $minConfidence)
                  AND f.depth < $maxDepth
                  -- DI-aware narrowing (plan §5): when e is an ImplementedBy edge and its
                  -- source interface has exactly one DI registration with a resolvable
                  -- ResolvesTo target, only the implementation that registration points at is
                  -- kept. Zero registrations, multiple registrations, or a registration with
                  -- no ResolvesTo edge all leave ImplementedBy unfiltered, mirroring the
                  -- snapshot path's IsDiSelectedOrUnregisteredImplementation.
                  AND (
                    e.kind <> 'ImplementedBy'
                    OR (SELECT COUNT(*) FROM edges reg WHERE reg.target_id = e.source_id AND reg.kind = 'Registers') <> 1
                    OR NOT EXISTS (
                        SELECT 1 FROM edges reg
                        JOIN edges resolves ON resolves.source_id = reg.source_id AND resolves.kind = 'ResolvesTo'
                        WHERE reg.target_id = e.source_id AND reg.kind = 'Registers'
                    )
                    OR e.target_id = (
                        SELECT resolves.target_id FROM edges reg
                        JOIN edges resolves ON resolves.source_id = reg.source_id AND resolves.kind = 'ResolvesTo'
                        WHERE reg.target_id = e.source_id AND reg.kind = 'Registers'
                        LIMIT 1
                    )
                  )
            ),
            min_depth AS (
                SELECT id, MIN(depth) AS depth FROM flow WHERE depth > 0 GROUP BY id
            ),
            via_candidates AS (
                SELECT md.id, md.depth, e.source_id AS via_source_id, e.target_id AS via_target_id, e.kind AS via_kind,
                       e.source_file_id AS via_source_file_id, e.line AS via_line, e.resolution_kind AS via_resolution_kind,
                       e.confidence AS via_confidence, e.start_column AS via_start_column, e.end_line AS via_end_line, e.end_column AS via_end_column,
                       ROW_NUMBER() OVER (
                           PARTITION BY md.id
                           ORDER BY e.kind, e.source_id, e.target_id
                       ) AS row_number
                FROM min_depth md
                JOIN edges e ON e.target_id = md.id AND e.kind IN ({kindList})
                              AND (e.confidence IS NULL OR e.confidence >= $minConfidence)
                              -- Re-applies the same DI-aware ImplementedBy narrowing as the
                              -- recursive part above: an edge that the recursive traversal
                              -- would have rejected as an unregistered implementer must not be
                              -- selectable here as evidence just because some other accepted
                              -- edge reaches the same (id, depth).
                              AND (
                                e.kind <> 'ImplementedBy'
                                OR (SELECT COUNT(*) FROM edges reg WHERE reg.target_id = e.source_id AND reg.kind = 'Registers') <> 1
                                OR NOT EXISTS (
                                    SELECT 1 FROM edges reg
                                    JOIN edges resolves ON resolves.source_id = reg.source_id AND resolves.kind = 'ResolvesTo'
                                    WHERE reg.target_id = e.source_id AND reg.kind = 'Registers'
                                )
                                OR e.target_id = (
                                    SELECT resolves.target_id FROM edges reg
                                    JOIN edges resolves ON resolves.source_id = reg.source_id AND resolves.kind = 'ResolvesTo'
                                    WHERE reg.target_id = e.source_id AND reg.kind = 'Registers'
                                    LIMIT 1
                                )
                              )
                JOIN flow parent ON parent.id = e.source_id AND parent.depth = md.depth - 1
            )
            SELECT s.id, s.file_id, f.project, f.relative_path, s.kind, s.name,
                   s.qualified_name, s.signature, s.start_line, s.end_line, s.visibility, s.language,
                   vc.via_source_id, vc.via_target_id, vc.via_kind, vc.via_source_file_id, vc.via_line, vc.via_resolution_kind, vc.via_confidence,
                   vc.via_start_column, vc.via_end_line, vc.via_end_column,
                   vc.depth
            FROM via_candidates vc JOIN symbols s ON s.id = vc.id JOIN files f ON f.id = s.file_id
            WHERE vc.row_number = 1
            ORDER BY vc.depth, s.qualified_name, s.id
            LIMIT $maxResults
            """;
        command.Parameters.AddWithValue("$rootId", rootId);
        command.Parameters.AddWithValue("$maxDepth", Math.Max(1, maxDepth));
        command.Parameters.AddWithValue("$minConfidence", minConfidence);
        command.Parameters.AddWithValue("$maxResults", Math.Max(1, maxResults));
        using var reader = command.ExecuteReader();
        var result = new List<ImpactItem>();
        while (reader.Read())
        {
            var via = IndexedEdgeReader.ReadWithSpan(reader, 12);
            result.Add(new ImpactItem(ReadIndexedSymbol(reader), via, reader.GetInt32(22), rootId));
        }
        return result;
    }

    public IReadOnlyList<RelationQueryResult> Relations(
        string sourceId,
        string targetId,
        EdgeKind? edgeKind = null,
        int maxResults = 20,
        double minConfidence = 0)
    {
        if (_connection is not null)
            return QueryRelations(sourceId, targetId, edgeKind, maxResults, minConfidence);

        var fileById = _graph.Files.ToDictionary(file => file.Id, StringComparer.Ordinal);
        var results = new List<RelationQueryResult>();
        foreach (var edge in _graph.Edges)
        {
            if (edge.SourceId != sourceId || edge.TargetId != targetId)
                continue;
            if (edgeKind is not null && edge.Kind != edgeKind)
                continue;
            if (edge.Confidence is null || edge.Confidence < minConfidence)
                continue;
            if (!_byId.TryGetValue(edge.SourceId, out var source) || !_byId.TryGetValue(edge.TargetId, out var target))
                continue;
            var (file, line) = ResolveEdgeLocation(edge, fileById);
            results.Add(new RelationQueryResult(source, target, edge, RelationEvidenceMapper.FromEdge(edge, source, target, file, line)));
            if (results.Count >= Math.Max(1, maxResults))
                break;
        }
        return results;
    }

    private IReadOnlyList<RelationQueryResult> QueryRelations(
        string sourceId,
        string targetId,
        EdgeKind? edgeKind,
        int maxResults,
        double minConfidence)
    {
        using var command = _connection!.CreateCommand();
        command.CommandText = """
            SELECT ss.id, ss.file_id, sf.project, sf.relative_path, ss.kind, ss.name,
                   ss.qualified_name, ss.signature, ss.start_line, ss.end_line, ss.visibility, ss.language,
                   st.id, st.file_id, tf.project, tf.relative_path, st.kind, st.name,
                   st.qualified_name, st.signature, st.start_line, st.end_line, st.visibility, st.language,
                   e.source_id, e.target_id, e.kind, e.source_file_id, e.line, e.resolution_kind, e.confidence,
                   e.start_column, e.end_line, e.end_column,
                   ef.relative_path
            FROM edges e
            JOIN symbols ss ON ss.id = e.source_id
            JOIN files sf ON sf.id = ss.file_id
            JOIN symbols st ON st.id = e.target_id
            JOIN files tf ON tf.id = st.file_id
            LEFT JOIN files ef ON ef.id = e.source_file_id
            WHERE e.source_id = $sourceId
              AND e.target_id = $targetId
              AND ($edgeKind IS NULL OR e.kind = $edgeKind)
              AND (e.confidence IS NULL OR e.confidence >= $minConfidence)
            ORDER BY e.kind, e.line, e.source_id
            LIMIT $maxResults
            """;
        command.Parameters.AddWithValue("$sourceId", sourceId);
        command.Parameters.AddWithValue("$targetId", targetId);
        command.Parameters.AddWithValue("$edgeKind", edgeKind?.ToString() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$minConfidence", minConfidence);
        command.Parameters.AddWithValue("$maxResults", Math.Max(1, maxResults));
        using var reader = command.ExecuteReader();
        var results = new List<RelationQueryResult>();
        while (reader.Read())
        {
            var source = new IndexedSymbol(
                reader.GetString(0), reader.GetString(2), reader.GetString(1), reader.GetString(3),
                Enum.Parse<NodeKind>(reader.GetString(4)), reader.GetString(5), reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetInt32(8),
                reader.IsDBNull(9) ? null : reader.GetInt32(9),
                reader.IsDBNull(10) ? null : reader.GetString(10), reader.GetString(11));
            var target = new IndexedSymbol(
                reader.GetString(12), reader.GetString(14), reader.GetString(13), reader.GetString(15),
                Enum.Parse<NodeKind>(reader.GetString(16)), reader.GetString(17), reader.GetString(18),
                reader.IsDBNull(19) ? null : reader.GetString(19),
                reader.IsDBNull(20) ? null : reader.GetInt32(20),
                reader.IsDBNull(21) ? null : reader.GetInt32(21),
                reader.IsDBNull(22) ? null : reader.GetString(22), reader.GetString(23));
            var edge = IndexedEdgeReader.ReadWithSpan(reader, 24);
            var file = reader.IsDBNull(34) ? null : reader.GetString(34);
            results.Add(new RelationQueryResult(source, target, edge, RelationEvidenceMapper.FromEdge(edge, source, target, file, edge.Line)));
        }
        return results;
    }

    private static (string? File, int? Line) ResolveEdgeLocation(IndexedEdge edge, IReadOnlyDictionary<string, IndexedFile> fileById)
    {
        if (edge.SourceFileId is null || !fileById.TryGetValue(edge.SourceFileId, out var file))
            return (null, edge.Line);
        return (file.RelativePath, edge.Line);
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null && _ownsConnection)
            await _connection.DisposeAsync();
    }

    private IndexedRelation? TryRelation(string symbolId, IndexedEdge edge)
    {
        if (!_byId.TryGetValue(symbolId, out var symbol))
            return null;
        return new IndexedRelation(symbol, edge);
    }

    public RepoMap BuildMap(string? focus, string? project, int tokenBudget)
    {
        if (_connection is not null)
            return BuildMapFromSql(focus, project, tokenBudget);

        var symbols = _graph.Symbols
            .Where(symbol => project is null || string.Equals(symbol.Project, project, StringComparison.OrdinalIgnoreCase))
            .Where(symbol => symbol.Kind is not NodeKind.Namespace)
            .ToArray();
        var focusDistances = BuildFocusDistances(focus);
        var scores = symbols.ToDictionary(symbol => symbol.Id, symbol => ScoreForMap(symbol, focus, focusDistances), StringComparer.Ordinal);




        var deprioritizeExternal = project is null;
        var ranked = symbols
            .OrderByDescending(symbol => scores[symbol.Id])
            .ThenBy(symbol => deprioritizeExternal && SqliteCodeMapStore.IsExternalProject(symbol.Project))
            .ThenBy(symbol => symbol.Project, StringComparer.Ordinal)
            .ThenBy(symbol => symbol.QualifiedName, StringComparer.Ordinal)
            .ThenBy(symbol => symbol.Signature ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(symbol => symbol.Id, StringComparer.Ordinal)
            .ToArray();

        var projectLabel = project ?? (symbols.Select(symbol => symbol.Project).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1
            ? symbols.FirstOrDefault()?.Project ?? "unknown" : "multiple");
        return RenderMap(ranked, projectLabel, _graph.Symbols.Count, tokenBudget,
            symbol => Outgoing(symbol.Id, EdgeKind.Calls, EdgeKind.UsesType, EdgeKind.Constructs)
                .Select(edge => _byId.GetValueOrDefault(edge.TargetId))
                .Where(target => target is not null)
                .Select(target => target!)
                .OrderBy(target => target.QualifiedName, StringComparer.Ordinal)
                .Take(3));
    }



















    private RepoMap BuildMapFromSql(string? focus, string? project, int tokenBudget)
    {
        var ranked = QueryBuildMapCandidates(focus, project).Select(item => item.Symbol).ToArray();
        var projectLabel = project ?? QueryDistinctProjectLabel();
        var totalSymbols = QueryTotalSymbolCount();
        var outgoingTargets = new LazyOutgoingMapTargetCache(this, ranked);
        return RenderMap(ranked, projectLabel, totalSymbols, tokenBudget, outgoingTargets.Get);
    }









    private sealed class LazyOutgoingMapTargetCache(CodeMapQueryService service, IReadOnlyList<IndexedSymbol> ranked)
    {
        private const int WindowSize = 200;
        private readonly Dictionary<string, IReadOnlyList<IndexedSymbol>> _resolved = new(StringComparer.Ordinal);
        private int _nextUnresolvedIndex;

        public IEnumerable<IndexedSymbol> Get(IndexedSymbol symbol)
        {
            if (!_resolved.TryGetValue(symbol.Id, out var targets))
            {
                FetchNextWindow();
                targets = _resolved.GetValueOrDefault(symbol.Id, Array.Empty<IndexedSymbol>());
            }
            return targets;
        }

        private void FetchNextWindow()
        {
            if (_nextUnresolvedIndex >= ranked.Count)
                return;
            var windowCount = Math.Min(WindowSize, ranked.Count - _nextUnresolvedIndex);
            var windowIds = new string[windowCount];
            for (var i = 0; i < windowCount; i++)
                windowIds[i] = ranked[_nextUnresolvedIndex + i].Id;
            _nextUnresolvedIndex += windowCount;
            var found = service.QueryTopOutgoingMapTargets(windowIds);




            foreach (var id in windowIds)
                _resolved[id] = found.GetValueOrDefault(id, Array.Empty<IndexedSymbol>());
        }
    }








    private string QueryDistinctProjectLabel()
    {
        using var command = _connection!.CreateCommand();
        command.CommandText = """
            SELECT project FROM (SELECT DISTINCT f.project FROM symbols s JOIN files f ON f.id = s.file_id WHERE s.kind <> 'Namespace')
            LIMIT 2
            """;
        using var reader = command.ExecuteReader();
        string? first = null;
        var count = 0;
        while (reader.Read())
        {
            count++;
            first ??= reader.GetString(0);
        }
        return count == 1 ? first ?? "unknown" : count == 0 ? "unknown" : "multiple";
    }








    private static RepoMap RenderMap(
        IReadOnlyList<IndexedSymbol> ranked,
        string projectLabel,
        int totalSymbols,
        int tokenBudget,
        Func<IndexedSymbol, IEnumerable<IndexedSymbol>> outgoingTargets)
    {
        var lines = new List<string>();
        var charsSoFar = 0;
        AddWithinBudget(lines, "# CodeMap", tokenBudget, ref charsSoFar);
        AddWithinBudget(lines, $"project: {projectLabel}", tokenBudget, ref charsSoFar);
        AddWithinBudget(lines, $"symbols: {totalSymbols}", tokenBudget, ref charsSoFar);
        AddWithinBudget(lines, string.Empty, tokenBudget, ref charsSoFar);

        var emitted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var symbol in ranked)
        {
            if (!emitted.Add(symbol.Id))
                continue;
            var label = MapLabel(symbol);
            if (!AddWithinBudget(lines, $"  {label}", tokenBudget, ref charsSoFar))
                continue;

            foreach (var target in outgoingTargets(symbol))
            {
                if (!AddWithinBudget(lines, $"    > {MapLabel(target)}", tokenBudget, ref charsSoFar))
                    break;
            }
        }
        return new RepoMap(lines, tokenBudget, ApproximateTokens(charsSoFar));
    }

    private static int FindScore(IndexedSymbol symbol, string query)
    {
        var qualified = symbol.QualifiedName;
        var display = symbol.DisplayName;
        if (string.Equals(qualified, query, StringComparison.OrdinalIgnoreCase)
            || string.Equals(display, query, StringComparison.OrdinalIgnoreCase)
            || string.Equals(symbol.Id, query, StringComparison.OrdinalIgnoreCase))
            return 1000;
        if (string.Equals(symbol.Name, query, StringComparison.OrdinalIgnoreCase))
            return 900;
        if (qualified.StartsWith(query, StringComparison.OrdinalIgnoreCase) || display.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            return 700;
        if (qualified.Contains(query, StringComparison.OrdinalIgnoreCase) || display.Contains(query, StringComparison.OrdinalIgnoreCase))
            return 500;
        return -1;
    }

    private int ScoreForMap(IndexedSymbol symbol, string? focus, IReadOnlyDictionary<string, int>? focusDistances)
    {
        var incoming = _byTarget[symbol.Id].Count(edge => edge.Kind is not (EdgeKind.Contains or EdgeKind.Defines));
        var outgoing = _bySource[symbol.Id].Count(edge => edge.Kind is not (EdgeKind.Contains or EdgeKind.Defines));
        var score = incoming * 2 + outgoing;
        if (symbol.Kind is NodeKind.Interface) score += 6;
        if (symbol.Kind is (NodeKind.Class or NodeKind.Struct or NodeKind.Record) && symbol.Visibility == "public") score += 4;
        if (symbol.Visibility == "public") score += 2;
        if (symbol.Name.Contains("Service", StringComparison.OrdinalIgnoreCase)
            || symbol.Name.Contains("Handler", StringComparison.OrdinalIgnoreCase)
            || symbol.Name.Contains("Repository", StringComparison.OrdinalIgnoreCase)
            || symbol.Name.Contains("Controller", StringComparison.OrdinalIgnoreCase)
            || symbol.Name.Contains("Factory", StringComparison.OrdinalIgnoreCase)
            || symbol.Name.Contains("Provider", StringComparison.OrdinalIgnoreCase)
            || symbol.Name.Contains("Gateway", StringComparison.OrdinalIgnoreCase)
            || symbol.Name.Contains("Writer", StringComparison.OrdinalIgnoreCase)
            || symbol.Name.Contains("Query", StringComparison.OrdinalIgnoreCase)) score += 3;
        if (symbol.Kind is NodeKind.Constructor) score += outgoing;
        if (IsTestOnly(symbol)) score -= 10;
        if (symbol.Name.Contains("Dto", StringComparison.OrdinalIgnoreCase) || symbol.Name.Contains("Options", StringComparison.OrdinalIgnoreCase)) score -= 2;
        if (symbol.Name.Contains("Converter", StringComparison.OrdinalIgnoreCase) || symbol.Kind is NodeKind.Enum) score--;
        if (!string.IsNullOrWhiteSpace(focus))
        {
            if (string.Equals(symbol.Name, focus, StringComparison.OrdinalIgnoreCase)
                || string.Equals(symbol.QualifiedName, focus, StringComparison.OrdinalIgnoreCase)) score += 30;
            else if (symbol.Name.Contains(focus, StringComparison.OrdinalIgnoreCase)
                || symbol.QualifiedName.Contains(focus, StringComparison.OrdinalIgnoreCase)
                || symbol.RelativePath.Contains(focus, StringComparison.OrdinalIgnoreCase)) score += 12;
            if (focusDistances is not null && focusDistances.TryGetValue(symbol.Id, out var distance) && distance is >= 0 and <= 2)
                score += 10 - distance * 3;
        }
        return score;
    }






    private IReadOnlyDictionary<string, int>? BuildFocusDistances(string? focus)
    {
        if (string.IsNullOrWhiteSpace(focus))
            return null;
        var focusSymbol = _graph.Symbols.FirstOrDefault(symbol => string.Equals(symbol.Name, focus, StringComparison.OrdinalIgnoreCase)
            || string.Equals(symbol.QualifiedName, focus, StringComparison.OrdinalIgnoreCase));
        if (focusSymbol is null)
            return null;

        var adjacent = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        void Link(string from, string to)
        {
            if (!adjacent.TryGetValue(from, out var list))
                adjacent[from] = list = [];
            list.Add(to);
        }
        foreach (var edge in _graph.Edges)
        {
            if (edge.Kind is EdgeKind.Contains or EdgeKind.Defines)
                continue;
            Link(edge.SourceId, edge.TargetId);
            Link(edge.TargetId, edge.SourceId);
        }

        var distances = new Dictionary<string, int>(StringComparer.Ordinal) { [focusSymbol.Id] = 0 };
        var frontier = new List<string> { focusSymbol.Id };
        for (var distance = 1; distance <= 2 && frontier.Count > 0; distance++)
        {
            var next = new List<string>();
            foreach (var id in frontier)
                foreach (var neighbor in adjacent.GetValueOrDefault(id, []))
                    if (distances.TryAdd(neighbor, distance))
                        next.Add(neighbor);
            frontier = next;
        }
        return distances;
    }

    internal static bool IsTestOnly(IndexedSymbol symbol)
    {
        var pathSegments = symbol.RelativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        return pathSegments.Any(segment => segment.Equals("test", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("tests", StringComparison.OrdinalIgnoreCase)
            || segment.EndsWith("Tests.cs", StringComparison.OrdinalIgnoreCase))
            || symbol.Project.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase)
            || symbol.Name.EndsWith("Tests", StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesChangedFilePath(HashSet<string> queryPaths, IndexedSymbol symbol)
    {
        var relative = CodeMapPath.Normalize(symbol.RelativePath);
        if (queryPaths.Contains(relative))
            return true;
        var withProject = CodeMapPath.Normalize($"{symbol.Project}/{relative}");
        return queryPaths.Contains(withProject);
    }

    private static string MapLabel(IndexedSymbol symbol)
    {
        if (symbol.Kind is not (NodeKind.Method or NodeKind.Constructor))
            return symbol.Name;

        var qualified = symbol.QualifiedName;
        var parameterStart = qualified.IndexOf('(');
        var baseName = parameterStart >= 0 ? qualified[..parameterStart] : qualified;
        var segments = baseName.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length >= 2)
            return segments[^2] + "." + segments[^1] + "()";
        return symbol.Name + "()";
    }

    private static IReadOnlyList<IndexedEdge> FilterEdges(IEnumerable<IndexedEdge> edges, EdgeKind[] kinds)
    {
        var kindSet = kinds.Length == 0 ? null : kinds.ToHashSet();
        return edges.Where(edge => kindSet is null || kindSet.Contains(edge.Kind)).ToArray();
    }







    private static bool AddWithinBudget(List<string> lines, string line, int tokenBudget, ref int charsSoFar)
    {
        var additionLength = (lines.Count == 0 ? 0 : 1) + line.Length;
        if (lines.Count > 0 && ApproximateTokens(charsSoFar) + ApproximateTokens(additionLength) > Math.Max(1, tokenBudget))
            return false;
        lines.Add(line);
        charsSoFar += additionLength;
        return true;
    }

    private static int ApproximateTokens(string text) => ApproximateTokens(text.Length);

    private static int ApproximateTokens(int length) => (int)Math.Ceiling(length / 4d);
}

public sealed record ImpactItem(IndexedSymbol Symbol, IndexedEdge Via, int Depth, string? RootId = null);

public sealed record RepoMap(IReadOnlyList<string> Lines, int TokenBudget, int EstimatedTokens)
{
    public string Text => string.Join(Environment.NewLine, Lines);
}
