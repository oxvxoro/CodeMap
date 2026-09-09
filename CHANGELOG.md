# Changelog

## Unreleased

### Added

- `CodeMap.Engine` application contracts, readiness policy, root freshness service, keyed async locks, and pure incremental planning utilities.
- Explicit query backend primitives, focused query facade, SQLite migration ledger, and shared variable batching.
- Architecture, compatibility, benchmark baseline, and release validation documentation.

### Changed

- SQLite project replacement commits the `updating` to `ready` lifecycle transition with the graph transaction.
- C# edge identity and Web confidence/fallback policy are centralized without changing the existing analyzer versions.

### Fixed

- Query-layer helpers now share the same batching and deterministic traversal rules.

### Performance

- Benchmark tiers and baseline artifact format are defined; wall-clock regressions remain warnings until a stable runner baseline is established.

### Compatibility

- Existing CLI JSON v4/v5, custom LSP v1, semantic slice v1, and SQLite schema v4 contracts remain unchanged.
