# 구현 참고 메모

## 목적

이 문서는 새 구현 세션에서 놓치기 쉬운 자잘하지만 중요한 결정을 한곳에 모아 둔다.

주요 참고 문서:

- `docs/plan.md`
- `docs/architecture.md`
- `docs/large-repo-test-plan.md`

## 최신 상태 요약

2026-05-17 현재 `main` 기준 구현 상태:

- M0/M1 workspace 기반 완료
- M2a read-tool foundation, `document_symbols`, `hover` 완료
- M2b `go_to_definition`, `find_references` 완료
- M2c `find_symbols` 완료
- M2d `diagnostics`, `DiagnosticStore` 완료
- M2 large repo readiness 일부 완료
  - explicit workspace file 선택을 위한 `solution/open`/`project/open` notification
  - Roslyn LS project load error를 `LoadedWithErrors` 상태와 workspace warnings로 노출
  - LSP read loop fault handling
  - git scanner pathspec
  - filesystem scanner candidate-limit 조기 중단
  - large repo tuning CLI option 공개
- M3 사용자/클라이언트 사용성 정리 완료
  - `README.md`와 `docs/usage.md`에 사용자 설치/설정 흐름 작성
  - `roslyn-language-server` 탐색/설치 오류 메시지 정리
  - PowerShell, Semantic Kernel, ASP.NET Core stdio smoke 결과 기록
  - `solution_overview`는 M3에서 구현하지 않고 M4 이후 후보로 보류
- M4 startup initial solution load 완료
  - `--load-solution <path>`가 지정되면 MCP 서버 시작 후 background task가 기존 solution load 경로로 `.sln`/`.slnx`를 로드한다.
  - 지정하지 않으면 기존 명시적 load 또는 첫 read tool auto-load 동작을 유지한다.

현재 MCP tool:

- `list_workspaces`
- `load_solution`
- `load_project`
- `get_workspace_status`
- `document_symbols`
- `hover`
- `go_to_definition`
- `find_references`
- `find_symbols`
- `diagnostics`

현재 남은 주요 후보:

- diagnostics notification offload
- 추가 실제 MCP client smoke 반복
- opt-in large repo 검증과 default tuning
- `solution_overview` M4 이후 구현 여부 판단

최근 로컬 검증 결과:

```text
dotnet test roslyn-mcp-server.sln
```

- 94 passed / 0 failed / 1 skipped / 95 total

아래 milestone별 완료 메모는 당시 구현 시점의 이력으로 남긴다.

## M3 완료 메모

2026-05-17 기준 M3 사용자/클라이언트 사용성 정리는 완료된 상태로 본다.

- `README.md`는 현재 구현된 tool과 권장 시작 흐름을 짧게 안내한다.
- `docs/usage.md`는 `roslyn-language-server` 별도 설치, MCP client 설정 예시, 권장 tool flow, large repo 주의사항을 담는다.
- `roslyn-language-server` 미설치/탐색 실패 오류는 설치 명령과 명시 경로 옵션을 포함하도록 정리했다.
- `docs/solution-overview-evaluation.md`는 `solution_overview`를 M3에서 구현하지 않고 M4 또는 별도 milestone 후보로 남긴다는 결정을 기록한다.
- PowerShell, Semantic Kernel, ASP.NET Core smoke 결과는 `docs/smoke-tests/` 아래에 기록되어 있다.

M3 이후 기준으로 사용자용 설치/설정 문서 정리는 남은 항목이 아니다. 후속 우선순위는 diagnostics notification offload, opt-in large repo 검증/default tuning, 필요 시 추가 실제 MCP client smoke 반복이다.

## M2b 완료 메모

2026-05-17 기준 M2b(Go To Definition, Find References)는 완료되어 `main` branch에 push되어 있다.

- M2b 완료 기준 commit: `79f7dbd Implement M2b definition and reference tools`
- 당시 다음 구현 세션은 `docs/m2-plan.md`의 M2c(`find_symbols`)부터 진행하는 상태였다. 현재는 M2c/M2d까지 완료되어 위 최신 상태 요약을 기준으로 본다.

구현된 M2b 기능:

- MCP read tools
  - `go_to_definition(file, line, column)`
  - `find_references(file, line, column, includeDeclaration = true, maxResults?)`
- LSP 요청
  - `textDocument/definition`
  - `textDocument/references`
- location mapper
  - `Location`, `Location[]`, `LocationLink`, `LocationLink[]`
  - `null`/empty response는 empty result로 정리
  - `LocationLink`는 `targetRange`를 MCP location/range 기준으로 사용
  - root 밖 URI 또는 non-file URI는 user-facing 결과에 노출하지 않고 버린다.
- references result limiting
  - 기본 `DefaultReferencesMaxResults = 200`
  - 사용자 `maxResults`와 서버 hard cap 중 작은 값을 적용
  - 반환 metadata에 `totalKnown`, `returned`, `truncated` 포함
- expensive LSP request 제한
  - `find_references`는 expensive request로 분류한다.
  - `CliOptions.MaxExpensiveLspRequests`를 추가했고 기본값은 2다.
  - 전체 in-flight 제한(`MaxInFlightLspRequests`)과 별도로 expensive semaphore를 적용한다.
- 기존 M2a foundation 재사용
  - 모든 위치 기반 요청은 LSP 요청 전에 `DocumentStateManager.EnsureOpenAsync`를 호출한다.
  - 사용자 입력 file path는 `DocumentPathMapper`/`PathGuard`를 통과한다.
  - MCP 입출력 line/column은 1-based, LSP 내부 position/range는 0-based로 유지한다.

M2b metadata 계약:

- `go_to_definition`
  - `Ready`: `complete`
  - `WorkspaceWarming`: `partial`
- `find_references`
  - `Ready`: `complete`
  - `WorkspaceWarming`: `partial`
  - warming/early ready 상태에서는 cross-project reference 누락 가능성을 `reason`에 포함한다.
- 현재 구현에서는 load 성공 후 바로 `WorkspaceWarming`으로 전환하므로 `LspReady`는 외부 상태로 노출되지 않는다.

M2b 테스트 상태:

```text
dotnet format roslyn-mcp-server.sln --verify-no-changes
dotnet build roslyn-mcp-server.sln
dotnet test roslyn-mcp-server.sln
```

- 당시 확인 결과: 50 passed / 0 failed / 0 skipped
- 현재 환경에는 `roslyn-language-server`가 설치되어 있어 Roslyn LS smoke integration test가 skip 없이 실행됐다.
- Roslyn LS integration test는 작은 sample solution에서 `document_symbols`, `hover`, `go_to_definition`, `find_references` smoke를 확인한다.

M2b 직후 남은 범위:

- `find_symbols`
- `diagnostics`
- `DiagnosticStore`
- incremental text edit sync
- write/refactoring tool

후속 구현 주의사항:

- M2c `find_symbols`는 workspace-wide expensive request로 보고, M2b에서 추가한 expensive LSP request 제한을 재사용한다.
- 대량 workspace symbol 결과도 `totalKnown`, `returned`, `truncated` metadata를 포함해야 한다.
- warming 중 빈 workspace symbol 결과를 "없음"으로 단정하지 말고 `completeness`와 `reason`을 유지한다.
- root 밖 URI filtering, 1-based/0-based 변환, `StartingLanguageServer`의 즉시 `workspace_loading` 계약은 M2b와 같은 기준을 따른다.

## M2c 완료 메모

2026-05-17 기준 M2c(`find_symbols`)가 완료되었다.

- MCP read tool
  - `find_symbols(query, maxResults?)`
- LSP 요청
  - `workspace/symbol`
- 결과 mapper
  - `SymbolInformation` 형태와 `WorkspaceSymbol` 형태를 모두 안전하게 처리한다.
  - `location`이 없거나 range가 없는 workspace symbol은 symbol 정보만 반환하고 location은 비운다.
  - root 밖 file URI, non-file URI, path 변환 실패 결과는 반환하지 않는다.
  - MCP 출력 location은 root-relative path와 1-based line/column/range를 사용한다.
- result limiting
  - 기본 `DefaultSymbolMaxResults = 300`
  - 서버 hard cap은 1000
  - 사용자 `maxResults`와 hard cap 중 작은 값을 적용한다.
  - metadata에 `totalKnown`, `returned`, `truncated`를 포함한다.
- workspace 상태 metadata
  - `WorkspaceWarming`: `partial`
  - `Ready`: Roslyn LS가 workspace symbol index completeness를 알려주지 않으므로 `unknown`
  - warming 중 빈 결과에는 symbol index가 불완전할 수 있다는 `reason`을 포함한다.
  - 현재 구현에서는 load 성공 후 바로 `WorkspaceWarming`으로 전환하므로 `LspReady`는 외부 상태로 노출되지 않는다.
- expensive request
  - `find_symbols`는 M2b에서 추가한 expensive LSP request limit을 재사용한다.

## M2a 완료 메모

2026-05-17 기준 M2a(Read Tool Foundation, Document Symbols, Hover)는 완료되어 `main` branch에 push되어 있다.

- M2a 완료 기준 commit: `4f9d1e7 Implement M2a read tools`
- 이후 후속 정리 commit:
  - `b1429ac Document LSP symbol constants`
  - `5f87914 Move symbol kind formatter to LSP model`
  - `8c94803 Clarify protocol constant helper placement`
  - `8b1aaf6 Clarify scan budget fallback comments`
  - `fbf7653 Address M2a review feedback`
- M2b까지 완료되었으므로 최신 진행 기준은 위 `M2b 완료 메모`를 본다.

구현된 M2a 기능:

- `DocumentStateManager`
  - 최초 접근 시 disk read 후 `textDocument/didOpen`
  - timestamp/length 변경 시 full document `textDocument/didChange`
  - `MaxOpenDocuments` 초과 시 LRU `textDocument/didClose`
  - `MaxDocumentBytes` 초과 시 `document_too_large` user-facing error
  - 같은 파일 path casing 차이로 중복 open하지 않도록 OS별 path comparer 사용
- `DocumentPathMapper`
  - root-relative file input -> guarded full path -> file URI
  - LSP file URI -> guarded root-relative path
- position/range mapper
  - MCP tool 입출력은 1-based line/column
  - LSP 내부 `Position`/`Range`는 0-based
  - 1 미만 line/column은 `invalid_position`
- read-tool 공통 state gate
  - `NotLoaded`에서 workspace 후보가 하나면 자동 load
  - solution이 여러 개면 `workspace_not_loaded`
  - `StartingLanguageServer`에서는 queue하지 않고 즉시 `workspace_loading`
  - 현재 구현은 load 성공 후 `LspReady`를 외부 상태로 노출하지 않고 바로 `WorkspaceWarming`으로 전환한다.
  - `WorkspaceWarming`에서는 best-effort 실행 및 metadata 반환
- common metadata/result DTO
  - `workspaceState`, `completeness`, `reason`, `retryAfterMs`, `truncated`
- typed LSP model 추가
  - text document sync model, `Position`, `Range`, `DocumentSymbol`, `Hover`, `MarkupContent`
  - LSP `SymbolKind` enum은 `LspModels.cs`에 두고 spec 링크를 주석으로 남김
  - MCP 출력용 symbol kind 문자열은 `SymbolKindExtensions.ToMcpName(this SymbolKind kind)`에서 처리
- MCP read tools
  - `document_symbols(file)`
  - `hover(file, line, column)`

M2a review 반영 메모:

- workspace reload 중에는 기존 Roslyn LS shutdown이 끝나기 전이라도 세션 상태를 즉시 `StartingLanguageServer`로 바꾸고 기존 handle을 fast path에서 제거한다. 이 동안 `document_symbols`/`hover`는 이전 LSP client로 요청하지 않고 `workspace_loading`을 반환해야 한다.
- `hover` mapper는 LSP `null` response와 missing `contents`를 빈 결과로 처리한다. object가 아닌 hover response 또는 malformed `range`는 raw exception이 아니라 `invalid_lsp_response` user-facing error로 정리한다.
- LSP framing은 `Content-Length`가 전역 body 상한(`LspFraming.DefaultMaxContentLength`, 현재 16MB)을 넘으면 body allocation 전에 거부한다. `LspClient`는 이 경우 pending request를 `invalid_lsp_response`로 완료한다.
- `document_symbols` mapper는 전체 `DocumentSymbol` tree DTO를 먼저 만들지 않고 MCP 반환 상한(`MaxDocumentSymbolNodes`)까지만 item을 만든다. `totalKnown`은 가능한 범위에서 계속 세고 `returned`와 비교해 `truncated`를 설정한다.
- `hover` contents array는 `MaxHoverCharacters`를 넘으면 추가 string 조립을 중단하고 `truncated` metadata를 설정한다.

M2a 테스트 상태:

```text
dotnet test roslyn-mcp-server.sln
dotnet format roslyn-mcp-server.sln --verify-no-changes
```

- 당시 확인 결과: 40 passed / 0 failed / 0 skipped
- 현재 환경에는 `roslyn-language-server`가 설치되어 있어 Roslyn LS smoke integration test가 skip 없이 실행됐다.
- Roslyn LS가 없는 환경에서는 `RoslynLanguageServerIntegrationTests`가 설치 명령을 포함한 이유로 skip한다.

M2a 직후 남은 범위:

- `go_to_definition`
- `find_references`
- `find_symbols`
- `diagnostics`
- `DiagnosticStore`
- incremental text edit sync
- write/refactoring tool

후속 구현 주의사항:

- 위치 기반 tool은 LSP 요청 전에 항상 `DocumentStateManager.EnsureOpenAsync`를 호출한다.
- `StartingLanguageServer`에서 navigation 요청을 내부 queue에 쌓지 않는다.
- warming 중 빈 결과는 "없음"으로 단정하지 말고 `completeness`/`reason`을 유지한다.
- 모든 사용자 입력 file path는 `PathGuard` 또는 `DocumentPathMapper`를 통과해야 한다.
- LSP spec 숫자 enum/상수는 tool class에 inline magic number로 두지 않는다. `docs/coding-principles.md`의 "외부 protocol 상수" 원칙을 따른다.
- 테스트 seam이 필요하면 production class를 `virtual`/상속 가능하게 열지 말고 작은 interface를 둔다.

## M0/M1 완료 메모

2026-05-17 기준 M0/M1 구현은 완료되어 `main` branch에 push되어 있다.

- M0/M1 완료 기준 commit: `3407ad5 Clarify coding principles scope`
- 이후 새 세션은 이 commit 이후의 `main`에서 M2를 시작하면 된다.

구현된 M0/M1 기능:

- `net10.0` C# solution/app/test project
- 공식 C# MCP SDK 기반 stdio MCP server
- CLI 옵션: `--root`, `--roslyn-language-server`, `--log-level`, `--log-file`, `--ls-log-dir`, `--startup-timeout`
- workspace root 검증과 path guard
- git-aware workspace scanner: git worktree에서는 `git ls-files -co --exclude-standard -z` 우선, 실패 시 bounded recursive scanner fallback
- `.sln`, `.slnx`, `.csproj` 후보 탐색과 `list_workspaces`
- `roslyn-language-server` locator와 미설치 오류
- Roslyn LS process start/stop
- LSP framing, request/response correlation, server-to-client request 응답, timeout/cancellation 처리
- LSP initialize/shutdown
- `IRoslynWorkspaceLoader` 뒤의 `load_solution`, `load_project`
- `get_workspace_status`
- scanner, path guard, LSP framing/client, 기본 workspace 상태 전이 테스트

Workspace scan 참고:

- git worktree에서는 `GitWorkspaceScanner`가 `git ls-files -co --exclude-standard -z`를 우선 사용한다.
- 이 방식은 `.gitignore`, 하위 `.gitignore`, `.git/info/exclude`, global exclude를 git이 직접 적용하므로 filesystem scan보다 정확하다.
- git이 없거나 git worktree 밖이거나 scan budget을 다 쓰기 전에 실패하면 `WorkspaceScanner`가 bounded filesystem scan으로 fallback한다.
- git이 scan budget을 소진한 경우에는 다시 filesystem tree walk를 시작하지 않고 `scan_timeout`을 반환한다.

Roslyn LS spike 결과 요약:

- `roslyn-language-server --stdio --autoLoadProjects`는 정상 실행된다.
- `initialize.rootUri`와 `workspaceFolders`를 workspace directory URI로 넘기면 initialize가 성공한다.
- initialize 뒤 `initialized` notification이 필요하다.
- Roslyn LS는 `workspace/configuration` server-to-client request를 보낼 수 있으므로 client가 응답해야 한다.
- 작은 sample에서 `textDocument/didOpen` 뒤 `textDocument/documentSymbol`은 정상 응답했다.
- `workspace/projectInitializationComplete`는 root/workspace 구성에 따라 관찰되지만 항상 즉시 온다고 가정하면 안 된다.
- `workspace/symbol`은 오류 없이 응답해도 trivial query에서 빈 배열을 반환할 수 있으므로 readiness 판단에 사용하지 않는다.
- M1 loader는 선택된 `.sln`, `.slnx`, `.csproj` 파일 경로를 Roslyn LS에 직접 전달하지 않고, 해당 파일의 directory를 process working directory, `rootUri`, `workspaceFolders`로 사용한다.

통과한 테스트:

```text
dotnet format roslyn-mcp-server.sln --verify-no-changes
dotnet build roslyn-mcp-server.sln
dotnet test roslyn-mcp-server.sln
```

- 당시 확인 결과: 17 passed / 0 failed

남은 known issue:

- MCP client와의 end-to-end smoke test는 아직 없다.
- `Ready` 상태는 `workspace/projectInitializationComplete` notification에 의존하지만, notification이 항상 빨리 오지 않을 수 있다.
- solution/project 파일을 Roslyn LS에 직접 지정하는 전용 option/command는 아직 확인되지 않았다.
- 현재 Roslyn LS integration test는 작은 sample 기반 `document_symbols`/`hover`/`go_to_definition`/`find_references` smoke를 포함한다.

M2에서 주의할 점:

- M2 read-only tool을 추가하기 전에 `DocumentStateManager`와 file URI/line-column 변환 테스트를 먼저 보강한다.
- Roslyn navigation/diagnostics tool은 `StartingLanguageServer`에서 queue에 쌓지 말고 `workspace_loading`을 반환해야 한다.
- 현재 구현은 load 성공 후 `WorkspaceWarming`으로 전환한다. `WorkspaceWarming`에서는 가능한 read tool을 best-effort로 실행하되 `workspaceState`, `completeness` metadata를 포함한다.
- 모든 사용자 입력 path는 `PathGuard`를 통과해야 한다.
- stdout에는 로그를 쓰지 않는다.
- 대량 결과는 result limit/truncation metadata를 포함해야 한다.
- 테스트 seam이 필요하면 production class를 `virtual`/상속 가능하게 열지 말고 작은 interface를 둔다. 자세한 원칙은 `docs/coding-principles.md`를 따른다.

M2에서 건드리면 안 되는 범위:

- NuGet/.NET global tool 게시
- `roslyn-language-server` 번들링
- release 자동화
- write/refactoring tool
- rename/code action/formatting/apply 계열 tool
- workspace 전체 diagnostics 무제한 반환
- 대규모 repository 무제한 재귀 탐색

## 현재 구현 상태

최신 구현 상태는 이 문서 상단의 `최신 상태 요약`과 아래 `M2d 완료 메모`, `M2 large repo readiness 메모`를 기준으로 본다.

## 확정된 결정

- 프로젝트 이름: `roslyn-mcp-server`
- 실행 파일 이름: `roslyn-mcp-server`
- 구현 언어: C#/.NET
- 첫 구현의 target framework: `net10.0`
- MCP 구현: 공식 C# MCP SDK, `modelcontextprotocol/csharp-sdk`
- README 언어: 영어
- MCP 서버 transport: stdio
- Roslyn 연동 방식: `roslyn-language-server`를 자식 프로세스로 실행하고 LSP stdio로 통신
- `roslyn-language-server`는 이 프로젝트에 번들하지 않음
- 사용자는 Roslyn LS를 명시적으로 설치함:

```text
dotnet tool install --global roslyn-language-server --prerelease
```

- 이 프로젝트는 당분간 NuGet/.NET global tool로 게시하지 않음
- MCP 서버 자체의 사용자 배포 채널은 아직 결정하지 않음
- 구현 작업 repository: `https://github.com/kirnot92/roslyn-mcp-server`

## M0/M1 첫 구현 범위 이력

첫 구현 세션은 M0/M1에서 멈췄고, 이후 M2 read-only tool 구현이 이어졌다. 이 섹션은 M0/M1 당시 범위 기록이다.

포함:

- solution과 project 생성
- `net10.0` 설정
- MCP C# SDK 추가
- 기본 stdio MCP 서버 추가
- `README.md`는 짧은 영어 소개로 유지
- CLI 옵션 추가
- `--root` 또는 현재 작업 디렉터리에서 root 결정
- workspace scanner 구현
- `list_workspaces` 구현
- `roslyn-language-server` 탐색
- Roslyn LS가 없을 때 명확한 설치 오류 반환
- workspace load 동작을 확정하기 전에 실제 Roslyn LS spike 수행
- 자식 프로세스 start/stop 구현
- LSP initialize/shutdown framing 구현
- `IRoslynWorkspaceLoader` 구현
- `load_solution` 구현
- `load_project` 구현
- `get_workspace_status` 구현
- scanner, path guard, LSP framing, 기본 상태 전이에 대한 단위 테스트 추가

M0/M1 당시 제외:

- `document_symbols`
- `hover`
- `go_to_definition`
- `find_references`
- `find_symbols`
- `diagnostics`
- rename/code action/formatting
- publish/release 자동화
- NuGet global tool 게시

## 필수 Spike

`load_solution`과 `load_project`를 본격 구현하기 전에 설치된 `roslyn-language-server`를 대상으로 작은 spike를 수행한다.

확인할 항목:

- `roslyn-language-server --stdio --autoLoadProjects`가 예상대로 실행되는지
- `initialize.rootUri`만으로 충분한지
- `initialize.workspaceFolders`가 필요한지
- working directory가 선택된 solution/project 파일의 디렉터리여야 하는지
- `.sln`, `.slnx`, `.csproj` 경로를 직접 전달할 수 있는지
- Roslyn 전용 command 또는 initialization option이 필요한지
- `workspace/projectInitializationComplete`가 실제로 오는지
- initialize 이후 `workspace/symbol` 또는 `textDocument/documentSymbol` 같은 간단한 요청이 동작하는지

결과는 최종 loader 구현 전에 `docs/architecture.md`에 짧게 기록한다. 최종 load 동작은 `IRoslynWorkspaceLoader` 뒤에 숨겨 Roslyn LS 동작이 바뀌어도 교체 가능하게 둔다.

## Agent CLI 동작 계약

이 서버는 Agent CLI가 사용하는 도구다. 긴 blocking 대기보다 예측 가능하고 다음 행동이 분명한 응답을 우선한다.

핵심 동작:

- 기본 root는 서버 프로세스의 현재 작업 디렉터리
- `--root`는 escape hatch로만 사용
- 서버 시작 시 solution을 강제로 load하지 않음
- `list_workspaces`는 `.sln`, `.slnx`, `.csproj`를 찾음
- 적절한 solution이 정확히 하나면 첫 Roslyn tool 호출에서 자동 load 가능
- solution이 여러 개면 `load_solution`을 요구
- solution이 없고 project가 정확히 하나면 `load_project` 자동 사용 가능
- project가 많으면 project를 자동 선택하지 않음

Loading 동작:

- `StartingLanguageServer` 중에는 Roslyn tool이 `workspace_loading`을 반환
- loading 중 navigation 요청을 내부 queue에 쌓지 않음
- `retryAfterMs`는 완료 예상 시간이 아니라 polling 힌트
- LSP initialize 이후에는 `WorkspaceWarming` 상태여도 읽기 tool을 best-effort로 허용
- warming 중 결과에는 `workspaceState`와 `completeness`를 포함

## Git 작업 방식

구현 agent는 다음 repository에 작업을 commit/push 하면서 진행한다.

```text
https://github.com/kirnot92/roslyn-mcp-server
```

작업 규칙:

- 작업 시작 시 현재 directory가 git repository인지 확인한다.
- remote가 없으면 `origin`을 위 URL로 설정한다.
- 기본 branch는 특별한 이유가 없으면 `main`을 사용한다.
- 의미 있는 단위로 commit한다.
- 관련 테스트 또는 빌드 확인 후 push한다.
- commit message는 변경 내용을 구체적으로 적는다.
- force push, history rewrite, 불필요한 rebase는 하지 않는다.
- secrets, local logs, build artifacts, temporary spike output은 commit하지 않는다.
- 인증 문제나 push 권한 문제가 있으면 작업 내용을 보존하고 사용자에게 보고한다.

## 대규모 Repo 기본값

초기값은 나중에 조정할 수 있지만, 구현은 처음부터 제한이 있는 구조로 시작한다.

```text
ScanMaxDepth = 6
ScanTimeout = 3s
MaxSolutionCandidates = 100
MaxProjectCandidates = 1000
MaxOpenDocuments = 200
MaxDocumentBytes = 2MB
MaxInFlightLspRequests = 16
MaxExpensiveLspRequests = 2
DefaultSymbolMaxResults = 300
DefaultReferencesMaxResults = 200
DefaultDiagnosticsMaxResults = 200
```

M0/M1 당시에는 모든 result-limit 기능을 완성할 필요는 없었다. 다만 나중에 이 제한들을 추가하기 어렵게 만드는 API는 만들지 않는다는 원칙을 두었다.

## 로깅 규칙

- stdout에는 로그를 쓰지 않는다. stdout은 MCP protocol 채널이다.
- 콘솔 로그는 stderr로 보낸다.
- `--log-file`을 지원한다.
- Roslyn LS stderr는 별도 logger/category로 수집한다.
- LSP payload body 로깅은 기본 비활성화한다.
- trace 로그에는 method, id, duration, payload size 정도만 남길 수 있다.

## 경로 규칙

- 사용자 입력 경로는 모두 `Path.GetFullPath`로 정규화한다.
- 상대 경로는 root 아래에서 해석한다.
- 절대 경로도 root 내부인지 확인한다.
- root 밖 escape는 거부한다.
- Windows path 비교는 case-insensitive로 처리한다.
- tool 결과는 root-relative path를 우선한다.
- tool 입력/출력의 line/column은 1-based로 통일한다.
- LSP 내부 line/column은 0-based다.

## 오류 스타일

오류는 사용자가 바로 다음 행동을 알 수 있게 작성한다.

예시:

```text
roslyn-language-server was not found.
Install it with:
dotnet tool install --global roslyn-language-server --prerelease
```

```text
Multiple solutions were found. Call load_solution with one of:
- App.sln
- Samples/Samples.sln
```

tool 응답에는 명시적 error code를 선호한다.

- `roslyn_language_server_not_found`
- `workspace_not_loaded`
- `workspace_loading`
- `workspace_failed`
- `path_outside_root`
- `file_not_found`
- `request_timeout`
- `result_truncated`

## 아직 하지 않을 것

- NuGet global tool로 게시하지 않는다.
- GitHub Actions release 자동화는 아직 추가하지 않는다.
- `roslyn-language-server`를 번들하지 않는다.
- M1에서 write/refactoring tool을 구현하지 않는다.
- `load_solution`이 모든 diagnostics 완료까지 기다리게 만들지 않는다.
- 대규모 repo를 제한 없이 재귀 탐색하지 않는다.
- workspace 전체 diagnostics를 제한 없이 반환하지 않는다.
- stdout에 로그를 쓰지 않는다.

## 나중에 쓸 실제 Repo

나중에 opt-in 실제 repo 테스트에 사용할 후보:

- `dotnet/roslyn`
- `dotnet/sdk`
- `dotnet/aspnetcore`
- `dotnet/runtime`
- `Azure/azure-sdk-for-net`

자세한 내용은 `docs/large-repo-test-plan.md`를 참고한다.

## M2d 완료 메모

2026-05-17 기준 M2d(`diagnostics`와 `DiagnosticStore`)를 완료했다.

- `textDocument/publishDiagnostics` notification을 받아 파일별 diagnostics를 저장한다.
- 같은 파일의 새 notification은 기존 diagnostics를 교체하고, empty notification은 기존 diagnostics를 clear한다.
- diagnostics cache는 파일 수와 파일별 상세 diagnostics 수를 제한하며, 상한 초과 시 오래된 entry를 eviction한다.
- `diagnostics(file?, severity?, maxResults?, scope?)` tool을 추가했다.
- file-specific 호출은 `DocumentStateManager.EnsureOpenAsync(file)`를 먼저 호출하고 현재 알려진 diagnostics만 반환한다.
- workspace 조회는 명시적 `scope: "workspace"`에서만 허용하며 기본 limit은 `DefaultDiagnosticsMaxResults = 200`이다.
- 결과 metadata에는 `workspaceState`, `completeness`, `totalKnown`, `returned`, `truncated`, `lastUpdatedAt`를 포함한다.
- `get_workspace_status`는 열린 문서 수, known diagnostics file count, 마지막 diagnostic update 시간을 반환한다.
- Roslyn LS integration smoke는 의도적 compile error 파일에 대해 diagnostics tool 호출 경로를 확인한다.

## M4 diagnostics notification offload 완료 메모

2026-05-17 기준 diagnostics notification offload를 완료했다.

- `textDocument/publishDiagnostics` notification handler는 `DiagnosticStore`를 직접 update하지 않고 bounded background queue에 enqueue한 뒤 즉시 반환한다.
- Background worker가 queue에서 notification을 꺼내 `DiagnosticStore.TryUpdateFromPublishDiagnostics`를 호출한다.
- Queue capacity 기본값은 1024이고 overflow 정책은 `drop_newest_when_full`이다. Queue가 full이면 새 notification을 drop하고 dropped count를 증가시킨다.
- `get_workspace_status`는 diagnostics queue capacity, pending, processed, dropped, stale count와 overflow policy를 additive field로 반환한다.
- Workspace reload 시 diagnostics generation을 증가시키고 queue를 clear하며 store를 clear한다. 이미 처리 중이던 이전 generation notification은 reload lock 순서상 reload 전 반영 후 clear되거나, generation mismatch로 discard된다.
- `diagnostics` tool 결과와 메시지는 마지막으로 처리 완료된 `textDocument/publishDiagnostics` 기준이다.

## M2 large repo readiness 메모

2026-05-17 기준 M2 read-only tools 이후 large repo smoke 전에 Phase 1 blocker와 scanner hardening 일부를 반영했다.

- Roslyn LS 명시 workspace 파일 선택 spike
  - 설치된 `roslyn-language-server --version`: `5.8.0-1.26262.10+036e7a58b9d4348a62b6854544274551ae17ae8c`
  - `roslyn-language-server --help`에서 확인되는 workspace 관련 옵션은 `--autoLoadProjects`뿐이다.
  - `.sln`, `.slnx`, `.csproj` 파일 경로를 안정적으로 직접 지정하는 CLI option은 확인되지 않았다.
  - 추가 확인 결과 Roslyn LS client들은 initialize/initialized 이후 `solution/open` 또는 `project/open` notification으로 선택 파일을 전달한다.
  - 작은 `.csproj` fixture에서 workspace folder와 `--autoLoadProjects`만으로는 `workspace/symbol("Calculator")`가 0건이었고, `project/open` 후에는 `workspace/projectInitializationComplete`와 함께 1건이 반환됐다.
  - 따라서 현재 구현은 Roslyn LS를 `--stdio`로 실행하고, `WorkspaceDirectory`를 process working directory, `rootUri`, `workspaceFolders`로 전달한 뒤 `WorkspaceTarget.FullPath`를 `solution/open` 또는 `project/open` notification으로 명시 전달한다.
- LSP read loop fault handling
  - oversized/malformed response나 stream close로 read loop가 중단되면 `LspClient`가 fault 상태를 저장한다.
  - fault 이후 pending request는 실패하고, 새 request/notification은 timeout까지 기다리지 않고 즉시 실패한다.
  - `WorkspaceSession`은 `ILspClient.Faulted`를 받아 workspace 상태를 `Failed`로 전환하고 `FailureCode`/`FailureMessage`에 원인을 남긴다.
- Roslyn LS project load error handling
  - `window/logMessage`의 `LanguageServerProjectLoader` load error를 감지해 `WorkspaceStatus.Warnings`에 `workspace_project_load_failed`로 기록한다.
  - load error가 기록된 상태에서 `workspace/projectInitializationComplete`가 도착하면 `Ready`가 아니라 `LoadedWithErrors`로 전환한다.
  - read tool은 `LoadedWithErrors`에서도 best-effort로 실행하되 `completeness: partial`과 project load error reason을 포함한다.
- Scanner hardening
  - git scanner는 `git ls-files -co --exclude-standard -z -- '*.sln' '*.slnx' '*.csproj'` 형태의 pathspec을 사용해 workspace 후보 파일만 요청한다.
  - filesystem fallback scanner는 solution/project candidate limit이 모두 찬 경우 조기 중단하고 `candidate_limit` truncation reason을 반환한다.
- CLI tuning
  - `--scan-max-depth`, `--scan-timeout`, `--max-solution-candidates`, `--max-project-candidates`, `--max-in-flight-lsp-requests`를 parser와 usage에 열었다.

아직 남긴 항목:

- diagnostics notification offload는 M4에서 완료했다. 다음 large repo 작업은 opt-in real repo 검증과 default tuning을 기준으로 본다.
