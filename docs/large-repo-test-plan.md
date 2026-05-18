# Large Repository Test Plan

## 목적

`roslyn-mcp-server`는 대규모 C# repository와 mono-repo에서 사용될 가능성이 높다. 이 문서는 작은 샘플 solution에서는 드러나지 않는 성능, 상태 전이, 결과 제한, 동시성 문제를 검증하기 위한 현재 테스트 전략이다.

목표는 모든 대형 solution을 완벽히 분석하는 것이 아니다. 목표는 대규모 환경에서도 MCP tool이 예측 가능하게 응답하고, Agent CLI가 다음 행동을 결정할 수 있는 metadata를 받는지 확인하는 것이다.

## 검증 원칙

- 실제 거대 repository를 테스트 fixture로 커밋하지 않는다.
- clone, restore, workload 설치는 테스트가 자동 수행하지 않는다.
- 빠른 unit test와 opt-in real repo 검증을 분리한다.
- 모든 대량 결과 tool은 result cap과 `truncated`, `totalKnown`, `returned`, `workspaceState`, `completeness` metadata를 검증한다.
- Warming 상태는 실패가 아니라 정상 상태로 본다.
- Semantic 정확도보다 hang/crash 방지, bounded response, 명확한 상태 보고를 먼저 본다.

## 테스트 레벨

Fast:

```powershell
dotnet test tests\RoslynMcpServer.Tests\RoslynMcpServer.Tests.csproj
```

일반 개발 루프에 포함한다. Scanner, path guard, CLI parsing, LSP framing, mapper, diagnostics queue, result metadata를 검증한다.

Integration:

```powershell
dotnet test tests\RoslynMcpServer.Tests\RoslynMcpServer.Tests.csproj
```

`roslyn-language-server`가 설치된 환경에서만 Roslyn LS integration test가 실행된다. 없으면 설치 명령과 skip reason을 남긴다.

Real repo opt-in:

```powershell
$env:ROSLYN_MCP_REAL_REPOS_DIR = ".local\real-repos"
python scripts/smoke-tests/mcp_powershell_smoke.py
python scripts/smoke-tests/mcp_semantic_kernel_smoke.py
python scripts/smoke-tests/mcp_aspnetcore_smoke.py
```

실제 repo smoke는 수동 또는 opt-in으로 실행한다. Raw output과 log는 `.local/` 아래에 두고 commit하지 않는다.

Stress:

```text
별도 stress profile 또는 수동 검증
```

수만 파일, 수천 project, 과도한 diagnostics notification, 높은 concurrent call 같은 조건을 기본 test 성공 조건에서 분리한다.

## 기본 검증 명령

Windows에서 실행 중인 apphost가 build output을 잠글 수 있으므로, local output directory를 사용한 검증을 권장한다.

```powershell
dotnet format roslyn-mcp-server.sln --verify-no-changes
dotnet build roslyn-mcp-server.sln -p:UseAppHost=false -p:OutDir=.local\build-out\
dotnet test tests\RoslynMcpServer.Tests\RoslynMcpServer.Tests.csproj -p:UseAppHost=false -p:OutDir=.local\test-out\
```

## 실제 Repository 후보

Tier 1:

- `PowerShell/PowerShell`: 중간 규모, Windows 개발 환경에서 접근성이 좋고 smoke 반복에 적합
- `microsoft/semantic-kernel`: 일반적인 SDK/library 구조와 여러 project를 포함
- `dotnet/aspnetcore`: 대형 solution, warming과 result cap 검증에 적합

Tier 2:

- `dotnet/roslyn`: Roslyn LS 자체와 가까운 대형 C# repo
- `dotnet/sdk`: SDK/workload/global.json 영향이 크므로 startup/load warning 검증에 적합
- `dotnet/runtime`: 매우 큰 repo, stress 성격
- `Azure/azure-sdk-for-net`: 많은 project와 package-style 구조, scanner와 candidate cap 검증에 적합

Tier 2는 기본 smoke 반복 대상이 아니라 tuning이나 stress 검증 시 선택한다.

## 주요 Scenario

Workspace scanner:

- 많은 directory와 file이 있어도 `--scan-timeout` 안에 반환한다.
- `--scan-max-depth`를 넘는 후보는 탐색하지 않는다.
- candidate cap에 걸리면 `Truncated`와 reason을 반환한다.
- git repository에서는 git pathspec scan을 우선 활용한다.

Workspace selection:

- 여러 `.sln`/`.slnx`가 있으면 자동으로 임의 선택하지 않는다.
- `--load-solution`은 exact root-relative path 또는 root 내부 absolute path만 허용한다.
- `--load-solution Foo.sln`이 root 바로 아래에 없으면 하위 폴더를 검색하지 않고 실패한다.
- 단일 후보만 있는 repo에서는 첫 read tool 호출 auto-load가 가능하다.

Loading and warming:

- `StartingLanguageServer` 상태에서 read tool은 오래 blocking하지 않고 `workspace_loading`을 반환한다.
- `WorkspaceWarming` 상태에서 가능한 read tool은 partial/unknown metadata와 함께 best-effort 결과를 반환한다.
- `LoadedWithErrors`는 실패가 아니라 warnings가 있는 usable state로 취급한다.
- `get_workspace_status`가 warnings와 last LSP response timestamp를 제공한다.

Result limiting:

- `find_references`, `find_symbols`, `document_symbols`, `diagnostics`, hierarchy tool은 result cap을 지킨다.
- `truncated`와 `returned`가 실제 반환량과 일치한다.
- `find_symbols`의 `kindFilter`, `matchMode`, `includePathPrefixes` 적용 뒤 metadata가 filter 전/후 수를 구분한다.
- `get_call_hierarchy`와 `get_type_hierarchy`는 recursive unbounded traversal을 하지 않는다.

Document state:

- root 밖 path와 URI를 차단한다.
- 큰 파일은 `--max-document-bytes`를 넘으면 열지 않는다.
- open document 수는 `--max-open-documents`를 넘지 않는다.
- LRU eviction 시 didClose가 필요하면 정상 전송한다.

Diagnostics:

- `publishDiagnostics` notification handler가 read loop를 오래 붙잡지 않는다.
- bounded queue overflow 시 newest notification을 drop하고 count를 올린다.
- workspace reload 뒤 이전 generation notification은 stale로 집계한다.
- `diagnostics`는 현재 처리된 cache만 조회하며 full build처럼 동작하지 않는다.

Concurrency:

- `--max-in-flight-lsp-requests`가 전체 request 수를 제한한다.
- `--max-expensive-lsp-requests`가 references/symbols/hierarchy 같은 비싼 요청을 제한한다.
- timeout/cancellation 이후 pending request가 leak되지 않는다.

Path handling:

- Windows backslash, slash, case-insensitive path 비교를 검증한다.
- URI decode, space 포함 path, root prefix collision을 검증한다.
- 모든 결과 path는 root-relative 형태로 agent가 읽기 쉽게 반환한다.

## 측정 항목

수동 large repo 검증 시 기록할 항목:

- repo name, commit, OS, .NET SDK, Roslyn LS version
- server args
- workspace candidate count and truncation
- load target and load state transitions
- time to first `WorkspaceWarming`
- time to `Ready` or `LoadedWithErrors`, if reached
- first useful response time for common read tools
- warnings count and representative warning text
- diagnostics queue pending/processed/dropped/stale
- max memory if 쉽게 측정 가능할 때
- observed blockers, issues, recommendations

## 기본값 Tuning 후보

현재 기본값:

- `--scan-max-depth`: 6
- `--scan-timeout`: 3초
- `--max-solution-candidates`: 100
- `--max-project-candidates`: 1000
- `--max-open-documents`: 200
- `--max-document-bytes`: 2 MiB
- `--max-in-flight-lsp-requests`: 16
- `--max-expensive-lsp-requests`: 4
- `--startup-timeout`: 60초

Tuning 판단 기준:

- 정상적인 대형 repo에서 `list_workspaces`가 너무 자주 truncated 되는가
- startup timeout이 유효한 실패와 느린 warming을 구분하는가
- expensive request limit이 interactive MCP 사용을 과도하게 막는가
- diagnostics queue capacity가 burst를 견디되 memory를 과도하게 쓰지 않는가
- result cap이 agent가 다음 행동을 정하기에 충분한가

최근 tuning 근거:

- 2026-05-18 MAUI `Microsoft.Maui.sln` 기준 기본 scanner 설정은 6 solution, 134 project를 0.157초에 찾았고 truncated `false`였다.
- 같은 MAUI repo에서 기본 `--max-expensive-lsp-requests 2`는 병렬 `find_symbols` 4개 중 2개를 거절했다.
- `--max-expensive-lsp-requests 4` 후보는 동일 병렬 요청 4개를 모두 처리했고 각 요청은 약 2.1초에 완료됐다.
- 이 결과를 반영해 기본 expensive LSP request 상한은 4로 조정한다.

## Smoke 기록 위치

Smoke driver는 `scripts/smoke-tests/`에 둔다.

과거 상세 결과:

- `docs/archive/smoke-tests/m2-powershell-smoke.md`
- `docs/archive/smoke-tests/m2-semantic-kernel-smoke.md`
- `docs/archive/smoke-tests/m2-aspnetcore-smoke.md`
- `docs/archive/smoke-tests/m4-diagnostics-queue-smoke.md`
- `docs/archive/smoke-tests/m5-codex-mcp-usage-smoke.md`

새 raw output은 `.local/` 아래에 두고 commit하지 않는다. 장기 보존이 필요한 결과만 요약해서 archive에 추가한다.

## 열린 질문

- Tier 1 실제 repo 검증 뒤 scan/result/default 값 조정이 필요한가
- 대형 solution startup에서 최소로 노출해야 할 observability는 무엇인가
- Roslyn LS crash/restart를 자동화할지, 명확한 failure state와 수동 reload로 둘지
- `solution_overview`가 large repo에서 유용한 read-only context인지
