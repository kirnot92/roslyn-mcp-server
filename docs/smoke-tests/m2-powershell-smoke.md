# M2 PowerShell Smoke Test

## Environment
- Date: 2026-05-17 (Asia/Seoul)
- roslyn-mcp-server commit: `5791edb9feedf8fe6bc4feca62d388b3779e30e8`
- PowerShell commit: `90d3b7f2e355e457d92b6929f6b4cfe4fa651e35`
- OS: Microsoft Windows NT 10.0.19045.0
- dotnet: SDK `10.0.203`, host `10.0.7`
- roslyn-language-server: `5.8.0-1.26262.10+036e7a58b9d4348a62b6854544274551ae17ae8c`
- MCP client method: temporary newline-delimited JSON-RPC stdio client script in `.local`; final complete discovery run used `dotnet run --project src/RoslynMcpServer -- --root D:\Workspace\real-repos\PowerShell --log-file .local\powershell-smoke.log --scan-timeout 10`

## Server Validation
- format: Passed, `dotnet format roslyn-mcp-server.sln --verify-no-changes`
- build: Passed, `dotnet build roslyn-mcp-server.sln`
- test: Passed, `dotnet test roslyn-mcp-server.sln` - 94 passed, 1 skipped

## Workspace Discovery
- elapsed: MCP wall time 3.104s; server scan elapsed `00:00:00.0227122` with `--scan-timeout 10`
- solutions: 3
- projects: 42
- truncated: false
- selected workspace: `PowerShell.sln`

`PowerShell.sln` was selected because it is the top-level product solution. The other solutions are nested test/perf tool solutions: `test/tools/TestAlc/TestAlc.sln` and `test/perf/dotnet-tools/ResultsComparer/ResultsComparer.sln`.

The default scan timeout is a notable blocker: with the same MCP client and no `--scan-timeout` override, `list_workspaces(refresh: true)` returned in about 3.09s with `truncated: true`, `truncationReason: scan_timeout`, and 0 solution/project candidates. A direct `git ls-files -co --exclude-standard -- '*.sln' '*.slnx' '*.csproj'` check found 45 candidates in about 0.04s, so this needs follow-up before relying on defaults for broader real-repo smoke.

Follow-up on 2026-05-17: after fixing git scanner stdio handling and streaming output parsing, default `list_workspaces(refresh: true)` on the same PowerShell checkout returned 3 solutions and 42 projects. MCP wall time was 0.103s, server scan elapsed was `00:00:00.0662145`, `truncated` was false, and `PowerShell.sln` was included.

## Tool Results
Test file: `src/powershell/Program.cs`, selected because it is a small real entry point file with visible `ManagedPSEntry` and `Main` symbols and is not generated.

| Tool | Result | Elapsed | Count | Workspace State | Completeness | Truncated | Notes |
| --- | --- | ---: | ---: | --- | --- | --- | --- |
| list_workspaces | OK | 3.104s | 45 |  |  | false | 3 solutions, 42 projects with `--scan-timeout 10`; default timed out with 0 candidates |
| load_solution | OK | 0.465s | 0 | WorkspaceWarming |  |  | Loaded `PowerShell.sln`; language server running |
| get_workspace_status | OK | 0.020s | 0 | WorkspaceWarming |  |  | Immediate, +3s, and +10s polls stayed `WorkspaceWarming`; later read tools observed `Ready` |
| document_symbols | OK | 1.856s | 2 | WorkspaceWarming | partial | false | `totalKnown: 2`, `returned: 2`; returned `ManagedPSEntry` and `Main` |
| hover | OK | 3.006s | 0 | WorkspaceWarming | partial | false | Returned hover text for `ManagedPSEntry` |
| go_to_definition | OK | 0.989s | 0 | WorkspaceWarming | partial | false | `UnmanagedPSEntry.Start`; no locations, but no error or crash |
| find_references | OK | 1.998s | 1 | Ready | complete | false | `ManagedPSEntry`, `includeDeclaration: true`, `maxResults: 20`; `totalKnown: 1`, `returned: 1` |
| find_symbols | OK | 0.021s | 0 | Ready | unknown | false | Query `ManagedPSEntry`, `maxResults: 20`; `totalKnown: 0`, `returned: 0`, completeness reason present |
| diagnostics(file) | OK | 0.020s | 0 | Ready | unknown | false | No publish diagnostics received for file yet; `totalKnown: 0`, `returned: 0`, reason present |
| diagnostics(workspace) | OK | 0.020s | 0 | Ready | unknown | false | `scope: workspace`, `maxResults: 50`; current known diagnostics only, reason present |

## Findings
- Blockers:
  - Default `list_workspaces` on `PowerShell/PowerShell` can return `scan_timeout` with 0 candidates even though the repo has 3 `.sln` files and 42 `.csproj` files. This blocks the default Agent CLI discovery path unless the user increases `--scan-timeout` or manually provides `PowerShell.sln`.
- Issues:
  - `get_workspace_status` remained `WorkspaceWarming` through the explicit polling window, while later read-tool metadata moved to `Ready`. This is usable, but the state transition timing should be watched in later client smoke.
  - `find_symbols("ManagedPSEntry")` returned 0 results with `completeness: unknown`. This is acceptable for smoke because the server stayed responsive and included metadata, but it limits Agent CLI usefulness for symbol search in this repo/session.
  - `go_to_definition` for `UnmanagedPSEntry.Start` returned 0 locations. This may be semantic/project-load related rather than MCP transport failure.
- Observations:
  - The MCP server process did not hang or crash during tool calls.
  - Read tools returned best-effort results or clear unknown/partial metadata.
  - File/workspace diagnostics returned only currently known diagnostics and did not attempt an unbounded workspace diagnostic computation.
  - Closing stdin shut down the stdio server cleanly with exit code 0.

## Recommendation
- Can proceed to M3 docs/client usability: Partial. The stdio/read-tool path is usable after increasing scan timeout or explicitly loading `PowerShell.sln`.
- Need fixes before broader large-repo testing: Yes. Investigate default `list_workspaces` timeout/candidate loss before running wider default-config real-repo validation.

## Blocker Retest
- Date: 2026-05-17 (Asia/Seoul)
- roslyn-mcp-server commit: `ab27d2a Fix git workspace discovery under stdio`
- MCP client method: same temporary stdio client script, default server options without `--scan-timeout` override
- Result: Default `list_workspaces(refresh: true)` returned successfully in 0.101s wall time, with server scan elapsed `00:00:00.0696729`.
- Workspace candidates: 3 solutions, 42 projects
- truncated: false
- truncationReason: none
- Verdict: The previous default-discovery blocker is no longer reproduced on `PowerShell/PowerShell`.

Retest tool summary:

| Tool | Result | Elapsed | Count | Workspace State | Completeness | Truncated | Notes |
| --- | --- | ---: | ---: | --- | --- | --- | --- |
| list_workspaces | OK | 0.101s | 45 |  |  | false | Default options; 3 solutions, 42 projects |
| load_solution | OK | 0.445s | 0 | WorkspaceWarming |  |  | Loaded `PowerShell.sln` |
| document_symbols | OK | 1.874s | 2 | WorkspaceWarming | partial | false | `totalKnown: 2`, `returned: 2` |
| hover | OK | 6.546s | 0 | WorkspaceWarming | partial | false | Returned hover metadata/text for `ManagedPSEntry` |
| go_to_definition | OK | 0.061s | 0 | Ready | complete | false | No locations, no error |
| find_references | OK | 0.729s | 1 | Ready | complete | false | `totalKnown: 1`, `returned: 1` |
| find_symbols | OK | 0.021s | 0 | Ready | unknown | false | `totalKnown: 0`, reason present |
| diagnostics(file) | OK | 0.021s | 0 | Ready | unknown | false | No known diagnostics yet |
| diagnostics(workspace) | OK | 0.021s | 0 | Ready | unknown | false | Current known diagnostics only |
