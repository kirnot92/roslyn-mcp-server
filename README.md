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

- The server does not load a solution at startup.
- Workspace discovery is bounded by scan depth, timeout, and candidate limits.
- If multiple `.sln`, `.slnx`, or `.csproj` files exist, the agent must choose.
- Roslyn LS is launched only after `load_solution`, `load_project`, or an
  unambiguous first read-tool auto-load.
- Read tools do not block indefinitely while the language server is warming.
- Large result sets are capped and report `totalKnown`, `returned`, and
  `truncated` metadata.
- Warming or partially loaded workspaces return best-effort results with
  `workspaceState`, `completeness`, and retry metadata.
- Diagnostics are based on `textDocument/publishDiagnostics` notifications already
  received from Roslyn LS; the server does not run an unbounded workspace-wide
  diagnostic computation.

In practice, `WorkspaceWarming` is a normal state for large repositories. Agents
should use partial results when they are good enough and call
`get_workspace_status` when they need to understand load warnings or failures.

## Implemented Tools

Workspace tools:

- `list_workspaces`
- `load_solution`
- `load_project`
- `get_workspace_status`

Read-only Roslyn tools:

- `document_symbols`
- `hover`
- `go_to_definition`
- `find_references`
- `find_symbols`
- `diagnostics`

The current implementation is read-only. Rename, code actions, formatting, and
apply/edit tools are intentionally out of scope for now.

## Getting Started

Install `roslyn-language-server` separately:

```text
dotnet tool install --global roslyn-language-server --prerelease
```

`roslyn-mcp-server` does not bundle Roslyn LS and is not currently published as a
NuGet/.NET global tool. Build it from source, then configure your MCP client to
run the server from the repository root you want to inspect.

With the default configuration, the current working directory becomes the
workspace root. Use `--root <path>` only when your MCP client cannot set the
working directory. Use `--roslyn-language-server <path>` only when
`roslyn-language-server` is not on `PATH`.

Recommended tool flow:

1. `list_workspaces`
2. `load_solution` or `load_project`
3. `get_workspace_status`
4. read-only Roslyn tools

For code review workflows, start workspace loading early. A good pattern is to
inspect the changed file list, load the intended solution or project, then read
the diff while Roslyn LS warms in the background.

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
- Diagnostics are current-known diagnostics, not a guaranteed full build result.
- Write/refactoring tools are not implemented.

Implementation notes for maintainers are in [AGENTS.md](AGENTS.md) and
[docs/implementation-notes.md](docs/implementation-notes.md).
