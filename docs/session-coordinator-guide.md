# Session Coordinator Guide

## 목적

이 문서는 `roslyn-mcp-server` 작업에서 코디네이터 세션이 해야 할 역할을 정의한다. 새 세션에 이 문서만 전달해도 같은 방식으로 문서 맥락을 로드하고, 작업 단계를 나누고, 구현/리뷰/반영/검증 세션을 조율할 수 있어야 한다.

코디네이터는 직접 대규모 구현을 맡기보다, 현재 상태를 판단하고 다음 세션에 줄 정확한 작업 프롬프트를 만든다. 필요할 때는 작은 문서 수정, 상태 확인, 커밋 단위 정리도 수행한다.

## 기본 역할

- 저장소의 현재 milestone과 작업 범위를 파악한다.
- 관련 문서를 읽고 문서 간 충돌이나 outdated 내용을 구분한다.
- dirty working tree가 있으면 어떤 변경인지 확인하고, 사용자 변경을 되돌리지 않는다.
- 작업을 구현 세션, 리뷰 세션, 리뷰 반영 세션, smoke 세션으로 나누어 지시한다.
- 리뷰 finding은 명령이 아니라 검증 가능한 주장으로 취급한다.
- 다음 단계로 넘어가도 되는지 판단한다.
- 커밋/푸시가 필요하면 사용자가 지정한 범위만 포함한다.

## 먼저 확인할 것

새 코디네이터 세션은 보통 아래 순서로 상태를 로드한다.

```powershell
git status --short
git log --oneline -8
git remote -v
git branch --show-current
rg --files docs
```

그 다음 가능한 경우 아래 문서를 읽는다.

- `AGENTS.md`
- `docs/implementation-notes.md`
- `docs/architecture.md`
- `docs/m2-plan.md`
- `docs/large-repo-test-plan.md`
- `docs/m2-large-repo-readiness-plan.md`
- `docs/code-review-principles.md`
- `docs/coding-principles.md`
- 관련 `docs/reviews/*.md`
- 관련 `docs/smoke-tests/*.md`

`docs/reviews/`는 `.gitignore` 대상일 수 있다. 리뷰/반영 문서는 로컬 판단 자료로 보고, 사용자가 명시하지 않는 한 커밋 대상으로 가정하지 않는다.

## 프로젝트 기준

- 이 프로젝트는 C#/.NET MCP 서버다.
- Roslyn 기능은 `roslyn-language-server`를 자식 프로세스로 실행하고 LSP stdio로 통신한다.
- MCP transport는 stdio다.
- stdout은 MCP protocol 채널이므로 로그를 쓰면 안 된다.
- `roslyn-language-server`는 번들하지 않고 사용자가 별도 설치한다.
- NuGet/.NET global tool 게시, release 자동화, write/refactoring tool은 현재 범위 밖이다.
- 사용자 입력 path는 `PathGuard`를 통과해야 한다.
- MCP tool 입출력 line/column은 1-based, LSP 내부 position/range는 0-based다.
- `StartingLanguageServer`에서는 read tool을 queue/hang하지 않고 `workspace_loading`을 반환해야 한다.
- 현재 구현은 load 성공 후 `LspReady`를 외부 상태로 노출하지 않고 `WorkspaceWarming`으로 전환한다.
- `WorkspaceWarming`에서는 best-effort 실행과 `workspaceState`, `completeness` metadata를 포함해야 한다.
- 대량 결과는 `maxResults`, `totalKnown`, `returned`, `truncated` 같은 metadata를 가져야 한다.
- 현재 read-only Roslyn tool은 `document_symbols`, `hover`, `go_to_definition`, `peek_definition`, `find_references`, `find_implementations`, `find_symbols`, `diagnostics`다.
- `peek_definition`은 symbol name 검색이 아니라 `file`, `line`, `column` 위치 기반 tool이다. `go_to_definition` 결과 위치와 bounded source snippet을 함께 반환하며, snippet은 path guard, document size, line/context, character cap을 따라야 한다.
- `find_implementations`도 위치 기반 tool이다. LSP `textDocument/implementation`을 호출하고, `find_references`와 같은 bounded location metadata를 반환해야 한다. 전체 구현체를 찾게 하려면 interface/abstract/base contract의 선언 위치나 그 contract로 정적으로 타입 지정된 사용 위치에서 호출하게 한다. 구체 class/member 구현 위치에서 호출하면 Roslyn LS가 자기 자신만 반환할 수 있으며, 이는 정상 응답일 수 있다.
- 다음 read productivity 후보는 `peek_references`, `get_call_hierarchy`, `get_type_hierarchy`, `get_completions`다. `peek_references`는 `find_references` 위치 목록에 bounded source snippet을 붙여 reference 사용 맥락을 바로 보게 하는 tool 후보이며, `get_call_hierarchy`는 callable의 incoming/outgoing 호출 관계를 다루고, `get_type_hierarchy`는 타입의 base/derived 관계를 다루므로 역할을 분리한다.
- `diagnostics`는 bounded background queue를 통해 처리된 `textDocument/publishDiagnostics` 기준이다. `get_workspace_status`는 diagnostics queue capacity, pending, processed, dropped, stale count와 overflow policy를 노출한다.

## 진행 방식

### 구현 세션을 시킬 때

프롬프트에는 항상 다음을 포함한다.

- 작업 대상 저장소 경로
- 반드시 읽을 문서
- 작업 시작 전 git 상태 확인
- 포함 범위와 제외 범위
- 유지해야 할 계약
- 필수 테스트
- 실행할 검증 명령
- 커밋/푸시 규칙
- 최종 보고 형식

구현 범위가 크면 milestone을 쪼갠다. 예를 들어 M2는 `M2a`, `M2b`, `M2c`, `M2d`처럼 나누고, 각 단계 뒤에 리뷰와 반영을 둔다.

### 리뷰 세션을 시킬 때

리뷰어에게는 코드를 수정하지 말고 리뷰 문서만 작성하게 한다.

- findings를 먼저 적게 한다.
- 각 finding은 severity, 위치, 문제, 영향, 권장 수정으로 쓰게 한다.
- 추측은 추측이라고 표시하게 한다.
- 문제가 없던 항목도 `Non-Issues Checked`에 남기게 한다.
- 가능한 경우 `dotnet format`, `dotnet build`, `dotnet test`를 실행하게 한다.
- verdict는 `approve`, `approve with issues`, `request changes` 중 하나로 쓰게 한다.

### 리뷰 반영 세션을 시킬 때

반영자는 리뷰를 전부 기계적으로 반영하면 안 된다.

- finding마다 타당성을 확인하게 한다.
- 타당한 버그와 milestone 계약 위반만 수정하게 한다.
- 반영하지 않은 항목은 이유를 resolution 문서에 남기게 한다.
- `High`/`Critical` 또는 `request changes`는 다음 milestone로 넘어가기 전 해결하게 한다.
- 코드 수정이 있으면 관련 테스트를 추가하게 한다.

### smoke 세션을 시킬 때

smoke의 목표는 완전한 semantic 정확도가 아니라 실제 사용 가능성 확인이다.

- 서버가 hang/crash하지 않는지 본다.
- workspace discovery가 제한 시간 안에 되는지 본다.
- load 후 상태가 `WorkspaceWarming`, `Ready`, `LoadedWithErrors`, `Failed` 중 명확히 드러나는지 본다.
- read tool이 partial/unknown metadata와 함께 응답하는지 본다.
- 대량 결과가 제한되는지 본다.
- `peek_definition` smoke는 정의 위치와 snippet이 root-relative path, 1-based range, bounded text로 반환되는지 확인한다.
- `find_implementations` smoke는 interface/abstract/base contract 위치에서 구현 위치가 반환되는지 확인하고, 구체 구현 위치에서는 자기 자신만 반환될 수 있음을 기록한다. warming 중 partial metadata를 허용하되, `retryAfterMs`가 있으면 재시도 후 결과 차이도 확인한다.
- 결과를 `docs/smoke-tests/*.md`에 기록하게 한다.

Smoke driver script는 `scripts/smoke-tests/`에 커밋할 수 있다. 로그, raw output, cloned repo 같은 실행 산출물은 `.local/` 아래에 두고 커밋하지 않는다.

## 다음 단계 판단

- 리뷰가 `approve`이고 finding이 없으면 해당 단계는 닫을 수 있다.
- `approve with issues`이면 반영 세션을 먼저 진행한다.
- `request changes` 또는 High/Critical finding이 있으면 다음 milestone로 넘어가지 않는다.
- 테스트 실패가 있으면 환경 문제인지 코드 문제인지 분리한다.
- smoke에서 실패가 있으면 blocker, issue, observation으로 나눈다.
- 대형 repo 관련 문제는 `docs/m2-large-repo-readiness-plan.md`와 연결해 Phase 1/2/3로 분류한다.

## Git 원칙

- 작업 시작 시 branch, remote, working tree 상태를 확인한다.
- 사용자가 지정한 파일만 커밋해야 할 때는 `git add -- <path>`로 정확히 제한한다.
- unrelated 변경은 되돌리지 않고 commit에도 포함하지 않는다.
- force push, history rewrite, 불필요한 rebase는 하지 않는다.
- 테스트 또는 문서만 변경한 경우에도 커밋 메시지는 구체적으로 쓴다.
- push 권한 문제가 있으면 작업 내용은 보존하고 사용자에게 보고한다.

## 자주 쓰는 검증

```powershell
dotnet format roslyn-mcp-server.sln --verify-no-changes
dotnet build roslyn-mcp-server.sln
dotnet test roslyn-mcp-server.sln
```

Roslyn LS가 필요한 smoke 전에는 다음을 확인한다.

```powershell
roslyn-language-server --help
dotnet --info
```

## 보고 방식

코디네이터의 답변은 짧고 판단 중심이어야 한다.

- 무엇을 확인했는지
- blocker가 있는지
- 다음 액션이 무엇인지
- 새 세션에 줄 프롬프트가 필요하면 바로 제공한다
- 커밋/푸시를 했다면 commit hash와 남은 working tree 변경을 말한다

불확실한 내용은 추측이라고 표시한다. 문서와 구현이 다르면 어느 쪽을 최신 기준으로 볼지 명시한다.
