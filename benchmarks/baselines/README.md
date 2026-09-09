# Benchmark baselines

Baseline artifacts are JSON metadata, not hard-coded pass/fail timings. Each
run records runtime, OS, CPU, commit, graph size, median, optional p95, and
allocated bytes. Tier A microbenchmarks are suitable for pull requests; Tier B
full runs are scheduled weekly. Correctness is a hard gate; wall-clock and
allocation changes are reported for review until a stable runner is established.

Generate a fresh artifact with:

```bash
dotnet run -c Release --project benchmarks/CodeMap.Benchmarks -- --filter '*'
python3 benchmarks/quota_ab.py
```
