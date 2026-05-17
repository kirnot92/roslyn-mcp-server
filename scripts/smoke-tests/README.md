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
