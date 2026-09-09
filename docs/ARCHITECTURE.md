# CodeMap architecture

```text
Core (graph, IDs, analyzer contracts)
  ├── CSharp ──┐
  ├── Web ─────┼── Storage (SQLite schema, migrations, query persistence)
  └── Scip ────┘             ↑
                        Engine (application/use-case contracts)
                          ↑              ↑
                       CLI             MCP / LSP bridge
```

`CodeMap.Engine` is the transport-independent application boundary. The existing `CodeMapQueryService` and `IncrementalCodeMapIndexer` remain compatibility facades while focused services and planners are adopted. Storage owns durable graph access; analyzer projects do not depend on Storage.

## Graph and query

Nodes represent files, declarations, routes, markup, and imported symbols. Edges carry kind, source location, resolution kind, and confidence. Snapshot and SQLite readers implement the same primitive query contract. Shared ranking, source-over-external resolution, traversal ordering, visited-ID behavior, and SQLite variable batching are covered by parity tests.

## Index lifecycle

The durable metadata state is `building`, `updating`, or `ready`; a missing database is `missing`. Graph replacement and the final `ready` marker are committed in one transaction, so readers see the previous complete graph during an update and never a partial project replacement. A cancelled or failed transaction rolls back to the previous ready graph. Stale freshness is process-local and does not mutate graph data.

## Analyzer pipeline

C# analysis loads a Roslyn compilation, collects declarations and anonymous functions, resolves semantic relations, computes public-surface fingerprints, collects external roots, and then merges ASP.NET/Razor/XAML enrichment. Web analysis selects Tree-sitter first and deterministic regex fallback when the AST has no usable result; module, DOM, and CSS relations apply centralized confidence policy.

## Adapters and versioning

CLI presentation owns v4/v5 JSON rendering and exit codes. MCP serializes application responses into its existing tool envelopes. `codemap lsp` is a custom one-JSON-object-per-line stdio bridge, not a standard LSP server. See the compatibility matrix for which version changes require reindexing or a schema bump.
