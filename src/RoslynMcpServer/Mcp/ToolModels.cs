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
                RetryAfterMs: 1000),
            _ => new ToolError(exception.Code, exception.Message)
        };
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
    int Kind,
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
