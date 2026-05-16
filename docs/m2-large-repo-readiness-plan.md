# M2 Large Repo Readiness Plan

## 목적

이 문서는 M2 read-only tool 구현 이후, 실제 MCP client와 대규모 repository smoke test 전에 처리해야 할 readiness 항목을 정리한다.

M2의 read tool 자체는 작은/중간 repository에서 제한적 실사용이 가능해지는 것을 목표로 한다. 하지만 대형 mono-repo에서는 workspace 선택, LSP 응답 크기, scanner 비용, diagnostics notification 폭주가 별도의 실패 원인이 될 수 있다. 이 문서는 그 위험을 M3/M4로 넘어가기 전에 정리하기 위한 작업 지시서다.

## 배경

대형 repository 관점의 리뷰에서 다음 문제가 확인됐다.

- 명시적으로 선택한 `.sln`, `.slnx`, `.csproj`가 Roslyn LS에 실제 파일 단위로 전달되지 않는다.
- 큰 LSP 응답 하나가 read loop를 멈추게 하고 이후 요청이 timeout으로 흐를 수 있다.
- git scanner가 `git ls-files` 전체 출력을 메모리에 읽은 뒤 workspace 파일만 필터링한다.
- filesystem fallback scanner가 candidate limit에 걸린 뒤에도 timeout 또는 queue 소진까지 탐색을 계속한다.
- diagnostics notification 처리가 LSP read loop에서 동기적으로 실행되어 notification 폭주 시 응답 처리를 밀 수 있다.
- 대형 repo 튜닝용 CLI 옵션 일부가 `CliOptions` 필드로는 있지만 parser에는 열려 있지 않다.

## 목표

- M2 read-only tool의 public contract를 유지한다.
- 실제 MCP client smoke 전에 blocker급 대형 repo 위험을 줄인다.
- 문서와 구현의 차이를 줄인다.
- 모든 변경은 테스트 가능한 단위로 작게 나눈다.

## 현재 반영 상태

2026-05-17 기준 다음 항목은 구현에 반영되어 있다.

- Roslyn LS 명시 workspace file 선택 spike와 directory ambiguity warning
- LSP read loop fault 상태 노출 및 `WorkspaceSession`의 `Failed` 전환
- git scanner pathspec 적용
- stdio MCP 환경에서 git child process stdin 상속을 끊고 null-delimited output을 streaming 처리하도록 git scanner 보강
- filesystem fallback scanner candidate-limit 조기 중단
- large repo tuning CLI 옵션 공개

아직 남은 항목:

- diagnostics notification offload. bounded background queue와 overflow 정책을 함께 설계해야 하므로 후속 작업으로 남긴다.

## 하지 않을 것

- write/refactoring tool을 추가하지 않는다.
- rename/code action/format/apply 계열을 구현하지 않는다.
- Roslyn LS를 번들하지 않는다.
- NuGet/.NET global tool 배포 작업을 하지 않는다.
- 대형 repo 전체 diagnostics를 완전하게 계산하려고 하지 않는다.
- 모든 Tier 1/Tier 2 대형 repo 검증을 이 단계에서 끝내려고 하지 않는다.

## Phase 1: Blockers

### 1. Explicit Workspace Selection - 반영 완료

문제:

- `load_solution`/`load_project`는 선택 파일을 `WorkspaceTarget.FullPath`에 담지만, 현재 Roslyn LS에는 안정적인 명시 file 선택 option이 없어 loader는 `WorkspaceDirectory`를 Roslyn LS process working directory, `rootUri`, `workspaceFolders`로 사용한다.
- 같은 directory에 여러 `.sln`, `.slnx`, `.csproj`가 있으면 서로 다른 파일을 선택해도 Roslyn LS 입장에서는 같은 workspace로 보일 수 있다.
- 대형 repo에서는 잘못된 workspace를 load하거나 너무 넓게 auto-load할 위험이 있다.

작업:

- 현재 설치 가능한 `roslyn-language-server`에서 solution/project 파일을 명시적으로 선택하는 CLI option, initialization option, command가 있는지 spike한다.
- 가능한 방식이 있으면 `WorkspaceTarget.FullPath`를 실제 Roslyn LS load에 반영한다.
- 표준적이고 안정적인 방식이 없으면 다음 fallback 중 하나를 구현한다.
  - 같은 `WorkspaceDirectory`에 workspace 파일이 여러 개 있으면 명시 파일 선택이 directory 단위로만 반영된다는 warning/metadata를 반환한다.
  - 위험한 ambiguous directory에서는 load를 거부하고 더 좁은 root 또는 명시 설정을 요구한다.
  - 문서에 현재 Roslyn LS 제약과 동작을 명확히 기록한다.

검증:

- 같은 directory에 두 개 이상의 workspace 파일이 있는 fixture를 추가한다.
- 서로 다른 workspace 파일을 load할 때 구현이 같은 directory ambiguity를 감지하는지 검증한다.
- spike 결과를 `docs/implementation-notes.md` 또는 이 문서에 짧게 기록한다.

### 2. LSP Read Loop Fault Handling - 반영 완료

문제:

- LSP response body가 `LspFraming.DefaultMaxContentLength`를 넘거나 malformed이면 read loop가 멈춘다.
- 반영 전에는 pending request가 실패할 수 있지만 workspace 상태가 명확히 `Failed`로 전환되거나 Roslyn LS 재시작 경로가 보장되지는 않았다.
- 이후 요청은 read loop가 없는 상태에서 timeout으로 흐를 수 있다.

작업:

- `LspClient`에 read loop fault 상태를 노출한다.
- read loop가 oversized/malformed response로 중단되면 이후 request가 즉시 user-facing error를 반환하도록 한다.
- 가능하면 `WorkspaceSession`이 LSP fault를 감지해 workspace state를 `Failed`로 전환한다.
- 오류 메시지는 다음 행동을 알려야 한다. 예: workspace reload 또는 status 확인.

검증:

- fake stream으로 oversized response를 보내 read loop fault를 발생시킨다.
- fault 이후 pending request가 실패하는지 확인한다.
- fault 이후 새 request가 timeout까지 기다리지 않고 즉시 실패하는지 확인한다.
- workspace status가 실패 상태 또는 명확한 LSP fault metadata를 포함하는지 확인한다.

## Phase 2: Scanner Hardening

### 3. Git Scanner Filtering Or Streaming - 반영 완료

문제:

- 반영 전 git scanner는 `git ls-files -co --exclude-standard -z` 전체 출력을 메모리에 읽은 뒤 `.sln`, `.slnx`, `.csproj`만 필터링했다.
- 대형 repo에서는 대부분이 workspace 파일이 아니므로 불필요한 I/O와 메모리 사용이 발생한다.

작업:

- 가능하면 `git ls-files`에 pathspec을 추가해 workspace 파일만 출력하게 한다.
  - 예: `git ls-files -co --exclude-standard -z -- '*.sln' '*.slnx' '*.csproj'`
- pathspec 방식이 플랫폼/쉘 quoting 문제 없이 동작하도록 `ProcessStartInfo.ArgumentList`를 사용한다.
- pathspec으로 충분하지 않으면 null-delimited streaming parser로 전환한다.

검증:

- git-backed test fixture에서 `.sln`, `.slnx`, `.csproj` 후보를 여전히 찾는지 확인한다.
- workspace 파일이 아닌 대량 파일이 있어도 scanner가 candidate limit과 timeout을 지키는지 확인한다.

### 4. Filesystem Scanner Early Stop - 반영 완료

문제:

- fallback scanner는 solution/project candidate limit에 도달해도 탐색을 계속한다.
- non-git 대형 directory에서 매번 scan timeout을 소모할 수 있다.

작업:

- solution과 project limit이 모두 찼거나, 더 이상 유용한 후보를 받을 수 없는 상태면 scan을 조기 중단한다.
- truncation reason을 유지한다.
- root/top-level solution 우선 정렬 계약을 깨지 않는다.

검증:

- synthetic non-git fixture에서 candidate limit 도달 시 scan이 조기 종료되는지 확인한다.
- `truncated: true`와 적절한 truncation reason이 유지되는지 확인한다.

## Phase 3: Operational Tuning

### 5. Diagnostics Notification Offload - 후속 작업

문제:

- `textDocument/publishDiagnostics` notification은 LSP read loop에서 synchronous event handler로 처리된다.
- notification 폭주 시 URI/path validation, parsing, locking, eviction이 read loop를 지연시킬 수 있다.

작업:

- read loop가 오래 막히지 않도록 diagnostics notification을 background queue/channel로 넘기는 방안을 검토한다.
- 최소한 notification handler가 예외를 read loop 밖으로 전파하지 않도록 유지한다.
- queue를 도입한다면 bounded로 만들고 overflow 정책을 명확히 한다.

검증:

- publish storm fake test에서 read loop가 다른 response 처리를 계속할 수 있는지 확인한다.
- queue overflow 시 process가 crash하지 않고 metadata나 log로 드러나는지 확인한다.

### 6. Large Repo CLI Tuning Options - 반영 완료

문제:

- 반영 전에는 `CliOptions`에 scan depth, scan timeout, candidate limit, in-flight limit 필드가 있었지만 모든 값이 CLI parser에 열려 있지는 않았다.
- 대형 repo smoke에서 사용자가 기본값을 조정하기 어렵다.

작업:

- 다음 옵션을 CLI parser와 usage에 추가하는 것을 검토한다.
  - `--scan-max-depth`
  - `--scan-timeout`
  - `--max-solution-candidates`
  - `--max-project-candidates`
  - `--max-in-flight-lsp-requests`
- 필요하면 diagnostics cache 관련 옵션은 후속으로 남긴다.

검증:

- CLI parse tests를 추가한다.
- invalid value가 user-facing usage error를 반환하는지 확인한다.

## 완료 기준

- Phase 1 blocker가 해결됐거나, 해결 불가능한 Roslyn LS 제약은 명확한 fallback과 문서로 정리됐다.
- oversized/malformed LSP response 이후 요청이 timeout까지 매달리지 않는다.
- scanner가 대형 repo에서 불필요한 전체 탐색이나 전체 git output materialization을 줄인다.
- `dotnet format roslyn-mcp-server.sln --verify-no-changes` 통과
- `dotnet build roslyn-mcp-server.sln` 통과
- `dotnet test roslyn-mcp-server.sln` 통과

## 후속 단계

이 계획의 Phase 1과 Phase 2가 끝나면 M2 completion smoke를 수행한다.

- 작은/중간 real repo에서 MCP client smoke
- 필요 시 Tier 1 repo 중 하나에서 opt-in `list_workspaces`/`load_solution`/read tool smoke
- 본격적인 Tier 1/Tier 2 반복 검증과 default tuning은 M4 품질 강화 단계에서 수행한다.
