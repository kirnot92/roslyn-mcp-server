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
