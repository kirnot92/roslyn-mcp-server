# Usage

This document covers end-user setup for `roslyn-mcp-server`. Implementation notes
for agents and maintainers remain in `AGENTS.md` and `docs/implementation-notes.md`.

## Prerequisites

Install `roslyn-language-server` separately:

```text
dotnet tool install --global roslyn-language-server --prerelease
```

`roslyn-mcp-server` does not bundle `roslyn-language-server`. By default it looks
for `roslyn-language-server` on `PATH`. If your environment cannot expose the
global tool on `PATH`, pass an explicit executable path:

```text
--roslyn-language-server <path>
```

Current risk: `roslyn-language-server` is a prerelease package and currently
requires a .NET 10 runtime/SDK environment. Its CLI options and runtime
requirements may change before a stable release.

## Workspace Root

The recommended setup is to start the MCP server from the repository root and pass
no arguments. In that mode, the current working directory becomes the server root.

Use `--root <path>` only as an escape hatch when your MCP client cannot set the
server working directory. User-supplied paths still must stay under the configured
root.

## Client Configuration

Windows example, assuming the client starts the server with the target repository
as the working directory:

```json
{
  "mcpServers": {
    "roslyn": {
      "command": "D:\\Workspace\\roslyn-mcp-server\\src\\RoslynMcpServer\\bin\\Debug\\net10.0\\roslyn-mcp-server.exe"
    }
  }
}
```

Unix example:

```json
{
  "mcpServers": {
    "roslyn": {
      "command": "/home/me/roslyn-mcp-server/src/RoslynMcpServer/bin/Debug/net10.0/roslyn-mcp-server"
    }
  }
}
```

If the client cannot set the working directory, use `--root`:

```json
{
  "mcpServers": {
    "roslyn": {
      "command": "D:\\Workspace\\roslyn-mcp-server\\src\\RoslynMcpServer\\bin\\Debug\\net10.0\\roslyn-mcp-server.exe",
      "args": ["--root", "D:\\Workspace\\my-csharp-repo"]
    }
  }
}
```

If the Roslyn language server is installed outside `PATH`, add the explicit path:

```json
{
  "mcpServers": {
    "roslyn": {
      "command": "/home/me/roslyn-mcp-server/src/RoslynMcpServer/bin/Debug/net10.0/roslyn-mcp-server",
      "args": ["--roslyn-language-server", "/home/me/.dotnet/tools/roslyn-language-server"]
    }
  }
}
```

## Recommended Tool Flow

1. Call `list_workspaces`.
2. If one intended `.sln`, `.slnx`, or `.csproj` candidate is visible, call
   `load_solution` or `load_project` with its root-relative path.
3. If multiple candidates are visible, choose explicitly. The server does not guess
   between multiple solutions or projects.
4. Call `get_workspace_status`.
5. Call read-only Roslyn tools such as `document_symbols`, `hover`,
   `go_to_definition`, `find_references`, `find_symbols`, or `diagnostics`.

During startup, read tools may return `workspace_loading` instead of blocking.
After LSP initialize, large workspaces may remain in `WorkspaceWarming`. If
Roslyn LS reports project load errors, the state becomes `LoadedWithErrors` and
`get_workspace_status.warnings` includes the affected project paths and a short
cause. Read tools still return best-effort results with `workspaceState`,
`completeness`, and truncation metadata when applicable.

## Notes For Large Repositories

Workspace discovery is bounded by scan depth, scan timeout, and candidate limits.
Large result tools include metadata such as `totalKnown`, `returned`, and
`truncated`. A truncated result is expected behavior, not a transport failure.

Diagnostics are currently based on diagnostics already published by Roslyn LS.
The server does not perform unbounded workspace-wide diagnostics computation.
