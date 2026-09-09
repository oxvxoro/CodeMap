# Compatibility and versioning

| Surface | Current version | Bump when |
|---|---:|---|
| CodeMap tool | 0.1.0 | shipped CLI behavior changes |
| SQLite schema | 4 | durable table/column/encoding changes |
| C# analyzer | 7 | node/edge meaning, IDs, confidence, or syntax support changes |
| Web analyzer | 7 | web node/edge meaning, IDs, confidence, or syntax support changes |
| CLI query JSON | v4 | intentional v4 contract change |
| CLI evidence JSON | v5 | intentional evidence contract change |
| Custom LSP JSON bridge | v1 | request/response contract change |
| Semantic slice | v1 | slice request/result meaning changes |
| SCIP import | v1 | imported graph/provider lifecycle changes |

Refactors, file moves, and application-layer extraction do not by themselves bump analyzer or JSON versions. Analyzer version changes require reindexing. A schema change must be represented by a migration or explicitly require `codemap index --force`; analyzer semantic changes are reindex-only and are not database migrations.

Breaking release notes must call out symbol ID meaning, node/edge kinds, confidence, schema, CLI JSON, MCP parameters, traversal defaults, source-over-external precedence, and ignore rules.
