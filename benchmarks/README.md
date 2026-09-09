# CodeMap benchmarks

Benchmark classes cover Tier A query/update paths and Tier B graph, SCIP,
external assembly, semantic slice, and freshness paths. Reproducible graph
corpora are generated on demand:

```bash
python3 benchmarks/generate_fixture.py /tmp/codemap-generated-medium --projects 50 --symbols 50000
dotnet run -c Release --project benchmarks/CodeMap.Benchmarks -- --filter '*FindBenchmarks*'
```

Artifacts use `formatVersion`, commit, runtime, OS/CPU, graph size, and per-case
`medianMs`, optional `p95Ms`, `allocatedBytes`, and `correct`. The comparison
script hard-fails correctness and allocations that reach 2x; wall-clock changes
remain warnings until the runner is stabilized.
