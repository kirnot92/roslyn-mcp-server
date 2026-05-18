# solution_overview Evaluation

## Decision

Do not implement `solution_overview` in M3.

M3 user and Agent CLI usability work is complete. The decision for that milestone
was to avoid adding a new summary tool before broader implementation
hardening. The current workspace and read-only tools are enough for the recommended
client flow:

1. `list_workspaces`
2. `load_solution` or `load_project`
3. `get_workspace_status`
4. targeted read tools

## Existing Coverage

`list_workspaces` already exposes discovered `.sln`, `.slnx`, and `.csproj`
candidates with scan truncation metadata. `get_workspace_status` exposes the
selected target, workspace state, process readiness, open document count, and known
diagnostics summary. Targeted read tools then provide file symbols, hover,
definition, references, workspace symbol search, and diagnostics.

For an Agent CLI, this is enough to decide the next action without adding another
summary tool during M3.

## Remaining Gap

A future `solution_overview` could still be useful as a convenience layer after
the server is stable. It would summarize the selected workspace, project list,
target frameworks, and perhaps high-level dependency shape. That is not a blocker
for current usability because it can be approximated with existing workspace tools
and targeted reads.

## Recommended Milestone

Keep `solution_overview` as a post-M4 or separate milestone candidate, after:

- the completed diagnostics notification offload behavior has been validated in larger repositories
- additional real MCP client smoke has been repeated
- large repository default tuning is revisited

If implemented later, it should remain read-only, bounded, and explicit about
partial data while the workspace is warming.
