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

## Install `roslyn-mcp-server`

Download the artifact for your OS from the
[v0.3.0 release](https://github.com/kirnot92/roslyn-mcp-server/releases/tag/v0.3.0)
or the [latest release](https://github.com/kirnot92/roslyn-mcp-server/releases/latest):

- `roslyn-mcp-server-v0.3.0-win-x64.zip`
- `roslyn-mcp-server-v0.3.0-linux-x64.tar.gz`
- `roslyn-mcp-server-v0.3.0-osx-x64.tar.gz`
- `roslyn-mcp-server-v0.3.0-osx-arm64.tar.gz`

Extract the archive and point your MCP client at the extracted executable:
`roslyn-mcp-server.exe` on Windows or `roslyn-mcp-server` on macOS/Linux.
If your macOS or Linux archive tool does not preserve executable permissions,
run `chmod +x roslyn-mcp-server`.

The release artifacts are self-contained and single-file. The server executable
does not require a separate .NET runtime installation, though `roslyn-language-server`
and the target C# repository still need a compatible .NET SDK/runtime environment.

For local development or unsupported platforms, build from source:

```powershell
git clone https://github.com/kirnot92/roslyn-mcp-server.git
cd roslyn-mcp-server
dotnet build .\src\RoslynMcpServer\RoslynMcpServer.csproj -c Release
```

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
      "command": "C:\\Tools\\roslyn-mcp-server\\roslyn-mcp-server.exe"
    }
  }
}
```

Unix example:

```json
{
  "mcpServers": {
    "roslyn": {
      "command": "/home/me/roslyn-mcp-server/src/RoslynMcpServer/bin/Release/net10.0/roslyn-mcp-server"
    }
  }
}
```

If the client cannot set the working directory, use `--root`:

```json
{
  "mcpServers": {
    "roslyn": {
      "command": "C:\\Tools\\roslyn-mcp-server\\roslyn-mcp-server.exe",
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
      "command": "/home/me/roslyn-mcp-server/src/RoslynMcpServer/bin/Release/net10.0/roslyn-mcp-server",
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
      "command": "C:\\Tools\\roslyn-mcp-server\\roslyn-mcp-server.exe",
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
   `go_to_definition`, `peek_definition`, `find_references`,
   `peek_references`, `find_implementations`, `get_call_hierarchy`,
   `get_type_hierarchy`, `find_symbols`, or `diagnostics`.

During startup, read tools may return `workspace_loading` instead of blocking.
After LSP initialize, large workspaces may remain in `WorkspaceWarming`. If
Roslyn LS reports project load errors, the state becomes `LoadedWithErrors` and
`get_workspace_status.warnings` includes the affected project paths and a short
cause. Read tools still return best-effort results with `workspaceState`,
`completeness`, and truncation metadata when applicable. While a workspace is
warming, retry hints use `retryAfterMs: 30000` so clients avoid tight polling on
large repositories.

`list_workspaces` uses git-based discovery by default when the root is a git
worktree. If git discovery cannot return candidates, the server retries with a
shallow filesystem BFS at depth 3. Git discovery uses an internal 30 second
budget rather than a public CLI option. Passing `maxDepth` skips git and
performs bounded filesystem BFS to that depth, which is useful when a large
repository has near-root solution files and git is slow in the current
environment.

`document_symbols` returns a bounded file-level symbol tree. Its optional
`kindFilter` accepts the same MCP symbol kind names as `find_symbols`, such as
`class`, `interface`, `method`, `property`, `field`, `enum`, `enumMember`,
`constructor`, `event`, `operator`, `struct`, or `typeParameter`. When a
filtered descendant matches, ancestor symbols are retained as context, but
non-matching child branches are pruned. `totalUnfilteredKnown` reports mappable
document symbols before filtering; `totalKnown`, `returned`, and `truncated`
describe the filtered response tree including retained context ancestors.
`document_symbols`, `find_references`, and `peek_references` accept optional
`timeoutSec`. It defaults to 10 seconds and is capped by the server; increase it
only for known large files or expensive reference searches.

`find_implementations` is position-based, not a symbol-name search. To discover
all implementations, call it on the interface, abstract member, or base contract
position, for example the `ICalculator` identifier in `interface ICalculator`,
the `Add` identifier in an interface method declaration, or a usage whose static
type is the contract. If it is called on a concrete class or concrete method
implementation such as `class Calculator : ICalculator` or `Calculator.Add`, the
language server may correctly return only that concrete implementation. The tool
description and top-level tool result include a `usageHint` with this call-site
guidance. Use `includePathPrefixes` to keep only implementation locations under
specific root-relative directories, such as production `src/` paths.
When the result metadata says `completeness: "partial"` or includes
`retryAfterMs`, retry after workspace warming before treating missing
implementations as absent.

`get_call_hierarchy` is position-based. Call it on a method, constructor, or
property accessor position. It supports `direction: "incoming"`, `"outgoing"`,
or `"both"`, and only returns direct depth-1 callers/callees. Its optional
`kindFilter` accepts edge counterpart MCP symbol kind names: `method`,
`constructor`, `property`, `event`, `operator`, and `field`, case-insensitively.
For incoming results the filter applies to the caller (`from`) symbol; for
outgoing results it applies to the callee/accessed (`to`) symbol; for `both`,
each direction uses its own counterpart. The server still asks Roslyn LS for the
same call hierarchy data and applies the filter to mappable edges before
`maxResults`, so it reduces returned noise but does not reduce Roslyn LS request
cost. `includePathPrefixes` applies to the same direction-specific counterpart:
incoming filters caller files and outgoing filters callee/accessed files. Call
site files are not filtered independently. `totalKnown`, `returned`, and
`truncated` describe the filtered mappable edge set, while
`totalUnfilteredKnown` reports mappable edges before MCP-side filters. Use
incoming for impact analysis and outgoing for dependencies. It is static Roslyn
context, not a runtime-complete graph, and results can be partial or truncated
while the workspace is warming or when limits apply. For most code review call
graph checks, start with `kindFilter: ["method"]` to avoid property and field
access noise. Add `constructor` or `property` only when object creation or
accessor calls are part of the question.

`get_type_hierarchy` is position-based. Call it on a type identifier when you
need base types, derived types, or interface implementation relationships. It
supports `direction: "supertypes"`, `"subtypes"`, or `"both"`, with
`supertypes` as the default. `maxDepth` controls bounded breadth-first
traversal and is capped by the server; `maxResults` caps returned hierarchy
edges. The server preserves the original LSP type hierarchy items for follow-up
requests, filters non-file or outside-root items from returned edges, and marks
results truncated when the edge cap prevents later directions or deeper
follow-up traversal. `includePathPrefixes` filters discovered follow-up type
locations before `maxResults`; excluded follow-up types are not traversed
further, even if their descendants might have matched the prefix. Use
`get_call_hierarchy` for caller/callee analysis; `get_type_hierarchy` is only
for inheritance and implementation structure.

`find_symbols` is a workspace symbol-name search. Its optional `kindFilter`
accepts MCP symbol kind names such as `class`, `interface`, `method`,
`property`, `field`, `enumMember`, and `typeParameter`, case-insensitively.
`matchMode` accepts `default`, `exact`, `prefix`, or `contains`; omit it to keep
Roslyn LS default matching. Default matching keeps Roslyn LS fuzzy candidates
but ranks exact, prefix, and contains name matches ahead of fuzzy-only results
before applying `maxResults`. `includePathPrefixes` accepts root-relative path
prefixes such as `src/RoslynMcpServer/Mcp` and keeps only symbols with locations
at or under one of those prefixes. Slash styles are normalized, matching uses
path segment boundaries, and symbols without a mappable location are excluded
when `includePathPrefixes` is provided. The server still calls Roslyn LS
`workspace/symbol` with the same query and applies `kindFilter`, `matchMode`,
and `includePathPrefixes` to mappable results before `maxResults`, so these
options reduce returned noise but do not reduce Roslyn LS search cost.
`totalKnown`, `returned`, and `truncated` describe the filtered mappable result
set, while `totalUnfilteredKnown` reports mappable symbols before those filters.
The same path prefix coordinate system is used by references, implementations,
call hierarchy, and type hierarchy tools where `includePathPrefixes` is
available.

## Code Review Preflight

For code review workflows, start workspace loading before reading the full diff:

1. Inspect only the changed file list first, for example `git diff --name-only`.
2. Call `load_solution` or the relevant `load_project` immediately.
3. Do not wait for `Ready`; read the diff while the workspace is warming.
4. Use `go_to_definition`, `peek_definition`, `hover`, `find_references`,
   `peek_references`, and `find_implementations` from the changed code once the
   review needs semantic context.

This lets Roslyn LS use the human/agent diff-reading time for background project
load and keeps the later navigation calls from paying the full cold-start cost.

## Notes For Large Repositories

Workspace discovery is bounded by internal 30 second scan budgets, optional `list_workspaces.maxDepth`, and candidate limits.
Large result tools include metadata such as `totalKnown`, `returned`, and
`truncated`. A truncated result is expected behavior, not a transport failure.
If workspace discovery times out, check `truncated`, `truncationReason`, and
`elapsed` on `list_workspaces` or `get_workspace_status.workspaces`. The common
causes are cold git state, slow or network-backed disks, antivirus scanning,
many untracked files, git being unavailable, or using a root that is wider than
the repository you meant to inspect. If the default shallow fallback misses a
known workspace file, retry `list_workspaces` with a larger `maxDepth`.

Diagnostics are currently based on diagnostics already published by Roslyn LS
and processed by the bounded background diagnostics queue. The server does not
perform unbounded workspace-wide diagnostics computation. If the queue is full,
the incoming publish diagnostics notification is dropped and existing pending
notifications are kept. `get_workspace_status` exposes the queue capacity,
pending, processed, dropped, and stale notification counts plus the
`drop_newest_when_full` overflow policy. `dropped` counts incoming notifications
that could not be enqueued because the queue was full; stale generation
notifications and workspace reset clears are reported as `stale`.
