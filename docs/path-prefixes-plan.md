# Path Prefixes Plan

## Current Status

2026-05-18 현재 첫 구현은 `find_symbols`에만 반영되어 있다.
`includePathPrefixes`는 root-relative path prefix 목록을 받아 Roslyn LS
`workspace/symbol` 응답을 MCP 쪽에서 후처리한다. 필터가 지정되면 location이
없는 workspace symbol은 제외된다.

`find_references`, `peek_references`, `find_implementations` 확장은 아직
남은 후보이며, 아래 계획은 그 후속 구현의 기준으로 유지한다.

## Decision

Add an optional `includePathPrefixes` parameter to multi-result read tools.

This lets the user or agent narrow Roslyn results to known repository areas
without asking the server to guess the right project or subsystem. The option is
an include-side result filter, not a new search mode.

Use the explicit `includePathPrefixes` name even though the MVP only implements
include filtering. Exclude filtering is a likely follow-up for tests, generated
code, samples, and fixtures, and `includePathPrefixes` leaves a clear symmetric
slot for a future `excludePathPrefixes` parameter.

## Contract

Proposed parameter:

```csharp
string[]? includePathPrefixes = null
```

Rules:

- Values use the same coordinate system as returned `file` fields: paths are
  relative to the configured server root, not necessarily to the loaded solution
  directory.
- For a nested loaded solution, use the prefix that matches returned files, such
  as `.local/real-repos/maui/src/Core/src` instead of `src/Core/src`.
- `/` and `\` are accepted and normalized.
- Matching uses the same path comparison semantics as the current platform.
- A result is kept when its root-relative file path exactly equals a prefix, or
  is under a prefix with a `/` segment boundary. `src/Foo` must not match
  `src/Foobar.cs`.
- `null` means no include path filtering.
- Empty arrays, empty entries, root-outside paths, and invalid paths return a
  validation error such as `invalid_path_prefix`.

The include filter is OR-based:

```json
{
  "includePathPrefixes": [
    "src/System.Management.Automation",
    "src/Microsoft.PowerShell.ConsoleHost"
  ]
}
```

## Initial Tool Scope

Planned broader scope is to add `includePathPrefixes` to tools that can return
many locations:

- `find_symbols` - implemented
- `find_references`
- `peek_references`
- `find_implementations`

Do not add it in the MVP to single-position, single-file, or traversal/store
tools:

- `hover`
- `document_symbols`
- `go_to_definition`
- `peek_definition`
- `get_call_hierarchy`
- `get_type_hierarchy`
- `diagnostics`
- `load_solution`
- `load_project`
- `list_workspaces`
- `get_workspace_status`

`go_to_definition` and `peek_definition` can be reconsidered later if partial or
generated definitions become noisy enough to justify it.

`get_call_hierarchy` and `get_type_hierarchy` should wait until
traversal-specific semantics are designed. Filtering returned edges during
traversal can otherwise either hide matching descendants/callers or expand raw
traversal cost.

`diagnostics` should wait until `DiagnosticStore` can apply severity and
include path filtering before `maxResults`. The current workspace diagnostics query
accepts `maxResults` at the store boundary, so tool-level post-filtering would
let prefix-excluded diagnostics consume the result budget.

## Semantics

`includePathPrefixes` is MCP-side post-filtering. It reduces returned noise but
does not reduce Roslyn LS request cost.

Filtering should happen before `maxResults` is applied:

```text
Roslyn LS result
  -> map root-internal locations
  -> existing kind/name/severity filters
  -> includePathPrefixes
  -> maxResults
```

Location-based helpers must not apply `maxResults` before `includePathPrefixes`.
For example, `MapLocations` currently maps locations and applies the cap in the
same pass; refactor that shape so prefix-excluded results cannot consume the
result budget before in-prefix results are considered.

For `peek_references`, snippets are read only for results that survive path
filtering.

Deferred call hierarchy behavior should apply the filter to edge counterpart
locations:

- `incoming`: filter by caller `from` location.
- `outgoing`: filter by callee `to` location.

Do not independently filter call-site files in the deferred call-hierarchy pass.
Call sites are returned only for edges whose counterpart survived the path
filter. Keep the prepared root symbol even if the root itself is outside the
prefixes, because the user explicitly selected that root position.

When `includePathPrefixes` is omitted, preserve current behavior for results
without a usable location, such as workspace symbols that Roslyn returns without
a file range. When `includePathPrefixes` is provided, exclude results that do
not have a root-relative file path to match.

Future `excludePathPrefixes` semantics should apply after the include filter and
before `maxResults`:

```text
included = includePathPrefixes is null || path is under any include prefix
excluded = excludePathPrefixes is not null && path is under any exclude prefix
keep = included && !excluded
```

Deferred diagnostics behavior should apply only with `scope: "workspace"`.
Combining it with file-specific diagnostics should return a validation error.

Metadata should keep existing count meanings and avoid widening every result DTO
in the MVP:

- Do not add a shared `totalBeforePathFilter` field yet.
- `totalKnown`: results after all filters including `includePathPrefixes`.
- `returned`: actual returned item count after `maxResults`.
- `truncated`: `totalKnown > returned`, plus any existing tool-specific
  truncation reason such as call-site truncation.

Implementations may keep local pre-path-filter counters if useful for tests or
future diagnostics, but they should not be exposed in the MVP contract.

## Implementation Notes

Likely code areas:

- Add a shared parser/normalizer for `includePathPrefixes`.
- Reuse `PathGuard` and existing root-relative path normalization.
- Apply filtering in each tool after current mapping to user-facing locations.
- Avoid duplicating prefix comparison logic in every tool.
- Do not wire `includePathPrefixes` into workspace diagnostics until
  `DiagnosticStore` can query by path before applying `maxResults`.

The MVP should not add `excludeTests` or `excludeGenerated`. Those are useful
but heuristic. Explicit include/exclude path prefixes are easier to reason about
and compose across repositories.

## Tests

Add focused tests for:

- accepts one prefix
- accepts multiple prefixes as OR
- normalizes slash styles
- rejects empty prefix entries
- rejects paths outside root
- keeps `src/Core` separate from `src/Core2` by segment-boundary matching
- applies before `maxResults`
- ensures prefix-excluded results do not consume the `maxResults` budget
- preserves current behavior when omitted
- excludes locationless `find_symbols` results when `includePathPrefixes` is provided
- works with `find_symbols`
- works with references/peek references

No Roslyn LS integration test is required for the prefix matching itself because
the behavior is MCP-side post-processing.
