# 구현 참고 메모

## 목적

이 문서는 새 구현 세션에서 바로 확인할 현재 상태 메모다. 완료된 milestone 계획, 과거 spike 기록, 오래된 smoke 결과는 `docs/archive/`로 옮겼다. 기본 컨텍스트에는 이 문서와 `AGENTS.md`, `docs/architecture.md`, 필요한 테스트 문서만 올린다.

## 현재 상태

기준일: 2026-05-18.

현재 `main`에는 다음 범위가 반영되어 있다.

- M0/M1: C# `net10.0` app, MCP stdio server, CLI option parsing, root 결정, workspace scanner, Roslyn LS locator/process, LSP initialize/shutdown, `list_workspaces`, `load_solution`, `load_project`, `get_workspace_status`, 기본 단위 테스트
- M2: `document_symbols`, `hover`, `go_to_definition`, `find_references`, `find_symbols`, `diagnostics`, document sync, URI/path mapping, diagnostics store, warming metadata, result limit, expensive request limit, LSP read-loop fault handling
- M2 large repo readiness 일부: scan depth/timeout/candidate cap, git pathspec scanner, document LRU와 큰 파일 제한, in-flight/expensive LSP request 제한
- M3: README/usage 정리, Claude Code/Gemini CLI/Codex 설정 예시, smoke test helper, Roslyn LS 설치 오류 메시지 정리, 실제 repo smoke 기록
- M4 startup initial solution load: `--load-solution <path>`와 background startup load, exact path contract, startup 상태 전이 테스트
- M4 diagnostics notification offload: `textDocument/publishDiagnostics` bounded background queue, `drop_newest_when_full`, queue 통계, generation 기반 stale notification 차단
- M5 read productivity 일부: `find_implementations`, `peek_definition`, `peek_references`, `find_symbols` kind/match/path-prefix filtering, `get_call_hierarchy`, `get_type_hierarchy`, multi-result navigation `includePathPrefixes`

최근 문서 정리로 오래된 계획 문서는 `docs/archive/plans/`, milestone 문서는 `docs/archive/milestones/`, 과거 smoke 결과는 `docs/archive/smoke-tests/`, retired guide는 `docs/archive/guides/`에 보관한다.

## 현재 Tool Surface

Workspace:

- `list_workspaces`: root 아래 `.sln`, `.slnx`, `.csproj` 후보 탐색
- `load_solution`: root 내부 `.sln` 또는 `.slnx` 로드
- `load_project`: root 내부 `.csproj` 로드
- `get_workspace_status`: load state, current target, warnings, open documents, diagnostics queue/cache 상태 조회

Read-only Roslyn:

- `document_symbols`: 파일 단위 symbol tree, optional kind filtering
- `hover`: source position hover text
- `go_to_definition`: definition location
- `peek_definition`: definition location과 bounded source snippet
- `find_references`: reference location
- `peek_references`: reference location과 bounded source snippet
- `find_implementations`: interface/abstract/base member 구현 위치, optional path-prefix filtering
- `get_call_hierarchy`: callable position의 direct depth-1 incoming/outgoing edge, optional kind/path-prefix filtering
- `get_type_hierarchy`: type position의 supertype/subtype edge를 bounded BFS로 조회, optional path-prefix filtering
- `find_symbols`: workspace symbol search, kind/match/path-prefix filtering
- `diagnostics`: 현재 처리된 publish diagnostics cache 조회

Resources:

- `roslyn://server/guide`
- `roslyn://server/capabilities`

## 핵심 계약

- 이 서버는 read-only context provider다. write/refactoring tool은 추가하지 않는다.
- `roslyn-language-server`는 별도 설치물이다. PATH에서 찾거나 `--roslyn-language-server <path>`로 받는다.
- 기본 root는 current working directory다. MCP client가 cwd를 잡기 어렵다면 `--root <path>`를 쓴다.
- 기본값에서는 startup 시 solution을 강제로 load하지 않는다.
- `--load-solution <path>`는 root-relative exact path 또는 root 내부 absolute path만 허용한다. 하위 폴더에서 파일명 검색을 하지 않는다.
- solution/project 후보가 모호하면 agent가 `load_solution` 또는 `load_project`로 직접 고른다.
- unambiguous 후보가 정확히 하나면 첫 Roslyn read tool 호출에서 auto-load할 수 있다.
- `StartingLanguageServer` 상태에서는 read tool이 오래 기다리지 않고 `workspace_loading` error와 retry hint를 반환한다.
- LSP initialize 이후 `WorkspaceWarming` 상태에서는 가능한 tool이 best-effort로 실행되고 결과 metadata에 completeness를 표시한다.
- root 밖 URI/path는 결과에서 제외하거나 user-facing error로 막는다.
- stdout은 MCP protocol 전용이다. 로그는 stderr 또는 `--log-file`을 사용한다.

## 대규모 Repo 기본값

대규모 repo에서는 "완벽한 분석 완료"보다 "bounded response와 명확한 metadata"가 더 중요하다.

현재 중요한 기본값:

- `--scan-max-depth`: 6
- `--scan-timeout`: 10초
- `--max-solution-candidates`: 100
- `--max-project-candidates`: 1000
- `--max-open-documents`: 200
- `--max-document-bytes`: 2 MiB
- `--max-in-flight-lsp-requests`: 16
- `--max-expensive-lsp-requests`: 4
- `--startup-timeout`: 60초

대량 결과 tool은 `totalKnown`, `returned`, `truncated`, `workspaceState`, `completeness`, `reason`, `retryAfterMs`를 통해 결과의 한계를 드러낸다. MCP 쪽 필터가 있는 tool은 `totalUnfilteredKnown`도 함께 제공한다. `find_symbols`의 `kindFilter`, `matchMode`, `includePathPrefixes`와 hierarchy/location 계열 tool의 `includePathPrefixes`는 Roslyn LS 응답 뒤 MCP 쪽에서 적용하는 noise reduction 기능이며 Roslyn LS 검색 비용 절감을 보장하지 않는다.

## Diagnostics 계약

- `diagnostics`는 현재까지 처리된 `textDocument/publishDiagnostics` notification cache를 조회한다.
- workspace-wide full diagnostic computation을 직접 시작하지 않는다.
- notification handler는 bounded queue enqueue 후 즉시 반환한다.
- queue overflow 정책은 `drop_newest_when_full`이다.
- `get_workspace_status`는 queue capacity, pending, processed, dropped, stale count를 노출한다.
- workspace reload 시 generation을 증가시켜 이전 notification을 stale로 집계한다.

## 다음 우선순위

높은 우선순위:

- opt-in large repo 검증과 default tuning
- 대형 solution startup 성능 측정
- workspace warming, load warning, diagnostics queue 관측성 강화
- Roslyn LS crash/restart 처리 정책

후속 후보:

- 실제 MCP client smoke 반복
- `solution_overview` 재평가
- path narrowing의 추가 확장 후보 평가, 예: diagnostics workspace query나 future excludePathPrefixes

제외:

- `get_completions`: IDE 자동완성 성격이 강하고 write-adjacent 도구에 가까우므로 현재 방향에서 제외한다.
- rename/code action/formatting/apply edit: agent의 일반 파일 편집 흐름이나 별도 도구가 맡는다.

## 검증 기준

기본 검증:

```powershell
dotnet format roslyn-mcp-server.sln --verify-no-changes
dotnet build roslyn-mcp-server.sln -p:UseAppHost=false -p:OutDir=.local\build-out\
dotnet test tests\RoslynMcpServer.Tests\RoslynMcpServer.Tests.csproj -p:UseAppHost=false -p:OutDir=.local\test-out\
```

Windows에서 실행 중인 `roslyn-mcp-server.exe`가 Debug output을 잡고 있으면 기본 `dotnet build`가 apphost overwrite lock으로 실패할 수 있다. 이 경우 위처럼 `UseAppHost=false`와 `.local\...` output directory를 사용한다.

최근 확인된 테스트 상태:

- 210 passed
- 0 failed
- 1 skipped
- 211 total

## 최근 Large Repo Tuning 메모

2026-05-18 MAUI smoke/tuning:

- 대상: `.local/real-repos/maui`, branch `main`, commit `7e47a710`, `Microsoft.Maui.sln`
- 기본 scan 설정에서 `list_workspaces`는 0.157초, solution 6개, project 134개, truncated `false`
- 30초 warmup 뒤에도 state는 `WorkspaceWarming`이었지만 read tool은 partial metadata와 함께 응답
- 기본 `--max-expensive-lsp-requests 2`에서는 병렬 `find_symbols` 4개 중 2개가 `too_many_expensive_lsp_requests`로 거절됨
- `--max-expensive-lsp-requests 4` 후보에서는 동일 병렬 요청 4개가 모두 성공했고 각 요청은 약 2.1초에 완료됨
- 튜닝 결정: scanner/default candidate 값은 유지하고, interactive agent 병렬 호출을 덜 막도록 `DefaultMaxExpensiveLspRequests`를 4로 상향

2026-05-18 scan timeout 재검토:

- `scan_timeout`은 git scan이 전체 budget을 소진하거나 filesystem fallback이 budget 안에 끝나지 못할 때 발생한다.
- 정상적인 warm git repo는 MAUI 기준 `git rev-parse` 약 25ms, `git ls-files` 약 98ms로 매우 빠르다.
- 간헐 timeout은 cold git index, 느린/네트워크 디스크, antivirus, 대량 untracked file, git 사용 불가로 인한 filesystem fallback, root를 너무 넓게 잡은 경우에 발생할 가능성이 높다.
- `list_workspaces`는 명시적 discovery 호출이고 보통 한 세션에서 자주 반복하지 않으므로, false timeout을 줄이기 위해 `DefaultScanTimeout`을 10초로 상향한다.
- timeout 원인을 볼 때는 `list_workspaces` 또는 `get_workspace_status.workspaces`의 `truncated`, `truncationReason`, `elapsed`를 확인하고, 필요하면 `--log-level debug --log-file <path>`로 git scan/fallback 로그를 남긴다.

## 문서 구조

활성 문서:

- `README.md`: GitHub 첫 화면용 영어 소개와 quick start
- `AGENTS.md`: agent 작업 지침
- `docs/usage.md`: 사용자 설치/설정/권장 tool flow
- `docs/architecture.md`: 현재 architecture와 component 계약
- `docs/implementation-notes.md`: 현재 구현 상태와 다음 작업 메모
- `docs/large-repo-test-plan.md`: 대규모 repo 검증 전략
- `docs/smoke-test-guide.md`: 실제 repo/client smoke test 실행 방식
- `docs/coding-principles.md`: 코드 작성 원칙과 테스트 seam
- `docs/release-process.md`: 수동 릴리즈 버전/artifact/검증 절차

Archive:

- `docs/archive/plans/`: 완료되었거나 보류된 계획 문서
- `docs/archive/milestones/`: 과거 milestone 계획
- `docs/archive/smoke-tests/`: 과거 smoke 결과 원문
- `docs/archive/guides/`: 현재 기본 흐름에서 제외된 guide

새 구현 세션은 archive를 기본으로 읽지 않는다. 과거 결정 배경이 필요할 때만 archive를 열어본다.
