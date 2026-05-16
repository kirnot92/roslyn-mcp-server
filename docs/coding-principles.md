# Coding Principles

이 문서는 다음 구현 세션에서도 유지해야 할 코드 작성 원칙을 기록한다.

## Production code를 테스트 때문에 열지 않는다

테스트 double을 만들기 위해 production class를 `virtual`/상속 가능하게 바꾸지 않는다.

나쁜 방향:

- 테스트 전용 subclass를 만들기 위해 concrete class의 `sealed`를 제거한다.
- 테스트 override를 위해 production method를 `virtual`로 만든다.
- 실제 도메인 확장점이 아닌데 테스트 편의를 위해 inheritance seam을 추가한다.

좋은 방향:

- 실제 협력 객체 경계가 필요하면 작은 interface를 둔다.
- production 구현과 test double은 같은 interface를 각각 구현한다.
- class는 기본적으로 `sealed`로 유지한다.
- 테스트 seam은 production 설계에서 의미 있는 의존성 경계여야 한다.

예:

```csharp
public interface IGitWorkspaceScanner
{
    WorkspaceScanResult? TryScan(TimeSpan budget, CancellationToken cancellationToken = default);
}

public sealed class GitWorkspaceScanner : IGitWorkspaceScanner
{
    // real implementation
}

private sealed class SlowNullGitScanner : IGitWorkspaceScanner
{
    // test double
}
```

## Scope를 좁게 유지한다

- 리뷰나 요청에 없는 큰 리팩터링을 섞지 않는다.
- M0/M1 작업에서는 navigation, diagnostics, write/refactoring tool을 구현하지 않는다.
- 결함 수정은 현재 범위의 계약을 깨는 부분에 집중한다.

## 계약을 코드와 테스트로 맞춘다

- 문서에 적힌 계약과 코드 동작이 다르면 둘 중 하나를 반드시 정리한다.
- path escape, stdout 오염, LSP framing, request correlation, process shutdown, timeout/cancellation은 우선 검증한다.
- 새로 고친 버그는 가능한 한 regression test를 추가한다.

## Fallback은 safety net이다

- 더 정확한 primary path가 있으면 먼저 사용한다.
- fallback은 non-standard 환경과 실패 복구를 위한 안전망으로 유지한다.
- fallback 때문에 전체 timeout, result limit, path guard 같은 보호 장치가 약해지면 안 된다.
