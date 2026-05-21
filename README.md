# roslyn-mcp-server

`roslyn-mcp-server` is a C# MCP server that lets AI agents use Roslyn language
features through `roslyn-language-server`.

The server is meant for Agent CLI-style tools working inside real C# repositories,
including large mono-repos. It runs as an MCP stdio server, starts
`roslyn-language-server` as a child LSP process after a solution or project is
selected, then translates MCP tool calls into LSP requests.

The main goal is not to replace an IDE. The goal is to give an agent enough
compiler-backed context to read, review, and navigate C# code without forcing the
agent to parse a large repository by itself.

## Why This Exists

AI agents can inspect files with normal filesystem tools, but C# code often needs
semantic context:

- where a symbol is defined
- which overload or type a reference points to
- where a symbol is referenced
- what diagnostics Roslyn currently knows about
- what symbols are present in a file or workspace

Roslyn already knows how to answer these questions. This project provides a thin
MCP bridge so an agent can ask those questions while it works.

## Large Repository Design

The project assumes large repositories from the beginning. A useful agent tool
must stay predictable even when a repo has hundreds of projects, many solutions,
slow restore state, or partial Roslyn indexing.

That leads to a few design choices:

- By default the server does not load a solution at startup; `--load-solution`
  can opt into early solution loading.
- Workspace discovery is bounded by scan depth, timeout, and candidate limits.
- If multiple `.sln`, `.slnx`, or `.csproj` files exist, the agent must choose.
- Roslyn LS is launched only after `load_solution`, `load_project`, or an
  unambiguous first read-tool auto-load.
- Read tools do not block indefinitely while the language server is warming.
- Large result sets are capped and report `totalKnown`, `returned`, and
  `truncated` metadata.
- Warming or partially loaded workspaces return best-effort results with
  `workspaceState`, `completeness`, and retry metadata.
- Diagnostics are based on `textDocument/publishDiagnostics` notifications
  already processed by the bounded background diagnostics queue; the server does
  not run an unbounded workspace-wide diagnostic computation. Queue capacity and
  pending/processed/dropped/stale counts are exposed through
  `get_workspace_status`.

In practice, `WorkspaceWarming` is a normal state for large repositories. Agents
should use partial results when they are good enough and call
`get_workspace_status` when they need to understand load warnings or failures.

## Implemented Tools

Workspace tools:

- `list_workspaces` - Discover `.sln`, `.slnx`, and `.csproj` candidates under
  the workspace root. Optional `maxDepth` skips git-based discovery and performs
  bounded filesystem BFS for near-root workspace files.
- `load_solution` - Load a selected `.sln` or `.slnx` into Roslyn LS.
- `load_project` - Load a selected `.csproj` into Roslyn LS.
- `get_workspace_status` - Report the selected target, load state, warnings,
  open documents, and diagnostics queue/cache status.

Read-only Roslyn tools:

- `document_symbols` - Return file-level types, members, and symbol ranges,
  optionally filtering returned symbols by kind while preserving ancestor
  context for matching descendants.
- `hover` - Return Roslyn hover text for a source position.
- `go_to_definition` - Return definition locations for a source position.
- `peek_definition` - Return definition locations plus bounded source snippets.
- `find_references` - Return references for a source position.
- `peek_references` - Return reference locations plus bounded source snippets.
- `find_implementations` - Return implementation locations from an interface,
  abstract member, or base contract position. Calling it on a concrete
  implementation may legitimately return only that implementation. Results can
  be narrowed with root-relative `includePathPrefixes`.
- `get_call_hierarchy` - Return direct depth-1 incoming callers, outgoing
  callees, or both for a method, constructor, or property accessor position,
  optionally filtering returned edge counterparts by kind: `method`,
  `constructor`, `property`, `event`, `operator`, or `field`, and by
  root-relative `includePathPrefixes`.
- `get_type_hierarchy` - Return base type, derived type, or interface
  implementation hierarchy edges for a type position, with bounded breadth-first
  traversal depth, result caps, and optional root-relative `includePathPrefixes`.
- `find_symbols` - Search workspace symbols by name, optionally filtering
  returned results by kind such as `class`, `interface`, `method`, `property`,
  or `field`, and by symbol-name `matchMode`: `default`, `exact`, `prefix`,
  or `contains`. Default matching keeps Roslyn LS fuzzy candidates but ranks
  exact, prefix, and contains name matches before the result cap. It can also
  keep only symbols located under root-relative `includePathPrefixes`, which is
  useful in large repositories.
- `diagnostics` - Return currently known file or workspace diagnostics.

Resources:

- `roslyn://server/guide` - Short usage guidance for agents.
- `roslyn://server/capabilities` - Short read-only capability summary.

This project is intentionally read-only. Rename, code actions, formatting, and
apply/edit tools are outside the product direction; agents should make file
changes with their normal workspace editing tools after using Roslyn context to
understand the code.

## Getting Started

Prerequisites:

- .NET 10 SDK for installing `roslyn-language-server` as a global tool
- `roslyn-language-server`

Install `roslyn-language-server` separately:

```text
dotnet tool install --global roslyn-language-server --prerelease
```

`roslyn-mcp-server` does not bundle Roslyn LS and is not currently published as a
NuGet/.NET global tool.

### Windows Release Zip

Download `roslyn-mcp-server-v0.1.0-win-x64.zip` from the
[v0.1.0 release](https://github.com/kirnot92/roslyn-mcp-server/releases/tag/v0.1.0)
or the [latest release](https://github.com/kirnot92/roslyn-mcp-server/releases/latest),
then extract the whole zip to a folder.

The Windows artifact is self-contained and single-file. The server executable
does not require a separate .NET runtime installation, though `roslyn-language-server`
and the target C# repository still need a compatible .NET SDK/runtime environment.

Point your MCP client at the extracted executable:

```text
C:\Tools\roslyn-mcp-server\roslyn-mcp-server.exe
```

### Build From Source

Use source builds for non-Windows platforms, local development, or if you want to
run the server directly from this repository. Source builds require Git and the
.NET 10 SDK.

```powershell
git clone https://github.com/kirnot92/roslyn-mcp-server.git
cd roslyn-mcp-server
dotnet build .\src\RoslynMcpServer\RoslynMcpServer.csproj -c Release
```

The built server executable will be under:

```text
src/RoslynMcpServer/bin/Release/net10.0/
```

On Windows, the executable is typically:

```text
src/RoslynMcpServer/bin/Release/net10.0/roslyn-mcp-server.exe
```

On macOS or Linux, the executable is typically:

```text
src/RoslynMcpServer/bin/Release/net10.0/roslyn-mcp-server
```

After downloading or building the server, configure your MCP client to run the
server executable from the repository root you want to inspect.

If you prefer a self-built Windows release-style layout, publish instead of
building:

```powershell
git clone https://github.com/kirnot92/roslyn-mcp-server.git
cd roslyn-mcp-server
dotnet publish .\src\RoslynMcpServer\RoslynMcpServer.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o .\publish\roslyn-mcp-server
```

With the default configuration, the current working directory becomes the
workspace root. Use `--root <path>` only when your MCP client cannot set the
working directory. Use `--roslyn-language-server <path>` only when
`roslyn-language-server` is not on `PATH`.

`--load-solution <path>` is optional. When set, the server starts loading that
`.sln` or `.slnx` as soon as the MCP server starts, so Roslyn LS can warm while
the agent reads the repository. When it is omitted, the agent can still call
`load_solution` or `load_project` when it needs Roslyn context.

Recommended tool flow:

1. `list_workspaces`
2. `load_solution` or `load_project`
3. `get_workspace_status`
4. read-only Roslyn tools

For code review workflows, start workspace loading early. A good pattern is to
inspect the changed file list, load the intended solution or project, then read
the diff while Roslyn LS warms in the background.

## MCP Client Setup

Download or build the server first, then point your MCP client at the server
executable. Use `cwd` or `--root <repo-root>` so the server knows which
repository to inspect.
Solution paths passed to `--load-solution` must be the exact root-relative path,
such as `Sources/Server.sln`, or an absolute path inside the root. The server
does not recursively search for a matching file name from this option.

### 1. Claude Code

Local setup with `claude mcp add`:

```powershell
claude mcp add --transport stdio roslyn -- `
  <path-to-roslyn-mcp-server.exe> `
  --root <repo-root> `
  --load-solution Server.sln
```

Project-scoped `.mcp.json`:

```json
{
  "mcpServers": {
    "roslyn": {
      "command": "<path-to-roslyn-mcp-server.exe>",
      "args": ["--root", ".", "--load-solution", "Server.sln"]
    }
  }
}
```

### 2. Gemini CLI

Project `.gemini/settings.json`:

```json
{
  "mcpServers": {
    "roslyn": {
      "command": "<path-to-roslyn-mcp-server.exe>",
      "args": ["--load-solution", "Server.sln"],
      "cwd": "<repo-root>",
      "timeout": 30000
    }
  }
}
```

### 3. Codex

Global `~/.codex/config.toml`, or project-scoped `.codex/config.toml` in a
trusted project:

```toml
[mcp_servers.roslyn]
command = "<path-to-roslyn-mcp-server.exe>"
args = ["--root", "<repo-root>", "--load-solution", "Server.sln"]
```

If you keep a project-scoped `.codex/config.toml` in the repository root, `--root`
can usually be `.`:

```toml
[mcp_servers.roslyn]
command = "<path-to-roslyn-mcp-server.exe>"
args = ["--root", ".", "--load-solution", "Server.sln"]
```

### Multiple Solutions

For repositories with separate solutions, run separate MCP server entries. For
example, a Unity project can expose both server and client solutions:

```json
{
  "mcpServers": {
    "roslyn-server": {
      "command": "<path-to-roslyn-mcp-server.exe>",
      "args": ["--load-solution", "Server.sln"],
      "cwd": "<repo-root>"
    },
    "roslyn-unity": {
      "command": "<path-to-roslyn-mcp-server.exe>",
      "args": ["--load-solution", "UnityClient.sln"],
      "cwd": "<repo-root>"
    }
  }
}
```

More setup and client examples are in [docs/usage.md](docs/usage.md).

## Current Status

The project is pre-release but usable for read-only C# navigation and diagnostics
in agent workflows. It has been smoke-tested with real repositories including
PowerShell, Semantic Kernel, and ASP.NET Core. Those tests are intentionally about
responsiveness and usable metadata, not perfect semantic completeness for every
large solution.

Known constraints:

- `roslyn-language-server` is a prerelease dependency and may change behavior.
- Large repositories may remain in `WorkspaceWarming` for a long time.
- Project load errors usually reflect local SDK, workload, restore, or
  `global.json` environment problems and are surfaced through workspace warnings.
- Diagnostics are current-known diagnostics from the last processed
  `textDocument/publishDiagnostics` notifications, not a guaranteed full build
  result.
- Write/refactoring tools are not implemented.

Implementation notes for maintainers are in [AGENTS.md](AGENTS.md) and
[docs/implementation-notes.md](docs/implementation-notes.md).

## License

This project is licensed under the MIT License. See [LICENSE](LICENSE) for
details.
