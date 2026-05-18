using System.Text.Json;
using RoslynMcpServer.Lsp;
using RoslynMcpServer.Workspace;

namespace RoslynMcpServer.Mcp;

public sealed partial class NavigationTools
{
    private sealed record DocumentSymbolMapResult(
        IReadOnlyList<DocumentSymbolItem> Items,
        int TotalKnown,
        int TotalUnfilteredKnown,
        int Returned,
        bool Truncated);

    private sealed record HoverMapResult(
        string? Contents,
        string? Kind,
        McpRange? Range,
        bool Truncated);

    private sealed record HoverContentMapResult(
        string? Contents,
        string? Kind,
        bool Truncated);

    private sealed record LocationMapResult(
        IReadOnlyList<NavigationLocation> Items,
        int TotalKnown,
        int TotalUnfilteredKnown,
        int Returned,
        bool Truncated);

    private sealed record SourceSnippetReadResult(
        SourceSnippet? Snippet,
        SourceSnippetError? Error);

    private sealed record SourceSnippetBuildResult(
        string Text,
        int EndLine,
        bool Truncated);

    private sealed record WorkspaceSymbolMapResult(
        IReadOnlyList<WorkspaceSymbolItem> Items,
        int TotalKnown,
        int TotalUnfilteredKnown,
        int Returned,
        bool Truncated);

    private sealed record PreparedCallHierarchyItem(
        CallHierarchySymbol Symbol,
        JsonElement OriginalItem);

    private sealed record CallHierarchyCallSiteMapResult(
        IReadOnlyList<CallHierarchyCallSite> Items,
        int TotalKnown,
        bool Truncated);

    private sealed record PreparedTypeHierarchyItem(
        TypeHierarchySymbol Symbol,
        JsonElement OriginalItem);

    private sealed record TypeHierarchyQueueItem(
        TypeHierarchySymbol Symbol,
        JsonElement OriginalItem,
        int Depth);

    private sealed class TypeHierarchyTraversalState
    {
        public int TotalUnfilteredKnown { get; set; }
        public int TotalKnown { get; set; }
        public int Returned { get; set; }
        public bool HitResultLimit { get; set; }
    }

    private sealed record PositionRequestContext(
        ReadToolContext Context,
        OpenDocumentState Document,
        Position Position);

    private enum ToolKind
    {
        DocumentSymbols,
        Hover,
        Definition,
        References,
        Implementations,
        CallHierarchy,
        TypeHierarchy,
        Symbols
    }

    private enum CallHierarchyDirection
    {
        Incoming,
        Outgoing,
        Both
    }

    private enum TypeHierarchyDirection
    {
        Supertypes,
        Subtypes,
        Both
    }

    private enum SymbolMatchMode
    {
        Default,
        Exact,
        Prefix,
        Contains
    }
}
