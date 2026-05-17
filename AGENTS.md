# Agent 작업 지침

이 문서는 `roslyn-mcp-server`를 구현하는 agent가 가장 먼저 읽어야 하는 작업 지침이다.

`roslyn-mcp-server`는 Agent CLI류 도구가 Roslyn 언어 기능을 사용할 수 있도록, `roslyn-language-server`를 자식 프로세스로 실행하고 MCP tool 호출을 LSP 요청으로 변환하는 MCP 서버다.

현재 이 저장소는 M2 read-only tool 구현, M2 large repo readiness 일부, M3 사용자/클라이언트 사용성 정리까지 반영된 상태다.

## 목표

- MCP 서버는 C#/.NET으로 구현한다.
- 공식 C# MCP SDK를 사용한다.
- `roslyn-language-server`와 stdio LSP로 통신한다.
- 기본 repository root는 서버가 실행된 현재 작업 디렉터리로 둔다.
- agent가 `load_solution`, `load_project` 같은 MCP tool로 `.sln`, `.slnx`, `.csproj`를 선택하게 한다.
- 대규모 repository를 고려해 탐색 제한, timeout, 결과 제한, warming 중 best-effort 동작을 지원한다.

## 당장 하지 않을 것

- 이 프로젝트를 NuGet/.NET global tool로 게시하지 않는다.
- `roslyn-language-server`를 번들하지 않는다.
- release 자동화는 아직 추가하지 않는다.
- 현재 구현에서 write/refactoring tool을 구현하지 않는다.

## 외부 요구사항

사용자는 `roslyn-language-server`를 별도로 설치해야 한다.

```text
dotnet tool install --global roslyn-language-server --prerelease
```

MCP 서버는 기본적으로 PATH에서 `roslyn-language-server`를 찾는다. 필요한 경우 `--roslyn-language-server <path>` 옵션으로 명시 경로를 받을 수 있다.

## 현재 구현 범위

현재 `main` 기준으로 M0/M1, M2 read-only tool, M3 사용자/클라이언트 사용성 범위가 구현되어 있다.

M0/M1 포함:

- C# solution과 `net10.0` app project 생성
- MCP C# SDK 추가
- stdio MCP 서버 시작
- CLI 옵션 parsing
- `--root` 또는 현재 작업 디렉터리로 workspace root 결정
- `.sln`, `.slnx`, `.csproj` 탐색
- `list_workspaces` 구현
- `roslyn-language-server` 탐색
- `roslyn-language-server` 실제 동작 spike 수행
- LSP process start/stop 구현
- LSP initialize/shutdown 구현
- `load_solution`, `load_project`, `get_workspace_status` 구현
- scanner, path guard, LSP framing, 기본 상태 전이에 대한 단위 테스트 추가

M2 포함:

- `go_to_definition`
- `hover`
- `find_references`
- `find_symbols`
- `diagnostics`
- `document_symbols`
- `DocumentStateManager`, `DocumentPathMapper`, `DiagnosticStore`
- result limit, expensive LSP request limit, warming 중 metadata
- LSP read loop fault handling, git scanner pathspec, filesystem scanner candidate-limit 조기 중단

M3 포함:

- `README.md`의 짧은 사용자 시작 흐름 정리
- `docs/usage.md` 사용자 설치/설정 문서
- `roslyn-language-server` 탐색/설치 오류 메시지 정리
- PowerShell, Semantic Kernel, ASP.NET Core stdio smoke 기록
- `solution_overview` M3 미구현 결정과 후속 후보 평가

다음 범위는 M4 이후 후속 작업으로 남긴다.

- `solution_overview`
- write/refactoring tool
- rename/code action/formatting/apply 계열 tool
- diagnostics notification offload
- 추가 실제 MCP client smoke 반복과 opt-in large repo 검증/default tuning

## 중요한 설계 메모

- 서버 시작 시 solution을 강제로 load하지 않는다.
- solution이 여러 개 발견되면 agent가 `load_solution`을 호출해야 한다.
- 적절한 solution이 정확히 하나면 첫 Roslyn tool 호출에서 자동 load할 수 있다.
- `StartingLanguageServer` 상태에서는 Roslyn tool이 오래 대기하지 말고 `workspace_loading`을 반환한다.
- LSP initialize 이후에는 workspace가 warming 중이어도 가능한 읽기 tool은 best-effort로 실행한다.
- warming 중 반환된 결과에는 `workspaceState`와 `completeness` metadata를 포함한다.
- stdout에는 로그를 쓰지 않는다. stdout은 MCP protocol 채널이다.

## Git 작업 방식

구현 작업은 아래 GitHub repository를 기준으로 진행한다.

```text
https://github.com/kirnot92/roslyn-mcp-server
```

구현 agent는 작업을 의미 있는 단위로 commit하고 push한다.

- 작업 시작 시 현재 directory가 git repository인지 확인한다.
- remote가 없으면 `origin`을 위 URL로 설정한다.
- 기본 branch는 특별한 이유가 없으면 `main`을 사용한다.
- 기능 단위, 테스트 단위, 문서 정리 단위처럼 리뷰 가능한 크기로 commit한다.
- 관련 테스트 또는 빌드 확인 후 push한다.
- commit message는 변경 내용을 구체적으로 적는다.
- force push, history rewrite, 불필요한 rebase는 하지 않는다.
- secrets, local logs, build artifacts, temporary spike output은 commit하지 않는다.
- 인증 문제나 push 권한 문제가 있으면 작업 내용을 보존하고 사용자에게 보고한다.

## 읽어야 할 문서

구현 전에 아래 문서를 읽는다.

- `docs/implementation-notes.md`: 구현 세션용 한국어 참고 메모
- `docs/coding-principles.md`: 코드 작성 원칙과 테스트 seam 설계 기준
- `docs/plan.md`: 제품 방향과 milestone
- `docs/architecture.md`: 상세 architecture와 구현 설계
- `docs/large-repo-test-plan.md`: 대규모 repository 테스트 전략과 실제 후보 repo

문서 언어 기준:

- `README.md`: GitHub 첫 화면용 짧은 영어 소개
- `AGENTS.md`, `docs/implementation-notes.md`: 구현 agent와 사용자가 함께 검토하는 한국어 지침

## 현재 상태

M2d(`diagnostics`, `DiagnosticStore`), M2 large repo readiness 일부, M3 사용자/클라이언트 사용성 정리가 완료되어 있다.

다음 작업 후보는 `docs/implementation-notes.md`의 최신 상태 메모와 `docs/large-repo-test-plan.md`를 기준으로 정한다. 우선순위가 높은 남은 항목은 diagnostics notification offload 설계, opt-in large repo 검증과 default tuning, 필요 시 추가 실제 MCP client smoke 반복이다.
