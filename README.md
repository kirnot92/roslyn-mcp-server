# roslyn-mcp-server

A C# MCP server that bridges Agent CLI-style tools to `roslyn-language-server`.

Current implementation status:

- Workspace tools: `list_workspaces`, `load_solution`, `load_project`, `get_workspace_status`
- Read-only Roslyn tools: `document_symbols`, `hover`, `go_to_definition`, `find_references`, `find_symbols`, `diagnostics`
- Large repository safeguards: bounded workspace scanning, result limits, LSP request limits, best-effort warming metadata

Install Roslyn LS separately:

```text
dotnet tool install --global roslyn-language-server --prerelease
```

The project is still pre-release and does not bundle or publish `roslyn-language-server`. Planning and implementation notes live in [docs/](docs/).
