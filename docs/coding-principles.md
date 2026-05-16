# Coding Principles

이 문서는 사용자가 요구한 코드 작성의 주요 원칙들을 정리한 것이다.
사용자가 대화에서 명시한 원칙만 여기에 적는다.

## 멤버 변수 이름

멤버 변수는 앞에 `_`를 붙이지 않고 camelCase로 작성한다.
멤버 변수를 참조할 때는 반드시 `this.`를 붙인다.

나쁜 방향:

- `private readonly SemaphoreSlim _stateLock = new(1, 1);`
- `private WorkspaceLoadState _state = WorkspaceLoadState.NotLoaded;`
- `stateLock.Release();`

좋은 방향:

- `private readonly SemaphoreSlim stateLock = new(1, 1);`
- `private WorkspaceLoadState state = WorkspaceLoadState.NotLoaded;`
- `this.stateLock.Release();`

로컬 변수나 생성자 매개변수와 이름이 충돌하면 `_`를 붙이지 말고 의미가 드러나는 다른 camelCase 이름을 사용한다.
멤버 변수와 같은 이름의 로컬 변수 또는 매개변수가 필요한 경우에도 멤버 변수 참조에는 `this.memberName`을 사용한다.

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

## 외부 protocol 상수

LSP처럼 외부 specification이 숫자 enum이나 문자열 상수를 정의하는 경우, tool 구현 코드 안에 magic number switch를 직접 두지 않는다.

나쁜 방향:

- `1 => "file"`, `2 => "module"`처럼 출처가 보이지 않는 숫자 매핑을 tool class에 둔다.
- Roslyn 고유 값인지 LSP 표준 값인지 알 수 없는 상수를 inline으로 반복한다.

좋은 방향:

- protocol model 파일에 enum 또는 named constant로 정의한다.
- 정의 근처에 기준 specification 링크를 짧게 남긴다.
- 출력용 문자열 변환이 필요하면 enum/constant 근처의 static extension/helper에 둔다.
- tool layer는 enum/constant 이름과 extension/helper method만 사용한다.

예:

```csharp
// Values are defined by the Language Server Protocol SymbolKind constants:
// https://microsoft.github.io/language-server-protocol/specifications/lsp/3.17/specification/#symbolKind
public enum SymbolKind
{
    File = 1,
    Module = 2,
    Class = 5
}

public static class SymbolKindExtensions
{
    public static string ToMcpName(this SymbolKind kind) =>
        kind.ToString().ToLowerInvariant();
}
```
