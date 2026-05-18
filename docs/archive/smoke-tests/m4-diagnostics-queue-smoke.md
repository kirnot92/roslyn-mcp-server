# M4 Diagnostics Queue Smoke Test

## Environment

- Date: 2026-05-17 (Asia/Seoul)
- roslyn-mcp-server commit: `37bdba2d638592b227af991bdfa588d9ce28ce12`
- Semantic Kernel commit: `2fb749e4bb0da637ca26be0442930a5ad84f54f3`
- ASP.NET Core commit: `93a1b5295d92954d46e26f2bbb3abde15f332a4b`
- OS: Microsoft Windows NT 10.0.19045.0
- dotnet: SDK `10.0.203`, host `10.0.7`
- roslyn-language-server: `5.8.0-1.26262.10+036e7a58b9d4348a62b6854544274551ae17ae8c`
- MCP client methods:
  - `scripts/smoke-tests/mcp_semantic_kernel_smoke.py`
  - `scripts/smoke-tests/mcp_aspnetcore_long_warmup.py`
- Real repo roots were supplied through `ROSLYN_MCP_REAL_REPOS_DIR`; raw output and logs were written under `.local/`.

## Server Validation

- build: Passed, `dotnet build src/RoslynMcpServer/RoslynMcpServer.csproj --no-restore`
- Note: one ASP.NET Core attempt exited before initialize because a previous `roslyn-mcp-server.exe` process still held the Debug executable. After stopping that stale process, build and rerun succeeded.

## Semantic Kernel Smoke

- Raw output: `.local/semantic-kernel-smoke-raw.json`
- Server log: `.local/semantic-kernel-smoke.log`
- Selected workspace: `dotnet/SK-dotnet.slnx`
- Discovery: 3 solutions, 192 projects, not truncated

| Tool | Result | Elapsed | Count | Workspace State | Completeness | Notes |
| --- | --- | ---: | ---: | --- | --- | --- |
| list_workspaces | OK | 0.391s | 195 |  |  | default scan options |
| load_solution | OK | 0.437s | 0 | WorkspaceWarming |  | loaded `dotnet/SK-dotnet.slnx` |
| get_workspace_status | OK | 0.031s | 0 | WorkspaceWarming |  | immediate |
| get_workspace_status | OK | 0.016s | 0 | WorkspaceWarming |  | poll +3s |
| get_workspace_status | OK | 0.016s | 0 | WorkspaceWarming |  | poll +10s |
| document_symbols | OK | 0.890s | 40 | WorkspaceWarming | partial | `dotnet/src/SemanticKernel.Abstractions/Kernel.cs` |
| hover | OK | 0.469s | 0 | WorkspaceWarming | partial | `Kernel` class |
| go_to_definition | OK | 0.047s | 1 | WorkspaceWarming | partial | `KernelBuilder` |
| find_references | OK | 2.328s | 20 | WorkspaceWarming | partial | `totalKnown: 1708`, `returned: 20` |
| find_symbols | OK | 1.859s | 72 | WorkspaceWarming | partial | query `KernelBuilder` |
| diagnostics(file) | OK | 0.032s | 0 | WorkspaceWarming | unknown | no publish diagnostics for file yet |
| diagnostics(workspace) | OK | 0.015s | 0 | WorkspaceWarming | partial | currently processed diagnostics only |

Diagnostics queue status at immediate, +3s, and +10s:

| Capacity | Pending | Processed | Dropped | Stale | Overflow Policy |
| ---: | ---: | ---: | ---: | ---: | --- |
| 1024 | 0 | 0 | 0 | 0 | `drop_newest_when_full` |

## ASP.NET Core 60s Warmup Smoke

- Raw output: `.local/aspnetcore-long-warmup-20260517-1710-diagnostics-queue-raw.json`
- Server log: `.local/aspnetcore-long-warmup-20260517-1710-diagnostics-queue.log`
- stderr log: `.local/aspnetcore-long-warmup-20260517-1710-diagnostics-queue-stderr.log`
- Roslyn LS log dir: `.local/aspnetcore-ls-20260517-1710-diagnostics-queue`
- Selected workspace: `AspNetCore.slnx`
- Discovery: 2 solutions, 609 projects, not truncated

| Warmup | Tool | Result | Elapsed | Count | Workspace State | Completeness | Notes |
| ---: | --- | --- | ---: | ---: | --- | --- | --- |
| - | list_workspaces | OK | 0.891s | 611 |  |  | default scan options |
| - | load_solution | OK | 0.437s | 0 | WorkspaceWarming |  | loaded `AspNetCore.slnx` |
| 0s | get_workspace_status | OK | 0.031s | 0 | WorkspaceWarming |  | queue stats available |
| 20s | get_workspace_status | OK | 0.016s | 0 | WorkspaceWarming |  | queue stats unchanged |
| 20s | find_symbols | OK | 1.953s | 0 | WorkspaceWarming | partial | query `HttpContext` |
| 40s | get_workspace_status | OK | 0.015s | 0 | WorkspaceWarming |  | queue stats unchanged |
| 60s | get_workspace_status | OK | 0.016s | 0 | WorkspaceWarming |  | queue stats unchanged |
| 60s | find_symbols | OK | 1.969s | 0 | WorkspaceWarming | partial | query `HttpContext` |
| 60s | document_symbols | OK | 3.047s | 33 | WorkspaceWarming | partial | `src/Http/Http.Abstractions/src/HttpContext.cs` |
| 60s | find_references | OK | 22.281s | 3 | WorkspaceWarming | partial | `HttpContext`, `maxResults: 20` |

Diagnostics queue status at load, 0s, 20s, 40s, and 60s:

| Capacity | Pending | Processed | Dropped | Stale | Overflow Policy |
| ---: | ---: | ---: | ---: | ---: | --- |
| 1024 | 0 | 0 | 0 | 0 | `drop_newest_when_full` |

## Findings

- Blockers:
  - None observed after clearing the stale local server process.
- Issues:
  - No real `textDocument/publishDiagnostics` traffic arrived during these short Semantic Kernel and ASP.NET Core observation windows, so this smoke confirms status exposure and bounded behavior at idle, but does not yet stress the background diagnostics queue.
- Observations:
  - `get_workspace_status` exposes the new diagnostics queue fields in both repos.
  - The queue capacity is `1024` and the overflow policy is `drop_newest_when_full`; under pressure it drops incoming notifications when the queue is full and keeps existing pending notifications.
  - `pending`, `processed`, `dropped`, and `stale` stayed at 0 in both runs.
  - Large-repo read tools remained responsive while workspaces stayed in `WorkspaceWarming`.
  - ASP.NET Core `find_references` on `HttpContext` completed in 22.281s with partial metadata rather than hanging.

## Recommendation

- Keep the default diagnostics queue capacity at 1024 for now. This run does not provide evidence that it is too small or too large.
- For queue pressure tuning, add a targeted diagnostics publish smoke that reliably produces `textDocument/publishDiagnostics` traffic, or run a longer real-repo scenario that waits for diagnostics publication and records `processed`, `dropped`, and `stale` counters.
