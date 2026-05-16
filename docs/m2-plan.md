# M2 Read-Only Tools Implementation Plan

## 현재 기준

M0/M1은 완료된 상태로 본다. 최신 코드 구조 기준으로는 다음이 구현되어 있다.

- `WorkspaceSession`: workspace 후보 cache, `load_solution`, `load_project`, 상태 전이, Roslyn LS handle 관리
- `WorkspaceScanner`, `GitWorkspaceScanner`, `PathGuard`
- `LspClient`: LSP framing, request/response correlation, notification dispatch, server-to-client request 응답, timeout/cancellation, in-flight 제한
- `WorkspaceTools`: `list_workspaces`, `load_solution`, `load_project`, `get_workspace_status`
- 아직 없음: `NavigationTools`, `DiagnosticsTools`, `DocumentStateManager`, `DiagnosticStore`, typed LSP navigation/diagnostic mapper

M2의 핵심은 읽기 tool을 추가하되, 대규모 repo와 warming 상태에서 결과가 예측 가능하게 동작하도록 공통 기반을 먼저 넣는 것이다.

## 초안 조정 결론

기존 초안:

- M2a: `document_symbols`, `hover`, `go_to_definition`
- M2b: `find_references`, `find_symbols`
- M2c: `diagnostics`, `DocumentStateManager`, `DiagnosticStore`

권장 조정:

- `DocumentStateManager`는 M2c까지 미루면 안 된다. `document_symbols`, `hover`, `go_to_definition`, `find_references`, file-specific `diagnostics` 모두 현재 파일 내용을 Roslyn LS에 맞춰야 하므로 M2a의 기반 작업으로 먼저 구현한다.
- `go_to_definition`은 `find_references`와 같은 location/position mapper를 공유하므로 M2a보다 M2b에 두는 편이 리뷰 단위가 깔끔하다.
- `find_symbols`는 workspace-wide 검색이고 대규모 repo 위험이 커서 `find_references`와 같은 단계에 묶기보다 별도 단계로 분리한다.
- `diagnostics`는 `DiagnosticStore`와 publish notification 처리 때문에 독립 단계로 둔다.

권장 M2 split:

1. M2a: read-tool foundation + `document_symbols` + `hover`
2. M2b: `go_to_definition` + `find_references`
3. M2c: `find_symbols`
4. M2d: `diagnostics` + `DiagnosticStore`

3단계 이름만 유지해야 한다면 M2b 안에서 `go_to_definition/find_references`와 `find_symbols`를 별도 commit/review 단위로 나눈다.

## 공통 원칙

- MCP tool 입력/출력의 line/column은 항상 1-based다.
- LSP 내부 position은 항상 0-based다.
- 변환은 tool별로 흩뜨리지 않고 M2a에서 공통 mapper로 구현한다.
- 모든 사용자 입력 file path는 `PathGuard`를 통과한다.
- `StartingLanguageServer`에서는 navigation/diagnostics tool을 queue에 넣지 않고 즉시 `workspace_loading`을 반환한다.
- `LspReady`, `WorkspaceWarming`에서는 best-effort로 실행하고 `workspaceState`, `completeness`를 포함한다.
- `completeness` 값은 `complete`, `partial`, `unknown`만 사용한다.
- 대량 결과 tool은 `totalKnown`, `returned`, `truncated` metadata를 포함한다.
- stdout logging 금지 원칙은 유지한다.
- 테스트 seam은 작은 interface로 두고, production class를 테스트 편의로 `virtual`/상속 가능하게 열지 않는다.

## M2a: Read Tool Foundation, Document Symbols, Hover

### 목표

위치/문서 기반 read tool의 기반을 먼저 만든다. 이 단계가 끝나면 Roslyn LS에 파일을 open/sync하고, 단일 파일 중심의 낮은 fan-out tool을 안정적으로 실행할 수 있어야 한다.

### 포함 범위

- `DocumentStateManager`
  - `EnsureOpenAsync(file)`
  - 최초 접근 시 disk read 후 `textDocument/didOpen`
  - last write time/length 변경 시 full document `textDocument/didChange`
  - LRU 상한 초과 시 `textDocument/didClose`
  - `MaxOpenDocuments`, `MaxDocumentBytes` 옵션 추가
- URI/path mapper
  - root-relative path -> full path -> file URI
  - LSP URI/result path -> root-relative path
- position mapper
  - MCP 1-based line/column -> LSP 0-based position
  - LSP 0-based range/location -> MCP 1-based output
  - line/column 1 미만 입력 거부
- common read-tool state gate
  - NotLoaded 자동 load 시도
  - multiple workspace면 `workspace_not_loaded`
  - StartingLanguageServer면 `workspace_loading`
  - Warming이면 metadata 부착
- common result metadata DTO
  - `workspaceState`
  - `completeness`
  - `reason`
  - `retryAfterMs`
  - `truncated`
- LSP typed models 최소 추가
  - `TextDocumentIdentifier`
  - `VersionedTextDocumentIdentifier`
  - `TextDocumentItem`
  - `DidOpenTextDocumentParams`
  - `DidChangeTextDocumentParams`
  - `DidCloseTextDocumentParams`
  - `Position`
  - `Range`
  - `DocumentSymbol`
  - `Hover`
  - `MarkupContent`
- MCP tools
  - `document_symbols(file)`
  - `hover(file, line, column)`

### 제외 범위

- `go_to_definition`
- `find_references`
- `find_symbols`
- `diagnostics`
- incremental text edit sync
- write/refactoring tool

### didOpen/didChange/didClose 결정

이 단계에서 전부 구현한다. `document_symbols`조차 spike 기준으로 `didOpen` 뒤 정상 응답을 확인했으므로, read tool 첫 단계부터 문서 동기화 계약을 갖추는 편이 안전하다.

### completeness 결정

M2a부터 공통화한다.

- `Ready`: 기본 `complete`
- `WorkspaceWarming`: `document_symbols`는 `partial` 또는 `unknown`, `hover`는 `partial`
- `LspReady`: `unknown` 또는 `partial`
- 결과가 비어도 warming 중이면 `reason`을 포함한다.

### result limiting 결정

M2a부터 helper를 만든다. `document_symbols`도 큰 파일에서는 symbol tree가 커질 수 있으므로 반환 node 수 상한을 적용한다. `hover`는 대량 결과 tool은 아니지만 hover text 크기 상한을 둔다.

### 필수 테스트

- `DocumentStateManager`가 최초 접근 시 `didOpen`을 보낸다.
- 파일 timestamp/length 변경 시 `didChange`를 보낸다.
- 열린 문서 수가 상한을 넘으면 LRU 문서에 `didClose`를 보낸다.
- `MaxDocumentBytes` 초과 파일은 user-facing error를 반환한다.
- 같은 파일 path casing 차이로 중복 open하지 않는다.
- line/column 1-based 입력이 LSP 0-based로 변환된다.
- LSP range/location이 MCP 1-based로 반환된다.
- `StartingLanguageServer` 상태에서 `document_symbols`/`hover`는 즉시 `workspace_loading`을 반환한다.
- `WorkspaceWarming` 결과에는 `workspaceState`, `completeness`가 포함된다.
- fake LSP로 `documentSymbol`/`hover` response mapping을 검증한다.
- Roslyn LS 통합 테스트가 가능하면 작은 sample solution에서 `document_symbols`, `hover` smoke test를 추가한다.

### xhigh 리뷰 체크리스트

- `DocumentStateManager`가 path guard를 우회하지 않는가
- LSP notification 순서가 `didOpen` -> request, 변경 시 `didChange` -> request를 보장하는가
- full document sync가 큰 파일에서 무제한 read를 하지 않는가
- LRU eviction이 state만 지우고 `didClose`를 빠뜨리지 않는가
- version 증가가 didChange마다 단조 증가하는가
- line/column 변환이 tool별로 중복 구현되지 않았는가
- 1-based/0-based off-by-one 테스트가 실패 케이스까지 있는가
- Warming metadata가 성공/빈 결과 모두에 붙는가
- MCP stdout에 logging이 섞일 가능성이 없는가
- 새 테스트 seam이 interface 기반이고 production class를 열지 않았는가

## M2b: Go To Definition And Find References

### 목표

위치 기반 navigation 중 location 목록을 반환하는 tool을 구현한다. `go_to_definition`으로 mapper를 검증하고, 같은 기반에서 대량 결과 위험이 있는 `find_references`를 추가한다.

### 포함 범위

- MCP tools
  - `go_to_definition(file, line, column)`
  - `find_references(file, line, column, includeDeclaration = true, maxResults?)`
- LSP requests
  - `textDocument/definition`
  - `textDocument/references`
- LSP result mapper
  - `Location`
  - `Location[]`
  - `LocationLink`
  - `LocationLink[]`
  - null/empty result 처리
- references result limiting
  - default 200
  - configured server maximum 적용
  - `totalKnown`, `returned`, `truncated`
- expensive request classification
  - `find_references`는 expensive method로 분류한다.
  - 가능하면 `MaxExpensiveLspRequests` 옵션을 이 단계에서 추가한다.

### 제외 범위

- workspace-wide `find_symbols`
- diagnostics
- reference pagination cursor

### completeness 결정

- `go_to_definition`
  - `Ready`: `complete`
  - `WorkspaceWarming`: `partial`
  - `LspReady`: `unknown`
- `find_references`
  - `Ready`: `complete`
  - `WorkspaceWarming`/`LspReady`: `partial`
  - cross-project 누락 가능성을 `reason`에 명시한다.

### 대규모 repo 위험

`find_references`는 cross-project 분석으로 비싸고 결과가 클 수 있다. 반드시 timeout, cancellation, result truncation, expensive concurrency limit을 검증한다.

### 필수 테스트

- `go_to_definition`이 단일 `Location`을 root-relative path와 1-based position으로 반환한다.
- `go_to_definition`이 `LocationLink`를 올바른 target range로 반환한다.
- null/empty definition 결과가 user-facing empty result로 정리된다.
- `find_references`가 `includeDeclaration`을 LSP context에 전달한다.
- 대량 reference response가 `maxResults`로 잘리고 `truncated: true`를 반환한다.
- Warming 중 references 결과는 `partial` metadata를 포함한다.
- timeout 시 pending request가 제거되고 다음 요청이 정상 처리된다.
- cancellation 시 `$/cancelRequest`가 전송된다.
- fake LSP delay로 expensive request 상한을 검증한다.
- Roslyn LS 통합 테스트가 가능하면 sample solution에서 definition/reference smoke test를 추가한다.

### xhigh 리뷰 체크리스트

- `DocumentStateManager.EnsureOpenAsync`가 위치 기반 요청 전에 항상 호출되는가
- definition/reference mapper가 `Location`과 `LocationLink`를 모두 처리하는가
- 외부 URI 또는 root 밖 URI를 결과에 섞지 않는가
- result limit이 LSP response를 그대로 MCP에 노출하기 전에 적용되는가
- `maxResults` 사용자 입력에 서버 최대 상한이 적용되는가
- timeout/cancellation 이후 semaphore와 pending dictionary leak이 없는가
- Warming 중 empty references를 "참조 없음"으로 단정하지 않는 metadata가 있는가
- expensive request limit이 일반 hover/document_symbols까지 과도하게 막지 않는가

## M2c: Find Symbols

### 목표

workspace-wide symbol search를 독립 단계로 구현한다. 이는 대규모 repo에서 응답 크기와 완전성 판단 위험이 크므로 references와 분리해 리뷰한다.

### 포함 범위

- MCP tool
  - `find_symbols(query, maxResults?)`
- LSP request
  - `workspace/symbol`
- LSP result mapper
  - `SymbolInformation`
  - `WorkspaceSymbol` 형태가 올 경우 대비
- result limiting
  - default 100
  - `totalKnown`, `returned`, `truncated`
- empty result reason
  - 특히 warming 중 빈 배열은 "없음"이 아니라 "index incomplete 가능"으로 설명한다.

### 제외 범위

- `solution_overview`
- symbol pagination cursor
- fuzzy query ranking 자체 구현

### completeness 결정

- `Ready`: `complete` 또는 `unknown`
- `WorkspaceWarming`: `partial`
- `LspReady`: `unknown`
- query가 너무 짧아 대량 결과를 유발할 수 있으면 명확한 user-facing validation 또는 낮은 limit을 적용한다.

### 대규모 repo 위험

`workspace/symbol`은 가장 쉽게 큰 결과를 만들 수 있다. query validation, timeout, expensive concurrency limit, payload 크기 제한을 확인한다.

### 필수 테스트

- query가 비어 있으면 user-facing error를 반환한다.
- `workspace/symbol` result가 root-relative path와 1-based location으로 변환된다.
- 대량 symbol result가 default limit으로 잘린다.
- 사용자 `maxResults`와 server maximum 중 작은 값이 적용된다.
- Warming 중 빈 결과에 `completeness`와 `reason`이 포함된다.
- root 밖 URI 결과는 버리거나 안전한 오류로 정리된다.
- fake LSP로 10,000개 symbol response를 반환해 truncation과 payload 제한을 검증한다.
- opt-in 실제 repo에서는 `dotnet/sdk` 또는 `dotnet/roslyn`에서 timeout/hang 없이 반환하는지만 본다.

### xhigh 리뷰 체크리스트

- query validation이 너무 느슨해 workspace-wide dump tool이 되지 않는가
- result limit이 mapper 초기에 적용되어 불필요한 DTO 폭증을 줄이는가
- Warming 중 빈 결과의 의미가 명확히 표시되는가
- `SymbolInformation.location`이 없는 workspace symbol variant를 안전하게 처리하는가
- URI decode/path normalize가 Windows 경로와 공백 경로에서 깨지지 않는가
- expensive request concurrency가 references와 공유되는가
- 실제 Roslyn LS가 빈 배열을 정상 응답할 수 있다는 spike 결과를 반영했는가

## M2d: Diagnostics And DiagnosticStore

### 목표

publish diagnostics notification을 저장하고 제한된 diagnostic 조회 tool을 제공한다. workspace 전체 diagnostics를 완전하게 만들려 하지 않고, 현재 알려진 diagnostics를 bounded cache로 제공한다.

### 포함 범위

- `DiagnosticStore`
  - file별 diagnostics 저장
  - bounded cache
  - severity count summary
  - `lastUpdatedAt`
  - 오래된 상세 목록 eviction
- LSP notification handling
  - `textDocument/publishDiagnostics`
- MCP tool
  - `diagnostics(file?, severity?, maxResults?, scope?)`
- file-specific diagnostics
  - file이 있으면 `DocumentStateManager.EnsureOpenAsync(file)` 호출
  - 짧은 settle timeout은 선택 사항
- workspace diagnostics
  - 명시적 `scope: "workspace"`일 때만 전체 known diagnostics 조회
  - default 200 limit
  - `totalKnown`, `returned`, `truncated`, `lastUpdatedAt`
- `get_workspace_status` 확장
  - 열린 문서 수
  - known diagnostics file count
  - last diagnostic update time

### 제외 범위

- workspace 전체 diagnostic 계산 완료까지 대기
- restore/build 성공 보장
- diagnostics apply/fix/code action
- 무제한 diagnostics 반환

### completeness 결정

- file-specific diagnostics
  - 해당 파일에 publish가 도착했으면 `complete` 또는 `unknown`
  - 아직 publish가 없으면 `unknown`과 reason 포함
- workspace diagnostics
  - `Ready` 전에는 `partial`
  - `Ready` 이후에도 Roslyn LS가 전체 complete 신호를 주지 않으면 `unknown` 가능
- diagnostics 결과는 "현재 알려진 diagnostics"임을 명시한다.

### 대규모 repo 위험

diagnostics는 notification 폭주와 cache pressure가 핵심 위험이다. store는 파일 수, diagnostic 수, 응답 크기를 모두 제한해야 한다.

### 필수 테스트

- `publishDiagnostics` notification이 `DiagnosticStore`에 저장된다.
- 같은 파일 diagnostics가 새 notification으로 교체된다.
- empty diagnostics notification이 기존 diagnostics를 clear한다.
- severity filter가 적용된다.
- file-specific diagnostics가 root-relative path와 1-based range를 반환한다.
- workspace scope가 default limit으로 잘리고 `truncated: true`를 반환한다.
- cache 상한 초과 시 오래된 상세 diagnostics가 eviction된다.
- summary count는 가능한 범위에서 유지된다.
- publish storm fake test 후 memory/entry count가 상한을 넘지 않는다.
- file이 지정된 diagnostics 호출 전에 `didOpen`이 전송된다.
- Roslyn LS 통합 테스트가 가능하면 sample solution에 의도적 compile error를 두고 diagnostic smoke test를 추가한다.

### xhigh 리뷰 체크리스트

- diagnostics notification handler가 LSP read loop를 오래 막지 않는가
- store update가 thread-safe한가
- diagnostics cache가 file count와 item count 양쪽에서 bounded인가
- workspace diagnostics가 무제한 결과를 반환하지 않는가
- severity filter가 반환 후가 아니라 가능한 이른 단계에서 적용되는가
- stale diagnostics와 unknown completeness가 사용자에게 구분되는가
- `get_workspace_status` 확장이 비싼 계산을 하지 않는가
- diagnostics tool이 build/restore 완료를 기다리며 hang되지 않는가

## 통합 테스트 전략

Fast tests는 일반 `dotnet test`에 포함한다.

- mapper tests
- path/URI conversion tests
- document LRU tests
- fake LSP navigation tests
- result truncation tests
- diagnostics cache tests
- concurrency/cancellation tests

Roslyn LS integration tests는 설치된 환경에서만 실행하고, 미설치 시 skip한다.

- 작은 sample solution load
- `document_symbols`
- `hover`
- `go_to_definition`
- `find_references`
- `find_symbols`
- `diagnostics`

Opt-in real repo tests는 기본 성공 조건으로 삼지 않는다.

- `dotnet/roslyn`: warming 중 navigation
- `dotnet/sdk`: 다중 solution, workspace symbol
- `dotnet/aspnetcore`: 현실적 warming smoke
- `dotnet/runtime`, `Azure/azure-sdk-for-net`: stress profile

## 최종 M2 완료 기준

- 모든 read-only tool이 `StartingLanguageServer`에서 즉시 `workspace_loading`을 반환한다.
- `WorkspaceWarming`에서 가능한 tool은 best-effort로 실행하고 completeness metadata를 포함한다.
- 위치 입력/출력은 전부 1-based로 통일된다.
- LSP 내부 변환은 0-based로 검증된다.
- 모든 file 입력은 `PathGuard`를 통과한다.
- 모든 대량 결과 tool은 result limiting/truncation metadata를 포함한다.
- 열린 문서 수와 diagnostics cache는 bounded다.
- fake LSP 기반 fast tests가 대량 결과, cancellation, timeout, notification 폭주를 검증한다.
- Roslyn LS가 없는 환경에서도 integration tests는 명확한 skip 이유를 남긴다.
