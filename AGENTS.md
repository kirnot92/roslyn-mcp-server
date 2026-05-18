# Agent 작업 지침

이 문서는 `roslyn-mcp-server`를 구현하는 agent가 가장 먼저 읽어야 하는 작업 지침이다.

`roslyn-mcp-server`는 Agent CLI류 도구가 Roslyn 언어 기능을 사용할 수 있도록, `roslyn-language-server`를 자식 프로세스로 실행하고 MCP tool 호출을 LSP 요청으로 변환하는 MCP 서버다.

현재 이 저장소는 M2 read-only tool 구현, M2 large repo readiness 일부, M3 사용자/클라이언트 사용성 정리, M4 startup initial solution load와 diagnostics notification offload, M5 read productivity tool 일부까지 반영된 상태다.

## 목표

- MCP 서버는 C#/.NET으로 구현한다.
- 공식 C# MCP SDK를 사용한다.
- `roslyn-language-server`와 stdio LSP로 통신한다.
- 기본 repository root는 서버가 실행된 현재 작업 디렉터리로 둔다.
- agent가 `load_solution`, `load_project` 같은 MCP tool로 `.sln`, `.slnx`, `.csproj`를 선택하게 한다.
- 필요한 경우 `--load-solution <path>`로 MCP 서버 시작 직후 지정 solution을 background로 로드할 수 있게 한다.
- 대규모 repository를 고려해 탐색 제한, timeout, 결과 제한, warming 중 best-effort 동작을 지원한다.
- 이 프로젝트의 제품 방향은 best-effort read-only Roslyn context provider다. write/refactoring tool은 프로젝트 방향에 포함하지 않는다.

## 하지 않을 것

- 이 프로젝트를 NuGet/.NET global tool로 게시하지 않는다.
- `roslyn-language-server`를 번들하지 않는다.
- release 자동화는 아직 추가하지 않는다.
- write/refactoring tool을 구현하지 않는다.

## 외부 요구사항

사용자는 `roslyn-language-server`를 별도로 설치해야 한다.

```text
dotnet tool install --global roslyn-language-server --prerelease
```

MCP 서버는 기본적으로 PATH에서 `roslyn-language-server`를 찾는다. 필요한 경우 `--roslyn-language-server <path>` 옵션으로 명시 경로를 받을 수 있다.

## 현재 구현 범위

현재 `main` 기준으로 M0/M1, M2 read-only tool, M3 사용자/클라이언트 사용성 범위, M4 startup initial solution load와 diagnostics notification offload, M5 read productivity tool 일부가 구현되어 있다.

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
- README의 Claude Code, Gemini CLI, Codex MCP client 설정 예시
- `docs/smoke-test-guide.md`와 `scripts/smoke-tests/` smoke test helper 정리
- `roslyn-language-server` 탐색/설치 오류 메시지 정리
- PowerShell, Semantic Kernel, ASP.NET Core stdio smoke 기록
- `solution_overview` M3 미구현 결정과 후속 후보 평가

M4 startup initial solution load 포함:

- `--load-solution <path>` CLI 옵션 추가
- 지정된 `.sln` 또는 `.slnx`를 MCP 서버 시작 후 background task에서 기존 solution load 경로로 로드
- 옵션 미지정 시 기존처럼 agent의 명시적 `load_solution`/`load_project` 또는 단일 후보 auto-load 유지
- `--load-solution` 중복 지정 거부
- invalid extension/path, root 밖 경로, startup load 상태 전이에 대한 테스트 추가
- startup load 중 read tool은 기존 계약대로 `workspace_loading` 또는 warming metadata 반환
- `--load-solution` 경로는 root 아래를 재귀 탐색하지 않는다. 정확한 root-relative path 또는 root 내부 absolute path를 지정해야 한다.

M4 diagnostics notification offload 포함:

- `textDocument/publishDiagnostics` notification은 bounded background queue를 통해 `DiagnosticStore`에 반영한다.
- LSP read loop의 notification handler는 queue enqueue 후 즉시 반환한다.
- Queue overflow 정책은 `drop_newest_when_full`이며, pending/processed/dropped/stale 통계와 queue capacity를 `get_workspace_status`에 노출한다.
- Workspace reload 시 generation을 증가시키고 이전 generation diagnostics notification은 stale로 집계해 새 workspace store에 섞이지 않게 한다.

M5 read productivity 일부 포함:

- `find_implementations`
- `peek_definition`
- `peek_definition`은 definition 위치와 함께 root 내부 source snippet을 반환하고, root 밖 또는 읽을 수 없는 파일은 건너뛴다.
- `peek_references`
- `peek_references`는 `find_references`와 같은 LSP references semantics로 reference 위치를 찾고, 각 root 내부 위치에 bounded source snippet을 붙여 반환한다.
- `find_symbols` kind/match/path prefix filtering
- `find_symbols`의 `kindFilter`는 `class`, `interface`, `method`, `property`, `field`, `enumMember`, `typeParameter` 같은 MCP symbol kind 이름을 대소문자 무시로 받는다.
- `matchMode`는 `default`, `exact`, `prefix`, `contains`를 지원하며 simple symbol name에만 적용한다.
- `includePathPrefixes`는 root-relative path prefix 목록을 받아 해당 prefix 아래에 location이 있는 symbol만 유지한다. path 구분자는 normalize하고, prefix는 segment boundary 기준으로 비교한다.
- `kindFilter`, `matchMode`, `includePathPrefixes`는 Roslyn LS `workspace/symbol` 응답을 받은 뒤 MCP 쪽에서 적용한다. Roslyn LS 검색 비용 절감을 보장하지 않고, 반환 noise 감소를 목적으로 한다.
- `find_symbols` 결과 metadata에는 필터 후 mappable 결과 기준의 `totalKnown`, `returned`, `truncated`와 필터 전 mappable 결과 수인 `totalUnfilteredKnown`이 포함된다.
- `get_call_hierarchy`
- `get_call_hierarchy`는 LSP `textDocument/prepareCallHierarchy`, `callHierarchy/incomingCalls`, `callHierarchy/outgoingCalls`를 사용해 특정 callable의 직접 depth-1 incoming/outgoing 호출 관계를 반환한다.
- `direction`은 `incoming`, `outgoing`, `both`를 지원하고 recursive depth/maxDepth는 제공하지 않는다.
- `kindFilter`는 `method`, `constructor`, `property`, `event`, `operator`, `field` 같은 edge counterpart kind 이름을 대소문자 무시로 받는다. 필터는 Roslyn LS 응답을 받은 뒤 MCP 쪽에서 적용하므로 Roslyn LS 요청 비용 절감을 보장하지 않고, 반환 noise 감소를 목적으로 한다.
- 결과 metadata에는 필터 후 mappable edge 기준의 `totalKnown`, `returned`, `truncated`와 필터 전 mappable edge 수인 `totalUnfilteredKnown`이 포함된다.
- `get_type_hierarchy`
- `get_type_hierarchy`는 LSP `textDocument/prepareTypeHierarchy`, `typeHierarchy/supertypes`, `typeHierarchy/subtypes`를 사용해 타입의 base/derived/interface implementation 관계를 bounded BFS로 반환한다.
- `direction`은 `supertypes`, `subtypes`, `both`를 지원하고 `maxDepth`, `maxResults`로 traversal depth와 edge 결과 수를 제한한다.
- 결과 metadata에는 mappable edge 기준의 `totalKnown`, `returned`, `truncated`가 포함된다. result cap 때문에 다른 방향이나 더 깊은 follow-up을 요청하지 못한 경우도 `truncated`로 표시한다.

다음 범위는 후속 작업으로 남긴다.

- opt-in large repo 검증과 default tuning
- 추가 실제 MCP client smoke 반복
- 대형 솔루션 startup 성능 측정
- Roslyn LS crash/restart 처리
- 오류/상태 관측성 강화
- `solution_overview`

## 중요한 설계 메모

- 기본값에서는 서버 시작 시 solution을 강제로 load하지 않는다.
- `--load-solution <path>`가 지정되면 서버 시작 후 background task가 지정 solution을 로드한다.
- `--load-solution` 값은 root 기준 정확한 상대경로 또는 root 내부 absolute path여야 한다. 파일명만 넘겼을 때 하위 폴더를 검색하지 않는다.
- solution이 여러 개 발견되면 agent가 `load_solution`을 호출해야 한다.
- 적절한 solution이 정확히 하나면 첫 Roslyn tool 호출에서 자동 load할 수 있다.
- `StartingLanguageServer` 상태에서는 Roslyn tool이 오래 대기하지 말고 `workspace_loading`을 반환한다.
- LSP initialize 이후에는 workspace가 warming 중이어도 가능한 읽기 tool은 best-effort로 실행한다.
- write/refactoring 계열은 후속 milestone 후보가 아니다. rename/code action/formatting/apply 계열은 이 서버가 아니라 agent의 일반 파일 편집 흐름이나 별도 도구가 맡는다.
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

M2d(`diagnostics`, `DiagnosticStore`), M2 large repo readiness 일부, M3 사용자/클라이언트 사용성 정리, M4 startup initial solution load와 diagnostics notification offload, M5 read productivity tool 일부(`find_implementations`, `peek_definition`, `peek_references`, `find_symbols` kind/match/path prefix filtering, `get_call_hierarchy`, `get_type_hierarchy`)가 완료되어 있다.

다음 작업 후보는 `docs/implementation-notes.md`의 최신 상태 메모와 `docs/large-repo-test-plan.md`를 기준으로 정한다. 우선순위가 높은 남은 항목은 opt-in large repo 검증과 default tuning, 필요 시 추가 실제 MCP client smoke 반복, 대형 solution startup 성능과 관측성 강화다. `get_completions`는 IDE 자동완성 성격상 write-adjacent 도구에 가까우므로 현재 read-only context provider 방향의 후속 후보에서 제외한다.
