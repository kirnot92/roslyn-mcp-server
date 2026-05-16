# roslyn-mcp-server

A C# MCP server that bridges Agent CLI-style tools to `roslyn-language-server`.

`roslyn-mcp-server` runs as an MCP stdio server. It starts `roslyn-language-server`
as a child LSP process only after a solution or project is selected, then translates
MCP tool calls into Roslyn LSP requests.

Implemented tools:

- Workspace tools: `list_workspaces`, `load_solution`, `load_project`, `get_workspace_status`
- Read-only Roslyn tools: `document_symbols`, `hover`, `go_to_definition`, `find_references`, `find_symbols`, `diagnostics`
- Large repository safeguards: bounded workspace scanning, result limits, LSP request limits, best-effort warming metadata

## Getting Started

Install `roslyn-language-server` separately:

```text
dotnet tool install --global roslyn-language-server --prerelease
```

Then configure your MCP client to run this server from the repository root you want
to inspect. With the default configuration, the server uses its current working
directory as the workspace root. Use `--root <path>` only when your client cannot
set the working directory, and use `--roslyn-language-server <path>` only when the
language server is not on `PATH`.

Recommended tool flow:

1. `list_workspaces`
2. `load_solution` or `load_project`
3. `get_workspace_status`
4. read-only Roslyn tools

If multiple `.sln`, `.slnx`, or `.csproj` candidates are found, select one
explicitly with `load_solution` or `load_project`.

More setup and client examples are in [docs/usage.md](docs/usage.md). The project
is still pre-release, does not bundle `roslyn-language-server`, and is not
published as a NuGet/.NET global tool.
