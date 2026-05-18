# find_symbols Match Mode Plan

## Decision

Add an optional `matchMode` parameter to `find_symbols`.

This is preferred over adding a separate `search_everywhere` tool because the
core use case is still Roslyn workspace symbol search. `matchMode` narrows
existing `find_symbols` results and reduces noise without broadening the MCP
server into file/content/action search.

## Contract

Proposed tool signature:

```csharp
FindSymbols(
    string query,
    int? maxResults = null,
    string[]? kindFilter = null,
    string? matchMode = null,
    CancellationToken cancellationToken = default)
```

Supported values:

- `default`: keep current behavior, meaning Roslyn LS decides how to match
  `workspace/symbol` candidates for the query.
- `exact`: keep symbols whose `Name` equals `query`.
- `prefix`: keep symbols whose `Name` starts with `query`.
- `contains`: keep symbols whose `Name` contains `query`.

Matching should be case-insensitive with `StringComparison.OrdinalIgnoreCase`.
Omitted or `null` `matchMode` means `default`. Explicit empty, whitespace, or
unknown values return `invalid_match_mode`.

## Important Semantics

`matchMode` is MCP-side post-filtering. Roslyn LS still receives the same
standard LSP request:

```json
{ "query": "WorkspaceSession" }
```

This means:

- `matchMode` reduces returned noise.
- It does not reduce Roslyn LS request cost.
- It cannot recover symbols that Roslyn LS did not return for the query.
- `default` must preserve the current behavior exactly.
- `contains` is not the same as `default`. Local smoke shows Roslyn LS returns
  matches that are broader than simple symbol-name contains matching. For
  example, query `Session` can return symbols such as `Version`, while
  `contains` would only keep symbols whose simple name contains `Session`.

Filtering order should be:

```text
Roslyn LS workspace/symbol(query)
  -> map current mappable workspace symbols
  -> kindFilter
  -> matchMode
  -> maxResults
```

Keep existing workspace symbol mapping behavior: symbols without a location, or
with a location that has no range, can still be returned. Only non-file and
outside-root locations should be excluded as they are today.

Metadata should keep the current meaning:

- `totalUnfilteredKnown`: mappable Roslyn LS results before `kindFilter` and
  `matchMode`.
- `totalKnown`: results after `kindFilter` and `matchMode`.
- `returned`: actual returned item count.
- `truncated`: `totalKnown > returned`.

## Implementation Notes

Likely code areas:

- `src/RoslynMcpServer/Mcp/NavigationTools.cs`
  - Add optional MCP parameter and description.
- `src/RoslynMcpServer/Mcp/Navigation/NavigationTools.Options.cs`
  - Add `ParseSymbolMatchMode`.
  - Trim and parse values case-insensitively.
- `src/RoslynMcpServer/Mcp/Navigation/NavigationTools.Types.cs`
  - Add a small private enum, probably `SymbolMatchMode`.
- `src/RoslynMcpServer/Mcp/Navigation/NavigationTools.WorkspaceSymbols.cs`
  - Apply match filtering after kind filtering and before result limiting.
- `src/RoslynMcpServer/Mcp/ToolModels.cs`
  - No result DTO change required unless implementation wants to echo
    `matchMode`; prefer no output shape change for now.

Do not add postfix/suffix matching in the MVP. `contains` covers the common
suffix cases such as `Controller`, `Options`, `Exception`, and `Attribute`.

Preserve existing large-repo safeguards:

- query trim and minimum length validation
- `workspace/symbol` timeout
- `isExpensive: true`
- default and hard result caps
- existing `WorkspaceWarming` and `LoadedWithErrors` metadata behavior

`matchMode` applies to `WorkspaceSymbolItem.Name`, the simple symbol name. It
does not match `ContainerName`, qualified names, file paths, or source text.

## Tests

Add focused tests near existing `FindSymbols_*` tests:

- `FindSymbols_DefaultMatchModePreservesCurrentBehavior`
- `FindSymbols_ExactMatchModeKeepsOnlyExactNames`
- `FindSymbols_PrefixMatchModeKeepsOnlyPrefixNames`
- `FindSymbols_ContainsMatchModeKeepsOnlyContainingNames`
- `FindSymbols_MatchModeIsCaseInsensitive`
- `FindSymbols_AppliesMatchModeAfterKindFilter`
- `FindSymbols_ReturnsValidationErrorForUnknownMatchMode`
- `FindSymbols_MatchModeTrimsAndParsesCaseInsensitively`
- `FindSymbols_MetadataCountsAfterMatchModeFiltering`
- `FindSymbols_DoesNotSendMatchModeToRoslynLs`
- `FindSymbols_MaxResultsAppliesAfterMatchMode`
- `FindSymbols_DefaultKeepsRoslynLsResultsThatContainsWouldFilterOut`

No Roslyn LS integration test is required for the mode itself because it is
post-processing. Existing integration coverage for `find_symbols` is enough.

## Documentation

Update after implementation:

- `README.md` tool list or short `find_symbols` description.
- `docs/usage.md` `find_symbols` guidance.
- `docs/implementation-notes.md` latest status.
- `docs/architecture.md` `find_symbols` contract.

Docs must state that `matchMode` is applied after Roslyn LS returns candidates.
