# M2 ASP.NET Core Smoke Test

## Environment
- Date: 2026-05-17 (Asia/Seoul)
- roslyn-mcp-server commit: `08ddd3f056ffa7fd9d0a8542d12c53259aa2816b`
- aspnetcore commit: `93a1b5295d92954d46e26f2bbb3abde15f332a4b`
- OS: Microsoft Windows NT 10.0.19045.0
- dotnet: SDK `10.0.203`, host `10.0.7`
- roslyn-language-server: `5.8.0-1.26262.10+036e7a58b9d4348a62b6854544274551ae17ae8c`
- MCP client method: temporary newline-delimited JSON-RPC stdio client script in `.local`; server command used default scan options with `--root D:\Workspace\real-repos\aspnetcore`

## Server Validation
- format: Passed, `dotnet format roslyn-mcp-server.sln --verify-no-changes`
- build: Passed, `dotnet build roslyn-mcp-server.sln`
- test: Passed, `dotnet test roslyn-mcp-server.sln` - 97 passed, 1 skipped

## Workspace Discovery
- elapsed: MCP wall time 0.203s; server scan elapsed `00:00:00.1531056`
- solutions: 2 returned (`.slnx`/`.sln`; `.slnf` files are not scanner candidates)
- projects: 500 returned, local repo contains 609 `.csproj` files
- truncated: true
- truncationReason: `project_candidate_limit`
- selected workspace: `AspNetCore.slnx`

`AspNetCore.slnx` was selected because it is the top-level repository solution. The other returned solution candidate was a nested template sample solution: `src/ProjectTemplates/Web.ProjectTemplates/content/BlazorWeb-CSharp/BlazorWebCSharp.1.sln`.

## Tool Results
Test file: `src/Http/Http.Abstractions/src/HttpContext.cs`, selected because it is a small, central ASP.NET Core abstraction file with visible `HttpContext`, `HttpRequest`, and member symbols and is not generated.

| Tool | Result | Elapsed | Count | Workspace State | Completeness | Truncated | Notes |
| --- | --- | ---: | ---: | --- | --- | --- | --- |
| list_workspaces | OK | 0.203s | 502 |  |  | true | 2 solutions, 500 projects; `project_candidate_limit` |
| load_solution | OK | 0.445s | 0 | WorkspaceWarming |  |  | Loaded `AspNetCore.slnx`; language server running |
| get_workspace_status | OK | 0.020s | 0 | WorkspaceWarming |  |  | Immediate, +3s, and +10s polls stayed `WorkspaceWarming` |
| document_symbols | OK | 2.790s | 33 | WorkspaceWarming | partial | false | `totalKnown: 33`, `returned: 33` |
| hover | OK | 2.087s | 0 | WorkspaceWarming | partial | false | Returned hover text for `HttpContext` |
| go_to_definition | OK | 8.032s | 0 | WorkspaceWarming | partial | false | `HttpRequest`; no locations, no error |
| find_references | OK | 8.131s | 3 | WorkspaceWarming | partial | false | `HttpContext`, `includeDeclaration: true`, `maxResults: 20`; `totalKnown: 3`, `returned: 3` |
| find_symbols | OK | 5.572s | 0 | WorkspaceWarming | partial | false | Query `HttpContext`, `maxResults: 20`; empty result included incomplete-index reason |
| diagnostics(file) | OK | 0.021s | 0 | WorkspaceWarming | unknown | false | No publish diagnostics received for file yet; `totalKnown: 0`, `returned: 0`, reason present |
| diagnostics(workspace) | OK | 0.020s | 0 | WorkspaceWarming | partial | false | `scope: workspace`, `maxResults: 50`; current known diagnostics only |

## Findings
- Blockers:
  - None observed. The MCP server stayed responsive and completed all requested read-only tool calls.
- Issues:
  - `list_workspaces` hit the default project candidate limit on aspnetcore. This is expected large-repo behavior and still returned the top-level `AspNetCore.slnx`, but client UX should surface `project_candidate_limit` clearly.
  - `get_workspace_status` remained `WorkspaceWarming` through the explicit polling window. Read tools still worked with partial/unknown metadata.
  - `go_to_definition` for `HttpRequest` returned no locations, and `find_symbols("HttpContext")` returned no symbols while warming. Both responses included partial metadata instead of hanging or failing.
- Observations:
  - `find_references` returned bounded results with `totalKnown`, `returned`, and `truncated` metadata.
  - Diagnostics returned only currently known diagnostics and did not attempt unbounded workspace computation.
  - Closing stdin shut down the stdio server cleanly with exit code 0 when the client allowed up to 30 seconds for shutdown.

## Recommendation
- Can proceed to M3 docs/client usability: Yes for this smoke target.
- Need fixes before broader large-repo testing: No ASP.NET Core-specific blocker found. Keep `project_candidate_limit` and warming-state guidance visible in client docs.

## 30-Second Warmup Retest
- Date: 2026-05-17 (Asia/Seoul)
- MCP client method: same temporary stdio client script, but waited 30 seconds after `load_solution` before invoking read tools.
- Result: `get_workspace_status` still reported `WorkspaceWarming` after the 30-second wait.
- Semantic difference: no material improvement for the selected positions. `go_to_definition(HttpRequest)` still returned 0 locations, and `find_symbols("HttpContext")` still returned 0 items with partial/incomplete-index metadata.

Comparison against the original short-wait run:

| Tool | Short-Wait Result | Short-Wait Elapsed | 30s-Wait Result | 30s-Wait Elapsed | Difference |
| --- | --- | ---: | --- | ---: | --- |
| get_workspace_status | OK, `WorkspaceWarming` | 0.020s | OK, `WorkspaceWarming` | 0.021s | No state change after 30s |
| document_symbols | OK, 33 items | 2.790s | OK, 33 items | 3.052s | Same result count and metadata |
| hover | OK, partial | 2.087s | OK, partial | 5.062s | Still partial; one prior 30s-wait run hit `request_timeout` for hover at 10.040s |
| go_to_definition | OK, 0 items | 8.032s | OK, 0 items | 4.054s | No semantic improvement |
| find_references | OK, 3 items | 8.131s | OK, 3 items | 9.630s | Same result count and metadata |
| find_symbols | OK, 0 items | 5.572s | OK, 0 items | 2.015s | No semantic improvement |
| diagnostics(file) | OK, 0 items | 0.021s | OK, 0 items | 0.020s | Same currently-known diagnostics behavior |
| diagnostics(workspace) | OK, 0 items | 0.020s | OK, 0 items | 0.021s | Same currently-known diagnostics behavior |

Warmup retest findings:

- Waiting 30 seconds did not move aspnetcore from `WorkspaceWarming` to `Ready` for this session.
- The `go_to_definition` no-location result should remain classified as a successful tool execution with weak semantic usefulness, not as a transport/tool failure.
- A transient `hover` timeout occurred in one 30-second-wait run: `request_timeout`, `LSP request timed out: textDocument/hover`. A repeated 30-second-wait run succeeded and the server remained usable afterward, so this is an issue to watch rather than a confirmed blocker.

## 3-Minute Warmup Retest
- Date: 2026-05-17 (Asia/Seoul)
- roslyn-mcp-server commit: `893768452f534fe84ad5864f4ec6b9867dee029f`
- aspnetcore commit: `93a1b5295d92954d46e26f2bbb3abde15f332a4b`
- MCP client method: same temporary stdio client script with `ASPNETCORE_SMOKE_WARMUP_SECONDS=180`.
- Result: `get_workspace_status` still reported `WorkspaceWarming` after the 180-second wait.
- Workspace discovery note: after increasing the default project candidate limit to 1000, default `list_workspaces(refresh: true)` returned 2 solutions and 609 projects in 0.888s wall time, server scan elapsed `00:00:00.8339725`, `truncated: false`.
- Semantic difference: no improvement for `find_symbols("HttpContext")`; it still returned 0 items with `WorkspaceWarming`, `completeness: partial`, and the incomplete-index reason.

| Tool | 3m-Wait Result | 3m-Wait Elapsed | Count | Workspace State | Completeness | Notes |
| --- | --- | ---: | ---: | --- | --- | --- |
| get_workspace_status | OK | 0.021s | 0 | WorkspaceWarming |  | Still warming after 180s |
| document_symbols | OK | 4.077s | 33 | WorkspaceWarming | partial | Same result count as short-wait run |
| hover | FAIL | 10.032s | 0 |  |  | `request_timeout`: `textDocument/hover` |
| go_to_definition | OK | 16.590s | 0 | WorkspaceWarming | partial | Still no locations for `HttpRequest` |
| find_references | OK | 13.340s | 3 | WorkspaceWarming | partial | Same result count as short-wait run |
| find_symbols | OK | 2.539s | 0 | WorkspaceWarming | partial | `totalKnown: 0`, `returned: 0`, incomplete-index reason present |
| diagnostics(file) | OK | 0.020s | 0 | WorkspaceWarming | unknown | No known diagnostics yet |
| diagnostics(workspace) | OK | 0.020s | 0 | WorkspaceWarming | partial | Current known diagnostics only |

3-minute retest findings:

- The previous `find_symbols("HttpContext")` 0-result behavior is not explained by only a short warmup window. It still reproduces after 3 minutes while the workspace remains warming.
- The result remains a successful MCP/LSP tool execution with weak semantic usefulness, not a transport failure: the response includes `WorkspaceWarming`, `partial`, `totalKnown`, `returned`, and a reason.
- `hover` timed out again in this long-wait run. This reinforces the earlier note that `textDocument/hover` can intermittently hit the 10-second request timeout on aspnetcore while warming, while the server remains usable for subsequent tools.

## 10-Minute Symbol Warmup Retest
- Date: 2026-05-17 (Asia/Seoul)
- roslyn-mcp-server commit: `71c0e67`
- aspnetcore commit: `93a1b5295d92954d46e26f2bbb3abde15f332a4b`
- MCP client method: targeted temporary stdio client script in `.local`; server command used `--log-level trace`, `--log-file`, and `--ls-log-dir`.
- Raw local artifacts: `.local/aspnetcore-long-warmup-20260517-081619-raw.json`, `.local/aspnetcore-long-warmup-20260517-081619.log`, `.local/aspnetcore-long-warmup-20260517-081619-stderr.log`, `.local/aspnetcore-ls-20260517-081619/`.
- Result: `get_workspace_status` still reported `WorkspaceWarming` at every 60-second poll through 600 seconds.
- Roslyn LS logging note: `Microsoft.CodeAnalysis.LanguageServer.exe` was launched with `--logLevel Trace --extensionLogDirectory .local/aspnetcore-ls-20260517-081619`, but no files were emitted under that directory during this run. Trace output was captured through server stderr/log instead.
- Captured log note: no `workspace/projectInitializationComplete` or `textDocument/publishDiagnostics` notification was observed in the captured logs for this run.

Status and symbol checkpoints:

| Warmup | Tool | Result | Elapsed | Count | Workspace State | Completeness | Notes |
| ---: | --- | --- | ---: | ---: | --- | --- | --- |
| 0s | get_workspace_status | OK | 0.020s | 0 | WorkspaceWarming |  | `openDocumentCount: 0`, `knownDiagnosticsFileCount: 0` |
| 60s | get_workspace_status | OK | 0.021s | 0 | WorkspaceWarming |  | no status metric change |
| 120s | get_workspace_status | OK | 0.021s | 0 | WorkspaceWarming |  | no status metric change |
| 180s | get_workspace_status | OK | 0.020s | 0 | WorkspaceWarming |  | no status metric change |
| 180s | find_symbols | OK | 1.613s | 0 | WorkspaceWarming | partial | Query `HttpContext`; `totalKnown: 0`, `returned: 0` |
| 240s | get_workspace_status | OK | 0.021s | 0 | WorkspaceWarming |  | no status metric change |
| 300s | get_workspace_status | OK | 0.020s | 0 | WorkspaceWarming |  | no status metric change |
| 300s | find_symbols | OK | 2.133s | 0 | WorkspaceWarming | partial | Query `HttpContext`; `totalKnown: 0`, `returned: 0` |
| 360s | get_workspace_status | OK | 0.021s | 0 | WorkspaceWarming |  | no status metric change |
| 420s | get_workspace_status | OK | 0.021s | 0 | WorkspaceWarming |  | no status metric change |
| 480s | get_workspace_status | OK | 0.021s | 0 | WorkspaceWarming |  | no status metric change |
| 540s | get_workspace_status | OK | 0.021s | 0 | WorkspaceWarming |  | no status metric change |
| 600s | get_workspace_status | OK | 0.021s | 0 | WorkspaceWarming |  | no status metric change |
| 600s | find_symbols | OK | 1.612s | 0 | WorkspaceWarming | partial | Query `HttpContext`; `totalKnown: 0`, `returned: 0` |
| 600s | document_symbols | OK | 3.047s | 33 | WorkspaceWarming | partial | Final usability probe |
| 600s | find_references | OK | 18.425s | 3 | WorkspaceWarming | partial | Final usability probe for `HttpContext` |

10-minute retest findings:

- Waiting 10 minutes did not move aspnetcore from `WorkspaceWarming` to `Ready` in this session.
- `find_symbols("HttpContext")` remained 0-result at 3, 5, and 10 minutes. This is now a long-warm behavior observation, not just a short-wait artifact.
- `get_workspace_status` currently lacks a Roslyn-internal project/document progress metric. `openDocumentCount` and `knownDiagnosticsFileCount` stayed 0 during passive polling because no file-specific read tool or publish diagnostics changed those MCP-side counters before the final probes.
- The server remained usable after 10 minutes: final `document_symbols` returned 33 items and final `find_references` returned 3 items.
- Follow-up should focus on better observability or Roslyn LS load behavior for large `.slnx` workspaces rather than increasing smoke wait time beyond 10 minutes.

## 10-Minute SDK-Aligned Retest
- Date: 2026-05-17 (Asia/Seoul)
- roslyn-mcp-server commit: `9dfa960 Increase warming retry hint to thirty seconds`
- aspnetcore commit: `93a1b5295d92954d46e26f2bbb3abde15f332a4b`
- MCP client method: `.local/mcp_aspnetcore_long_warmup.py`
- Raw local artifacts: `.local/aspnetcore-long-warmup-20260517-094921-raw.json`, `.local/aspnetcore-long-warmup-20260517-094921.log`, `.local/aspnetcore-long-warmup-20260517-094921-stderr.log`, `.local/aspnetcore-ls-20260517-094921/`.

Environment changes:

- ASP.NET Core `global.json` now requests SDK `11.0.100-preview.5.26227.104`.
- Installed SDK `11.0.100-preview.5.26227.104` into `C:\Users\Beretta\AppData\Local\Microsoft\dotnet`.
- Smoke command used `DOTNET_ROOT=C:\Users\Beretta\AppData\Local\Microsoft\dotnet` and a `PATH` prefix of `C:\Users\Beretta\AppData\Local\Microsoft\dotnet;C:\Users\Beretta\.dotnet\tools`.
- In `D:\Workspace\real-repos\aspnetcore`, `dotnet --version` resolved to `11.0.100-preview.5.26227.104`.

Status and symbol checkpoints:

| Warmup | Tool | Result | Elapsed | Count | Workspace State | Completeness | Notes |
| ---: | --- | --- | ---: | ---: | --- | --- | --- |
| - | list_workspaces | OK | 0.284s | 611 |  |  | 2 solutions, 609 projects, `truncated: false` |
| - | load_solution | OK | 0.506s | 0 | WorkspaceWarming |  | Loaded `AspNetCore.slnx` |
| 0s | get_workspace_status | OK | 0.020s | 0 | WorkspaceWarming |  | 0 warnings |
| 60s | get_workspace_status | OK | 0.020s | 0 | WorkspaceWarming |  | 50 `workspace_project_load_failed` warnings surfaced |
| 120s | get_workspace_status | OK | 0.041s | 0 | WorkspaceWarming |  | 50 warnings |
| 180s | get_workspace_status | OK | 0.020s | 0 | WorkspaceWarming |  | 50 warnings |
| 180s | find_symbols | OK | 4.776s | 20 | WorkspaceWarming | partial | Query `HttpContext`; `totalKnown: 122`, `returned: 20`, `retryAfterMs: 30000` |
| 300s | find_symbols | OK | 0.040s | 20 | WorkspaceWarming | partial | Query `HttpContext`; `totalKnown: 122`, `returned: 20`, `retryAfterMs: 30000` |
| 600s | get_workspace_status | OK | 0.020s | 0 | WorkspaceWarming |  | 50 warnings |
| 600s | find_symbols | OK | 0.041s | 20 | WorkspaceWarming | partial | Query `HttpContext`; `totalKnown: 122`, `returned: 20`, `retryAfterMs: 30000` |
| 600s | document_symbols | OK | 0.081s | 33 | WorkspaceWarming | partial | Final usability probe; `retryAfterMs: 30000` |
| 600s | find_references | OK | 0.587s | 3 | WorkspaceWarming | partial | Final usability probe for `HttpContext`; `retryAfterMs: 30000` |

Retest findings:

- Waiting 10 minutes still did not move aspnetcore from `WorkspaceWarming` to `Ready`.
- With the exact SDK requested by current aspnetcore `global.json`, `find_symbols("HttpContext")` no longer returns 0. It returned 20 capped results at 3, 5, and 10 minutes.
- `get_workspace_status` surfaced 50 project load warnings from 60 seconds onward. The first warning path was `src/Middleware/Microsoft.AspNetCore.OutputCaching.StackExchangeRedis/src/Microsoft.AspNetCore.OutputCaching.StackExchangeRedis.csproj`.
- No `workspace/projectInitializationComplete` notification was observed in the captured logs, so the state remained `WorkspaceWarming` rather than moving to `LoadedWithErrors`.
- Roslyn LS was launched with `--logLevel Trace --extensionLogDirectory`, but no files were emitted under `.local/aspnetcore-ls-20260517-094921/`.
- The new 30-second warming retry hint is visible in read-tool results as `retryAfterMs: 30000`.

## 0-180s Symbol Ramp Retest
- Date: 2026-05-17 (Asia/Seoul)
- roslyn-mcp-server commit: `8f91cb3 Document ASP.NET Core SDK-aligned smoke retest`
- aspnetcore commit: `93a1b5295d92954d46e26f2bbb3abde15f332a4b`
- MCP client method: `.local/mcp_aspnetcore_symbol_ramp.py`
- Raw local artifact: `.local/aspnetcore-symbol-ramp-20260517-100948-raw.json`

This run cleaned stale MSBuild node-reuse processes from prior ASP.NET Core smoke runs before starting. It then loaded `AspNetCore.slnx` and called `find_symbols("HttpContext", maxResults: 20)` every 10 seconds from 0s through 180s.

| Warmup | Elapsed | Returned | Total Known | Workspace State | Completeness | Notes |
| ---: | ---: | ---: | ---: | --- | --- | --- |
| 0s | 0.061s | 0 | 0 | WorkspaceWarming | partial | Empty partial result |
| 10s | 1.319s | 3 | 3 | WorkspaceWarming | partial | First non-empty result |
| 20s | 0.406s | 20 | 33 | WorkspaceWarming | partial | First capped page |
| 30s | 0.809s | 20 | 43 | WorkspaceWarming | partial | Total known still growing |
| 40s | 1.129s | 20 | 47 | WorkspaceWarming | partial | Total known reached 47 |
| 50s-180s | ~0.020s each | 20 | 47 | WorkspaceWarming | partial | Result count stayed capped; `retryAfterMs: 30000` |

Ramp findings:

- The first incomplete non-empty symbol result appeared at the 10-second checkpoint after `load_solution`.
- The first capped result page appeared at the 20-second checkpoint.
- In this active polling run, `totalKnown` grew to 47 by 40 seconds and then stayed there through 180 seconds.
- The 180-second final `get_workspace_status` still reported `WorkspaceWarming`, with 50 `workspace_project_load_failed` warnings and no open documents or known diagnostics.
