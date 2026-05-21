using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynMcpServer.Infrastructure;
using RoslynMcpServer.Lsp;
using static RoslynMcpServer.Mcp.NavigationToolLimits;

namespace RoslynMcpServer.Mcp;

public sealed partial class NavigationTools
{
    [McpServerTool(Name = "hover")]
    [Description("Use when you have an exact C# source position and need compiler-backed type, signature, or documentation info.")]
    public async Task<object> Hover(
        [Description(FileParameterDescription)]
        string file,
        [Description(LineParameterDescription)]
        int line,
        [Description(ColumnParameterDescription)]
        int column,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var request = await NavigationPositionRequests.PrepareAsync(this.session, this.documents, file, line, column, cancellationToken);
            var response = await request.Context.Handle.Client.RequestAsync(
                "textDocument/hover",
                new
                {
                    textDocument = new TextDocumentIdentifier(request.Document.Uri),
                    position = request.Position
                },
                NavigationTimeout,
                cancellationToken);

            var hover = HoverMapper.Map(response);
            var metadata = NavigationToolMetadata.Create(request.Context.State, ToolKind.Hover, hover.Truncated);
            return new HoverResult(
                hover.Contents,
                hover.Kind,
                hover.Range,
                metadata.WorkspaceState,
                metadata.Completeness,
                metadata.Reason,
                metadata.RetryAfterMs,
                metadata.Truncated);
        }
        catch (UserFacingException ex)
        {
            return ToolError.FromException(ex);
        }
    }

    [McpServerTool(Name = "go_to_definition")]
    [Description("Use when you have an exact C# source position and need compiler-backed definition locations only. Prefer peek_definition when you also need source snippets.")]
    public async Task<object> GoToDefinition(
        [Description(FileParameterDescription)]
        string file,
        [Description(LineParameterDescription)]
        int line,
        [Description(ColumnParameterDescription)]
        int column,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var request = await NavigationPositionRequests.PrepareAsync(this.session, this.documents, file, line, column, cancellationToken);
            var response = await request.Context.Handle.Client.RequestAsync(
                "textDocument/definition",
                new
                {
                    textDocument = new TextDocumentIdentifier(request.Document.Uri),
                    position = request.Position
                },
                DefinitionTimeout,
                cancellationToken);

            var locations = NavigationLocationMapper.Map(this.workspaceRoot, response, "textDocument/definition", maxResults: null);
            var metadata = NavigationToolMetadata.Create(request.Context.State, ToolKind.Definition, truncated: false);
            return new DefinitionResult(
                locations.Items,
                metadata.WorkspaceState,
                metadata.Completeness,
                metadata.Reason,
                metadata.RetryAfterMs,
                metadata.Truncated);
        }
        catch (UserFacingException ex)
        {
            return ToolError.FromException(ex);
        }
    }

    [McpServerTool(Name = "peek_definition")]
    [Description("Use when you have an exact C# source position and need definition locations with bounded source snippets. Prefer go_to_definition when locations alone are enough.")]
    public async Task<object> PeekDefinition(
        [Description(FileParameterDescription)]
        string file,
        [Description(LineParameterDescription)]
        int line,
        [Description(ColumnParameterDescription)]
        int column,
        [Description("Non-negative number of surrounding lines to include in each source snippet; defaults to 3 and is capped by the server.")]
        int? contextLines = null,
        [Description("Positive definition result cap; defaults to 20 and is capped by the server.")]
        int? maxDefinitions = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var effectiveContextLines = NavigationToolOptions.NormalizePeekContextLines(contextLines);
            var effectiveMaxDefinitions = NavigationToolOptions.NormalizePeekMaxDefinitions(maxDefinitions);
            var request = await NavigationPositionRequests.PrepareAsync(this.session, this.documents, file, line, column, cancellationToken);
            var response = await request.Context.Handle.Client.RequestAsync(
                "textDocument/definition",
                new
                {
                    textDocument = new TextDocumentIdentifier(request.Document.Uri),
                    position = request.Position
                },
                DefinitionTimeout,
                cancellationToken);

            var locations = NavigationLocationMapper.Map(this.workspaceRoot, response, "textDocument/definition", effectiveMaxDefinitions);
            var items = new List<PeekDefinitionItem>(locations.Items.Count);
            foreach (var location in locations.Items)
            {
                var snippet = await SourceSnippetReader.ReadAsync(this.workspaceRoot, this.documents, location, effectiveContextLines, "Definition", cancellationToken);
                items.Add(new PeekDefinitionItem(
                    location.File,
                    location.Line,
                    location.Column,
                    location.Range,
                    snippet.Snippet,
                    snippet.Error));
            }

            var metadata = NavigationToolMetadata.Create(request.Context.State, ToolKind.Definition, locations.Truncated);
            return new PeekDefinitionResult(
                items,
                locations.TotalKnown,
                locations.Returned,
                metadata.WorkspaceState,
                metadata.Completeness,
                metadata.Reason,
                metadata.RetryAfterMs,
                metadata.Truncated);
        }
        catch (UserFacingException ex)
        {
            return ToolError.FromException(ex);
        }
    }
}
