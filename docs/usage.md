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

Use `--load-solution <path>` when you want a specific `.sln` or `.slnx` to start
loading as soon as the MCP server starts. The path must be the exact
root-relative path, such as `Sources/Server.sln`, or an absolute path inside the
configured root. The server does not recursively search for a matching file name
from this option. The option is optional, only accepts solution files, and can be
specified once.

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

To preload a solution during server startup, add `--load-solution`:

```json
{
  "mcpServers": {
    "roslyn": {
      "command": "D:\\Workspace\\roslyn-mcp-server\\src\\RoslynMcpServer\\bin\\Debug\\net10.0\\roslyn-mcp-server.exe",
      "args": ["--root", "D:\\Workspace\\my-csharp-repo", "--load-solution", "Server.sln"]
    }
  }
}
```

## Recommended Tool Flow

1. Optionally configure `--load-solution <path>` for the intended `.sln` or
   `.slnx`.
2. Call `list_workspaces`.
3. If one intended `.sln`, `.slnx`, or `.csproj` candidate is visible, call
   `load_solution` or `load_project` with its root-relative path.
4. If multiple candidates are visible, choose explicitly. The server does not guess
   between multiple solutions or projects.
5. Call `get_workspace_status`.
6. Call read-only Roslyn tools such as `document_symbols`, `hover`,
   `go_to_definition`, `find_references`, `find_symbols`, or `diagnostics`.

During startup, read tools may return `workspace_loading` instead of blocking.
After LSP initialize, large workspaces may remain in `WorkspaceWarming`. If
Roslyn LS reports project load errors, the state becomes `LoadedWithErrors` and
`get_workspace_status.warnings` includes the affected project paths and a short
cause. Read tools still return best-effort results with `workspaceState`,
`completeness`, and truncation metadata when applicable. While a workspace is
warming, retry hints use `retryAfterMs: 30000` so clients avoid tight polling on
large repositories.

## Code Review Preflight

For code review workflows, start workspace loading before reading the full diff:

1. Inspect only the changed file list first, for example `git diff --name-only`.
2. Call `load_solution` or the relevant `load_project` immediately.
3. Do not wait for `Ready`; read the diff while the workspace is warming.
4. Use `go_to_definition`, `hover`, and `find_references` from the changed code
   once the review needs semantic context.

This lets Roslyn LS use the human/agent diff-reading time for background project
load and keeps the later navigation calls from paying the full cold-start cost.

## Notes For Large Repositories

Workspace discovery is bounded by scan depth, scan timeout, and candidate limits.
Large result tools include metadata such as `totalKnown`, `returned`, and
`truncated`. A truncated result is expected behavior, not a transport failure.

Diagnostics are currently based on diagnostics already published by Roslyn LS
and processed by the bounded background diagnostics queue. The server does not
perform unbounded workspace-wide diagnostics computation. If the queue is full,
new publish diagnostics notifications are dropped. `get_workspace_status`
exposes the queue capacity, pending, processed, dropped, and stale notification
counts plus the overflow policy.
