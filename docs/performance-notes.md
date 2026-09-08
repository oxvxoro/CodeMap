# CodeMap 성능 메모

대규모 저장소 질의는 결과 상한과 SQLite 매개변수 예산을 함께 지켜야 한다.

* `CodeMapQueryService`의 부분집합 스캔 임계값은 `400`이다. 작은 질의는
  전체 그래프를 메모리로 올리고, 큰 질의는 SQLite 집합 연산을 사용한다.
* SQLite `IN` 절은 변수 수 제한을 넘지 않도록 가변 청크로 분할한다.
* 지연 outgoing 조회는 `200`개 윈도우로 페이지를 읽어 대규모 관계 조회에서
  메모리 사용량이 결과 상한에 비례하도록 한다.

변경 전후 확인 명령:

```bash
dotnet run -c Release --project benchmarks/CodeMap.Benchmarks -- --filter '*RelationScanBenchmarks*'
dotnet test CodeMap.slnx --no-restore --filter 'FullyQualifiedName~QueryEngineSqlTests'
```

이 메모는 의미 변경 없는 성능 경계와 측정 절차를 기록한다. 실제 운영 저장소
벤치마크 결과가 확보되기 전에는 임계값을 임의로 조정하지 않는다.
