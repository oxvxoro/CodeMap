# 에이전트 통합 가이드

CodeMap은 C#(Roslyn)과 웹 자산(JS/TS/HTML/CSS)을 위한 **로컬 시맨틱 인덱스**다.

CodeMap은 저장소 탐색을 위한 **선택적 도구**이며, 모든 조사에 강제로 사용할 필요는 없다. `grep`, 파일 직접 읽기, 기타 탐색 도구도 계속 유효하다. 아래 기준에 따라 **CodeMap이 실제로 탐색 비용을 줄여 주는 경우에만 사용**한다.

> 핵심 원칙: CodeMap은 “정확한 심볼 또는 진입점을 빠르게 찾고, 관계를 한 단계 추적하는 용도”로 우선 사용한다. 정적 분석 범위를 벗어나는 레거시 WebForms, 문자열 기반 HTTP 호출, native/unreadable 외부 어셈블리 등은 초기에 한계를 인지하고 `grep`/파일 읽기로 전환한다. 외부 **managed** 어셈블리는 소스 코드가 실제로 도달한 심볼을 root로 same-assembly reachable graph를 best-effort 인덱싱할 수 있다.

---

## 1. 에이전트 플레이북

### 1.1 최소 워크플로 — CodeMap 호출은 보통 2~3회로 끝낸다

집중된 코드 조사에서는 다음 순서를 기본값으로 사용한다.

1. 저장소를 한 번 인덱싱한다.

   * 최초: `codemap index [path]`
   * 코드 수정 후: `codemap update`
   * 현재 어떤 프로젝트가 인덱싱되어 있는지 `.codemap/state.json`에서 확인한다.

2. 조사 대상 심볼을 찾는다.

   * `codemap find <query>`

3. **후속 관계 명령은 원칙적으로 하나만 선택한다.**

   * `flow`
   * `callees`
   * `callers`
   * `refs`
   * `impl`
   * `impact`

   같은 심볼에 대해 여러 관계 명령을 병렬로 남발하지 않는다. 먼저 질문에 가장 직접적인 명령 하나를 사용한다.

4. CodeMap이 반환한 **파일과 라인 범위를 실제 소스에서 읽는다.**

   * 답이 명확해졌으면 즉시 종료한다.
   * 인덱스 결과만으로 최종 판단하지 말고, 필요한 경우 원본 소스를 확인한다.

### 1.2 집중된 질문에서 시작하면 안 되는 명령

하나의 심볼, 호출 경로, 핸들러 등을 추적하는 질문이라면 처음부터 다음 명령을 사용하지 않는다.

* `map`
* `context`

대규모 레거시 웹 저장소에서는 이 명령들이 관련 없는 유틸리티 심볼까지 넓게 노출해 컨텍스트를 낭비할 수 있다.

집중된 질문의 기본 패턴은 다음과 같다.

```text
find → 관계 명령 1개 → 실제 파일 읽기 → 종료
```

---

## 2. 어떤 상황에서 CodeMap을 사용할 것인가

| 목적                                  | 우선 명령                               |
| ----------------------------------- | ----------------------------------- |
| 심볼 위치 찾기                            | `find`                              |
| 앞으로 이어지는 호출 체인 추적(JS 또는 애플리케이션 그래프) | `flow` 또는 `callees`                 |
| 이 심볼을 누가 호출하는지 확인                   | `callers`                           |
| 변경 영향 범위 확인                         | `impact --changed` 또는 `diff <base>` |
| ASP.NET Core 라우트 → 핸들러 추적           | `flow "<route>" --kind http`        |
| Razor/Blazor/XAML UI 연결 관계 추적       | `flow "<component>" --kind ui`      |

### 선택 기준

* **이름/심볼을 찾는 문제**라면 `find`부터 시작한다.
* **진입점에서 앞으로 흐름을 따라가는 문제**라면 `flow`가 우선이다.
* **호출자 탐색**이 목적이면 `callers`를 사용한다.
* **변경이 어디까지 영향을 주는지**가 목적이면 `impact` 또는 `diff`를 사용한다.
* ASP.NET Core, Razor/Blazor, WPF처럼 애플리케이션 그래프가 지원되는 경우에는 수동으로 `callers`/`callees`를 여러 번 이어 붙이기보다 `flow`를 우선한다.

### 성능 참고 (이 저장소 self-repo 벤치마크, 참고용)

`benchmarks/quota_ab.py`로 이 저장소 자체에 대해 측정한 결과(12개 focused 코드 탐색 태스크, 토큰 프록시 = UTF-8 bytes/4, 실제 모델 tokenizer/과금 계산기가 아님):

* CodeMap Lean(evidence 없이 find + 관계 명령 1개): baseline 대비 payload 약 10~15% 절감, tool call 약 25~30% 감소.
* CodeMap Verified(`--evidence` 사용): 관계마다 evidence를 붙이므로 baseline보다 payload가 오히려 커질 수 있다 — evidence는 discovery 기본값이 아니라 최종 검증 단계에서만 사용한다.
* 이 수치는 이 저장소 규모/구조에 특화된 참고치이며, 다른 저장소에 그대로 일반화되지 않는다. `find`는 대체로 payload를 크게 줄이고, depth를 늘린 `callees`/`flow`는 더 많은 semantic 정보를 반환하므로 오히려 payload가 커질 수 있다 — 이는 회귀가 아니라 다단계 조사를 한 번의 요청으로 대체한 결과일 수 있다.

---

## 3. 언제 `grep`/파일 읽기로 전환해야 하는가

다음 조건에서는 CodeMap 재시도를 반복하지 말고 텍스트 검색과 직접 파일 읽기로 전환한다.

### 3.1 레거시 ASP.NET WebForms

다음 영역은 CodeMap의 C# 애플리케이션 그래프 범위 밖이다.

* `.aspx` 내부의 인라인 `<script>`
* `App_Code/*.cs`
* WebForms code-behind

`.codemap/state.json`에 C# 프로젝트 없이 `web:<name>` 형태의 웹 전용 인덱스만 있다면, 독립된 `.js`/`.ts` 파일만 인덱싱된 상태다.

따라서 WebForms UI 이벤트, `.aspx` 인라인 핸들러, code-behind 연결은 `grep`과 파일 읽기를 사용한다.

### 3.2 외부 어셈블리

CodeMap은 **managed** 외부 어셈블리에 대해, 소스 코드의 시맨틱 분석이 실제로 도달한 심볼을 root로 same-assembly reachable implementation graph를 **best-effort**로 인덱싱할 수 있다. 결과는 `external:<identity>` synthetic project 아래에 저장되며, source→external `Calls` edge로 연결된다.

지원 범위:

* managed assembly only (PE metadata를 읽을 수 있는 경우)
* source-reached roots only — 프로젝트의 전체 reference set을 일괄 디컴파일하지 않음
* same-assembly reachable graph only — cross-assembly 재귀 확장 없음
* completeness 보장 없음 — unreadable metadata, native DLL, unresolved root, empty graph는 skip

다음 경우에는 CodeMap 결과가 없거나 불완전할 수 있으므로 `grep`/파일 읽기 또는 별도 디컴파일 조사로 전환한다.

* native/unmanaged DLL
* metadata를 읽을 수 없는 assembly
* source analysis가 external root에 도달하지 못한 참조
* source symbol과 external symbol 이름이 충돌할 때 — query는 source-first 동작을 유지한다

### 3.3 종료 코드 2 또는 빈 결과가 두 번 나온 경우

다음과 같이 이름을 조금씩 바꾸며 `find`를 반복하지 않는다.

```text
doapprov → doProcess → Page_Load
```

`exit code 2` 또는 실질적인 빈 결과가 두 번 이어지면, 더 이상의 유사어 재검색보다 텍스트 검색이 빠르다고 판단하고 `grep`으로 전환한다.

### 3.4 JS 문자열 안의 HTTP 경로

다음과 같은 코드는 CodeMap `flow`의 서버 핸들러 연결 엣지가 아니다.

```js
xmlhttp.open("POST", "aspx/foo.aspx")
```

또는 문자열 기반 `fetch("aspx/...")` 호출도 서버 핸들러까지 자동 연결되지 않는다.

이 경우:

1. 문자열 경로를 `grep`한다.
2. 해당 `.aspx` 및 code-behind 파일을 직접 읽는다.

---

## 4. 피해야 할 안티패턴

| 패턴                                                       | 문제점 / 올바른 대응                                 |
| -------------------------------------------------------- | -------------------------------------------- |
| `codemap .`, `--deps`, `--importers`                     | 제거된 CLI 사용 방식이다. 현재 서브커맨드를 사용한다.             |
| 하나의 추적 작업에 `map` / `context`부터 사용                        | 레거시 저장소에서 노이즈가 크다. `find` + 관계 명령 1개를 우선한다.  |
| 동일 심볼에 CLI와 MCP를 둘 다 사용                                  | 중복 비용이 발생한다. 한 조사에서는 한 인터페이스만 사용한다.          |
| `find`가 `.aspx` 인라인 UI를 놓쳤는데 `callers` / `impact`를 계속 사용 | 인라인 핸들러는 JS 인덱스에 없을 수 있다. `grep`/read로 전환한다. |
| `no_matches` 이후 `find`를 여러 번 변형 재시도                      | 수익이 급격히 줄어든다. 텍스트 검색이 더 빠르다.                 |
| 답이 이미 확정됐는데도 관계 명령을 추가로 실행                        | 두 번째 관계 조회는 반드시 새로운 미해결 질문에 답해야 한다. 그렇지 않으면 종료한다. |
| 첫 조사부터 `--evidence`/`get_flow(includeEvidence=true)`를 기본 사용 | evidence는 discovery 기본값이 아니라 특정 edge를 증명해야 할 때만 사용하는 검증 단계다. |

---

## 5. MCP와 CLI 중 무엇을 사용할 것인가

한 번의 조사에서는 **CLI 또는 MCP 중 하나만 사용한다.**

같은 심볼에 대해 두 인터페이스를 동시에 사용하지 않는다.

대응 관계:

| CLI      | MCP           |
| -------- | ------------- |
| `find`   | `find_symbol` |
| `flow`   | `get_flow`    |
| `impact` | `get_impact`  |

즉, 기능상 동일한 질의를 CLI와 MCP로 중복 실행하지 않는다.

이미 MCP 프로세스가 연결되어 있는 반복 조사 세션(같은 저장소에 대해 여러 번 질의하는 경우)에서는, 매번 새 CLI 프로세스를 띄우기보다 이미 연결된 MCP 프로세스를 계속 재사용하는 편을 우선한다. MCP 호스트는 프로세스 로컬 freshness 캐시를 이미 가지고 있어 반복 조회 시 매번 새로 인덱스 최신성을 재계산하지 않는다. 다만 이것이 "MCP가 항상 더 빠르다"는 보장은 아니며, 단발성 조사에서는 CLI와 MCP 중 어느 쪽을 사용해도 무방하다.

---

## 6. 전체 워크플로

### 6.1 인덱스 생성 및 갱신

```bash
codemap index [path]
```

`.codemap/index.db`를 생성한다.

코드가 수정된 뒤에는:

```bash
codemap update
```

를 사용한다.

저장소가 오래된 CodeMap 버전으로 인덱싱되어 있으면, 특히 .NET application-graph 기능이 추가되기 이전 버전의 인덱스라면 C# analyzer 버전 검사가 오래된 인덱스를 거부할 수 있다.

이때 오류 메시지가 `codemap index --force`를 안내하면 한 번 실행한다.

```bash
codemap index --force
```

### 6.2 심볼 해석

```bash
codemap find <query>
```

다음 형태로 심볼을 찾을 수 있다.

* 이름
* 정규화된 전체 이름(qualified name)
* 안정적인 심볼 ID

### 6.3 관계 명령

필요에 따라 다음을 사용한다.

```text
refs
callers
callees
impl
impact
```

지원되는 명령에서는 `--depth`를 사용할 수 있다.

**quota 효율적인 첫 시도(lean) 패턴:**

```bash
codemap find SaveApproveInfo --json --max-results 20
codemap callees SaveApproveInfo --json --depth 1
```

`--depth 1`(callees 기본값)로 시작하고, 전이적(transitive) 관계가 실제로 필요할 때만 depth를 높인다. `--evidence`는 기본으로 켜지 않는다 — 특정 edge를 증명해야 할 때만 그 edge에 대해서만 추가한다.

```bash
codemap callees SaveApproveInfo --json --depth 1 --evidence
```

PR 리뷰나 변경 영향 분석에서는 다음을 우선한다.

```bash
codemap impact --changed
```

또는:

```bash
codemap diff <base>
```

### 6.4 `flow`로 HTTP/UI 진입점 추적

```bash
codemap flow <entry> --kind http|ui|all
```

`flow`는 HTTP 또는 UI 진입점에서 애플리케이션 그래프를 따라 앞으로 추적한다.

HTTP:

```text
route
→ handler method
→ DI로 해석된 구현체
→ call graph
```

UI:

```text
view/component
→ binding / event handler
→ ViewModel member
```

ASP.NET Core, Razor/Blazor, WPF 진입점을 조사할 때는 `callers`와 `callees`를 수동으로 여러 번 연결하기보다 `flow`를 우선한다.

### 6.5 `map`은 넓은 저장소 파악용

```bash
codemap map --focus <name> --tokens 500
```

`map`은 **광범위한 구조 파악**에 사용한다.

다이어그램이 필요하면:

```bash
--format mermaid
```

를 사용할 수 있다.

단일 심볼이나 호출 경로를 추적하는 용도로는 사용하지 않는다.

### 6.6 `context`는 낯선 저장소를 위한 1회성 컨텍스트 묶음

```bash
codemap context <task>
```

저장소 구조를 거의 모르는 상태에서 한 번에 관련 컨텍스트를 얻는 용도다.

관계 근거와 신뢰도 필터가 필요하면:

```bash
--evidence
--min-confidence <0..1>
```

를 추가한다.

이미 찾으려는 심볼 이름을 알고 있다면 `context`보다 `find`를 우선한다.

### 6.7 아키텍처 검사

```bash
codemap check architecture
```

CI에서 사용하기 적합한 아키텍처 게이트다.

### 6.8 MCP 서버

```bash
codemap mcp
```

주요 MCP 도구:

* `find_symbol`
* `get_context`
* `get_impact`
* `explain_relation`
* `get_flow`
* `refresh_index`

---

## 7. 에이전트/스크립트용 출력 규칙

에이전트나 스크립트에서 CodeMap을 호출할 때는 **항상 `--json`을 사용한다.**

관계의 소스 위치와 evidence label이 필요하면 `--evidence`를 사용한다. 이 경우 JSON v5 응답을 사용한다.

또한 응답에 다음이 있는지 확인한다.

```json
"stale": true
```

작업 트리가 변경되었고 `stale: true`라면:

```bash
codemap update
```

를 실행한 뒤 다시 조사한다.

### Semantic Slice와 SCIP

로컬 값의 정의·사용 흐름이 실제 질문일 때만 C# 심볼에 `codemap slice <query> --json`을 사용한다. Slice는 intraprocedural 분석이며 fresh index가 필요하다. 호출 경계를 넘는 흐름은 `callers`/`callees`를 사용한다. 원문이 필요한 경우에만 `--include-source`를 추가하며, 기본 응답에는 source가 포함되지 않는다.

지원되지 않는 언어는 사용자가 만든 artifact를 `codemap scip import <artifact> --name <name>`으로 가져온다. C#/Razor/XAML/JS/TS/HTML/CSS source와 겹치는 SCIP document는 거부된다. plain SCIP reference는 `References`이며 `Calls`로 추측되지 않는다. source나 artifact가 바뀌면 재import가 필요하다.

---

## 8. 레거시 WebForms + JS 추적 예시

### 작업

“승인 버튼 → 서버 핸들러” 흐름을 추적한다.

### 권장 절차

1. JS 심볼 위치 확인

```bash
codemap find SaveApproveInfo
```

결과 예:

```text
ApprovUI/ApprovUI_Edge.js
```

2. JS 내부 전방 흐름 추적

```bash
codemap flow SaveApproveInfo
```

결과 예:

```text
SaveFile
SignSave
...
```

3. JS 내부에 문자열로 들어 있는 서버 엔드포인트 검색

```text
grep doapprov.aspx
```

문자열 기반 HTTP 경로는 CodeMap 그래프에 서버 핸들러 엣지로 존재하지 않는다.

4. code-behind 직접 읽기

```text
ApprovUI/aspx/doapprov.aspx.cs
```

웹 전용 프로젝트 인덱스에서는 이 code-behind가 인덱싱되지 않을 수 있으므로 직접 읽는다.

### 이 상황에서 기대하지 말아야 할 것

UI 핸들러가 `.aspx` 인라인 스크립트에만 존재한다면 다음 명령이 해결책이 될 것이라고 기대하지 않는다.

```text
flow doapprov
callers SaveApproveInfo
find btnApprove_onclick
```

이 경우 CodeMap 실패가 아니라 **분석 범위 밖의 코드이므로 grep/read로 전환해야 하는 상황**이다.

---

## 9. `flow`를 진입점 탐색에 사용하는 방법

### 9.1 HTTP 진입점

예:

```bash
codemap flow "GET /api/orders/{id}" --kind http --evidence
```

또는 `find`에서 얻은 route display name을 사용할 수 있다.

추적 흐름:

```text
route
→ RoutesTo
→ handler method
→ Calls / UsesType / Implements
→ call graph
→ Registers / ResolvesTo
→ DI-resolved implementation
```

### 9.2 UI 진입점

예:

```bash
codemap flow "Views/MainWindow.xaml" --kind ui --evidence
```

또는 `.razor`, `.cshtml` 경로를 사용할 수 있다.

주요 엣지:

* `Renders`: 자식 컴포넌트 렌더링
* `BindsTo`: parameter / bind / binding 대상
* `HandlesEvent`: click / command 등 이벤트 핸들러
* `UsesViewModel`: XAML DataContext 기반 ViewModel 연결

### 9.3 depth 규칙

기본 depth:

```text
4
```

허용 범위:

```text
1..8
```

* 라우트에서 더 깊게 호출 그래프를 따라가야 하면 `--depth`를 높인다.
* 첫 번째 연결만 집중해서 보고 싶으면 낮춘다.

### 9.4 `flow` 결과 자동 신뢰 기준

`flow` 엣지를 별도 수동 검증 없이 자동으로 신뢰할 수 있는 기준:

```text
confidence >= 0.85
```

| 엣지              | confidence |
| --------------- | ---------: |
| `RoutesTo`      |     `1.00` |
| `Registers`     |     `1.00` |
| `ResolvesTo`    |     `1.00` |
| `Renders`       |     `0.90` |
| `HandlesEvent`  |     `0.90` |
| `UsesViewModel` |     `0.90` |
| `BindsTo`       |     `0.85` |

CodeMap은 모호하거나 상수가 아닌 일치에 대해 이러한 application-graph 엣지를 생성하지 않는다. 따라서 위 신뢰도 수준에서 반환된 엣지는 휴리스틱 추측이 아니라 정적인 단일 일치로 취급할 수 있다.

단, `confidence < 0.85`인 `flow` 결과는 검증한다.

현재 application-graph 엣지에는 정상적으로 0.85 미만의 종류가 없으므로, `flow` 출력에 그보다 낮은 값이 보이면 정상적인 약한 일치라기보다 버그 또는 기본 허용 목록 밖의 엣지가 섞인 상황으로 본다.

---

## 10. JSON 스키마 — version 4 / version 5

현재 CLI `--json` 출력은 **version 4**(evidence 없음)와 **version 5**(`--evidence`)를 사용한다. 과거 version 2/3 계약은 [cli-json-v2.schema.json](cli-json-v2.schema.json) / [cli-json-v3.schema.json](cli-json-v3.schema.json)에 과거 계약으로만 보존된다.

### 10.1 `--evidence`가 없는 경우

일반 질의 명령은 **version 4**를 출력한다.

### 10.2 `--evidence`를 사용하는 경우

관계를 생성하는 명령은 **version 5**를 사용하며 relation에 다음과 같은 선택 필드가 추가된다.

```json
{
  "location": { "file": "app.ts", "line": 4 },
  "evidence": "static-import"
}
```

가능한 evidence 값:

* `semantic`
* `static-import`
* `same-file-fallback`
* `name-fallback`
* `css-selector`
* `unknown`

### 10.3 confidence 필터

```bash
--min-confidence <0..1>
```

기본값은 `0`이며 모든 엣지를 유지한다.

CLI, MCP, LSP는 다음 값을 거부한다.

* 유한수가 아닌 값
* `0..1` 범위를 벗어난 값

이 경우 오류 코드는 `query_failed`다.

### 10.4 두 심볼의 직접 관계 조회

```bash
relation <source> <target> --json --evidence
```

두 심볼을 해석한 뒤 둘 사이의 직접 엣지를 반환한다.

`relation`은 항상 v5 응답 계약을 사용하므로 `--evidence`가 필수다.

### 10.5 일반 질의 응답 예시

```json
{
  "version": 4,
  "query": "Greet",
  "matches": [{
    "id": "sym://...",
    "project": "ProjB",
    "kind": "method",
    "name": "Greet",
    "qualifiedName": "Fixture.ProjB.Greeter.Greet",
    "signature": "Greet()",
    "file": "ProjB/Greeter.cs",
    "startLine": 10,
    "endLine": 10,
    "language": "csharp"
  }],
  "relations": [{
    "kind": "caller",
    "source": "Call()",
    "target": "Greet()",
    "sourceId": "sym://...",
    "targetId": "sym://...",
    "depth": null,
    "edgeKind": "Calls",
    "resolutionKind": "semantic",
    "confidence": 1.0
  }],
  "stale": false,
  "reason": null
}
```

### 10.6 `context --json`

`context --json`은 `query` 대신 다음을 추가한다.

* `task`
* `mapLines`

### 10.7 오류 응답

오류도 요청과 동일한 envelope version을 사용한다.

* 일반 JSON 명령 → version 4
* `--evidence` 명령 → version 5

형태:

```json
{
  "version": 4,
  "error": {
    "code": "index_not_found|schema_outdated|query_failed|git_unavailable",
    "message": "..."
  }
}
```

### 10.8 diff / changed impact 응답

다음 명령:

```bash
codemap diff --json
codemap impact --changed --json
```

은 `diffResponse`를 반환하며 주요 필드는:

* `changedFiles`
* `matches`
* `relations`
* `risk`
* `analysisComplete` — git diff 대상 파일이 모두 index에서 해석 가능하면 `true`
* `unresolvedChangedFiles` — `analysisComplete`가 `false`일 때 index에 없어 영향 분석되지 않은 변경 파일 경로

`--json` without `--evidence` → version 4. `--json --evidence` → version 5.

---

## 11. MCP 설정

MCP 서버 실행:

```bash
codemap mcp --root <repo>
```

에이전트 설정 예시:

```json
{
  "servers": {
    "codemap": {
      "type": "stdio",
      "command": "codemap",
      "args": ["mcp", "--root", "/path/to/repo"]
    }
  }
}
```

사용 가능한 주요 도구:

* `find_symbol`
* `get_context`
* `get_impact`
* `explain_relation`
* `get_flow`
* `refresh_index`

### 11.1 `get_flow`

```text
get_flow(
  entry,
  kind="all",
  depth=4,
  root?,
  maxResults=20,
  minConfidence=0,
  includeEvidence=true
)
```

애플리케이션 그래프를 순회한다.

기본값(`includeEvidence` 생략 또는 `true`)에서는 `explain_relation`과 동일한 v5 relation 형태를 반환하며 다음을 포함한다.

* `location`
* `evidence`
* `stale`

**첫 조사 단계에서는 `includeEvidence=false`를 우선 사용한다.** 이 경우 `location`/`evidence` 필드 없이 위상 정보(`sourceId`, `targetId`, `depth`, `edgeKind`, `resolutionKind`, `confidence`)만 반환하여 응답 payload를 줄인다. 이후 특정 edge의 근거가 필요할 때만 `explain_relation(source, target)`을 그 edge에 대해 호출한다.

### 11.2 `get_impact`와 profile

```text
get_impact(
  query,
  root?,
  depth=2,
  maxResults=20,
  profile="code"
)
```

CLI의 `impact`, `impact --changed`, LSP의 `impact`도 `profile`을 지원한다.

#### `profile="code"` — 기본값

다음 코드 참조 엣지만 따라간다.

* `References`
* `Calls`
* `Constructs`
* `UsesType`
* `Implements`
* `Inherits`

#### `profile="app"`

`code` 프로필에 더해 `flow(kind=all)`이 사용하는 route/DI/UI 엣지도 포함한다.

따라서 변경된 핸들러에서 시작해 관련 route나 UI artifact까지 영향 범위를 확장할 수 있다.

### 11.3 LSP JSON-line 서버

```bash
codemap lsp
```

지원 명령:

* `find`
* `impact`
* `context`
* `relation`
* `flow`

예:

```json
{ "command":"relation", "source":"...", "target":"...", "minConfidence":0 }
```

```json
{ "command":"flow", "entry":"...", "kind":"all", "depth":4, "minConfidence":0 }
```

```json
{ "command":"impact", "query":"...", "profile":"code" }
```

기계 판독용 스키마:

* [cli-json-v2.schema.json](cli-json-v2.schema.json) — 과거 evidence 없는 계약
* [cli-json-v3.schema.json](cli-json-v3.schema.json) — 과거 evidence 계약
* [cli-json-v4.schema.json](cli-json-v4.schema.json) — 현재 evidence 없는 CLI 출력
* [cli-json-v5.schema.json](cli-json-v5.schema.json) — 현재 evidence CLI 출력

---

## 12. 종료 코드

|  Code | 의미                                                      | 에이전트 처리                             |
| ----: | ------------------------------------------------------- | ----------------------------------- |
|   `0` | 성공                                                      | 결과를 해석하고 필요한 파일을 읽는다.               |
|   `1` | 실패: 인덱스 없음, 오래된 스키마, 예기치 않은 오류 등                        | 오류 메시지에 따라 인덱스 생성/재생성 등을 수행한다.      |
|   `2` | 매치 없음 또는 질의 모호함 (`reason`: `no_matches` 또는 `ambiguous`) | 후보를 구체화하거나 반복 실패 시 grep/read로 전환한다. |
| `130` | 취소됨                                                     | 작업이 취소된 상태로 처리한다.                   |

---

## 13. 정확도 및 신뢰도 필드

| `resolutionKind` | 의미                                                                                                                           |
| ---------------- | ---------------------------------------------------------------------------------------------------------------------------- |
| `semantic`       | Roslyn이 해석한 C# 엣지. `RoutesTo`/`Registers`/`ResolvesTo` 포함. `confidence = 1.0`                                                |
| `syntactic`      | 정확한 CSS selector ↔ HTML 매치 또는 Razor/Blazor/XAML markup 엣지. `Renders`/`HandlesEvent`/`UsesViewModel = 0.90`, `BindsTo = 0.85` |
| `heuristic`      | Regex/AST 기반 웹 추출. `confidence = 0.55–0.70`                                                                                  |

### 13.1 신뢰도 해석 규칙

`confidence < 0.7`이면 **확정 정보가 아니라 힌트**로 취급한다.

반드시 실제 소스에서 검증한다.

특히 heuristic web call edge를 런타임 동작의 증거로 취급하지 않는다.

반면 application-graph edge는 다음 조건을 만족하면 자동 처리에 사용할 수 있다.

```text
confidence >= 0.85
```

해당 엣지 종류:

* `RoutesTo`
* `Renders`
* `BindsTo`
* `HandlesEvent`
* `Registers`
* `ResolvesTo`
* `UsesViewModel`

이 엣지들은 CodeMap이 단일하고 모호하지 않은 정적 매치를 찾았을 때만 생성한다.

---

## 14. 모호한 심볼 처리

응답에서:

```json
"reason": "ambiguous"
```

이면 `matches`에 후보 목록이 들어 있다.

이 경우 일반 이름으로 반복 검색하지 말고 더 구체적인 식별자를 사용한다.

### 방법 1: 전체 qualified name

```text
Fixture.ProjB.Greeter.Greet
```

### 방법 2: stable id

```text
sym://...
```

동명 심볼(homonym)의 경우:

```text
sym://T:Shared.Widget#2
```

---

## 15. 한계 사항

에이전트는 다음 제한을 항상 고려해야 한다.

1. **JS/TS 동적 호출은 해석되지 않는다.**

   * 동적으로 결정되는 호출 대상은 정적으로 연결되지 않을 수 있다.

2. **계산된 selector는 해석되지 않는다.**

3. **패키지 import는 파일로 연결되지 않는다.**

   예:

   ```js
   import ... from 'react'
   ```

4. **cross-compilation explicit interface member mapping은 불완전할 수 있다.**

5. **`flow`는 런타임 전용 연결을 따라가지 않는다.**

   다음은 추적하지 않는다.

   * runtime routing / middleware
   * reflection
   * source-generator output
   * dynamic Razor
   * DI factory delegate
   * JS interop

   `flow`는 문서에 정의된 정적 패턴만 따른다.

6. **Razor `@code` 블록 선언은 직접 인덱싱되지 않는다.**

   component member는 Roslyn이 볼 수 있는 `.cs` 또는 code-behind partial을 통해서만 해석된다.

7. **상수가 아닌 route template 또는 factory-delegate DI registration**은 다음 엣지를 만들지 않는다.

   * `RoutesTo`
   * `Registers`
   * `ResolvesTo`

   이 경우 route/registration node만 남거나 아무 결과도 없을 수 있다.

   단, minimal API의 lambda/anonymous handler를 Roslyn이 하나의 semantic anonymous-function symbol로 명확히 해석하면 `Function` node에 대한 `RoutesTo` 엣지가 생성된다.

   해석되지 않거나 모호한 anonymous form만 엣지가 생성되지 않는다.

8. **레거시 ASP.NET WebForms는 C# application graph 지원 범위 밖이다.**

   예:

   * `.aspx` 인라인 script
   * modern csproj/sln에 포함되지 않은 `App_Code`

   웹 전용 인덱스는 독립 `.js`/`.ts`만 분석한다.

   `.aspx`, `.cs` code-behind, 인라인 이벤트 핸들러는 `grep`/read가 필요하다.

9. **JS 문자열 기반 HTTP 호출은 서버 핸들러 `flow` 엣지를 만들지 않는다.**

   예:

   ```js
   xmlhttp.open(...)
   fetch("aspx/...")
   ```

10. **최종 기준은 항상 원본 소스 코드다.**

    CodeMap 인덱스는 탐색을 돕는 도구이며, 소스 코드보다 우선하지 않는다.

---

## 16. 에이전트용 최종 판단 규칙

에이전트가 CodeMap을 사용할 때는 다음 의사결정 순서를 기본 규칙으로 삼는다.

```text
1. 질문이 특정 심볼/호출/진입점에 관한가?
   ├─ 예 → find
   │       └─ 가장 직접적인 관계 명령 1개
   │           └─ 반환된 실제 소스 파일 읽기
   │               └─ 답이 명확하면 종료
   │
   └─ 아니오 → 저장소 전체 구조를 모르는가?
           ├─ 예 → context 또는 map
           └─ 아니오 → 목적에 맞는 직접 명령 사용

2. 결과가 WebForms inline/code-behind, native/unreadable 외부 어셈블리,
   문자열 HTTP 경로 등 CodeMap 범위 밖인가?
   ├─ 예 → 즉시 grep/read로 전환
   └─ 아니오 → CodeMap 관계 결과 활용

3. no_matches / exit code 2가 반복되는가?
   ├─ 예 → 유사어 find 반복 금지, grep/read로 전환
   └─ 아니오 → 필요 시 qualified name / stable id로 구체화

4. confidence가 낮은가?
   ├─ < 0.7 → 힌트로만 사용하고 소스 검증
   ├─ 0.7~0.84 → 자동 확정하지 말고 검증
   └─ >= 0.85인 application-graph edge → 자동 처리 가능

5. 어떤 경우에도 최종 판단은 소스 코드가 우선한다.
```

이 규칙의 목적은 CodeMap 호출 횟수를 늘리는 것이 아니라, **가장 짧은 경로로 정확한 소스 위치와 관계를 찾고, 지원 범위를 벗어나면 즉시 다른 탐색 방식으로 전환하는 것**이다.
