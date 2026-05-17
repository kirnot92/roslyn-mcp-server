using RoslynMcpServer.Infrastructure;
using RoslynMcpServer.Lsp;

namespace RoslynMcpServer.Mcp;

public sealed record ToolError(string Error, string Message, string? WorkspaceState = null, int? RetryAfterMs = null)
{
    public static ToolError FromException(UserFacingException exception) =>
        exception.Code switch
        {
            "workspace_loading" => new ToolError(
                exception.Code,
                exception.Message,
                Workspace.WorkspaceLoadState.StartingLanguageServer.ToString(),
                RetryAfterMs: ToolRetryHints.WorkspaceStartupMs),
            _ => new ToolError(exception.Code, exception.Message)
        };
}

public static class ToolRetryHints
{
    public const int WorkspaceStartupMs = 1000;
    public const int WorkspaceWarmingMs = 30_000;
}

public sealed record ReadToolMetadata(
    string WorkspaceState,
    string Completeness,
    string? Reason,
    int? RetryAfterMs,
    bool Truncated);

public sealed record McpRange(int StartLine, int StartColumn, int EndLine, int EndColumn);

public sealed record DocumentSymbolItem(
    string Name,
    SymbolKind Kind,
    string KindName,
    McpRange Range,
    McpRange SelectionRange,
    string? Detail,
    IReadOnlyList<DocumentSymbolItem> Children);

public sealed record DocumentSymbolsResult(
    IReadOnlyList<DocumentSymbolItem> Items,
    int TotalKnown,
    int Returned,
    string WorkspaceState,
    string Completeness,
    string? Reason,
    int? RetryAfterMs,
    bool Truncated);

public sealed record HoverResult(
    string? Contents,
    string? Kind,
    McpRange? Range,
    string WorkspaceState,
    string Completeness,
    string? Reason,
    int? RetryAfterMs,
    bool Truncated);

public sealed record NavigationLocation(
    string File,
    int Line,
    int Column,
    McpRange Range);

public sealed record DefinitionResult(
    IReadOnlyList<NavigationLocation> Items,
    string WorkspaceState,
    string Completeness,
    string? Reason,
    int? RetryAfterMs,
    bool Truncated);

public sealed record SourceSnippet(
    int StartLine,
    int EndLine,
    string Text,
    bool Truncated);

public sealed record SourceSnippetError(
    string Error,
    string Message);

public sealed record PeekDefinitionItem(
    string File,
    int Line,
    int Column,
    McpRange Range,
    SourceSnippet? Snippet,
    SourceSnippetError? SnippetError);

public sealed record PeekDefinitionResult(
    IReadOnlyList<PeekDefinitionItem> Items,
    int TotalKnown,
    int Returned,
    string WorkspaceState,
    string Completeness,
    string? Reason,
    int? RetryAfterMs,
    bool Truncated);

public sealed record ReferencesResult(
    IReadOnlyList<NavigationLocation> Items,
    int TotalKnown,
    int Returned,
    string WorkspaceState,
    string Completeness,
    string? Reason,
    int? RetryAfterMs,
    bool Truncated);

public sealed record ImplementationsResult(
    IReadOnlyList<NavigationLocation> Items,
    int TotalKnown,
    int Returned,
    string UsageHint,
    string WorkspaceState,
    string Completeness,
    string? Reason,
    int? RetryAfterMs,
    bool Truncated);

public sealed record WorkspaceSymbolItem(
    string Name,
    SymbolKind Kind,
    string KindName,
    string? ContainerName,
    NavigationLocation? Location);

public sealed record FindSymbolsResult(
    IReadOnlyList<WorkspaceSymbolItem> Items,
    int TotalKnown,
    int TotalUnfilteredKnown,
    int Returned,
    string WorkspaceState,
    string Completeness,
    string? Reason,
    int? RetryAfterMs,
    bool Truncated);

public sealed record DiagnosticItem(
    string File,
    McpRange Range,
    int Line,
    int Column,
    string Severity,
    string? Code,
    string? Source,
    string Message);

public sealed record DiagnosticsResult(
    IReadOnlyList<DiagnosticItem> Items,
    int TotalKnown,
    int Returned,
    string WorkspaceState,
    string Completeness,
    string? Reason,
    int? RetryAfterMs,
    bool Truncated,
    DateTimeOffset? LastUpdatedAt);

public static class PositionMapper
{
    public static Position ToLspPosition(int line, int column)
    {
        if (line < 1 || column < 1)
        {
            throw new UserFacingException(
                "invalid_position",
                "line and column must be 1-based positive integers.");
        }

        return new Position(line - 1, column - 1);
    }

    public static McpRange ToMcpRange(Lsp.Range range) =>
        new(
            range.Start.Line + 1,
            range.Start.Character + 1,
            range.End.Line + 1,
            range.End.Character + 1);
}
