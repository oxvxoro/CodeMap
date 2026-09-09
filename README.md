# CodeMap

CodeMap builds a local semantic graph of a repository and exposes deterministic, agent-friendly queries for finding symbols, callers/callees, application flow, impact, and source context.

## Supported input

- C# and .NET, including ASP.NET Core
- Razor/Blazor and XAML/WPF enrichment
- JavaScript, TypeScript, HTML, and CSS
- SCIP graph import for additional languages

## Install and quickstart

```bash
dotnet tool install --global CodeMap.Cli
cd path/to/repository
codemap index
codemap find OrderService --json
codemap callers "OrdersController.Get" --json
codemap flow "/orders" --kind http --evidence
```

The recommended agent loop is `find -> flow/callers -> source read`. Run `codemap update` after source changes. `codemap status --check-freshness` reports the current index and freshness state.

## Accuracy model

Edges report `Semantic`, `Syntactic`, or `Heuristic` resolution and an optional confidence between 0 and 1. Roslyn C# relations are semantic; markup and web bindings are generally syntactic or heuristic. Unsupported syntax, generated code, dynamic dispatch, and non-local build configuration are best-effort and may remain unresolved.

CLI JSON query responses are versioned independently from the SQLite schema. MCP and the custom JSON-lines LSP bridge use the same application semantics; their transport envelopes differ.

## Architecture and development

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md), [docs/COMPATIBILITY.md](docs/COMPATIBILITY.md), and [docs/AGENT_INTEGRATION.md](docs/AGENT_INTEGRATION.md). Build and test with:

```bash
dotnet restore CodeMap.slnx
dotnet build CodeMap.slnx --no-restore -warnaserror
dotnet test CodeMap.slnx --no-build
```

Contributions should preserve snapshot/SQLite query parity, analyzer resolution meaning, and the public CLI/MCP contracts.
