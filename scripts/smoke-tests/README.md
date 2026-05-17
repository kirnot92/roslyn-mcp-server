# Smoke Test Scripts

These scripts are committed smoke drivers for real-repo MCP stdio checks.

Run them from anywhere; each script resolves the project root from its own path.
By default, cloned real repositories are expected under:

```text
.local/real-repos/
```

Default repo paths:

- `.local/real-repos/PowerShell`
- `.local/real-repos/semantic-kernel`
- `.local/real-repos/aspnetcore`

Useful overrides if your clones live somewhere else:

```powershell
$env:ROSLYN_MCP_REAL_REPOS_DIR = "<clone-parent>"
$env:ROSLYN_MCP_POWERSHELL_ROOT = "<PowerShell-repo>"
$env:ROSLYN_MCP_SEMANTIC_KERNEL_ROOT = "<semantic-kernel-repo>"
$env:ROSLYN_MCP_ASPNETCORE_ROOT = "<aspnetcore-repo>"
```

Logs, raw JSON output, Roslyn LS logs, and cloned repositories should stay under
`.local/` and should not be committed.

Committed drivers:

- `mcp_powershell_smoke.py`
- `mcp_powershell_wait10m.py`
- `mcp_powershell_gotodef_ramp.py`
- `mcp_semantic_kernel_smoke.py`
- `mcp_aspnetcore_smoke.py`
- `mcp_aspnetcore_long_warmup.py`
- `mcp_aspnetcore_symbol_ramp.py`
- `mcp_aspnetcore_gotodef_ramp.py`

Useful timing overrides:

```powershell
$env:ASPNETCORE_SMOKE_WARMUP_SECONDS = "180"
$env:ASPNETCORE_LONG_WARMUP_SECONDS = "600"
$env:ASPNETCORE_LONG_POLL_SECONDS = "60"
$env:ASPNETCORE_SYMBOL_CHECKPOINTS = "180,300,600"
$env:ASPNETCORE_SYMBOL_RAMP_CHECKPOINTS = "0,10,20,30,40,50,60,70,80,90,100,110,120,130,140,150,160,170,180"
$env:ASPNETCORE_GOTODEF_CHECKPOINTS = "0,5,10,15,20,30,45,60,90,120,180,240,300"
$env:POWERSHELL_GOTODEF_CHECKPOINTS = "0,5,10,15,20,30,45,60,90,120,180,240"
```
