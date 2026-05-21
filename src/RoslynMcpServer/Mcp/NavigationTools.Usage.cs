using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynMcpServer.Infrastructure;
using RoslynMcpServer.Lsp;
using static RoslynMcpServer.Mcp.NavigationToolLimits;

namespace RoslynMcpServer.Mcp;

public sealed partial class NavigationTools
{
    [McpServerTool(Name = "find_references")]
    [Description("Use when you have an exact C# source position and need compiler-backed reference locations for that symbol. This can be expensive; check completeness and truncated before treating results as final.")]
    public async Task<object> FindReferences(
        [Description(FileParameterDescription)]
        string file,
        [Description(LineParameterDescription)]
        int line,
        [Description(ColumnParameterDescription)]
        int column,
        [Description("Whether to include the symbol declaration in the returned reference locations; defaults to true.")]
        bool includeDeclaration = true,
        [Description("Positive reference result cap; defaults to 200 and is capped by the server.")]
        int? maxResults = null,
        [Description("Optional root-relative path prefixes used to keep only reference locations at or under those paths; use . for the repository root. This is MCP-side filtering after Roslyn LS responds.")]
        string[]? includePathPrefixes = null,
        [Description("Positive LSP request timeout in seconds; defaults to 10 and is capped by the server.")]
        int? timeoutSec = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var effectiveMaxResults = NavigationToolOptions.NormalizeReferenceMaxResults(maxResults);
            var parsedIncludePathPrefixes = NavigationToolOptions.ParseIncludePathPrefixes(this.workspaceRoot, includePathPrefixes);
            var effectiveTimeout = NavigationToolOptions.NormalizeConfigurableTimeout(timeoutSec);
            var request = await NavigationPositionRequests.PrepareAsync(this.session, this.documents, file, line, column, cancellationToken);
            var response = await request.Context.Handle.Client.RequestAsync(
                "textDocument/references",
                new ReferenceParams(
                    new TextDocumentIdentifier(request.Document.Uri),
                    request.Position,
                    new ReferenceContext(includeDeclaration)),
                effectiveTimeout,
                cancellationToken,
                isExpensive: true);

            var locations = NavigationLocationMapper.Map(this.workspaceRoot, response, "textDocument/references", effectiveMaxResults, parsedIncludePathPrefixes);
            var metadata = NavigationToolMetadata.Create(request.Context.State, ToolKind.References, locations.Truncated);
            return new ReferencesResult(
                locations.Items,
                locations.TotalKnown,
                locations.TotalUnfilteredKnown,
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

    [McpServerTool(Name = "peek_references")]
    [Description("Use when you have an exact C# source position and need reference locations with bounded source snippets for surrounding context. Prefer find_references when locations alone are enough.")]
    public async Task<object> PeekReferences(
        [Description(FileParameterDescription)]
        string file,
        [Description(LineParameterDescription)]
        int line,
        [Description(ColumnParameterDescription)]
        int column,
        [Description("Whether to include the symbol declaration in the returned reference locations; defaults to true.")]
        bool includeDeclaration = true,
        [Description("Positive reference result cap; defaults to 200 and is capped by the server.")]
        int? maxResults = null,
        [Description("Non-negative number of surrounding lines to include in each source snippet; defaults to 3 and is capped by the server.")]
        int? contextLines = null,
        [Description("Optional root-relative path prefixes used to keep only reference locations at or under those paths; use . for the repository root. This is MCP-side filtering after Roslyn LS responds.")]
        string[]? includePathPrefixes = null,
        [Description("Positive LSP request timeout in seconds; defaults to 10 and is capped by the server.")]
        int? timeoutSec = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var effectiveMaxResults = NavigationToolOptions.NormalizeReferenceMaxResults(maxResults);
            var effectiveContextLines = NavigationToolOptions.NormalizePeekContextLines(contextLines);
            var parsedIncludePathPrefixes = NavigationToolOptions.ParseIncludePathPrefixes(this.workspaceRoot, includePathPrefixes);
            var effectiveTimeout = NavigationToolOptions.NormalizeConfigurableTimeout(timeoutSec);
            var request = await NavigationPositionRequests.PrepareAsync(this.session, this.documents, file, line, column, cancellationToken);
            var response = await request.Context.Handle.Client.RequestAsync(
                "textDocument/references",
                new ReferenceParams(
                    new TextDocumentIdentifier(request.Document.Uri),
                    request.Position,
                    new ReferenceContext(includeDeclaration)),
                effectiveTimeout,
                cancellationToken,
                isExpensive: true);

            var locations = NavigationLocationMapper.Map(this.workspaceRoot, response, "textDocument/references", effectiveMaxResults, parsedIncludePathPrefixes);
            var items = new List<PeekReferenceItem>(locations.Items.Count);
            foreach (var location in locations.Items)
            {
                var snippet = await SourceSnippetReader.ReadAsync(this.workspaceRoot, this.documents, location, effectiveContextLines, "Reference", cancellationToken);
                items.Add(new PeekReferenceItem(
                    location.File,
                    location.Line,
                    location.Column,
                    location.Range,
                    snippet.Snippet,
                    snippet.Error));
            }

            var metadata = NavigationToolMetadata.Create(request.Context.State, ToolKind.References, locations.Truncated);
            return new PeekReferencesResult(
                items,
                locations.TotalKnown,
                locations.TotalUnfilteredKnown,
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

    [McpServerTool(Name = "find_implementations")]
    [Description("Use when you have an exact C# source position on an interface, abstract member, virtual/base member, or base type and need implementation locations. Concrete implementation positions may return only themselves.")]
    public async Task<object> FindImplementations(
        [Description(FileParameterDescription)]
        string file,
        [Description(LineParameterDescription)]
        int line,
        [Description(ColumnParameterDescription)]
        int column,
        [Description("Positive implementation result cap; defaults to 200 and is capped by the server.")]
        int? maxResults = null,
        [Description("Optional root-relative path prefixes used to keep only implementation locations at or under those paths. This is MCP-side filtering after Roslyn LS responds.")]
        string[]? includePathPrefixes = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var effectiveMaxResults = NavigationToolOptions.NormalizeImplementationMaxResults(maxResults);
            var parsedIncludePathPrefixes = NavigationToolOptions.ParseIncludePathPrefixes(this.workspaceRoot, includePathPrefixes);
            var request = await NavigationPositionRequests.PrepareAsync(this.session, this.documents, file, line, column, cancellationToken);
            var response = await request.Context.Handle.Client.RequestAsync(
                "textDocument/implementation",
                new
                {
                    textDocument = new TextDocumentIdentifier(request.Document.Uri),
                    position = request.Position
                },
                ImplementationsTimeout,
                cancellationToken,
                isExpensive: true);

            var locations = NavigationLocationMapper.Map(this.workspaceRoot, response, "textDocument/implementation", effectiveMaxResults, parsedIncludePathPrefixes);
            var metadata = NavigationToolMetadata.Create(request.Context.State, ToolKind.Implementations, locations.Truncated);
            return new ImplementationsResult(
                locations.Items,
                locations.TotalKnown,
                locations.TotalUnfilteredKnown,
                locations.Returned,
                "Use interface/abstract/base contract positions; concrete implementation positions may return only themselves.",
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
