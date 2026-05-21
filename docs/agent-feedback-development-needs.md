# Agent Feedback Development Needs

## 목적

여러 agent가 실제 작업 중 `roslyn-mcp-server`를 사용하며 남긴 소감을 개발 항목으로 정리한다.
원문은 좋은점, 아쉬운점, 개선 요청이 섞여 있으며, 이 문서는 반복적으로 나온 신호를 우선순위와 요구사항 후보로 재구성한다.

## 요약

현재 도구는 `rg`로 넓게 후보를 찾은 뒤 Roslyn MCP로 심볼, 타입, 참조를 검증하는 보조 검증 도구로 유용했다. 특히 `find_references`, `document_symbols`, `hover`, `go_to_definition`, `get_workspace_status`는 agent 작업 흐름에 실제 도움을 줬다.

가장 큰 개발 필요 영역은 LSP 안정성, diagnostics 신뢰도, 대형 repository에서의 timeout/partial result, workspace 상태 관측성이다. 대형 Unity/C# repository에서는 전역 참조 추적보다 파일 단위 심볼 확인과 타입 확인이 더 안정적으로 평가됐다.

## 긍정 신호

### 참조와 심볼 검증

- `find_references`는 메서드 시그니처 변경 뒤 실제 호출부가 새 메서드에 물리는지 확인하는 데 유용했다.
- 단순 텍스트 검색으로 구분하기 어려운 overload/reference 확인에 의미가 있었다.
- 텍스트 검색으로 잡은 후보를 Roslyn 관점에서 검증할 수 있었다.
- `document_symbols`는 파일 단위 구조, 테스트 메서드, 변경된 메서드 시그니처가 Roslyn 기준으로 인식되는지 확인하는 데 유용했다.
- `hover`는 타입, 필드, 프로퍼티 확인에 도움이 됐다.
- `go_to_definition`, `hover`, `document_symbols`처럼 빠른 기능은 대형 repository에서도 체감이 좋았다.

### Workspace 상태 인식

- `get_workspace_status`로 `Ready`, `WorkspaceWarming`, `Failed`, `LoadedWithErrors` 상태를 바로 확인할 수 있었다.
- 응답에 `WorkspaceWarming`, `LoadedWithErrors`, `partial` 같은 상태가 표시되어 결과 신뢰도를 판단하는 데 도움이 됐다.
- 자동 탐색이 실패해도 `load_solution`으로 명시 solution을 로드할 수 있는 점은 긍정적으로 평가됐다.

### 빌드 전 보조 검증

- 빌드를 실행하기 어렵거나 스킵해야 하는 상황에서 컴파일러 기반 신호를 일부 얻을 수 있었다.
- 현재 사용 방식으로는 `rg`와 파일 읽기만으로는 부족한 semantic 확인을 보완하는 도구로 가치가 있었다.

## 반복된 문제

| 영역 | 관찰된 문제 | 영향 |
| --- | --- | --- |
| LSP 안정성 | `lsp_connection_closed`가 자주 발생하고 수동 `load_solution`이 필요했다. | 작업 흐름이 끊기고 결과 신뢰도가 낮아진다. |
| Diagnostics | `publishDiagnostics`를 아직 못 받음 상태가 많았다. | 실제 오류 없음인지 아직 모르는 상태인지 구분하기 어렵다. |
| 대형 파일 | 큰 파일에서 `document_symbols`가 timeout 됐다. | 파일 구조 확인의 안정성이 떨어진다. |
| 전역 참조 | `find_references`, `peek_references`가 대형 solution에서 30초 timeout으로 실패했다. | 가장 필요한 원인 분석 도구가 큰 repo에서 불안정하다. |
| Workspace scan | `list_workspaces`가 `scan_timeout` 또는 `truncated`로 남는 경우가 있었다. | solution은 로드되어도 후보 정보가 불완전하게 보인다. |
| Warming 신뢰도 | warming 중 결과가 `partial`/`unknown`이라 누락 가능성을 판단하기 어렵다. | 결과가 없을 때 "없음"인지 "아직 모름"인지 애매하다. |
| Error message | `An error occurred invoking ...`처럼 원인이 숨겨지는 메시지가 있었다. | 실패 원인과 다음 조치를 판단하기 어렵다. |
| LoadedWithErrors | 실패 project는 보이나 분석 영향 범위를 판단하기 어렵다. | 어떤 결과를 믿어도 되는지 알기 어렵다. |

## 우선순위 제안

### P0. LSP 연결 안정성과 재시도 정책

가장 반복적으로 나온 불만은 `lsp_connection_closed`로 인한 흐름 중단이다. 자동 재시작은 비용과 부작용이 있으므로 기본 동작을 즉시 바꾸기보다 opt-in 정책으로 설계하는 것이 안전하다.

요구사항 후보:

| 항목 | 내용 |
| --- | --- |
| 자동 재연결 옵션 | LSP connection closed 후 같은 solution/project로 재시작하는 opt-in 옵션을 제공한다. |
| 다음 요청 자동 reload | faulted 상태에서 다음 read tool 호출 시 마지막 target으로 한 번만 자동 reload를 시도하는 옵션을 검토한다. |
| Ready health check | `Ready` 판정 직후 실제 request 가능성을 확인하는 lightweight health check를 검토한다. |
| 상태 메시지 개선 | fault 발생 시 LSP method, workspace state, exception type, 마지막 target을 user-facing error에 포함한다. |

수용 기준 후보:

- LSP read loop fault 후 `get_workspace_status`가 `Failed`, failure code, 마지막 target, 재시도 가능 여부를 명확히 반환한다.
- opt-in 자동 재시작이 켜진 경우 동일 target reload 시도 횟수와 마지막 실패 원인이 status에 보인다.
- 자동 재시작이 무한 루프나 tight polling으로 이어지지 않는다.

### P0. Timeout과 부분 결과 반환 개선

대형 repository에서는 timeout 자체보다 timeout 때 결과가 버려지는 점이 더 큰 문제로 보고됐다. Roslyn LS가 streaming partial result를 주지 않는 요청도 있으므로, 가능한 tool과 불가능한 tool을 구분해야 한다.

요구사항 후보:

| 항목 | 내용 |
| --- | --- |
| per-call timeoutMs | `document_symbols`, `find_references`, `peek_references`, hierarchy 계열에 호출자 지정 timeout을 검토한다. |
| timeout metadata | timeout 실패 시 LSP method, elapsed, timeout, workspace state를 반환한다. |
| 부분 결과 반환 | MCP 서버 쪽 필터링/순회 중 이미 확보한 결과가 있는 tool은 `truncated`, `reason: timeout`과 함께 반환한다. |
| progressive reference 결과 | Roslyn LS가 partial result를 지원하거나 MCP 쪽에서 단계적 질의가 가능할 경우 먼저 찾은 참조를 반환한다. |

주의:

- 단일 LSP request가 timeout되기 전까지 Roslyn LS가 결과를 주지 않는 tool은 서버가 임의로 "찾은 만큼"을 만들 수 없다.
- 이 경우 먼저 명확한 timeout metadata와 범위 제한 옵션을 제공하는 쪽이 현실적이다.

### P0. Diagnostics 신뢰도와 파일 단위 분석 경험

현재 diagnostics는 publish notification cache 기반이라는 계약은 명확하지만, 사용자 입장에서는 "오류 없음"과 "아직 모름"이 섞여 보인다.

요구사항 후보:

| 항목 | 내용 |
| --- | --- |
| diagnostics 상태 구분 | file diagnostics에 `no_publish_received`, `last_publish_received`, `workspace_cache_only` 같은 reason을 더 명확히 제공한다. |
| 파일 열기 후 settle wait | 파일 diagnostics 호출 시 didOpen 후 짧은 settle wait를 선택할 수 있는 옵션을 검토한다. |
| 강제 분석 요청 검토 | Roslyn LS가 지원하는 안전한 read-only diagnostics request가 있는지 조사한다. |
| queue/status 노출 강화 | pending/processed/dropped/stale 외에 최근 diagnostics 수신 파일과 마지막 수신 시간을 요약한다. |

주의:

- workspace-wide full diagnostics computation은 현재 제품 방향과 맞지 않는다.
- "강제 분석"은 read-only 범위 안에서, bounded timeout과 명확한 비용 표시가 있을 때만 검토한다.

### P1. 대형 repository workspace discovery와 cache

대형 repo에서는 `list_workspaces`가 timeout/truncated로 남고 수동 solution 지정에 의존했다. 자동 탐색을 더 똑똑하게 하되, 대형 repo 전체를 무제한 탐색하지 않는 원칙은 유지해야 한다.

요구사항 후보:

| 항목 | 내용 |
| --- | --- |
| scan mode | `Sources/*.sln` 같은 얕은 탐색 우선 또는 glob/path hint 기반 discovery를 검토한다. |
| scan timeout tuning | internal scan budget과 `list_workspaces(maxDepth)` 가이드 개선을 검토한다. |
| workspace cache | 최근 scan 결과와 load target metadata를 세션 내에서 더 명확히 재사용한다. |
| load target cache | client/server 프로젝트를 오갈 때 warming 비용을 줄일 수 있는지 검토한다. |

주의:

- 여러 solution을 동시에 완전 warm 상태로 유지하는 cache는 메모리와 Roslyn LS process 비용이 크다.
- 우선은 "최근 target과 상태를 명확히 보여주기"와 "명시 path hint"가 더 현실적이다.

### P1. Workspace 상태 관측성 강화

`WorkspaceWarming`, `LoadedWithErrors`, `Ready`의 의미는 표시되지만, 결과 신뢰도 판단에 필요한 상세 정보가 부족하다는 피드백이 반복됐다.

요구사항 후보:

| 항목 | 내용 |
| --- | --- |
| 로드 project 카운트 | 가능하면 성공/실패/진행 중 project 수를 status에 노출한다. |
| 분석 범위 표시 | 현재 semantic analysis 대상 project와 실패 project의 영향을 요약한다. |
| warming progress | Roslyn LS에서 얻을 수 있는 progress/log signal을 수집해 상태에 반영한다. |
| 상태별 추천 액션 | `WorkspaceWarming`, `LoadedWithErrors`, `Failed`에서 신뢰 가능한 tool과 재시도 권장 시간을 안내한다. |
| LoadedWithErrors 요약 | 실패한 project 이름 외에 SDK, target framework, missing package, generated file missing 같은 1차 원인을 요약한다. |

### P1. 참조 검색 범위 제한과 noise reduction

대형 solution에서 전역 `find_references`가 timeout되는 경우가 많았다. agent는 대개 전체 repo보다 현재 파일, 현재 project, 특정 path/namespace에서 먼저 답을 원한다.

요구사항 후보:

| 항목 | 내용 |
| --- | --- |
| stronger scope | 현재 파일, 현재 project, namespace, path prefix 기반 참조 검색 옵션을 검토한다. |
| excludePathPrefixes | `includePathPrefixes`와 대응되는 exclude 옵션을 검토한다. |
| generated 제외 | `includeGenerated: false` 또는 공통 generated-code 필터를 검토한다. |
| Unity preset | `Assets/Scripts`, `Editor` 제외, `Packages` 제외, `.g.cs` 제외 같은 preset을 검토한다. |
| snippet 옵션 | `find_references`에 선택적 `contextLines`를 붙여 `peek_references` 재호출을 줄인다. |

주의:

- 현재 `includePathPrefixes`는 Roslyn LS 응답 후 MCP 쪽 필터라서 LSP 검색 비용을 줄이지 못한다.
- 실제 비용 절감을 하려면 Roslyn LS가 지원하는 scope 제한이나 server-side prefilter 가능성을 별도 조사해야 한다.

### P1. Symbol-first API 추가

LLM agent는 정확한 line/column을 자주 틀린다. 위치 기반 API만 있으면 `position_out_of_range`나 잘못된 overload 선택이 잦아진다.

요구사항 후보:

| 항목 | 내용 |
| --- | --- |
| file + symbolName API | `file`, `symbolName`, optional `kind`로 definition/reference/hierarchy를 호출하는 API를 검토한다. |
| nearby symbol fallback | line/column이 조금 벗어난 경우 같은 줄 또는 인접 토큰의 후보 symbol을 반환한다. |
| overload 후보 선택 | 같은 이름의 method가 여러 개면 후보 목록과 stable id를 반환하고, id로 후속 hierarchy/reference를 요청한다. |
| explain position | rg로 찾은 위치를 넘기면 symbol, type, definition, references summary를 묶어서 반환한다. |

### P2. Batch와 고수준 agent workflow

에이전트 작업에서는 작은 tool 호출 여러 개를 반복하는 경우가 많다. Batch와 고수준 read-only workflow는 호출 수와 실패 지점을 줄일 수 있다.

요구사항 후보:

| 항목 | 내용 |
| --- | --- |
| batch document_symbols | 여러 파일의 symbol tree를 한 번에 요청한다. |
| batch peek_definition | 여러 위치의 definition/snippet을 한 번에 요청한다. |
| batch references | 여러 symbol 또는 위치의 reference를 제한된 concurrency로 요청한다. |
| signature-change check | 메서드 시그니처 변경 후 깨진 호출부 후보를 찾는 read-only 검증 command를 검토한다. |
| file-change diagnostics check | 특정 파일 변경 후 관련 diagnostics 상태를 요약한다. |
| direct callers summary | 특정 symbol의 직접 호출자와 주요 call site를 요약한다. |

주의:

- 고수준 command는 write/refactoring tool이 되지 않도록 read-only 분석과 요약으로 제한한다.
- 내부적으로 기존 tool을 조합하는 helper인지, 새로운 MCP tool인지 구분해 설계한다.

### P2. Snippet과 경로 사용성

작업 중 `document_symbols` 결과를 보고 다시 파일을 여는 왕복이 많았다. 경로 입력도 Windows slash, root-relative, solution-relative가 섞인다.

요구사항 후보:

| 항목 | 내용 |
| --- | --- |
| symbol snippet | `document_symbols`에 선택적 본문 일부 또는 signature snippet 옵션을 추가한다. |
| path correction | root-relative/solution-relative/slash 스타일이 섞인 경로를 보정하거나 유사 경로 후보를 제안한다. |
| generated metadata | `.g.cs`, `*.Designer.cs`, generated header 등으로 `generated: true/false`를 표시한다. |

### P2. 문서와 tool guidance 보강

도구의 계약은 문서에 있으나, agent가 실전에서 오해한 부분이 있다. 특히 diagnostics와 completeness 의미를 더 눈에 띄게 해야 한다.

요구사항 후보:

| 항목 | 내용 |
| --- | --- |
| completeness guide | `complete`, `partial`, `unknown`의 의미와 warming 중 신뢰도를 짧게 정리한다. |
| diagnostics caveat | diagnostics가 빌드 대체가 아니며 publish cache 기반임을 tool description과 usage에 더 명확히 적는다. |
| large repo recipe | `rg`로 후보 좁히기, `document_symbols`/`hover`로 검증, `find_references` 범위 제한 순서를 권장 flow로 추가한다. |
| failure playbook | `lsp_connection_closed`, `scan_timeout`, `LoadedWithErrors`, `request_timeout`별 다음 행동을 문서화한다. |

## 개발 후보 Backlog

| 우선순위 | 후보 | 기대 효과 |
| --- | --- | --- |
| P0 | LSP fault 상태 개선과 opt-in 자동 재연결 | 수동 `load_solution` 반복 감소 |
| P0 | timeout/error metadata 구체화 | 실패 원인과 다음 조치 판단 개선 |
| P0 | diagnostics unknown/no-publish 상태 개선 | diagnostics 오해 감소 |
| P1 | workspace status 관측성 강화 | partial result 신뢰도 판단 개선 |
| P1 | 참조 검색 scope/exclude/generated 옵션 | 대형 repo timeout과 noise 감소 |
| P1 | file + symbolName 기반 API | LLM 위치 오류 감소 |
| P2 | batch API | 호출 수와 분석 시간 감소 |
| P2 | symbol snippet/explain position | 파일 읽기 왕복 감소 |
| P2 | Unity/generated preset | Unity 대형 repo 분석 품질 개선 |
| P2 | 문서와 failure playbook 보강 | agent 오용 감소 |

## 현재 제품 방향과의 정렬

요청 대부분은 read-only context provider 방향과 양립 가능하다. 다만 다음 항목은 설계 시 주의가 필요하다.

- 자동 재시작은 편하지만 expensive reload loop를 만들 수 있으므로 opt-in과 retry cap이 필요하다.
- diagnostics 강제 분석은 workspace-wide full build처럼 동작하면 안 된다.
- 고수준 검증 명령은 파일 수정이나 refactoring을 수행하지 않고, read-only 분석 결과와 후보만 반환해야 한다.
- scope/exclude/generated filter가 MCP 쪽 후처리인지 Roslyn LS 비용을 실제 줄이는 기능인지 metadata와 문서에 구분해야 한다.

## 다음 액션 제안

1. P0 안정성 작업을 먼저 분리한다: LSP fault metadata, status 개선, opt-in reconnect 정책 설계.
2. diagnostics의 `unknown` 상태를 더 명확히 표현하고 파일 단위 settle wait 가능성을 조사한다.
3. 대형 repo 참조 검색 timeout을 줄이기 위해 scope 제한과 timeout metadata를 우선 설계한다.
4. `docs/usage.md`에 large repo agent workflow와 failure playbook을 추가한다.
5. 이후 `file + symbolName` API와 batch API를 별도 milestone 후보로 평가한다.
