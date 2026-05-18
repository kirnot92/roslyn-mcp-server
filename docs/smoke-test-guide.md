# Smoke Test Guide

이 문서는 실제 repository 대상 MCP stdio smoke test를 짧게 실행하고 기록하기 위한 가이드다. 오래된 상세 결과 기록은 `docs/archive/smoke-tests/`에 보관한다.

## 목적

Smoke test의 성공 기준은 semantic 결과가 완벽한지가 아니라, 실제 repo에서 서버가 hang/crash 없이 응답하고 Agent CLI가 다음 행동을 판단할 metadata를 받는지 확인하는 것이다.

확인할 것:

- `list_workspaces`가 제한 시간 안에 반환되는지
- `load_solution` 후 `WorkspaceWarming`, `Ready`, `LoadedWithErrors`, `Failed` 중 명확한 상태가 나오는지
- read tool이 partial/unknown metadata와 함께 bounded result를 반환하는지
- diagnostics가 현재 알려진 publish diagnostics만 반환하고 무제한 계산을 시도하지 않는지

## 위치

Committed smoke driver scripts:

```text
scripts/smoke-tests/
```

기본 real repo clone 위치와 산출물 위치:

```text
.local/real-repos/
.local/*.json
.local/*.log
```

`.local/` 아래의 clone, raw output, log는 commit하지 않는다.

## 준비

필수 도구:

```text
dotnet tool install --global roslyn-language-server --prerelease
```

기본 clone 위치:

```text
.local/real-repos/PowerShell
.local/real-repos/semantic-kernel
.local/real-repos/aspnetcore
```

다른 위치의 clone을 쓰려면 환경 변수를 지정한다.

```powershell
$env:ROSLYN_MCP_REAL_REPOS_DIR = "<clone-parent>"
$env:ROSLYN_MCP_POWERSHELL_ROOT = "<PowerShell-repo>"
$env:ROSLYN_MCP_SEMANTIC_KERNEL_ROOT = "<semantic-kernel-repo>"
$env:ROSLYN_MCP_ASPNETCORE_ROOT = "<aspnetcore-repo>"
```

## 빠른 실행

PowerShell:

```powershell
python scripts/smoke-tests/mcp_powershell_smoke.py
```

Semantic Kernel:

```powershell
python scripts/smoke-tests/mcp_semantic_kernel_smoke.py
```

ASP.NET Core:

```powershell
python scripts/smoke-tests/mcp_aspnetcore_smoke.py
```

긴 warmup 또는 ramp 확인:

```powershell
python scripts/smoke-tests/mcp_powershell_wait10m.py
python scripts/smoke-tests/mcp_aspnetcore_long_warmup.py
python scripts/smoke-tests/mcp_aspnetcore_symbol_ramp.py
python scripts/smoke-tests/mcp_aspnetcore_gotodef_ramp.py
python scripts/smoke-tests/mcp_powershell_gotodef_ramp.py
```

## 유용한 옵션

```powershell
$env:ROSLYN_MCP_SMOKE_EXTRA_ARGS = "--scan-timeout 10"
$env:ASPNETCORE_SMOKE_WARMUP_SECONDS = "180"
$env:ASPNETCORE_LONG_WARMUP_SECONDS = "600"
$env:ASPNETCORE_LONG_POLL_SECONDS = "60"
$env:ASPNETCORE_SYMBOL_CHECKPOINTS = "180,300,600"
$env:ASPNETCORE_SYMBOL_RAMP_CHECKPOINTS = "0,10,20,30,40,50,60,70,80,90,100,110,120,130,140,150,160,170,180"
$env:ASPNETCORE_GOTODEF_CHECKPOINTS = "0,5,10,15,20,30,45,60,90,120,180,240,300"
$env:POWERSHELL_GOTODEF_CHECKPOINTS = "0,5,10,15,20,30,45,60,90,120,180,240"
```

## 기록 방식

새 smoke 결과를 문서로 남길 때는 raw output을 커밋하지 말고, 요약 결과만 남긴다. 장기 보존용 상세 기록은 필요할 때 `docs/archive/smoke-tests/`에 추가한다.

권장 형식:

- Environment
- Server Validation
- Workspace Discovery
- Tool Results 또는 Retest Summary
- Findings: blockers, issues, observations
- Recommendation

절대경로 대신 프로젝트 루트 기준 상대경로를 쓴다. 예: `.local/real-repos/aspnetcore`, `scripts/smoke-tests/mcp_aspnetcore_smoke.py`.

기본 검증 명령:

```powershell
dotnet format roslyn-mcp-server.sln --verify-no-changes
dotnet build roslyn-mcp-server.sln -p:UseAppHost=false -p:OutDir=.local\build-out\
dotnet test tests\RoslynMcpServer.Tests\RoslynMcpServer.Tests.csproj -p:UseAppHost=false -p:OutDir=.local\test-out\
```
