# Agent 작업 지침

이 문서는 `roslyn-mcp-server`를 구현하는 agent가 가장 먼저 읽어야 하는 작업 지침이다.

`roslyn-mcp-server`는 Agent CLI류 도구가 Roslyn 언어 기능을 사용할 수 있도록, `roslyn-language-server`를 자식 프로세스로 실행하고 MCP tool 호출을 LSP 요청으로 변환하는 MCP 서버다.

## 제품 방향

- MCP 서버는 C#/.NET으로 구현한다.
- 공식 C# MCP SDK를 사용한다.
- `roslyn-language-server`와 stdio LSP로 통신한다.
- 기본 repository root는 서버가 실행된 현재 작업 디렉터리다. 필요한 경우 `--root <path>`를 받는다.
- agent가 `list_workspaces`, `load_solution`, `load_project`로 `.sln`, `.slnx`, `.csproj`를 선택한다.
- 필요한 경우 `--load-solution <path>`로 MCP 서버 시작 직후 지정 solution을 background로 로드한다.
- 대규모 repository를 고려해 탐색 제한, timeout, 결과 제한, warming 중 best-effort 동작을 지원한다.
- 제품 방향은 best-effort read-only Roslyn context provider다.

## 하지 않을 것

- NuGet/.NET global tool로 게시하지 않는다.
- `roslyn-language-server`를 번들하지 않는다.
- release 자동화는 아직 추가하지 않는다.
- rename, code action, formatting, apply edit 같은 write/refactoring tool을 구현하지 않는다.

## 외부 요구사항

사용자는 `roslyn-language-server`를 별도로 설치해야 한다.

```text
dotnet tool install --global roslyn-language-server --prerelease
```

MCP 서버는 기본적으로 PATH에서 `roslyn-language-server`를 찾는다. 필요한 경우 `--roslyn-language-server <path>` 옵션으로 명시 경로를 받는다.

## 현재 구현 범위

현재 `main` 기준으로 M0/M1, M2 read-only tool, M2 large repo readiness 일부, M3 사용자/클라이언트 사용성 정리, M4 startup initial solution load와 diagnostics notification offload, M5 read productivity tool 일부가 구현되어 있다.

Workspace tool:

- `list_workspaces`
- `load_solution`
- `load_project`
- `get_workspace_status`

Read-only Roslyn tool:

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
- `diagnostics`

Server resource:

- `roslyn://server/guide`
- `roslyn://server/capabilities`

## 핵심 동작 계약

- 기본값에서는 서버 시작 시 solution을 강제로 load하지 않는다.
- `--load-solution <path>`가 지정되면 서버 시작 후 background task가 지정 `.sln` 또는 `.slnx`를 로드한다.
- `--load-solution` 값은 root 기준 정확한 상대경로 또는 root 내부 absolute path여야 한다. 파일명만 넘겼을 때 하위 폴더를 검색하지 않는다.
- solution이 여러 개 발견되면 agent가 `load_solution`을 호출해야 한다.
- 적절한 solution 또는 project가 정확히 하나면 첫 Roslyn read tool 호출에서 자동 load할 수 있다.
- `StartingLanguageServer` 상태에서는 Roslyn tool이 오래 대기하지 말고 `workspace_loading`을 반환한다.
- LSP initialize 이후 workspace가 warming 중이어도 가능한 read tool은 best-effort로 실행한다.
- warming 중 반환된 결과에는 `workspaceState`, `completeness`, `reason`, `retryAfterMs`, `truncated` metadata를 포함한다.
- 대량 결과는 `maxResults`와 metadata로 제한 상태를 드러낸다.
- `textDocument/publishDiagnostics` notification은 bounded background queue로 처리한다. Queue overflow 정책은 `drop_newest_when_full`이다.
- Workspace reload 시 diagnostics generation을 증가시켜 이전 generation notification이 새 workspace store에 섞이지 않게 한다.
- stdout에는 로그를 쓰지 않는다. stdout은 MCP protocol 채널이다.

## 주요 옵션

- `--root <path>`
- `--roslyn-language-server <path>`
- `--load-solution <path>`
- `--log-level <trace|debug|info|warn|error>`
- `--log-file <path>`
- `--ls-log-dir <path>`
- `--startup-timeout <seconds>`
- `--max-solution-candidates <count>`
- `--max-project-candidates <count>`
- `--max-open-documents <count>`
- `--max-document-bytes <bytes>`
- `--max-in-flight-lsp-requests <count>`
- `--max-expensive-lsp-requests <count>`

## Git 작업 방식

구현 작업은 아래 GitHub repository를 기준으로 진행한다.

```text
https://github.com/kirnot92/roslyn-mcp-server
```

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

구현 전에 필요한 범위만 읽는다.

- `docs/implementation-notes.md`: 현재 구현 상태, 다음 우선순위, 검증 기준
- `docs/architecture.md`: component 구조와 tool 계약
- `docs/coding-principles.md`: 코드 작성 원칙과 테스트 seam 기준
- `docs/usage.md`: 사용자 설치, MCP client 설정, tool flow
- `docs/large-repo-test-plan.md`: 대규모 repository 검증과 tuning 작업
- `docs/smoke-test-guide.md`: 실제 client/repo smoke test 실행 방식

`docs/archive/`는 완료된 milestone 계획, 과거 설계 메모, smoke 결과 원문을 보관하는 공간이다. 기본 구현 컨텍스트에는 포함하지 않고, 과거 결정을 감사해야 할 때만 읽는다.

문서 언어 기준:

- `README.md`: GitHub 첫 화면용 짧은 영어 소개
- `AGENTS.md`, `docs/implementation-notes.md`: 구현 agent와 사용자가 함께 검토하는 한국어 지침

## 현재 우선순위

다음 작업 후보는 `docs/implementation-notes.md`와 `docs/large-repo-test-plan.md`를 기준으로 정한다. 우선순위가 높은 남은 항목은 opt-in large repo 검증과 default tuning, 대형 solution startup 성능과 관측성 강화, 필요 시 추가 실제 MCP client smoke 반복이다.

`get_completions`는 IDE 자동완성 성격상 write-adjacent 도구에 가까우므로 현재 read-only context provider 방향의 후속 후보에서 제외한다.
