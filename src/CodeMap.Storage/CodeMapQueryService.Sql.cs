using CodeMap.Core.Models;
using Microsoft.Data.Sqlite;

namespace CodeMap.Storage;

public sealed partial class CodeMapQueryService
{
    private IReadOnlyList<IndexedSymbol> QuerySymbolsInFiles(HashSet<string> relativePaths, int maxResults)
    {
        var symbols = new Dictionary<string, IndexedSymbol>(StringComparer.Ordinal);
        foreach (var chunk in relativePaths.Chunk(SqliteVariableChunkSize))
        {
            using var command = _connection!.CreateCommand();
            var names = new List<string>();
            for (var index = 0; index < chunk.Length; index++)
            {
                var name = "$p" + index;
                names.Add(name);
                command.Parameters.AddWithValue(name, chunk[index]);
            }
            command.CommandText = $"""
                SELECT s.id, s.file_id, f.project, f.relative_path, s.kind, s.name,
                       s.qualified_name, s.signature, s.start_line, s.end_line, s.visibility, s.language
                FROM symbols s JOIN files f ON f.id = s.file_id
                WHERE (
                    replace(f.relative_path, char(92), '/') IN ({string.Join(", ", names)})
                    OR (f.project || '/' || replace(f.relative_path, char(92), '/')) IN ({string.Join(", ", names)})
                  )
                  AND s.kind NOT IN ('File', 'Namespace')
                ORDER BY s.qualified_name, s.id
                LIMIT $maxResults
                """;
            command.Parameters.AddWithValue("$maxResults", Math.Max(1, maxResults));
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var symbol = ReadIndexedSymbol(reader);
                symbols[symbol.Id] = symbol;
            }
        }
        return symbols.Values
            .OrderBy(symbol => symbol.QualifiedName, StringComparer.Ordinal)
            .ThenBy(symbol => symbol.Id, StringComparer.Ordinal)
            .Take(Math.Max(1, maxResults))
            .ToArray();
    }








    private IReadOnlyList<IndexedSymbol> QueryFindCandidates(string query)
    {
        var exact = QueryFindExactTier(query);
        if (exact.Count > 0)
            return exact;

        var prefix = QueryFindPrefixTier(query);
        if (prefix.Count > 0)
            return prefix;

        return QueryFindContainsTier(query);
    }

    private IReadOnlyList<IndexedSymbol> QueryFindExactTier(string query)
    {
        using var command = _connection!.CreateCommand();
        command.CommandText = """
            SELECT s.id, s.file_id, f.project, f.relative_path, s.kind, s.name,
                   s.qualified_name, s.signature, s.start_line, s.end_line, s.visibility, s.language
            FROM symbols s JOIN files f ON f.id = s.file_id
            WHERE s.id = $query
               OR s.name = $query COLLATE NOCASE
               OR s.qualified_name = $query COLLATE NOCASE
            """;
        command.Parameters.AddWithValue("$query", query);
        return ReadFindCandidates(command);
    }










    private IReadOnlyList<IndexedSymbol> QueryFindPrefixTier(string query)
    {
        using var command = _connection!.CreateCommand();
        command.CommandText = """
            SELECT s.id, s.file_id, f.project, f.relative_path, s.kind, s.name,
                   s.qualified_name, s.signature, s.start_line, s.end_line, s.visibility, s.language
            FROM symbols s JOIN files f ON f.id = s.file_id
            WHERE s.qualified_name LIKE $prefix ESCAPE '\'
            """;
        command.Parameters.AddWithValue("$prefix", EscapeLike(query) + "%");
        return ReadFindCandidates(command);
    }














    private IReadOnlyList<IndexedSymbol> QueryFindContainsTier(string query)
    {
        if (query.Length >= FtsTrigramMinimumQueryLength && TryQueryFindContainsTierFts(query, out var ftsResults))
            return ftsResults;

        using var command = _connection!.CreateCommand();
        command.CommandText = """
            SELECT s.id, s.file_id, f.project, f.relative_path, s.kind, s.name,
                   s.qualified_name, s.signature, s.start_line, s.end_line, s.visibility, s.language
            FROM symbols s JOIN files f ON f.id = s.file_id
            WHERE s.qualified_name LIKE $contains ESCAPE '\'
            """;
        command.Parameters.AddWithValue("$contains", "%" + EscapeLike(query) + "%");
        return ReadFindCandidates(command);
    }


    private const int FtsTrigramMinimumQueryLength = 3;







    private bool TryQueryFindContainsTierFts(string query, out IReadOnlyList<IndexedSymbol> results)
    {
        try
        {
            using var command = _connection!.CreateCommand();
            command.CommandText = """
                SELECT s.id, s.file_id, f.project, f.relative_path, s.kind, s.name,
                       s.qualified_name, s.signature, s.start_line, s.end_line, s.visibility, s.language
                FROM symbols_fts fts
                JOIN symbols s ON s.rowid = fts.rowid
                JOIN files f ON f.id = s.file_id
                WHERE symbols_fts MATCH $contains
                """;
            command.Parameters.AddWithValue("$contains", EscapeFtsMatchLiteral(query));
            results = ReadFindCandidates(command);
            return true;
        }
        catch (SqliteException)
        {
            results = Array.Empty<IndexedSymbol>();
            return false;
        }
    }









    private static string EscapeFtsMatchLiteral(string query) => "\"" + query.Replace("\"", "\"\"") + "\"";

    private static IReadOnlyList<IndexedSymbol> ReadFindCandidates(SqliteCommand command)
    {
        using var reader = command.ExecuteReader();
        var symbols = new List<IndexedSymbol>();
        while (reader.Read())
            symbols.Add(ReadIndexedSymbol(reader));
        return symbols;
    }

    private static IReadOnlyList<IndexedSymbol> RankFindResults(IReadOnlyList<IndexedSymbol> candidates, string query, int maxResults)
    {
        var ranked = candidates
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

    private IReadOnlyList<IndexedSymbol> QueryExactSymbols(string query, bool callableOnly)
    {
        using var command = _connection!.CreateCommand();
        command.CommandText = """
            SELECT s.id, s.file_id, f.project, f.relative_path, s.kind, s.name,
                   s.qualified_name, s.signature, s.start_line, s.end_line, s.visibility, s.language
            FROM symbols s JOIN files f ON f.id = s.file_id
            WHERE ($callableOnly = 0 OR s.kind IN ('Method', 'Constructor', 'Function'))
              AND (
                    s.id = $query
                 OR s.name = $query COLLATE NOCASE
                 OR s.qualified_name = $query COLLATE NOCASE
                 OR (s.qualified_name || COALESCE(s.signature, '')) = $query COLLATE NOCASE
                 OR (CASE
                        WHEN s.kind = 'Constructor' THEN s.qualified_name || COALESCE(s.signature, '')
                        WHEN s.kind = 'Method' THEN s.qualified_name || COALESCE(s.signature, '')
                        ELSE s.qualified_name
                     END) = $query COLLATE NOCASE
              )
            ORDER BY s.qualified_name, COALESCE(s.signature, ''), s.id
            """;
        command.Parameters.AddWithValue("$query", query);
        command.Parameters.AddWithValue("$callableOnly", callableOnly ? 1 : 0);
        using var reader = command.ExecuteReader();
        var symbols = new List<IndexedSymbol>();
        while (reader.Read())
            symbols.Add(ReadIndexedSymbol(reader));
        return symbols;
    }

    private IndexedSymbol? QuerySymbolById(string id)
    {
        using var command = _connection!.CreateCommand();
        command.CommandText = """
            SELECT s.id, s.file_id, f.project, f.relative_path, s.kind, s.name,
                   s.qualified_name, s.signature, s.start_line, s.end_line, s.visibility, s.language
            FROM symbols s JOIN files f ON f.id = s.file_id
            WHERE s.id = $id
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadIndexedSymbol(reader) : null;
    }

    private IReadOnlyDictionary<string, IndexedSymbol> QuerySymbolsByIds(IReadOnlyList<string> ids)
    {
        var symbols = new Dictionary<string, IndexedSymbol>(StringComparer.Ordinal);
        foreach (var chunk in ids.Chunk(SqliteVariableChunkSize))
        {
            using var command = _connection!.CreateCommand();
            var names = new List<string>();
            for (var index = 0; index < chunk.Length; index++)
            {
                var name = "$id" + index;
                names.Add(name);
                command.Parameters.AddWithValue(name, chunk[index]);
            }
            command.CommandText = $"""
                SELECT s.id, s.file_id, f.project, f.relative_path, s.kind, s.name,
                       s.qualified_name, s.signature, s.start_line, s.end_line, s.visibility, s.language
                FROM symbols s JOIN files f ON f.id = s.file_id
                WHERE s.id IN ({string.Join(", ", names)})
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var symbol = ReadIndexedSymbol(reader);
                symbols[symbol.Id] = symbol;
            }
        }
        return symbols;
    }








    private IReadOnlyDictionary<string, IndexedFile> QueryFilesByIds(IReadOnlyList<string> ids)
    {
        var result = new Dictionary<string, IndexedFile>(StringComparer.Ordinal);
        foreach (var chunk in ids.Chunk(SqliteVariableChunkSize))
        {
            using var command = _connection!.CreateCommand();
            var names = new List<string>();
            for (var index = 0; index < chunk.Length; index++)
            {
                var name = "$id" + index;
                names.Add(name);
                command.Parameters.AddWithValue(name, chunk[index]);
            }
            command.CommandText = $"""
                SELECT id, project, relative_path, language
                FROM files
                WHERE id IN ({string.Join(", ", names)})
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var file = new IndexedFile(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3));
                result[file.Id] = file;
            }
        }
        return result;
    }

    private static string EscapeLike(string value) => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private IReadOnlyList<IndexedSymbol> QueryRelatedSymbols(string sql, string symbolId, int maxResults)
    {
        using var command = _connection!.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$symbolId", symbolId);
        command.Parameters.AddWithValue("$maxResults", Math.Max(1, maxResults));
        using var reader = command.ExecuteReader();
        var symbols = new List<IndexedSymbol>();
        while (reader.Read())
            symbols.Add(ReadIndexedSymbol(reader));
        return symbols;
    }

    private IReadOnlyList<IndexedRelation> QueryEdgeRelations(string sql, string symbolId, int maxResults)
    {
        using var command = _connection!.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$symbolId", symbolId);
        command.Parameters.AddWithValue("$maxResults", Math.Max(1, maxResults));
        using var reader = command.ExecuteReader();
        var relations = new List<IndexedRelation>();
        while (reader.Read())
            relations.Add(new IndexedRelation(ReadIndexedSymbol(reader), IndexedEdgeReader.ReadWithSpan(reader, 12)));
        return relations;
    }














    private IReadOnlyList<IndexedRelation> QueryCalleeRelations(string rootId, int maxDepth, int maxResults)
    {
        using var command = _connection!.CreateCommand();
        command.CommandText = """
            WITH RECURSIVE callees(id, depth) AS (
                SELECT $rootId, 0
                UNION
                SELECT e.target_id, c.depth + 1
                FROM edges e JOIN callees c ON e.source_id = c.id
                WHERE e.kind = 'Calls' AND c.depth < $maxDepth
            ),
            min_depth AS (
                SELECT id, MIN(depth) AS depth FROM callees WHERE depth > 0 GROUP BY id
            ),
            via_candidates AS (
                SELECT md.id, md.depth, e.source_id AS via_source_id, e.target_id AS via_target_id, e.kind AS via_kind,
                       e.source_file_id AS via_source_file_id, e.line AS via_line, e.resolution_kind AS via_resolution_kind,
                       e.confidence AS via_confidence, e.start_column AS via_start_column, e.end_line AS via_end_line, e.end_column AS via_end_column,
                       ROW_NUMBER() OVER (PARTITION BY md.id ORDER BY e.source_id, e.target_id) AS row_number
                FROM min_depth md
                JOIN edges e ON e.target_id = md.id AND e.kind = 'Calls'
                JOIN callees parent ON parent.id = e.source_id AND parent.depth = md.depth - 1
            )
            SELECT s.id, s.file_id, f.project, f.relative_path, s.kind, s.name,
                   s.qualified_name, s.signature, s.start_line, s.end_line, s.visibility, s.language,
                   vc.via_source_id, vc.via_target_id, vc.via_kind, vc.via_source_file_id, vc.via_line, vc.via_resolution_kind, vc.via_confidence,
                   vc.via_start_column, vc.via_end_line, vc.via_end_column
            FROM via_candidates vc JOIN symbols s ON s.id = vc.id JOIN files f ON f.id = s.file_id
            WHERE vc.row_number = 1
            ORDER BY vc.depth, s.qualified_name, s.id
            LIMIT $maxResults
            """;
        command.Parameters.AddWithValue("$rootId", rootId);
        command.Parameters.AddWithValue("$maxDepth", Math.Max(1, maxDepth));
        command.Parameters.AddWithValue("$maxResults", Math.Max(1, maxResults));
        using var reader = command.ExecuteReader();
        var relations = new List<IndexedRelation>();
        while (reader.Read())
            relations.Add(new IndexedRelation(ReadIndexedSymbol(reader), IndexedEdgeReader.ReadWithSpan(reader, 12)));
        return relations;
    }

    private IReadOnlyList<IndexedSymbol> QueryCallees(string rootId, int maxDepth, int maxResults) =>
        QueryCalleeRelations(rootId, maxDepth, maxResults).Select(relation => relation.Symbol).ToArray();













    private IReadOnlyList<ImpactItem> QueryImpact(string rootId, int maxDepth, int maxResults, string profile)
    {
        var edgeKinds = ResolveImpactEdgeKinds(profile);
        var kindList = string.Join(",", edgeKinds.Select(k => $"'{k}'"));
        using var command = _connection!.CreateCommand();
        command.CommandText = $"""
            WITH RECURSIVE impact(id, depth) AS (
                SELECT $rootId, 0
                UNION
                SELECT e.source_id, i.depth + 1
                FROM edges e JOIN impact i ON e.target_id = i.id
                WHERE e.kind IN ({kindList})
                  AND i.depth < $maxDepth
                  -- DI-aware narrowing (mirrors QueryFlow): applies only to ImplementedBy
                  -- edges, a no-op for every other kind in the set, so it is safe to leave
                  -- unconditional for both "code" (which never includes ImplementedBy) and
                  -- "app" profiles.
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
                SELECT id, MIN(depth) AS depth FROM impact WHERE depth > 0 GROUP BY id
            ),
            via_candidates AS (
                SELECT md.id, md.depth, e.source_id AS via_source_id, e.target_id AS via_target_id, e.kind AS via_kind,
                       e.source_file_id AS via_source_file_id, e.line AS via_line, e.resolution_kind AS via_resolution_kind,
                       e.confidence AS via_confidence, e.start_column AS via_start_column, e.end_line AS via_end_line, e.end_column AS via_end_column,
                       ROW_NUMBER() OVER (
                           PARTITION BY md.id
                           ORDER BY e.source_id, e.kind, e.target_id, e.source_file_id, e.line
                       ) AS row_number
                FROM min_depth md
                JOIN edges e ON e.source_id = md.id AND e.kind IN ({kindList})
                              -- Re-applies the same DI-aware ImplementedBy narrowing as the
                              -- recursive part above: an edge the recursive traversal would
                              -- have rejected as an unregistered implementer must not be
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
                JOIN impact parent ON parent.id = e.target_id AND parent.depth = md.depth - 1
            )
            SELECT s.id, s.file_id, f.project, f.relative_path, s.kind, s.name,
                   s.qualified_name, s.signature, s.start_line, s.end_line, s.visibility, s.language,
                   vc.depth, vc.via_source_id, vc.via_target_id, vc.via_kind, vc.via_source_file_id, vc.via_line, vc.via_resolution_kind, vc.via_confidence,
                   vc.via_start_column, vc.via_end_line, vc.via_end_column
            FROM via_candidates vc JOIN symbols s ON s.id = vc.id JOIN files f ON f.id = s.file_id
            WHERE vc.row_number = 1
            ORDER BY vc.depth, s.qualified_name, s.id
            LIMIT $maxResults
            """;
        command.Parameters.AddWithValue("$rootId", rootId);
        command.Parameters.AddWithValue("$maxDepth", Math.Max(0, maxDepth));
        command.Parameters.AddWithValue("$maxResults", Math.Max(1, maxResults));
        using var reader = command.ExecuteReader();
        var result = new List<ImpactItem>();
        while (reader.Read())
        {
            var via = IndexedEdgeReader.ReadWithSpan(reader, 13);
            result.Add(new ImpactItem(ReadIndexedSymbol(reader), via, reader.GetInt32(12), rootId));
        }
        return result;
    }

















    private IReadOnlyList<ImpactItem> QueryImpactUnion(IReadOnlyList<string> rootIdsInOrder, int maxDepth, int maxResultsPerRoot, string profile)
    {
        var edgeKinds = ResolveImpactEdgeKinds(profile);
        var kindList = string.Join(",", edgeKinds.Select(k => $"'{k}'"));
        var rootValues = new List<string>();
        using var command = _connection!.CreateCommand();
        for (var index = 0; index < rootIdsInOrder.Count; index++)
        {
            var idParam = "$rootId" + index;
            var orderParam = "$rootOrder" + index;
            rootValues.Add($"({idParam}, {orderParam})");
            command.Parameters.AddWithValue(idParam, rootIdsInOrder[index]);
            command.Parameters.AddWithValue(orderParam, index);
        }
        command.CommandText = $"""
            WITH roots(root_id, root_order) AS (
                VALUES {string.Join(", ", rootValues)}
            ),
            impact(root_id, id, depth) AS (
                SELECT root_id, root_id, 0 FROM roots
                UNION
                SELECT i.root_id, e.source_id, i.depth + 1
                FROM edges e JOIN impact i ON e.target_id = i.id
                WHERE e.kind IN ({kindList})
                  AND i.depth < $maxDepth
                  -- DI-aware narrowing (mirrors QueryImpact/QueryFlow): applies only to
                  -- ImplementedBy edges, a no-op for every other kind in the set.
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
                SELECT root_id, id, MIN(depth) AS depth FROM impact WHERE depth > 0 GROUP BY root_id, id
            ),
            via_candidates AS (
                SELECT md.root_id, md.id, md.depth, e.source_id AS via_source_id, e.target_id AS via_target_id, e.kind AS via_kind,
                       e.source_file_id AS via_source_file_id, e.line AS via_line, e.resolution_kind AS via_resolution_kind,
                       e.confidence AS via_confidence, e.start_column AS via_start_column, e.end_line AS via_end_line, e.end_column AS via_end_column,
                       ROW_NUMBER() OVER (
                           PARTITION BY md.root_id, md.id
                           ORDER BY e.source_id, e.kind, e.target_id, e.source_file_id, e.line
                       ) AS row_number
                FROM min_depth md
                JOIN edges e ON e.source_id = md.id AND e.kind IN ({kindList})
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
                JOIN impact parent ON parent.root_id = md.root_id AND parent.id = e.target_id AND parent.depth = md.depth - 1
            ),
            ranked AS (
                SELECT vc.*, roots.root_order,
                       ROW_NUMBER() OVER (
                           PARTITION BY vc.root_id
                           -- Must match QueryImpact's LIMIT order exactly. Ranking by id alone
                           -- changes which same-depth items survive a per-root maxResults limit.
                           ORDER BY vc.depth, ranked_symbol.qualified_name, ranked_symbol.id
                       ) AS per_root_rank
                FROM via_candidates vc
                JOIN roots ON roots.root_id = vc.root_id
                JOIN symbols ranked_symbol ON ranked_symbol.id = vc.id
                WHERE vc.row_number = 1
            )
            SELECT s.id, s.file_id, f.project, f.relative_path, s.kind, s.name,
                   s.qualified_name, s.signature, s.start_line, s.end_line, s.visibility, s.language,
                   r.depth, r.via_source_id, r.via_target_id, r.via_kind, r.via_source_file_id, r.via_line, r.via_resolution_kind, r.via_confidence,
                   r.via_start_column, r.via_end_line, r.via_end_column, r.root_id, r.root_order
            FROM ranked r JOIN symbols s ON s.id = r.id JOIN files f ON f.id = s.file_id
            WHERE r.per_root_rank <= $maxResultsPerRoot
            ORDER BY r.root_order, r.depth, s.qualified_name, s.id
            """;
        command.Parameters.AddWithValue("$maxDepth", Math.Max(0, maxDepth));
        command.Parameters.AddWithValue("$maxResultsPerRoot", Math.Max(1, maxResultsPerRoot));
        using var reader = command.ExecuteReader();
        var result = new List<ImpactItem>();
        while (reader.Read())
        {
            var via = IndexedEdgeReader.ReadWithSpan(reader, 13);
            result.Add(new ImpactItem(ReadIndexedSymbol(reader), via, reader.GetInt32(12), reader.GetString(23)));
        }
        return result;
    }
















    private IReadOnlyList<(IndexedSymbol Symbol, int Score)> QueryBuildMapCandidates(string? focus, string? project)
    {
        var focusDistanceCte = ResolveFocusDistanceCte(focus, out var focusParameters);
        using var command = _connection!.CreateCommand();
        command.CommandText = $"""
            WITH degree AS (
                SELECT s.id AS symbol_id,
                       (SELECT COUNT(*) FROM edges e WHERE e.target_id = s.id AND e.kind NOT IN ('Contains', 'Defines')) AS incoming,
                       (SELECT COUNT(*) FROM edges e WHERE e.source_id = s.id AND e.kind NOT IN ('Contains', 'Defines')) AS outgoing
                FROM symbols s
            ){focusDistanceCte}
            SELECT s.id, s.file_id, f.project, f.relative_path, s.kind, s.name,
                   s.qualified_name, s.signature, s.start_line, s.end_line, s.visibility, s.language,
                   (
                     d.incoming * 2 + d.outgoing
                     + (CASE WHEN s.kind = 'Interface' THEN 6 ELSE 0 END)
                     + (CASE WHEN s.kind IN ('Class', 'Struct', 'Record') AND s.visibility = 'public' THEN 4 ELSE 0 END)
                     + (CASE WHEN s.visibility = 'public' THEN 2 ELSE 0 END)
                     + (CASE WHEN s.name LIKE '%Service%' COLLATE NOCASE OR s.name LIKE '%Handler%' COLLATE NOCASE
                             OR s.name LIKE '%Repository%' COLLATE NOCASE OR s.name LIKE '%Controller%' COLLATE NOCASE
                             OR s.name LIKE '%Factory%' COLLATE NOCASE OR s.name LIKE '%Provider%' COLLATE NOCASE
                             OR s.name LIKE '%Gateway%' COLLATE NOCASE OR s.name LIKE '%Writer%' COLLATE NOCASE
                             OR s.name LIKE '%Query%' COLLATE NOCASE THEN 3 ELSE 0 END)
                     + (CASE WHEN s.kind = 'Constructor' THEN d.outgoing ELSE 0 END)
                     + (CASE WHEN (
                          ('/' || replace(f.relative_path, char(92), '/') || '/') LIKE '%/test/%' COLLATE NOCASE
                          OR ('/' || replace(f.relative_path, char(92), '/') || '/') LIKE '%/tests/%' COLLATE NOCASE
                          OR ('/' || replace(f.relative_path, char(92), '/') || '/') LIKE '%Tests.cs/%' COLLATE NOCASE
                          OR f.project LIKE '%.Tests' COLLATE NOCASE
                          OR s.name LIKE '%Tests' COLLATE NOCASE
                        ) THEN -10 ELSE 0 END)
                     + (CASE WHEN s.name LIKE '%Dto%' COLLATE NOCASE OR s.name LIKE '%Options%' COLLATE NOCASE THEN -2 ELSE 0 END)
                     + (CASE WHEN s.name LIKE '%Converter%' COLLATE NOCASE OR s.kind = 'Enum' THEN -1 ELSE 0 END)
                     + (CASE WHEN $hasFocus = 0 THEN 0
                             WHEN s.name = $focus COLLATE NOCASE OR s.qualified_name = $focus COLLATE NOCASE THEN 30
                             WHEN s.name LIKE $focusContains COLLATE NOCASE ESCAPE '\' OR s.qualified_name LIKE $focusContains COLLATE NOCASE ESCAPE '\'
                                  OR f.relative_path LIKE $focusContains COLLATE NOCASE ESCAPE '\' THEN 12
                             ELSE 0 END)
                     + (CASE WHEN $hasFocus = 1 AND fd.depth IS NOT NULL AND fd.depth BETWEEN 0 AND 2 THEN 10 - fd.depth * 3 ELSE 0 END)
                   ) AS score
            FROM symbols s
            JOIN files f ON f.id = s.file_id
            JOIN degree d ON d.symbol_id = s.id
            LEFT JOIN focus_distance fd ON fd.id = s.id
            WHERE s.kind <> 'Namespace'
              AND ($project IS NULL OR f.project = $project COLLATE NOCASE)
            ORDER BY score DESC,
                     -- 프로젝트를 지정하지 않은 map에서는 외부 어셈블리 심볼을 소스보다 뒤에 배치한다.
                     -- 특정 프로젝트를 지정한 경우에는 요청한 프로젝트를 그대로 우선한다.
                     (CASE WHEN $project IS NULL AND f.project LIKE 'external:%' THEN 1 ELSE 0 END),
                     f.project, s.qualified_name, COALESCE(s.signature, ''), s.id
            """;
        command.Parameters.AddWithValue("$project", (object?)project ?? DBNull.Value);
        command.Parameters.AddWithValue("$hasFocus", string.IsNullOrWhiteSpace(focus) ? 0 : 1);
        command.Parameters.AddWithValue("$focus", (object?)focus ?? string.Empty);
        command.Parameters.AddWithValue("$focusContains", "%" + EscapeLike(focus ?? string.Empty) + "%");
        foreach (var (name, value) in focusParameters)
            command.Parameters.AddWithValue(name, value);

        using var reader = command.ExecuteReader();
        var results = new List<(IndexedSymbol, int)>();
        while (reader.Read())
            results.Add((ReadIndexedSymbol(reader), reader.GetInt32(12)));
        return results;
    }










    private string ResolveFocusDistanceCte(string? focus, out List<(string Name, object Value)> parameters)
    {
        parameters = [];
        if (string.IsNullOrWhiteSpace(focus))
            return ", focus_distance AS (SELECT NULL AS id, NULL AS depth WHERE 0)";

        using var focusCommand = _connection!.CreateCommand();
        focusCommand.CommandText = """
            SELECT s.id FROM symbols s JOIN files f ON f.id = s.file_id
            WHERE s.name = $focus COLLATE NOCASE OR s.qualified_name = $focus COLLATE NOCASE
            ORDER BY f.project, s.qualified_name, COALESCE(s.signature, ''), s.id
            LIMIT 1
            """;
        focusCommand.Parameters.AddWithValue("$focus", focus);
        var focusId = focusCommand.ExecuteScalar() as string;
        if (focusId is null)
            return ", focus_distance AS (SELECT NULL AS id, NULL AS depth WHERE 0)";

        parameters.Add(("$focusId", focusId));
        return """
            , undirected AS (
                SELECT source_id, target_id FROM edges WHERE kind NOT IN ('Contains', 'Defines')
                UNION ALL
                SELECT target_id, source_id FROM edges WHERE kind NOT IN ('Contains', 'Defines')
            ),
            bfs(id, depth) AS (
                SELECT $focusId, 0
                UNION
                SELECT u.target_id, b.depth + 1
                FROM undirected u JOIN bfs b ON u.source_id = b.id
                WHERE b.depth < 2
            ),
            focus_distance AS (
                SELECT id, MIN(depth) AS depth FROM bfs GROUP BY id
            )
            """;
    }








    internal const int SqliteVariableChunkSize = 900;










    internal IReadOnlyDictionary<string, IReadOnlyList<IndexedSymbol>> QueryTopOutgoingMapTargets(IReadOnlyList<string> symbolIds)
    {
        var result = new Dictionary<string, List<IndexedSymbol>>(StringComparer.Ordinal);
        if (symbolIds.Count == 0)
            return new Dictionary<string, IReadOnlyList<IndexedSymbol>>(StringComparer.Ordinal);

        foreach (var chunk in symbolIds.Chunk(SqliteVariableChunkSize))
        {
            using var command = _connection!.CreateCommand();
            var names = new List<string>();
            for (var index = 0; index < chunk.Length; index++)
            {
                var name = "$s" + index;
                names.Add(name);
                command.Parameters.AddWithValue(name, chunk[index]);
            }
            command.CommandText = $"""
                SELECT source_id, id, file_id, project, relative_path, kind, name, qualified_name, signature, start_line, end_line, visibility, language
                FROM (
                    SELECT e.source_id AS source_id, s2.id, s2.file_id, f2.project, f2.relative_path, s2.kind, s2.name,
                           s2.qualified_name, s2.signature, s2.start_line, s2.end_line, s2.visibility, s2.language,
                           ROW_NUMBER() OVER (PARTITION BY e.source_id ORDER BY s2.qualified_name, s2.id) AS rn
                    FROM edges e
                    JOIN symbols s2 ON s2.id = e.target_id
                    JOIN files f2 ON f2.id = s2.file_id
                    WHERE e.source_id IN ({string.Join(", ", names)})
                      AND e.kind IN ('Calls', 'UsesType', 'Constructs')
                )
                WHERE rn <= 3
                ORDER BY source_id, qualified_name, id
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var sourceId = reader.GetString(0);
                var symbol = ReadIndexedSymbol(reader, offset: 1);
                if (!result.TryGetValue(sourceId, out var list))
                    result[sourceId] = list = [];
                list.Add(symbol);
            }
        }
        return result.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<IndexedSymbol>)pair.Value, StringComparer.Ordinal);
    }

    private int QueryTotalSymbolCount()
    {
        using var command = _connection!.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM symbols";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static IndexedSymbol ReadIndexedSymbol(SqliteDataReader reader) => ReadIndexedSymbol(reader, offset: 0);

    private static IndexedSymbol ReadIndexedSymbol(SqliteDataReader reader, int offset) => new(
        reader.GetString(offset), reader.GetString(offset + 2), reader.GetString(offset + 1), reader.GetString(offset + 3),
        Enum.Parse<NodeKind>(reader.GetString(offset + 4)), reader.GetString(offset + 5), reader.GetString(offset + 6),
        reader.IsDBNull(offset + 7) ? null : reader.GetString(offset + 7),
        reader.IsDBNull(offset + 8) ? null : reader.GetInt32(offset + 8),
        reader.IsDBNull(offset + 9) ? null : reader.GetInt32(offset + 9),
        reader.IsDBNull(offset + 10) ? null : reader.GetString(offset + 10), reader.GetString(offset + 11));
}
