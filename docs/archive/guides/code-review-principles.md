# Code Review Principles

## Purpose

이 문서는 구현 리뷰와 리뷰 반영 세션에서 반복해서 지켜야 할 운영 원칙을 정리한다. 리뷰는 단순 승인 절차가 아니라, 구현 범위와 설계 계약을 검증하고 다음 작업으로 넘어가도 되는지 판단하기 위한 단계다.

## 공통 원칙

- 먼저 관련 문서를 읽고 현재 milestone 범위를 확인한다.
- 현재 working tree와 최근 commit을 확인한 뒤 리뷰 또는 수정을 시작한다.
- unrelated 변경은 되돌리거나 commit에 섞지 않는다.
- force push, history rewrite, 불필요한 rebase는 하지 않는다.
- 테스트 seam은 production class를 `virtual`/상속 가능하게 열지 않고, 필요한 경우 작은 interface로 둔다.
- stdout은 MCP protocol 채널이므로 로그를 섞지 않는다.
- user-facing 동작 계약을 우선 검증한다. 예: path guard, 1-based MCP position, 0-based LSP position, loading/warming metadata, result limit.

## 코드 리뷰어 원칙

- 리뷰어는 코드를 수정하지 않는다. 리뷰 결과만 `docs/reviews/<topic>-review.md`에 작성한다.
- findings를 먼저 적고, 요약은 뒤에 둔다.
- 각 finding은 검증 가능한 문제 주장이어야 한다.
- 가능하면 파일과 라인, 심각도(`Critical`, `High`, `Medium`, `Low`), 영향, 권장 수정을 함께 적는다.
- 추측은 추측이라고 표시하고, 확인 방법이나 재현 조건을 같이 적는다.
- 문제가 없는 영역도 `Non-Issues Checked`에 짧게 남긴다.
- 실행한 검증 명령과 결과를 기록한다. 실행하지 못한 검증은 이유를 적는다.
- 리뷰 문서의 verdict는 명확히 쓴다.
  - `approve`: 검증 가능한 결함 없음
  - `approve with issues`: 후속 반영은 필요하지만 blocker는 아님
  - `request changes`: 다음 milestone로 넘어가기 전에 수정 필요

## 코드 리뷰 반영자 원칙

- 리뷰 finding을 전부 기계적으로 반영하지 않는다.
- finding마다 먼저 타당성을 확인하고, 명백한 버그나 milestone 계약 위반만 수정한다.
- 리뷰가 오해했거나 현재 설계상 의도된 동작이면 수정하지 않고 이유를 남긴다.
- `High`/`Critical` 또는 `request changes`가 있으면 다음 milestone로 넘어가지 않는다.
- finding이 있으면 `docs/reviews/<topic>-review-resolution.md`를 작성한다.
- resolution 문서에는 반영한 항목, 반영하지 않은 항목, 남은 리스크, 검증 결과를 적는다.
- 코드 수정이 있으면 관련 테스트를 추가하거나 갱신한다.
- 가능한 경우 다음 검증을 실행한다.

```text
dotnet format roslyn-mcp-server.sln --verify-no-changes
dotnet build roslyn-mcp-server.sln
dotnet test roslyn-mcp-server.sln
```

## 리뷰 후 액션 판단

- `approve`이고 finding이 없으면 리뷰 파일만 commit/push하고 다음 milestone로 넘어갈 수 있다.
- `approve with issues`이면 별도 반영 세션에서 triage, 수정, resolution 문서 작성을 끝낸 뒤 넘어간다.
- `request changes` 또는 `High`/`Critical` finding이 있으면 수정과 필요 시 재리뷰가 먼저다.
- 테스트 실패가 있으면 환경 문제인지 코드 문제인지 분리한다. 코드 문제면 다음 milestone 착수 전에 해결한다.
