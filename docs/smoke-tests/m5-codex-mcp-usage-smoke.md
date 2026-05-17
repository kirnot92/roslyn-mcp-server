# M5 Codex MCP Usage Smoke

## Environment

- Date: 2026-05-17 (Asia/Seoul)
- roslyn-mcp-server commit: `4f3ad02568a405837abd9df59c8deef33f62e4bf`
- Target workspace: `roslyn-mcp-server.sln`
- OS: Microsoft Windows NT 10.0.19045.0
- dotnet: SDK `11.0.100-preview.3.26207.106`, host `11.0.0-preview.3.26207.106`
- roslyn-language-server: `5.8.0-1.26262.10+036e7a58b9d4348a62b6854544274551ae17ae8c`
- MCP client method: live Codex session using the configured Roslyn MCP server tools, not a scripted smoke driver.

## Server Status

- `get_workspace_status` reported `Ready`.
- Current target was `roslyn-mcp-server.sln`.
- Roslyn language server was running.
- `list_workspaces` returned 1 solution and 2 projects with `truncated: false`.
- Diagnostics queue status was idle: capacity `1024`, pending `0`, processed `0`, dropped `0`, stale `0`, overflow policy `drop_newest_when_full`.

## Tool Flow

| Tool | Input | Result | Usability note |
| --- | --- | --- | --- |
| `find_symbols` | `WorkspaceSession`, `kindFilter: ["class"]` | 2 class results, `totalUnfilteredKnown: 5`, `truncated: false` | The kind filter made the result immediately usable by removing non-class noise. |
| `find_symbols` | `McpTools` | 0 results | This is not a full-text or conceptual search. Agents need a real symbol name or should fall back to file search. |
| `document_symbols` | `src/RoslynMcpServer/Workspace/WorkspaceSession.cs` | 57 symbols, complete | Good first step for getting reliable 1-based method/type positions before calling position-based tools. |
| `document_symbols` | `src/RoslynMcpServer/Mcp/NavigationTools.cs` | 149 symbols, complete | The flat outline is long but still useful because kind names and exact line/column positions are included. |
| `hover` | `NavigationTools.GetCallHierarchy` | Concise method signature | Useful to confirm optional parameters and return shape without opening the file. |
| `peek_definition` | `WorkspaceLoadState` type position in `WorkspaceSession.cs` | Returned enum declaration snippet from `WorkspaceModels.cs` | Snippet output is enough to understand the target without a separate file read. |
| `peek_definition` | `state` field identifier in `WorkspaceSession.cs` | Returned the field declaration itself | Position precision matters. Calling on the field identifier and calling on its type name intentionally produce different answers. |
| `go_to_definition` | `WorkspaceLoadState.NotLoaded` enum member | Returned the enum member location | Good lightweight option when snippet context is not needed. |
| `peek_references` | `DefaultSymbolMaxResults` | 2 references with snippets, complete | The snippets make small constant/reference audits much faster than raw locations alone. |
| `find_implementations` | `IRoslynWorkspaceLoader` interface | 12 known, 10 returned, `truncated: true` | Correctly found production and test-double implementations. The usage hint is helpful, but agents should raise `maxResults` when test doubles are expected. |
| `find_references` | `IRoslynWorkspaceLoader.LoadAsync` | 16 references, complete | Good for confirming production usage after implementation discovery. |
| `get_call_hierarchy` | `WorkspaceSession.LoadTargetCoreAsync`, `direction: "both"` | 22 unfiltered edges, 20 returned, `truncated: true` | Useful but noisy without filtering because field/property/enum edges are included. |
| `get_call_hierarchy` | Same target, `kindFilter: ["method"]` | 7 method edges, complete | This is the best shape for agent reasoning about call flow. |
| `diagnostics` | workspace scope | 0 diagnostics, `completeness: unknown` | The response clearly explains that workspace diagnostics only reflect processed publish notifications. |

## Findings

- Blockers:
  - None observed.
- Issues:
  - `get_call_hierarchy` without `kindFilter` can spend the result budget on fields, properties, and enum members before all callable edges are returned. For agent workflows, `kindFilter: ["method"]` should be the default habit when the question is call flow.
  - Position-based tools are only as good as the selected column. For example, a call on `WorkspaceLoadState` returned the enum definition, while a call on the adjacent `state` identifier returned the field declaration. This is correct behavior, but agents should use `document_symbols` or a source snippet first when the exact token is ambiguous.
  - `find_symbols` empty results can mean the query is conceptual rather than a real symbol name. It should not be treated as proof that no relevant code exists.
- Observations:
  - The overall interaction felt responsive in this repo. Workspace/status calls and most navigation calls returned quickly; reference and symbol requests still returned bounded metadata rather than hanging.
  - `workspaceState`, `completeness`, `totalKnown`, `returned`, and `truncated` were consistently present where needed, which made it easy to decide whether to retry, widen limits, or switch tools.
  - `peek_definition` and `peek_references` are the highest productivity tools for code review style work because they remove many separate file reads.
  - `find_implementations` is effective on interface positions, but normal test-double noise means the caller often needs a higher `maxResults` or a follow-up filter in the agent.

## Recommendation

- For Codex-style use, prefer this sequence:
  - `get_workspace_status` or `list_workspaces`
  - `find_symbols` with `kindFilter` when the symbol name is known
  - `document_symbols` to get exact positions
  - `peek_definition` or `peek_references` for immediate context
  - `get_call_hierarchy` with `kindFilter: ["method"]` for call-flow questions
- No server fix is required from this smoke. The main follow-up is guidance/default-use tuning for agents so noisy-but-correct tool surfaces are called with the right filters and limits.
