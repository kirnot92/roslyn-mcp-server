# Coding Principles

이 문서는 사용자가 요구한 코드 작성의 주요 원칙들을 정리한 것이다.
사용자가 대화에서 명시한 원칙만 여기에 적는다.

## 테스트 seam

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
