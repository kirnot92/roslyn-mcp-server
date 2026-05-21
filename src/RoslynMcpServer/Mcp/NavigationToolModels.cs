using System.Text.Json;
using RoslynMcpServer.Lsp;
using RoslynMcpServer.Workspace;

namespace RoslynMcpServer.Mcp;

internal sealed record DocumentSymbolMapResult(
    IReadOnlyList<DocumentSymbolItem> Items,
    int TotalKnown,
    int TotalUnfilteredKnown,
    int Returned,
    bool Truncated);

internal sealed record HoverMapResult(
    string? Contents,
    string? Kind,
    McpRange? Range,
    bool Truncated);

internal sealed record HoverContentMapResult(
    string? Contents,
    string? Kind,
    bool Truncated);

internal sealed record LocationMapResult(
    IReadOnlyList<NavigationLocation> Items,
    int TotalKnown,
    int TotalUnfilteredKnown,
    int Returned,
    bool Truncated);

internal sealed record SourceSnippetReadResult(
    SourceSnippet? Snippet,
    SourceSnippetError? Error);

internal sealed record SourceSnippetBuildResult(
    string Text,
    int EndLine,
    bool Truncated);

internal sealed record WorkspaceSymbolMapResult(
    IReadOnlyList<WorkspaceSymbolItem> Items,
    int TotalKnown,
    int TotalUnfilteredKnown,
    int Returned,
    bool Truncated);

internal sealed record PreparedCallHierarchyItem(
    CallHierarchySymbol Symbol,
    JsonElement OriginalItem);

internal sealed record CallHierarchyCallSiteMapResult(
    IReadOnlyList<CallHierarchyCallSite> Items,
    int TotalKnown,
    bool Truncated);

internal sealed record PreparedTypeHierarchyItem(
    TypeHierarchySymbol Symbol,
    JsonElement OriginalItem);

internal sealed record TypeHierarchyQueueItem(
    TypeHierarchySymbol Symbol,
    JsonElement OriginalItem,
    int Depth);

internal sealed class TypeHierarchyTraversalState
{
    public int TotalUnfilteredKnown { get; set; }
    public int TotalKnown { get; set; }
    public int Returned { get; set; }
    public bool HitResultLimit { get; set; }
}

internal sealed record PositionRequestContext(
    ReadToolContext Context,
    OpenDocumentState Document,
    Position Position);

internal enum ToolKind
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

internal enum CallHierarchyDirection
{
    Incoming,
    Outgoing,
    Both
}

internal enum TypeHierarchyDirection
{
    Supertypes,
    Subtypes,
    Both
}

internal enum SymbolMatchMode
{
    Default,
    Exact,
    Prefix,
    Contains
}
