# Roslyn MCP Server Architecture

## 방향

`roslyn-mcp-server`는 Roslyn API를 직접 감싸는 서버가 아니라, `roslyn-language-server`를 자식 프로세스로 실행하고 MCP tool 호출을 LSP 요청으로 변환하는 얇은 브리지다.

핵심 원칙:

- MCP 서버는 C#/.NET `net10.0` app이다.
- MCP 구현은 공식 C# MCP SDK를 사용한다.
- `roslyn-language-server`는 사용자가 별도로 설치한다.
- 서버는 MCP client와 stdio로 통신하고, Roslyn LS와도 stdio LSP로 통신한다.
- 기본 root는 current working directory이며 `--root <path>`로 바꿀 수 있다.
- 기본 startup에서는 solution을 강제로 load하지 않는다.
- `--load-solution <path>`가 있으면 MCP server startup 뒤 background task가 지정 solution을 로드한다.
- 제품 방향은 best-effort read-only Roslyn context provider다.

## 프로세스 모델

```text
Agent CLI
  <-> MCP JSON-RPC over stdio
roslyn-mcp-server
  <-> LSP JSON-RPC over stdio
roslyn-language-server
  <-> selected .sln/.slnx/.csproj and source files
```

Roslyn 기능이 필요한 시점에 `roslyn-language-server --stdio`를 시작한다. Solution load는 LSP initialize 뒤 `.sln`/`.slnx`에는 `solution/open`, `.csproj`에는 `project/open` notification을 보내는 방식이다.

여러 solution이 있는 repository에서 모호한 자동 로드를 피하기 위해, 기본 동작은 agent가 `load_solution` 또는 `load_project`로 workspace를 고르는 것이다. 단일 후보만 있으면 첫 read tool 호출에서 auto-load할 수 있다.

## Project Layout

현재 주요 구성:

```text
src/RoslynMcpServer/
  Program.cs
  Cli/
    CliOptions.cs
  Infrastructure/
    FileLoggerProvider.cs
    IClock.cs
    ServerResourceUris.cs
    UserFacingException.cs
  Lsp/
    DiagnosticNotificationProcessor.cs
    DiagnosticStore.cs
    ILspClient.cs
    IRoslynWorkspaceLoader.cs
    JsonOptions.cs
    LspClient.cs
    LspFraming.cs
    LspModels.cs
    RoslynLanguageServerLocator.cs
    RoslynLanguageServerProcess.cs
    RoslynWorkspaceLoader.cs
  Mcp/
    DiagnosticsTools.cs
    NavigationTools.cs
    ServerResources.cs
    ToolModels.cs
    WorkspaceTools.cs
    Navigation/
      NavigationTools.*.cs
  Workspace/
    DocumentPathMapper.cs
    DocumentStateManager.cs
    GitWorkspaceScanner.cs
    IGitWorkspaceScanner.cs
    PathGuard.cs
    StartupSolutionLoader.cs
    WorkspaceModels.cs
    WorkspaceScanner.cs
    WorkspaceSession.cs
tests/RoslynMcpServer.Tests/
scripts/smoke-tests/
```

`Program.cs`는 CLI parsing, DI 구성, MCP stdio transport 시작만 담당한다. 실제 동작은 `WorkspaceSession`, `RoslynWorkspaceLoader`, `LspClient`, MCP tool class로 나뉜다.

## CLI Options

현재 공개 옵션:

```text
--root <path>
--roslyn-language-server <path>
--load-solution <path>
--log-level <trace|debug|info|warn|error>
--log-file <path>
--ls-log-dir <path>
--startup-timeout <seconds>
--scan-max-depth <depth>
--scan-timeout <seconds>
--max-solution-candidates <count>
--max-project-candidates <count>
--max-open-documents <count>
--max-document-bytes <bytes>
--max-in-flight-lsp-requests <count>
--max-expensive-lsp-requests <count>
```

`--load-solution`은 정확한 root-relative path 또는 root 내부 absolute path만 허용한다. 이 옵션은 파일명만 받아 하위 디렉터리를 재귀 검색하지 않는다.

## Workspace State

Workspace load state:

```text
NotLoaded
StartingLanguageServer
LspReady
WorkspaceWarming
LoadedWithErrors
Ready
Failed
```

주요 계약:

- `NotLoaded`: 아직 Roslyn LS를 시작하지 않은 상태다.
- `StartingLanguageServer`: process start 또는 initialize 중이다. read tool은 오래 대기하지 않고 `workspace_loading`을 반환한다.
- `LspReady`: LSP initialize는 끝났지만 workspace open 완료 신호 전이다.
- `WorkspaceWarming`: Roslyn LS가 workspace를 열었고 indexing/loading이 진행 중이다. read tool은 best-effort로 실행한다.
- `LoadedWithErrors`: load 경고나 오류가 있지만 read tool 호출은 가능하다.
- `Ready`: workspace initialization complete 신호를 받았고 알려진 fatal failure가 없다.
- `Failed`: process start, initialize, workspace open, path validation이 실패했다.

`get_workspace_status`는 state, current target, language server running 여부, pending LSP request, workspace scan result, warnings, diagnostics cache/queue 상태를 함께 반환한다.

## Workspace Discovery

`WorkspaceScanner`는 root 아래 `.sln`, `.slnx`, `.csproj` 후보를 찾는다.

탐색 보호 장치:

- `--scan-max-depth`
- `--scan-timeout`
- `--max-solution-candidates`
- `--max-project-candidates`
- git repository에서는 가능한 경우 git pathspec 기반 scan
- `.git`, build output, package cache 같은 불필요한 디렉터리 제외

결과가 제한에 걸리면 `WorkspaceScanResult.Truncated`와 `TruncationReason`으로 표시한다.

`PathGuard`는 모든 입력 경로가 root 내부인지 검증한다. Root 밖 path는 tool 입력으로 받지 않고, LSP 결과에서도 root 밖 URI는 제외한다.

## LSP Client

`RoslynLanguageServerProcess`는 Roslyn LS process start/stop을 담당한다. Locator는 `--roslyn-language-server`가 있으면 그 경로를 우선하고, 없으면 PATH에서 `roslyn-language-server`를 찾는다.

`LspClient` 책임:

- `Content-Length` framing
- request id 관리
- bounded in-flight request 관리
- expensive request 동시성 제한
- read loop와 response/notification dispatch
- initialize/shutdown
- request timeout/cancellation

주요 notification:

- `workspace/projectInitializationComplete`: workspace warming 종료 신호로 사용
- `window/logMessage`: workspace load warning/error로 축적
- `textDocument/publishDiagnostics`: diagnostics queue에 enqueue

stdout은 MCP protocol 채널이므로 application log를 쓰지 않는다. 파일 로그는 `--log-file`, Roslyn LS log directory는 `--ls-log-dir`로 분리한다.

## Document State

Navigation tool은 file path를 LSP document URI로 변환하고, 필요하면 `textDocument/didOpen`으로 문서를 연다.

관련 component:

- `DocumentPathMapper`: root 내부 path와 LSP URI 변환
- `DocumentStateManager`: open document LRU, 큰 파일 제한, didOpen/didClose 관리
- `PositionMapper`: MCP 1-based line/column과 LSP 0-based position 변환

큰 파일은 `--max-document-bytes`보다 크면 열지 않는다. 열려 있는 문서 수는 `--max-open-documents`로 제한한다.

## MCP Tools

Workspace tools:

- `list_workspaces`: workspace 후보 탐색
- `load_solution`: `.sln` 또는 `.slnx` load
- `load_project`: `.csproj` load
- `get_workspace_status`: state와 queue/cache 통계 조회

Navigation tools:

- `document_symbols`
- `hover`
- `go_to_definition`
- `peek_definition`
- `find_references`
- `peek_references`
- `find_implementations`
- `get_call_hierarchy`
- `get_type_hierarchy`
- `find_symbols`

Diagnostics tools:

- `diagnostics`

Resources:

- `roslyn://server/guide`
- `roslyn://server/capabilities`

## Result Metadata

Read tool 결과는 가능한 한 다음 metadata를 포함한다.

```text
workspaceState
completeness
reason
retryAfterMs
truncated
totalKnown
returned
```

`completeness`는 일반적으로 `complete`, `partial`, `unknown` 중 하나다. Warming 중이거나 result cap에 걸리거나 root 밖 위치를 제외한 경우 metadata로 드러낸다.

`document_symbols`, `find_references`, `peek_references`, `find_implementations`, `find_symbols`, `get_call_hierarchy`, `get_type_hierarchy`처럼 MCP 쪽 필터가 있는 tool은 필터 전 mappable 결과 수를 `totalUnfilteredKnown`으로 함께 제공한다.

## Tool별 계약

`document_symbols`:

- 파일 단위 symbol tree를 반환한다.
- `kindFilter`는 `find_symbols`와 같은 MCP symbol kind 이름을 대소문자 무시로 받는다.
- matching descendant가 있으면 ancestor symbol을 context로 유지한다.
- filter는 Roslyn LS 응답 뒤 MCP 쪽에서 적용하므로 Roslyn LS 요청 비용 절감을 보장하지 않는다.
- `totalKnown`, `returned`, `truncated`는 retained context ancestor를 포함한 filtered response tree 기준이다.

`find_symbols`:

- query는 최소 2자 이상이다.
- `kindFilter`는 `class`, `interface`, `method`, `property`, `field`, `enumMember`, `typeParameter` 같은 MCP symbol kind 이름을 대소문자 무시로 받는다.
- `matchMode`는 `default`, `exact`, `prefix`, `contains`를 지원한다.
- `includePathPrefixes`는 root-relative path prefix 목록으로 결과 위치를 좁힌다.
- kind, match, path-prefix filter는 Roslyn LS 응답 뒤 MCP 쪽에서 적용하므로 Roslyn LS 검색 비용 절감을 보장하지 않는다.

`get_call_hierarchy`:

- `textDocument/prepareCallHierarchy` 뒤 `callHierarchy/incomingCalls`, `callHierarchy/outgoingCalls`를 호출한다.
- `direction`은 `incoming`, `outgoing`, `both`다.
- recursive depth는 제공하지 않는다. 직접 depth-1 edge만 반환한다.
- `kindFilter`는 edge counterpart kind에 MCP 쪽에서 적용한다.
- `includePathPrefixes`는 edge counterpart location에 MCP 쪽에서 적용한다. `incoming`은 caller `from`, `outgoing`은 callee/accessed `to`를 기준으로 필터링한다.

`get_type_hierarchy`:

- `textDocument/prepareTypeHierarchy` 뒤 `typeHierarchy/supertypes`, `typeHierarchy/subtypes`를 호출한다.
- `direction`은 `supertypes`, `subtypes`, `both`다.
- `maxDepth`, `maxResults`로 bounded BFS를 제한한다.
- `includePathPrefixes`는 discovered follow-up type location에 MCP 쪽에서 적용한다. 제외된 follow-up type은 더 traversing하지 않는다.

`peek_definition`과 `peek_references`:

- location과 함께 root 내부 source snippet을 붙인다.
- root 밖 또는 읽을 수 없는 파일은 snippet을 생략하거나 snippet error로 표시한다.
- snippet line 수는 option으로 제한한다.

`diagnostics`:

- 현재까지 처리된 publish diagnostics cache만 조회한다.
- workspace-wide full diagnostic computation을 시작하지 않는다.
- severity filter와 max result cap을 적용한다.

## Diagnostics Queue

`textDocument/publishDiagnostics` notification은 LSP read loop에서 직접 무거운 작업을 하지 않고 `DiagnosticNotificationProcessor`의 bounded queue로 넘긴다.

계약:

- overflow policy는 `drop_newest_when_full`이다.
- pending, processed, dropped, stale count와 capacity를 `get_workspace_status`에 노출한다.
- workspace reload 시 generation이 증가한다.
- 이전 generation notification은 stale로 집계하고 새 `DiagnosticStore`에 섞지 않는다.

## Error Handling

사용자가 바로 이해할 수 있는 오류는 `UserFacingException`과 code/message로 반환한다.

대표 code:

- `workspace_loading`
- `workspace_not_loaded`
- `workspace_ambiguous`
- `workspace_not_found`
- `invalid_path`
- `invalid_position`
- `invalid_option`
- `language_server_not_found`
- `language_server_failed`

Tool 구현은 예외 stack trace를 MCP stdout에 흘리지 않는다. Debugging 정보는 log channel에 남긴다.

## 테스트 전략

Fast tests:

- CLI parsing
- path guard
- workspace scanner
- git scanner
- LSP framing
- LSP client request/notification 흐름
- document state/path mapping
- diagnostics store/queue
- MCP tool result mapping and metadata

Integration tests:

- `roslyn-language-server` 설치 환경에서만 실행
- 미설치 시 명확한 skip reason을 남긴다.

Smoke tests:

- `scripts/smoke-tests/`의 실제 repo driver를 사용한다.
- raw output은 `.local/` 아래에 두고 commit하지 않는다.
- 과거 결과 원문은 `docs/archive/smoke-tests/`에 보관한다.

기본 검증:

```powershell
dotnet format roslyn-mcp-server.sln --verify-no-changes
dotnet build roslyn-mcp-server.sln -p:UseAppHost=false -p:OutDir=.local\build-out\
dotnet test tests\RoslynMcpServer.Tests\RoslynMcpServer.Tests.csproj -p:UseAppHost=false -p:OutDir=.local\test-out\
```

## 열린 결정

- opt-in large repo 검증 결과에 따른 기본 timeout/result cap tuning
- 대형 solution startup 성능 관측 지표
- Roslyn LS crash/restart 처리 정책
- `solution_overview`를 read-only context provider 범위 안에서 제공할 가치가 있는지
- path narrowing option을 `find_symbols` 외 tool로 확장할지
