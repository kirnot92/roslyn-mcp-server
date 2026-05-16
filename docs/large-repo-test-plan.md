# Large Repository Test Plan

## 목적

`roslyn-mcp-server`는 대규모 C# repository와 mono-repo에서 사용될 가능성이 높다. 이 테스트 계획은 작은 샘플 솔루션에서는 드러나지 않는 성능, 상태 전이, 결과 제한, 동시성 문제를 검증하기 위한 것이다.

테스트의 목표는 "모든 대형 solution을 완벽히 분석한다"가 아니라, 대규모 환경에서도 MCP tool이 예측 가능하게 동작하고 Agent CLI가 다음 행동을 결정할 수 있는 응답을 받는지 확인하는 것이다.

## 검증 원칙

- 실제 거대 repository를 테스트 fixture로 커밋하지 않는다.
- 파일/디렉터리 수가 많은 synthetic repo를 생성해 scanner와 경로 처리 한계를 검증한다.
- Roslyn LS 통합 테스트는 작은 실제 C# solution으로 유지하되, loading/warming 상태와 best-effort 계약을 별도로 검증한다.
- 긴 테스트와 빠른 테스트를 분리한다.
- 모든 대량 결과 tool은 `maxResults`, `truncated`, `completeness`, `workspaceState`를 검증한다.

## 운영 방식

이 문서는 한 번에 실행하는 단일 테스트 스크립트가 아니라, 구현 단계와 품질 단계에 나누어 적용하는 검증 전략이다. 각 항목은 성격에 따라 fast test, Roslyn LS integration test, opt-in real repo test, stress test로 분리해서 운영한다.

- Fast/unit test 항목은 일반 개발 루프의 `dotnet test`에 포함한다.
- Roslyn LS integration test는 `roslyn-language-server`가 설치된 환경에서만 실행하고, 없으면 명확한 이유와 설치 명령을 남긴 뒤 skip한다.
- 실제 대규모 repository 테스트는 로컬 경로를 환경 변수로 받은 opt-in 테스트로 둔다. 테스트가 직접 clone하지 않는다.
- stress test는 기본 테스트와 CI 성공 조건에서 제외하고, 별도 profile 또는 수동 검증으로 실행한다.

기본 실행 레벨은 다음처럼 나눈다.

```text
Fast:
dotnet test roslyn-mcp-server.sln

Integration:
roslyn-language-server 설치 환경에서 dotnet test roslyn-mcp-server.sln

Real repo opt-in:
ROSLYN_MCP_REAL_REPO_ROSLYN=D:\repos\roslyn
ROSLYN_MCP_REAL_REPO_SDK=D:\repos\sdk
ROSLYN_MCP_REAL_REPO_ASPNETCORE=D:\repos\aspnetcore
dotnet test roslyn-mcp-server.sln --filter RealRepo

Stress:
별도 stress profile 또는 수동 실행
```

## 단계별 액션 플랜

### M2 구현 중

M2 각 단계에서는 해당 tool이 요구하는 fast test만 흡수한다. 이 시점에 실제 대규모 repository 검증 전체를 실행하려고 하지 않는다.

- M2a: document sync, path/URI 변환, position 변환, LRU, 큰 파일 제한, warming metadata를 fast test로 검증한다.
- M2b: definition/reference mapper, references result limit, expensive request limit, timeout/cancellation을 fast test로 검증한다.
- M2c: workspace symbol mapper, query validation, symbol result limit, root 밖 URI 차단, warming 중 empty result metadata를 fast test로 검증한다.
- M2d: diagnostics notification store, cache bound, severity filter, workspace diagnostics result limit을 fast test로 검증한다.

### M2 완료 직후

M2 전체 read-only tool이 들어간 뒤 이 문서를 기준으로 coverage audit을 수행한다. 목적은 실제 대형 repo를 모두 돌리는 것이 아니라, fast test와 integration test가 주요 위험을 커버하는지 확인하는 것이다.

- `dotnet format roslyn-mcp-server.sln --verify-no-changes`
- `dotnet build roslyn-mcp-server.sln`
- `dotnet test roslyn-mcp-server.sln`
- result limit/truncation metadata가 모든 대량 결과 tool에 있는지 확인한다.
- `StartingLanguageServer`, `LspReady`, `WorkspaceWarming`, `Ready`, `Failed` 상태별 tool 동작을 확인한다.
- root 밖 path/URI 차단 테스트가 read tool 전체에 적용되는지 확인한다.
- Roslyn LS 미설치 환경에서 integration test skip 메시지가 명확한지 확인한다.

### M3 시작 전 또는 초반

실제 MCP client에 붙이기 전에 작은/중간 real repo로 smoke test를 수행한다. 이 단계의 성공 기준은 완전한 semantic 정확도가 아니라 hang/crash 없이 Agent CLI가 다음 행동을 결정할 수 있는 응답을 받는 것이다.

권장 후보:

- `PowerShell/PowerShell`
- `microsoft/semantic-kernel`
- `dotnet/msbuild`
- `dotnet/aspnetcore` 중 작은 범위

검증 흐름:

1. MCP client에서 서버를 repo root 기준으로 실행한다.
2. `list_workspaces`가 제한 시간 안에 반환되는지 확인한다.
3. 명시적으로 `load_solution` 또는 `load_project`를 호출한다.
4. `get_workspace_status`가 `LspReady`, `WorkspaceWarming`, `Ready` 중 하나로 진행되는지 확인한다.
5. `document_symbols`, `hover`, `go_to_definition`, `find_references`, `find_symbols`, file-specific `diagnostics`를 작은 범위에서 호출한다.
6. warming 중 결과가 `completeness` metadata와 함께 반환되는지 확인한다.

### M4 품질 강화

Tier 1 실제 대형 repository opt-in 테스트를 본격적으로 실행한다. 이 단계에서 기본 timeout, result limit, warming metadata, diagnostics cache 정책을 실제 결과에 맞춰 조정한다.

- `dotnet/roslyn`
- `dotnet/sdk`
- `dotnet/aspnetcore`

이 테스트는 환경 변수로 repo 경로가 지정된 경우에만 실행한다. clone, restore, workload 설치는 테스트가 자동으로 수행하지 않는다.

### M4 이후 Stress Profile

stress test는 기본 개발 루프와 분리한다. 매우 큰 synthetic repo나 Tier 2 repository를 사용해 crash, OOM, 무제한 응답, pending request leak이 없는지 확인한다.

- synthetic 100,000 files 이상
- 수천 `.csproj`
- 수백 solution/slnx
- 100개 이상 concurrent tool calls
- 수만 diagnostics notification
- `dotnet/runtime`
- `Azure/azure-sdk-for-net`

## 실제 대규모 Repository 후보

실제 repository 테스트는 기본 테스트가 아니라 opt-in으로 둔다. clone 비용, restore 비용, workload 요구사항, 네트워크 상태가 테스트 안정성에 영향을 주기 때문이다.

아래 수치는 2026-05-16에 GitHub API로 default branch tree를 조회한 값이다. GitHub tree API가 `truncated: true`를 반환한 repo는 파일/프로젝트 수가 실제보다 적게 집계될 수 있다.

| Repo | 용도 | GitHub API 관측값 |
| --- | --- | --- |
| `dotnet/roslyn` | 이 프로젝트의 주 타깃에 가장 가까운 dogfood repo. Roslyn LS 자체와 유사한 코드베이스라 navigation/diagnostics 검증에 좋다. | 약 32k files, 30 `.sln`, 3 `.slnx`, 347 `.csproj`, tree not truncated |
| `dotnet/sdk` | 다수 solution/project 후보가 있는 repo. `list_workspaces`, 명시적 `load_solution`, 후보 truncation 테스트에 좋다. | 약 11k files, 95 `.sln`, 55 `.slnx`, 862 `.csproj`, tree not truncated |
| `dotnet/aspnetcore` | 크지만 비교적 현실적인 application/framework repo. warming 중 best-effort navigation 검증에 좋다. | 약 16k files, 1 `.sln`, 1 `.slnx`, 609 `.csproj`, tree not truncated |
| `dotnet/runtime` | 매우 큰 stress 대상. `.slnx`와 project 후보가 많아 scanner/result limit 검증에 좋다. | tree truncated, 최소 48k files, 3 `.sln`, 229 `.slnx`, 3,789 `.csproj` |
| `Azure/azure-sdk-for-net` | 극단적인 package mono-repo. scanner, path handling, result truncation, diagnostics cache pressure 검증에 좋다. | tree truncated, 최소 42k files, 97 `.sln`, 131 `.slnx`, 416 `.csproj` |
| `dotnet/maui` | 큰 mixed workload repo. mobile/workload 의존성 때문에 기본 테스트보다는 환경 준비가 된 머신의 optional test에 적합하다. | 약 25k files, 6 `.sln`, 134 `.csproj`, tree not truncated |
| `PowerShell/PowerShell` | 단일 제품형 C# repo. 규모는 아주 크지 않지만 실제 사용자 repo에 가까운 integration smoke test에 좋다. | 약 2.6k files, 3 `.sln`, 42 `.csproj`, tree not truncated |
| `dotnet/wpf` | Windows desktop workload repo. Windows 전용 path/build 환경 검증에 적합하다. | 약 7.3k files, 11 `.sln`, 92 `.csproj`, tree not truncated |
| `dotnet/winforms` | Windows desktop workload repo. WPF보다 작고 Windows path/casing 검증에 쓸 수 있다. | 약 6k files, 2 `.sln`, 55 `.csproj`, tree not truncated |
| `dotnet/msbuild` | build system 자체 repo. project loading, design-time build 오류 메시지 검증에 좋다. | 약 3k files, 1 `.sln`, 1 `.slnx`, 85 `.csproj`, tree not truncated |
| `microsoft/vstest` | 작은 편이지만 `.slnx` 기반 smoke test에 적합하다. | 약 2.5k files, 2 `.slnx`, 141 `.csproj`, tree not truncated |
| `microsoft/semantic-kernel` | 일반적인 modern C# repo 성격. Agent CLI 사용 시나리오 smoke test에 적합하다. | 약 5.4k files, 2 `.sln`, 1 `.slnx`, 192 `.csproj`, tree not truncated |

### 권장 Tier

Tier 1: 현실적인 기본 real-world 검증

- `dotnet/roslyn`
- `dotnet/sdk`
- `dotnet/aspnetcore`

이 세 repo는 C# 중심이고 규모가 충분하며, tree API가 truncate되지 않아 후보 수 검증에도 안정적이다. 실제 Roslyn LS와 MCP tool 동작을 보는 opt-in integration test의 1차 후보로 둔다.

Tier 2: stress 검증

- `dotnet/runtime`
- `Azure/azure-sdk-for-net`

이 둘은 clone과 탐색 비용이 크다. 기본 integration test에 넣지 않고, scanner limit, result truncation, path handling, diagnostics cache pressure를 검증하는 별도 stress profile로 둔다.

Tier 3: 환경 의존 optional 검증

- `dotnet/maui`
- `dotnet/wpf`
- `dotnet/winforms`
- `PowerShell/PowerShell`
- `dotnet/msbuild`

workload, OS, build tool 설치 상태에 영향을 받을 수 있다. Roslyn LS project loading 실패 메시지와 partial 결과 처리를 검증하는 용도로 유용하지만, 일반 test run의 성공 조건으로 삼지 않는다.

### Repo별 테스트 매핑

| 테스트 목적 | 우선 후보 |
| --- | --- |
| scanner depth/timeout/truncation | `dotnet/runtime`, `Azure/azure-sdk-for-net`, synthetic repo |
| 다중 solution 선택 UX | `dotnet/sdk`, `dotnet/roslyn`, `Azure/azure-sdk-for-net` |
| `.slnx` 처리 | `dotnet/runtime`, `dotnet/sdk`, `microsoft/vstest` |
| warming 중 best-effort navigation | `dotnet/roslyn`, `dotnet/aspnetcore` |
| workspace symbol 대량 결과 | `dotnet/runtime`, `dotnet/sdk`, `Azure/azure-sdk-for-net` |
| diagnostics cache pressure | fake LSP, `dotnet/runtime`, `Azure/azure-sdk-for-net` |
| Windows path/casing/long path | `dotnet/wpf`, `dotnet/winforms`, synthetic repo |
| 일반 Agent CLI smoke test | `microsoft/semantic-kernel`, `PowerShell/PowerShell`, `dotnet/aspnetcore` |

### 실제 Repo 테스트 운영 방식

실제 repo 테스트는 로컬 경로를 입력으로 받는다. 테스트가 직접 clone하지 않는 것을 기본으로 한다.

```text
ROSLYN_MCP_REAL_REPO_ROSLYN=D:\repos\roslyn
ROSLYN_MCP_REAL_REPO_SDK=D:\repos\sdk
ROSLYN_MCP_REAL_REPO_ASPNETCORE=D:\repos\aspnetcore
```

환경 변수가 없으면 해당 테스트는 skip한다. skip 메시지에는 필요한 repo와 권장 clone 명령을 표시한다.

실제 repo 테스트는 다음을 성공 기준으로 삼는다.

- MCP 서버 process가 hang/crash하지 않는다.
- `list_workspaces`가 제한 시간 안에 반환한다.
- 후보가 많은 경우 `truncated` 또는 명시적 선택 요구가 나온다.
- `load_solution` 이후 `LspReady` 또는 `WorkspaceWarming`까지 도달한다.
- warming 중 navigation tool이 가능한 경우 best-effort 결과를 반환하거나 partial metadata를 포함한다.
- 실패하더라도 user-facing 오류로 정리되어 Agent CLI가 다음 행동을 알 수 있다.

실제 repo 테스트는 다음을 성공 기준으로 삼지 않는다.

- 모든 project가 restore/build에 성공할 것
- workspace 전체 diagnostics가 완전할 것
- 모든 cross-project reference가 warming 중 정확히 반환될 것
- 각 repo의 전체 test suite가 통과할 것

### Repo 후보 갱신 절차

후보 목록은 주기적으로 GitHub API로 갱신한다.

확인 항목:

- default branch
- repository size
- tree API truncation 여부
- `.sln` 개수
- `.slnx` 개수
- `.csproj` 개수

tree API가 truncate되는 repo는 정확한 파일 수를 주장하지 않고 "최소 관측값"으로만 기록한다.

## 테스트 범주

### 1. Workspace Scanner Scalability

목표: 대규모 repo에서 `list_workspaces`가 전체 파일 트리를 무제한으로 훑지 않는지 확인한다.

Fixture:

- 임시 디렉터리에 synthetic repo 생성
- 10,000개 이상의 디렉터리
- 1,000개 이상의 `.csproj`
- 100개 이상의 `.sln`/`.slnx`
- 제외 대상 디렉터리 안에 많은 가짜 프로젝트 파일 배치
  - `.git`
  - `bin`
  - `obj`
  - `node_modules`
  - `artifacts`
  - `dist`

검증:

- 기본 scan timeout 안에 반환한다.
- `truncated: true`가 설정된다.
- 후보 개수는 `MaxSolutionCandidates`, `MaxProjectCandidates`를 넘지 않는다.
- 제외 디렉터리 아래 후보는 반환하지 않는다.
- root 바로 아래 및 1단계 하위 solution이 우선적으로 반환된다.
- `refresh: false`는 캐시를 사용한다.
- `refresh: true`는 제한 안에서 재탐색한다.

성공 기준:

- scanner 테스트는 일반 개발 머신에서 5초 이내 완료된다.
- scan 중 메모리 사용량이 파일 수에 선형으로 크게 증가하지 않는다.

### 2. Workspace Selection Ambiguity

목표: 대규모 repo에서 자동 선택이 위험한 경우 명시적 선택을 요구하는지 확인한다.

Fixture:

- solution 후보 2개 이상
- project 후보 다수
- top-level solution과 nested solution 혼재

검증:

- `go_to_definition` 같은 첫 Roslyn tool 호출 시 후보가 여러 개면 `workspace_not_loaded`를 반환한다.
- 오류 응답에 선택 가능한 solution 후보가 제한된 개수로 포함된다.
- `.csproj`가 매우 많으면 project 자동 선택을 하지 않는다.
- `load_solution`에 root 밖 경로를 넘기면 거부한다.
- `load_solution`에 존재하지 않는 경로를 넘기면 사용자 오류를 반환한다.

성공 기준:

- Agent CLI가 오류 응답만 보고 `load_solution`이 필요하다는 것을 알 수 있다.

### 3. Roslyn LS Loading State Contract

목표: solution loading 중 tool 동작이 Agent CLI 관점에서 예측 가능한지 확인한다.

Fixture:

- 작은 실제 C# solution
- Roslyn LS process wrapper를 테스트 double로 교체한 상태 머신 테스트
- 선택적으로 실제 Roslyn LS 통합 테스트

검증:

- `StartingLanguageServer` 상태에서 navigation tool은 queue에 쌓이지 않고 즉시 `workspace_loading`을 반환한다.
- `workspace_loading` 응답에는 `workspaceState`, `retryAfterMs`, 현재 operation이 포함된다.
- `LspReady` 상태에서는 `find_symbols`, `hover`, `go_to_definition`, `diagnostics`가 best-effort로 실행된다.
- `WorkspaceWarming` 상태에서는 결과에 `workspaceState: "WorkspaceWarming"`과 `completeness: "partial"` 또는 `unknown`이 포함된다.
- `workspace/projectInitializationComplete` notification 수신 시 상태가 `Ready`로 전환된다.
- `Failed` 상태에서는 Roslyn tool이 실패 원인과 재시도 안내를 반환한다.

성공 기준:

- loading 중 호출이 장시간 hang되지 않는다.
- CLI가 retry할 수 있는 오류와 partial 결과를 구분할 수 있다.

### 4. Best-Effort Tool Behavior During Warming

목표: workspace가 완전히 준비되지 않아도 읽기 tool이 가능한 결과를 반환하는지 확인한다.

검증 대상:

- `document_symbols`
- `hover`
- `go_to_definition`
- `find_references`
- `find_symbols`
- `diagnostics`

검증:

- `document_symbols`는 warming 중에도 정상 실행을 시도한다.
- `find_symbols` 결과가 비어 있더라도 `completeness`와 `reason`을 포함한다.
- `find_references`는 warming 중 `partial`로 표시된다.
- workspace 전체 `diagnostics`는 `Ready` 전까지 `partial`로 표시된다.
- file-specific `diagnostics`는 해당 파일 diagnostics 수신 여부와 `lastUpdatedAt`을 포함한다.

성공 기준:

- warming 중 결과가 없어도 "없음"과 "아직 불완전함"을 구분할 수 있다.

### 5. Result Limiting And Truncation

목표: 대량 결과가 MCP 응답을 과도하게 키우지 않는지 확인한다.

Fixture:

- 테스트 double LSP client가 10,000개 symbol/reference/diagnostic 결과를 반환

검증:

- `find_symbols` 기본 반환 개수는 기본 `maxResults`를 넘지 않는다.
- 사용자가 `maxResults`를 지정하면 설정된 상한과 서버 최대 상한 중 작은 값을 따른다.
- `find_references` 대량 결과는 `truncated: true`를 포함한다.
- `diagnostics scope: "workspace"`는 기본 제한을 적용한다.
- 결과 metadata에 `totalKnown`, `returned`, `truncated`가 포함된다.
- 너무 큰 payload는 사용자 오류 또는 truncation으로 처리되고 process가 죽지 않는다.

성공 기준:

- 단일 MCP tool 응답 크기가 설정된 최대 payload를 넘지 않는다.
- 대량 결과에서도 메모리 폭증이나 timeout이 없다.

### 6. Document State LRU

목표: 많은 파일을 조회해도 열린 문서 상태가 무제한 증가하지 않는지 확인한다.

Fixture:

- 1,000개 이상의 C# 파일
- `MaxOpenDocuments`를 작은 값으로 설정한 테스트

검증:

- 위치 기반 tool 호출 시 처음 보는 파일은 `didOpen`을 보낸다.
- 파일이 변경되면 `didChange`를 보낸다.
- 열린 문서 수가 `MaxOpenDocuments`를 넘으면 오래된 문서에 `didClose`를 보낸다.
- 매우 큰 파일은 `MaxDocumentBytes` 기준으로 거부된다.
- 거부 응답은 파일 크기와 설정값을 포함한다.

성공 기준:

- 열린 문서 상태 수가 상한을 넘지 않는다.
- LRU eviction이 LSP notification 순서와 함께 검증된다.

### 7. Concurrent Tool Calls

목표: Agent CLI가 병렬로 tool을 호출해도 Roslyn LS와 MCP 서버가 포화되지 않는지 확인한다.

Fixture:

- fake LSP client with controllable delay
- `MaxInFlightLspRequests`를 낮게 설정

검증:

- 전체 in-flight request가 상한을 넘지 않는다.
- `workspace/symbol`, `references`, diagnostics 같은 비싼 method는 별도 낮은 상한을 따른다.
- `load_solution` 중 들어온 navigation tool은 queue에 쌓이지 않고 `workspace_loading`을 반환한다.
- MCP cancellation이 들어오면 LSP `$/cancelRequest`를 전송한다.
- timeout된 요청은 pending dictionary에서 제거된다.

성공 기준:

- 병렬 호출 후 pending request leak이 없다.
- timeout/cancellation 이후에도 다음 요청을 정상 처리한다.

### 8. Diagnostics Cache Pressure

목표: workspace diagnostics가 많은 repo에서 `DiagnosticStore`가 메모리를 과도하게 사용하지 않는지 확인한다.

Fixture:

- fake LSP notification으로 수천 파일의 diagnostics publish
- 파일당 다수 diagnostic

검증:

- file별 diagnostic cache가 상한을 따른다.
- 오래된 상세 diagnostics는 버리고 summary count는 유지한다.
- `diagnostics` 기본 호출은 열린 문서와 최근 diagnostics 중심으로 반환한다.
- `scope: "workspace"`는 `maxResults`와 truncation metadata를 포함한다.
- severity filter가 서버 측에서 적용된다.

성공 기준:

- diagnostic notification 폭주 후에도 memory usage와 응답 크기가 제한된다.

### 9. Large Repo Path Handling

목표: 대규모 Windows repo에서 흔한 긴 경로, 대소문자, symlink 문제를 검증한다.

검증:

- 긴 경로를 root 내부 경로로 정규화한다.
- Windows path 비교는 case-insensitive로 처리한다.
- root 밖 symlink escape를 차단한다.
- 상대 경로 `..` 입력을 정규화 후 차단한다.
- tool 결과는 root 상대 경로로 반환한다.

성공 기준:

- root escape가 불가능하다.
- 같은 파일이 path casing 차이로 중복 open되지 않는다.

## 테스트 레벨

### Fast Tests

일반 `dotnet test`에서 실행한다.

- scanner synthetic tests
- path guard tests
- result truncation tests
- document LRU tests
- fake LSP concurrency tests
- diagnostics cache tests

목표 시간:

- 전체 fast test 30초 이내

### Integration Tests

Roslyn LS가 설치된 환경에서만 실행한다.

사전 조건:

```text
dotnet tool install --global roslyn-language-server --prerelease
```

검증:

- 작은 sample solution 로드
- `workspace/projectInitializationComplete` notification 수신
- warming/ready 상태 전이
- 실제 `document_symbols`, `hover`, `go_to_definition`, `find_references`, `diagnostics`

Roslyn LS가 없으면 skip하고 설치 명령을 출력한다.

### Stress Tests

기본 test run에서는 제외한다.

목표:

- synthetic repo 100,000 files
- 5,000 projects
- 500 solutions
- 100 concurrent tool calls
- 수만 diagnostics notification

성공 기준:

- process crash 없음
- OOM 없음
- 설정된 timeout/truncation 동작
- 테스트 종료 후 child process 정리

## 측정 항목

각 대규모 테스트는 가능한 경우 다음 값을 기록한다.

- scan elapsed time
- candidate count
- `truncated`
- peak open document count
- max in-flight LSP requests
- timeout count
- cancellation count
- response payload bytes
- diagnostic cache entry count
- state transition timestamps

이 값들은 테스트 실패 조건뿐 아니라 기본값 조정에도 사용한다.

## 초기 기본값 후보

초기값은 보수적으로 시작하고 실제 테스트 결과로 조정한다.

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

## 구현 우선순위

1. scanner synthetic tests
2. path guard tests
3. workspace state machine tests
4. loading 중 `workspace_loading`/best-effort contract tests
5. result truncation tests
6. document LRU tests
7. fake LSP concurrency and cancellation tests
8. diagnostics cache pressure tests
9. actual Roslyn LS integration tests
10. opt-in stress tests

## 열린 질문

- `workspace/projectInitializationComplete`가 모든 실행 모드에서 안정적으로 오는지 확인 필요
- Roslyn LS가 solution/project 명시 로드에 어떤 command 또는 initialization option을 요구하는지 확인 필요
- synthetic stress test를 어느 정도 규모까지 기본 자동 테스트에 포함할지 결정 필요
- default timeout과 maxResults 값을 실제 대형 repo 실험으로 조정 필요

## Repo 링크

- `dotnet/roslyn`: https://github.com/dotnet/roslyn
- `dotnet/runtime`: https://github.com/dotnet/runtime
- `dotnet/aspnetcore`: https://github.com/dotnet/aspnetcore
- `dotnet/sdk`: https://github.com/dotnet/sdk
- `dotnet/maui`: https://github.com/dotnet/maui
- `dotnet/wpf`: https://github.com/dotnet/wpf
- `dotnet/winforms`: https://github.com/dotnet/winforms
- `dotnet/msbuild`: https://github.com/dotnet/msbuild
- `microsoft/vstest`: https://github.com/microsoft/vstest
- `PowerShell/PowerShell`: https://github.com/PowerShell/PowerShell
- `Azure/azure-sdk-for-net`: https://github.com/Azure/azure-sdk-for-net
- `microsoft/semantic-kernel`: https://github.com/microsoft/semantic-kernel
