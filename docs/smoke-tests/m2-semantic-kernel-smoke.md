# M2 Semantic Kernel Smoke Test

## Environment
- Date: 2026-05-17 (Asia/Seoul)
- roslyn-mcp-server commit: `0c57a5a072128dd9f8053b86c46825dce4e94aa2`
- Semantic Kernel commit: `2fb749e4bb0da637ca26be0442930a5ad84f54f3`
- OS: Microsoft Windows NT 10.0.19045.0
- dotnet: SDK `10.0.203`, host `10.0.7`
- roslyn-language-server: `5.8.0-1.26262.10+036e7a58b9d4348a62b6854544274551ae17ae8c`
- MCP client method: temporary newline-delimited JSON-RPC stdio client script in `.local`; server command used default scan options with `--root D:\Workspace\real-repos\semantic-kernel`

## Server Validation
- format: Passed, `dotnet format roslyn-mcp-server.sln --verify-no-changes`
- build: Passed, `dotnet build roslyn-mcp-server.sln`
- test: Passed, `dotnet test roslyn-mcp-server.sln` - 97 passed, 1 skipped

## Workspace Discovery
- elapsed: MCP wall time 0.141s; server scan elapsed `00:00:00.0961456`
- solutions: 3
- projects: 192
- truncated: false
- selected workspace: `dotnet/SK-dotnet.slnx`

`dotnet/SK-dotnet.slnx` was selected because it is the top-level .NET solution for the repository. The other discovered solutions are nested demo sample solutions: `dotnet/samples/Demos/VoiceChat/VoiceChat.sln` and `dotnet/samples/Demos/CopilotAgentPlugins/CopilotAgentPluginsDemoSample/CopilotAgentPluginsDemoSample.sln`.

## Tool Results
Test file: `dotnet/src/SemanticKernel.Abstractions/Kernel.cs`, selected because it is a small, central library file with visible `Kernel`, `KernelBuilder`, and method symbols and is not generated.

| Tool | Result | Elapsed | Count | Workspace State | Completeness | Truncated | Notes |
| --- | --- | ---: | ---: | --- | --- | --- | --- |
| list_workspaces | OK | 0.141s | 195 |  |  | false | 3 solutions, 192 projects; default scan options |
| load_solution | OK | 0.443s | 0 | WorkspaceWarming |  |  | Loaded `dotnet/SK-dotnet.slnx`; language server running |
| get_workspace_status | OK | 0.020s | 0 | WorkspaceWarming |  |  | Immediate, +3s, and +10s polls stayed `WorkspaceWarming` |
| document_symbols | OK | 0.365s | 40 | WorkspaceWarming | partial | false | `totalKnown: 40`, `returned: 40` |
| hover | OK | 0.888s | 0 | WorkspaceWarming | partial | false | Returned hover text for `Kernel` |
| go_to_definition | OK | 0.061s | 1 | WorkspaceWarming | partial | false | `KernelBuilder` resolved to `dotnet/src/SemanticKernel.Abstractions/KernelBuilder.cs` |
| find_references | OK | 3.025s | 20 | WorkspaceWarming | partial | true | `Kernel`, `includeDeclaration: true`, `maxResults: 20`; `totalKnown: 1708`, `returned: 20` |
| find_symbols | OK | 2.690s | 20 | WorkspaceWarming | partial | true | Query `KernelBuilder`, `maxResults: 20`; `totalKnown: 72`, `returned: 20` |
| diagnostics(file) | OK | 0.020s | 0 | WorkspaceWarming | unknown | false | No publish diagnostics received for file yet; `totalKnown: 0`, `returned: 0`, reason present |
| diagnostics(workspace) | OK | 0.020s | 0 | WorkspaceWarming | partial | false | `scope: workspace`, `maxResults: 50`; current known diagnostics only |

## Findings
- Blockers:
  - None observed. The previous PowerShell default discovery blocker did not reproduce here; Semantic Kernel discovery completed quickly with default scan options.
- Issues:
  - `get_workspace_status` remained `WorkspaceWarming` through the explicit polling window. This did not block read tools, but later client UX should treat warming as a normal partial-results state.
  - `find_references` and `find_symbols` both returned truncated results as expected for large result sets. Metadata included `totalKnown`, `returned`, and `truncated`.
- Observations:
  - The MCP server process did not hang or crash during tool calls.
  - Read tools returned best-effort results with `WorkspaceWarming` and `partial` or `unknown` metadata.
  - File/workspace diagnostics stayed bounded to currently known diagnostics and did not attempt an unbounded workspace diagnostic computation.
  - Closing stdin shut down the stdio server cleanly with exit code 0.

## Recommendation
- Can proceed to M3 docs/client usability: Yes for this smoke target.
- Need fixes before broader large-repo testing: No Semantic Kernel-specific blocker found. Continue to watch warming-state UX and truncated-result presentation in client smoke.
