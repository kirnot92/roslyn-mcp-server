# get_completions 구현 계획

## 목적

`get_completions`는 특정 C# 파일의 위치에서 Roslyn LS의 completion 후보를
제한적으로 조회하는 read-only MCP tool 후보다. Agent가 코드를 작성하거나 수정하기
전에 "이 위치에 올 수 있는 멤버, 타입, 키워드, named argument 후보가 무엇인지"를
compiler-backed 정보로 확인하는 것이 목적이다.

이 tool은 IntelliSense 후보를 반환하지만, IntelliSense UI를 그대로 MCP로 옮기는
것이 목표는 아니다. Agent CLI가 후속 파일 편집 판단에 쓸 수 있을 만큼 작고,
설명 가능하고, bounded한 후보 목록을 주는 데 집중한다.

## 비목표

- 파일을 수정하지 않는다.
- completion 후보를 자동 적용하지 않는다.
- `rename`, `code action`, `formatting`, `apply` 계열 기능으로 확장하지 않는다.
- LSP completion payload 전체를 무가공으로 노출하지 않는다.
- broad workspace symbol search 대체재로 쓰지 않는다. 이름을 알고 찾는 경우는
  `find_symbols`가 우선이다.
- 저장되지 않은 임시 코드에 대한 완전한 completion을 보장하지 않는다.

`get_completions`는 이 프로젝트의 제품 방향인 best-effort read-only Roslyn context
provider 안에 있어야 한다. 반환값은 Agent가 참고할 근거일 뿐이며, 실제 파일
수정은 기존 일반 파일 편집 흐름이 담당한다.

## 기본 사용 시나리오

- `context.Handle.` 뒤에서 사용 가능한 멤버 후보를 확인한다.
- `new ` 또는 `new Foo(` 위치에서 타입/생성자 후보를 확인한다.
- `override ` 뒤에서 override 가능한 멤버 후보를 확인한다.
- attribute 위치에서 attribute 타입 후보를 확인한다.
- 메서드 호출 인자 위치에서 named argument 또는 현재 scope 변수 후보를 확인한다.
- `using` 지시문이나 namespace-like 위치에서 namespace/type 후보를 확인한다.

이 시나리오들은 prefix가 비어 있어도 유용할 수 있다. 따라서 전역
`minPrefixLength: 4` 같은 강한 제한은 기본 정책으로 두지 않는다. 다만 broad
context에서는 별도 guard가 필요하다.

## 제안 MCP 입력

초기 입력은 다음 정도로 시작한다.

```text
get_completions(
  file,
  line,
  column,
  maxResults?,
  kindFilter?,
  includeDetail?,
  includeDocumentation?,
  includeTextEdits?,
  includeSnippets?,
  triggerKind?,
  triggerCharacter?
)
```

- `file`: root-relative C# file path. 기존 read tool과 같이 root 내부 absolute path도
  허용할 수 있다.
- `line`, `column`: 1-based cursor position이다. 내부 LSP 요청 전 0-based position으로
  변환한다.
- `maxResults`: 반환 후보 수 제한이다. 기본값은 50, hard cap은 200 정도에서 시작한다.
- `kindFilter`: `method`, `property`, `field`, `class`, `interface`, `namespace`,
  `keyword`, `variable`, `constructor`, `enum`, `enumMember`, `event`, `operator`
  같은 MCP completion kind 이름을 대소문자 무시로 받는다.
- `includeDetail`: 짧은 signature/detail 포함 여부다. 기본값은 `true`가 적절하다.
- `includeDocumentation`: documentation 포함 여부다. 기본값은 `false`로 둔다.
- `includeTextEdits`: 후보가 적용될 때 필요한 text edit 정보를 advisory metadata로
  포함할지 여부다. 기본값은 `false`로 둔다.
- `includeSnippets`: snippet insert text를 포함할지 여부다. 기본값은 `false`로 둔다.
- `triggerKind`, `triggerCharacter`: LSP `CompletionContext`를 구성하기 위한 선택 입력이다.
  없으면 invoked completion으로 처리한다.

`prefix`를 caller 입력으로 받을지는 초기 버전에서 보류한다. 기본은 저장된 파일
내용에서 cursor 앞 identifier prefix를 추론한다. caller가 임의 prefix를 넘기면 현재
파일 내용과 불일치할 수 있어, overlay text sync 모델이 생기기 전에는 오히려 혼란을
키울 수 있다.

## LSP 매핑

기본 LSP 요청은 `textDocument/completion`이다.

```text
textDocument/completion
  textDocument: 현재 file URI
  position: 0-based cursor position
  context:
    triggerKind
    triggerCharacter?
```

응답 shape는 두 가지를 모두 처리한다.

- `CompletionItem[]`
- `CompletionList`
  - `isIncomplete`
  - `items`

`CompletionList.isIncomplete`는 중요하다. 이것은 LSP 서버가 "현재 응답이 전체 후보를
완전히 대표하지 않을 수 있다"고 알려주는 신호이지, MCP 쪽 result cap 때문에 잘린
것과 같은 의미가 아니다. 따라서 MCP 결과에는 별도 필드로 보존하거나, 최소한
`completeness`와 `reason`에 명확히 반영해야 한다.

`completionItem/resolve`는 초기 최소 구현에는 넣지 않는 것이 안전하다. 넣는다면 아래
제한을 만족해야 한다.

- `includeDocumentation`이 `true`이거나 반환 후보의 `detail`이 비어 있는 경우에만
  선택적으로 호출한다.
- resolve 대상은 최종 반환 후보 중 상위 N개로 제한한다. 초기값은 10 이하가 적당하다.
- resolve 호출 전체도 expensive request budget 또는 별도 fan-out budget에 포함한다.
- item별 timeout을 짧게 둔다.
- documentation에는 character cap을 적용한다.
- resolve 실패는 전체 tool 실패가 아니라 해당 item의 `resolveError` 또는 누락된
  documentation으로 처리한다.

## Read-Only 필드 정책

Completion LSP payload에는 실제 편집에 가까운 필드가 포함될 수 있다. 이 프로젝트는
write/refactoring tool을 제공하지 않으므로, 아래 정책을 둔다.

- `command`는 초기 버전에서 반환하지 않는다.
- `additionalTextEdits`는 초기 버전에서 반환하지 않는다.
- `textEdit`과 `insertText`는 기본 반환하지 않는다.
- `includeTextEdits: true`일 때도 "적용할 edit"가 아니라 "후보가 의미하는 교체 범위와
  텍스트 힌트"로만 반환한다.
- snippet은 `includeSnippets: true`일 때만 반환하고, `insertTextFormat: snippet`임을
  명확히 표시한다.
- commit characters는 초기 버전에서는 반환하지 않거나, 반환하더라도 적용 동작이
  아니라 UI 힌트로만 취급한다.

Agent가 후보를 선택해 파일을 바꾸려면 일반 파일 편집 도구로 직접 수정해야 한다.
`get_completions` 결과 자체가 patch나 edit instruction이 되어서는 안 된다.

## 결과 모델

초기 결과는 다음 정도를 목표로 한다.

```json
{
  "items": [
    {
      "label": "PrepareReadToolAsync",
      "kind": "Method",
      "kindName": "method",
      "detail": "Task<ReadToolContext> WorkspaceSession.PrepareReadToolAsync(...)",
      "documentation": null,
      "sortText": "...",
      "filterText": "...",
      "preselect": false,
      "dataAvailable": true,
      "textEdit": null,
      "insertText": null,
      "insertTextFormat": null
    }
  ],
  "totalKnown": 87,
  "totalUnfilteredKnown": 124,
  "returned": 50,
  "truncated": true,
  "serverIncomplete": false,
  "workspaceState": "Ready",
  "completeness": "unknown",
  "reason": "The language server does not report whether completion results are globally complete.",
  "retryAfterMs": null
}
```

Semantics:

- `totalKnown`: 이번 LSP completion 응답에서 MCP가 mappable하다고 판단한 후보 수다.
  전체 possible completion universe를 의미하지 않는다.
- `totalUnfilteredKnown`: `kindFilter` 적용 전 mappable 후보 수다. `kindFilter`가 없으면
  `totalKnown`과 같다.
- `returned`: 실제 반환한 후보 수다.
- `truncated`: MCP-side `maxResults`, hard cap, payload cap 때문에 반환을 자른 경우
  `true`다.
- `serverIncomplete`: LSP `CompletionList.isIncomplete` 값이다. `CompletionItem[]`
  응답에서는 `null` 또는 `false`로 처리할 수 있다.
- `completeness`: workspace warming, loaded-with-errors, `serverIncomplete`, Roslyn LS
  completion completeness 신호 부재를 반영한다.

`serverIncomplete: true`와 `truncated: true`는 서로 다른 이유다. 둘이 동시에 true일 수
있고, Agent는 누락 가능성의 원인을 구분해야 한다.

## Completion kind 매핑

LSP `CompletionItemKind`는 숫자 enum이다. 기존 `SymbolKind` 처리 원칙과 같이 protocol
model 근처에 enum 또는 named constant로 둔다.

초기 MCP kind 이름 후보:

- `text`
- `method`
- `function`
- `constructor`
- `field`
- `variable`
- `class`
- `interface`
- `module`
- `property`
- `unit`
- `value`
- `enum`
- `keyword`
- `snippet`
- `color`
- `file`
- `reference`
- `folder`
- `enumMember`
- `constant`
- `struct`
- `event`
- `operator`
- `typeParameter`

알 수 없는 kind는 `unknown`으로 매핑한다. `kindFilter`에 unknown kind 이름이 들어오면
기존 `find_symbols`처럼 user-facing `invalid_kind_filter` 오류로 거부한다.

## Prefix와 broad context 정책

completion은 prefix가 짧아도 의미 있는 경우가 많다. 전역 최소 글자 수 제한은 다음
문맥을 망가뜨린다.

- `.` 뒤 member access
- `?.` 뒤 null-conditional member access
- `new ` 뒤 object creation
- `override ` 뒤 override 후보
- `[` 안 attribute 후보
- method argument 위치의 named argument 후보

따라서 기본 정책은 context-sensitive guard다.

1. cursor 주변의 저장된 source text를 읽어 간단한 lexical context를 추론한다.
2. 명확한 narrow context는 prefix 0도 허용한다.
3. 명확하지 않은 broad context에서는 inferred prefix가 짧으면 요청을 거부하거나
   `completion_context_too_broad`를 반환한다.
4. broad context의 최소 prefix는 2 또는 3에서 시작한다. 4는 기본값으로 너무 강하다.
5. guard 판단은 compiler-authoritative가 아니라 heuristic임을 문서화한다.

초기 lexical heuristic 예:

- cursor 직전 non-whitespace token이 `.` 또는 `?.`이면 member access로 본다.
- cursor 앞 최근 token이 `new`이면 object creation으로 본다.
- cursor 앞 최근 token이 `override`이면 override context로 본다.
- 현재 line에서 cursor 앞에 `[`가 있고 닫히지 않았으면 attribute context 후보로 본다.
- 괄호 안이고 직전 identifier 뒤 `:` 가능성이 있으면 named argument 후보로 본다.

이 heuristic이 애매하면 broad context로 취급한다. broad context에서 prefix가 짧은
요청은 Roslyn LS에 보내기 전에 막아 large repo와 Agent UX를 보호한다.

## Expensive request와 timeout 정책

completion은 문맥에 따라 비용이 다르다. member access completion은 비교적 좁지만,
빈 prefix의 top-level/general identifier completion은 매우 noisy할 수 있다.

보수적인 초기 정책:

- `get_completions`는 기본적으로 expensive LSP request로 분류한다.
- 추후 smoke 결과가 충분하면 명확한 member access 같은 narrow context만 non-expensive로
  완화할 수 있다.
- timeout은 `hover`보다 길고 `find_references`와 비슷한 10~30초 범위에서 시작한다.
  초기값은 10초 또는 15초가 적당하다.
- `completionItem/resolve`를 추가하면 resolve fan-out도 같은 expensive budget에 포함한다.
- payload size cap을 둔다. documentation/snippet/text edit 포함 시 cap 초과 가능성이
  높으므로 옵션 기본값은 작게 유지한다.

## Workspace 상태와 completeness

기존 read tool과 같은 상태 계약을 따른다.

- `StartingLanguageServer`: 오래 기다리지 않고 `workspace_loading`을 반환한다.
- `WorkspaceWarming`: 가능한 경우 best-effort completion을 실행하되 `partial`로 표시한다.
- `LoadedWithErrors`: 결과를 반환할 수 있으면 반환하되 warning과 partial reason을 유지한다.
- `Ready`: LSP 응답 자체의 complete 여부는 여전히 `unknown`일 수 있다.

completion은 LSP가 workspace-wide completeness를 보장하지 않는다. `Ready` 상태에서도
`serverIncomplete`가 true이거나 Roslyn LS가 completion index completeness를 알려주지
않으면 `completeness: "unknown"`이 자연스럽다.

## 저장되지 않은 변경 한계

현재 `DocumentStateManager` 모델은 repository file을 열고 LSP에 `didOpen`/`didChange`를
보내는 구조지만, caller가 임시 buffer text를 tool input으로 넘기는 overlay model은 없다.
따라서 `get_completions` 초기 버전은 저장된 파일 내용 기준 completion으로 제한한다.

이 한계는 completion에서 특히 크다. Agent가 아직 저장하지 않은 코드를 머릿속으로
가정하고 completion을 물으면 Roslyn LS는 그 코드를 모른다. 해결하려면 별도 후속 설계가
필요하다.

후속 후보:

- `text` 또는 `documentText` overlay 입력을 허용해 임시 buffer를 LSP에 sync한다.
- overlay document version과 didChange lifecycle을 관리한다.
- tool 호출 후 overlay를 닫거나 cache한다.

하지만 이는 state complexity와 stale buffer 위험이 크므로 초기 `get_completions` 범위에는
넣지 않는다.

## Capability와 protocol 고려사항

LSP initialize capabilities에서 completion 관련 지원 범위를 명확히 해야 한다.

- documentation format: `plaintext`, `markdown` 중 무엇을 받을지 결정한다.
- snippet support: 초기에는 `false` 또는 반환 off를 기본으로 둔다.
- resolve support: 서버가 `completionItem/resolve`를 지원하더라도 MCP 쪽 fan-out 제한을
  우선한다.
- trigger characters: Roslyn LS가 제공하는 trigger character를 그대로 신뢰하되, MCP caller
  입력은 optional로 둔다.
- deprecated/tag 정보: 있으면 advisory metadata로 반환할 수 있다.

지원하지 않는 LSP field는 조용히 버리되, mapper test로 안전하게 무시되는지 검증한다.

## 테스트 계획

단위 테스트:

- `CompletionItem[]` 응답 mapping
- `CompletionList` 응답 mapping
- `CompletionList.isIncomplete`와 `serverIncomplete` metadata
- `maxResults` 기본값, hard cap, `truncated`
- `kindFilter` 적용과 `totalUnfilteredKnown`
- invalid kind filter 오류
- unknown completion kind mapping
- root 밖 URI 또는 non-file URI 방어가 필요한 field를 반환하지 않는지 확인
- documentation cap
- snippet off 기본값과 opt-in 동작
- `command`, `additionalTextEdits` drop
- `textEdit`, `insertText` off 기본값과 opt-in advisory 반환
- broad context guard: 짧은 prefix general context 거부
- narrow context guard: member access/new/override에서 prefix 0 허용
- `StartingLanguageServer`, `WorkspaceWarming`, `LoadedWithErrors`, `Ready` metadata
- timeout/cancellation과 expensive request limit
- `completionItem/resolve`를 넣는 경우 fan-out cap과 per-item failure 처리

통합/smoke 테스트:

- 작은 sample solution에서 member access completion
- object creation completion
- override completion이 가능한 fixture
- broad context 짧은 prefix가 제한되는지 확인
- Roslyn LS 미설치 환경 skip 메시지 유지
- real repo smoke에서는 응답 시간, returned count, truncation, `serverIncomplete` 관찰값을
  기록한다.

대형 repo 검증:

- broad context 요청이 Roslyn LS에 무제한으로 전달되지 않는지 확인한다.
- default `maxResults`가 실제 Agent 사용에 충분한지 확인한다.
- `kindFilter`가 반환 noise를 줄이는지 확인한다.
- documentation/resolve 옵션이 latency와 payload에 미치는 영향을 측정한다.

## 구현 단계 제안

1. Roslyn LS completion spike
   - 작은 fixture와 현재 repo에서 `textDocument/completion` 응답 shape, kind, detail,
     `isIncomplete`, trigger behavior를 기록한다.
   - `completionItem/resolve` 필요성을 판단한다.

2. protocol model 추가
   - `CompletionItemKind` enum과 MCP kind 변환 helper를 추가한다.
   - LSP completion response mapper를 unit-test first로 만든다.

3. 최소 `get_completions` tool
   - `file`, `line`, `column`, `maxResults`, `kindFilter`만 받는다.
   - documentation, text edit, snippet은 반환하지 않는다.
   - 기존 read tool metadata와 expensive request limit을 적용한다.

4. broad context guard
   - 저장된 파일 내용에서 prefix와 간단한 lexical context를 추론한다.
   - broad context 짧은 prefix를 막고 user-facing guidance를 반환한다.

5. 옵션 확장
   - `includeDetail` 기본 true.
   - `includeDocumentation` 기본 false.
   - 필요한 경우 제한된 `completionItem/resolve`를 추가한다.
   - `includeTextEdits`, `includeSnippets`는 마지막에 신중히 추가한다.

6. smoke와 tuning
   - 현재 repo, Semantic Kernel, ASP.NET Core 같은 기존 smoke 후보에서 latency와 noise를
     관찰한다.
   - default `maxResults`, broad prefix threshold, timeout, expensive 분류를 조정한다.

## 열어둘 결정

- `get_completions`를 항상 expensive로 둘지, narrow context만 non-expensive로 완화할지.
- broad context 최소 prefix를 2로 둘지 3으로 둘지.
- `serverIncomplete`를 별도 top-level field로 둘지 `reason`에만 반영할지.
- `includeTextEdits`와 `includeSnippets`를 초기 공개 API에 넣을지, 후속 옵션으로 남길지.
- `completionItem/resolve`를 구현할지, documentation 없는 최소 후보만으로 충분한지.
- overlay text sync 모델을 별도 milestone로 둘지.

## 1차 권장 결론

초기 구현은 보수적으로 가는 편이 맞다.

- 전역 4글자 제한은 두지 않는다.
- broad context에만 prefix 2 또는 3 guard를 둔다.
- 기본 반환은 label/kind/detail 중심으로 작게 유지한다.
- documentation, snippet, text edit, resolve는 opt-in 또는 후속으로 둔다.
- `serverIncomplete`, `totalUnfilteredKnown`, `truncated`를 분리해 Agent가 누락 원인을
  잘못 해석하지 않게 한다.
- 저장된 파일 기준 completion이라는 한계를 명확히 노출한다.

이렇게 하면 `get_completions`가 read-only 방향을 벗어나지 않으면서도 Agent가 C# 코드를
수정하기 전 후보 확인에 쓸 수 있는 실용적인 도구가 된다.
