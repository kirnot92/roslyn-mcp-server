# 구현 참고 메모

## 목적

이 문서는 새 구현 세션에서 놓치기 쉬운 자잘하지만 중요한 결정을 한곳에 모아 둔다.

주요 참고 문서:

- `docs/plan.md`
- `docs/architecture.md`
- `docs/large-repo-test-plan.md`

## 현재 구현 상태

2026-05-17 기준 M0/M1 구현은 `main` branch에 push되어 있다.

- 현재 구현 commit: `e9a41b4 Implement M1 Roslyn MCP server skeleton`
- 확인한 명령:

```text
dotnet format roslyn-mcp-server.sln --verify-no-changes
dotnet build roslyn-mcp-server.sln
dotnet test roslyn-mcp-server.sln --no-build
```

- 단위 테스트는 9개 통과했다.
- 구현된 MCP tool은 `list_workspaces`, `load_solution`, `load_project`, `get_workspace_status`뿐이다.
- navigation/diagnostics/write/refactoring tool은 아직 구현하지 않았다.
- Roslyn LS spike 결과는 `docs/architecture.md`의 `2026-05-16 Roslyn LS spike 결과` 섹션에 기록되어 있다.
- LSP client는 Roslyn LS가 보내는 `workspace/configuration` server-to-client request에 빈 배열 결과로 응답한다.
- M1 loader는 선택된 `.sln`, `.slnx`, `.csproj` 파일 경로를 Roslyn LS에 직접 전달하지 않는다. 대신 해당 파일의 directory를 Roslyn LS process working directory, `initialize.rootUri`, `initialize.workspaceFolders`로 사용한다.
- `workspace/symbol`은 initialize 직후 trivial sample에서 빈 배열을 반환할 수 있으므로 readiness 판단에 사용하지 않는다.
- 다음 구현 후보는 M2 read-only tool 전에 `DocumentStateManager`, 실제 Roslyn LS integration test, MCP tool smoke test를 보강하는 것이다.

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

## 첫 구현 범위

첫 구현 세션은 M0/M1에서 멈춘다.

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

첫 구현에서 제외:

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
MaxProjectCandidates = 500
MaxOpenDocuments = 200
MaxDocumentBytes = 2MB
MaxInFlightLspRequests = 16
MaxExpensiveLspRequests = 2
DefaultSymbolMaxResults = 100
DefaultReferencesMaxResults = 200
DefaultDiagnosticsMaxResults = 200
```

첫 구현에서 모든 result-limit 기능을 완성할 필요는 없다. 다만 나중에 이 제한들을 추가하기 어렵게 만드는 API는 만들지 않는다.

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
