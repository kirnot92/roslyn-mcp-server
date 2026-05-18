using RoslynMcpServer.Workspace;

namespace RoslynMcpServer.Mcp;

public sealed partial class NavigationTools
{
    private static ReadToolMetadata CreateMetadata(WorkspaceLoadState state, ToolKind toolKind, bool truncated) =>
        state switch
        {
            WorkspaceLoadState.Ready => new ReadToolMetadata(
                state.ToString(),
                toolKind is ToolKind.Symbols ? "unknown" : "complete",
                toolKind is ToolKind.Symbols
                    ? "The language server does not report workspace symbol index completeness."
                    : null,
                null,
                truncated),
            WorkspaceLoadState.WorkspaceWarming => new ReadToolMetadata(
                state.ToString(),
                "partial",
                toolKind switch
                {
                    ToolKind.DocumentSymbols => "Workspace is still warming; symbols from projects not loaded yet may be missing.",
                    ToolKind.Hover => "Workspace is still warming; hover may not include complete semantic information.",
                    ToolKind.Definition => "Workspace is still warming; definitions from projects not loaded yet may be missing.",
                    ToolKind.References => "Workspace is still warming; cross-project references may be missing.",
                    ToolKind.Implementations => "Workspace is still warming; implementations from projects not loaded yet may be missing.",
                    ToolKind.CallHierarchy => "Workspace is still warming; call hierarchy may miss callers or callees from projects not loaded yet.",
                    ToolKind.TypeHierarchy => "Workspace is still warming; type hierarchy may miss projects not loaded yet.",
                    ToolKind.Symbols => "Workspace is still warming; the workspace symbol index may be incomplete.",
                    _ => "Workspace is still warming; results may be incomplete."
                },
                ToolRetryHints.WorkspaceWarmingMs,
                truncated),
            WorkspaceLoadState.LoadedWithErrors => new ReadToolMetadata(
                state.ToString(),
                "partial",
                toolKind switch
                {
                    ToolKind.DocumentSymbols => "Workspace loaded with project errors; symbols from failed projects may be missing.",
                    ToolKind.Hover => "Workspace loaded with project errors; hover may not include complete semantic information.",
                    ToolKind.Definition => "Workspace loaded with project errors; definitions from failed projects may be missing.",
                    ToolKind.References => "Workspace loaded with project errors; cross-project references may be missing.",
                    ToolKind.Implementations => "Workspace loaded with project errors; implementations from failed projects may be missing.",
                    ToolKind.CallHierarchy => "Workspace loaded with project errors; call hierarchy may miss callers or callees from failed projects.",
                    ToolKind.TypeHierarchy => "Workspace loaded with project errors; type hierarchy may miss failed projects.",
                    ToolKind.Symbols => "Workspace loaded with project errors; workspace symbol results may be incomplete or empty. Call get_workspace_status for load warnings.",
                    _ => "Workspace loaded with project errors; results may be incomplete."
                },
                null,
                truncated),
            WorkspaceLoadState.LspReady => new ReadToolMetadata(
                state.ToString(),
                toolKind is ToolKind.References or ToolKind.Implementations or ToolKind.CallHierarchy or ToolKind.TypeHierarchy ? "partial" : "unknown",
                toolKind switch
                {
                    ToolKind.References => "The language server is ready, but cross-project references may be missing until workspace loading completes.",
                    ToolKind.Implementations => "The language server is ready, but cross-project implementations may be missing until workspace loading completes.",
                    ToolKind.CallHierarchy => "The language server is ready, but call hierarchy may be incomplete until workspace loading completes.",
                    ToolKind.TypeHierarchy => "The language server is ready, but type hierarchy may be incomplete until workspace loading completes.",
                    ToolKind.Symbols => "The language server is ready, but workspace symbol index completeness is not known yet.",
                    _ => "The language server is ready, but workspace completeness is not known yet."
                },
                ToolRetryHints.WorkspaceWarmingMs,
                truncated),
            _ => new ReadToolMetadata(
                state.ToString(),
                "unknown",
                null,
                null,
                truncated)
        };
}
