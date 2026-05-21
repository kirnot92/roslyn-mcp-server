using System.Text;
using RoslynMcpServer.Infrastructure;
using RoslynMcpServer.Workspace;
using static RoslynMcpServer.Mcp.NavigationToolLimits;

namespace RoslynMcpServer.Mcp;

internal static class SourceSnippetReader
{
    internal static async Task<SourceSnippetReadResult> ReadAsync(
        WorkspaceRoot workspaceRoot,
        DocumentStateManager documents,
        NavigationLocation location,
        int contextLines,
        string rangeDescription,
        CancellationToken cancellationToken)
    {
        string fullPath;
        try
        {
            fullPath = workspaceRoot.ResolveFileInput(location.File);
        }
        catch (UserFacingException ex)
        {
            return new SourceSnippetReadResult(null, new SourceSnippetError(ex.Code, ex.Message));
        }

        try
        {
            var info = new FileInfo(fullPath);
            if (!info.Exists)
            {
                return new SourceSnippetReadResult(
                    null,
                    new SourceSnippetError("file_not_found", $"File was not found: {location.File}"));
            }

            if (info.Length > documents.MaxDocumentBytes)
            {
                return new SourceSnippetReadResult(
                    null,
                    new SourceSnippetError(
                        "document_too_large",
                        $"File exceeds the configured MaxDocumentBytes limit ({documents.MaxDocumentBytes} bytes): {location.File}"));
            }

            var lines = await File.ReadAllLinesAsync(fullPath, cancellationToken);
            if (location.Range.StartLine < 1 ||
                location.Range.StartLine > lines.Length ||
                location.Range.EndLine < location.Range.StartLine)
            {
                return new SourceSnippetReadResult(
                    null,
                    new SourceSnippetError(
                        "invalid_range",
                        $"{rangeDescription} range is outside the readable source file: {location.File}"));
            }

            var startLine = Math.Max(location.Range.StartLine - contextLines, 1);
            var requestedEndLine = Math.Min(location.Range.EndLine + contextLines, lines.Length);
            if (requestedEndLine < startLine)
            {
                return new SourceSnippetReadResult(
                    null,
                    new SourceSnippetError(
                        "invalid_range",
                        $"{rangeDescription} range is outside the readable source file: {location.File}"));
            }

            var snippet = BuildSourceSnippet(lines, startLine, requestedEndLine);
            return new SourceSnippetReadResult(
                new SourceSnippet(startLine, snippet.EndLine, snippet.Text, snippet.Truncated),
                null);
        }
        catch (IOException ex)
        {
            return new SourceSnippetReadResult(
                null,
                new SourceSnippetError("file_read_error", $"Could not read {rangeDescription.ToLowerInvariant()} source: {ex.Message}"));
        }
        catch (UnauthorizedAccessException ex)
        {
            return new SourceSnippetReadResult(
                null,
                new SourceSnippetError("file_read_error", $"Could not read {rangeDescription.ToLowerInvariant()} source: {ex.Message}"));
        }
    }

    private static SourceSnippetBuildResult BuildSourceSnippet(string[] lines, int startLine, int requestedEndLine)
    {
        var builder = new StringBuilder(Math.Min(MaxPeekSnippetCharacters, 1024));
        var endLine = startLine;
        var truncated = false;

        for (var lineNumber = startLine; lineNumber <= requestedEndLine; lineNumber++)
        {
            if (lineNumber > startLine)
            {
                var separator = Environment.NewLine;
                var remainingForSeparator = MaxPeekSnippetCharacters - builder.Length;
                if (separator.Length > remainingForSeparator)
                {
                    if (remainingForSeparator > 0)
                    {
                        builder.Append(separator.AsSpan(0, remainingForSeparator));
                    }

                    truncated = true;
                    break;
                }

                builder.Append(separator);
            }

            var line = lines[lineNumber - 1];
            var remainingForLine = MaxPeekSnippetCharacters - builder.Length;
            if (line.Length > remainingForLine)
            {
                if (remainingForLine > 0)
                {
                    builder.Append(line.AsSpan(0, remainingForLine));
                }

                endLine = lineNumber;
                truncated = true;
                break;
            }

            builder.Append(line);
            endLine = lineNumber;
        }

        return new SourceSnippetBuildResult(builder.ToString(), endLine, truncated);
    }
}
