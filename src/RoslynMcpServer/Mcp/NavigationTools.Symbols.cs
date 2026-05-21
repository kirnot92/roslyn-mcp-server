using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynMcpServer.Infrastructure;
using RoslynMcpServer.Lsp;
using static RoslynMcpServer.Mcp.NavigationToolLimits;

namespace RoslynMcpServer.Mcp;

public sealed partial class NavigationTools
{
    [McpServerTool(Name = "document_symbols")]
    [Description("Use when you know a C# file and need a bounded compiler-backed outline of its symbols, such as classes, methods, and properties. Optional kindFilter and query narrow returned symbols while preserving ancestor context for matching descendants. Use find_symbols when you do not know the file location.")]
    public async Task<object> DocumentSymbols(
        [Description(FileParameterDescription)]
        string file,
        [Description(SymbolKindFilterParameterDescription + " Ancestors of matching descendants are retained as context.")]
        string[]? kindFilter = null,
        [Description("Optional case-insensitive symbol name contains query. Ancestors of matching descendants are retained as context.")]
        string? query = null,
        [Description("Positive result node cap; defaults to 1000 and is capped by the server.")]
        int? maxResults = null,
        [Description("Positive LSP request timeout in seconds; defaults to 10 and is capped by the server.")]
        int? timeoutSec = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var parsedKindFilter = NavigationToolOptions.ParseSymbolKindFilter(kindFilter);
            var normalizedQuery = NavigationToolOptions.NormalizeDocumentSymbolQuery(query);
            var effectiveMaxResults = NavigationToolOptions.NormalizeDocumentSymbolMaxResults(maxResults);
            var effectiveTimeout = NavigationToolOptions.NormalizeConfigurableTimeout(timeoutSec);
            var context = await this.session.PrepareReadToolAsync(cancellationToken);
            var document = await this.documents.EnsureOpenAsync(file, context.Handle.Client, cancellationToken);
            var response = await context.Handle.Client.RequestAsync(
                "textDocument/documentSymbol",
                new
                {
                    textDocument = new TextDocumentIdentifier(document.Uri)
                },
                effectiveTimeout,
                cancellationToken);

            var mappedSymbols = DocumentSymbolMapper.Map(response, parsedKindFilter, normalizedQuery, effectiveMaxResults);
            var metadata = NavigationToolMetadata.Create(context.State, ToolKind.DocumentSymbols, mappedSymbols.Truncated);

            return new DocumentSymbolsResult(
                mappedSymbols.Items,
                mappedSymbols.TotalKnown,
                mappedSymbols.TotalUnfilteredKnown,
                mappedSymbols.Returned,
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

    [McpServerTool(Name = "find_symbols")]
    [Description("Use when you know a C# symbol name but do not know its file location. This is compiler-backed workspace symbol search, not plain text search; query must be at least 2 non-whitespace characters and empty results can be inconclusive while warming.")]
    public async Task<object> FindSymbols(
        [Description("C# symbol name query with at least 2 non-whitespace characters.")]
        string query,
        [Description("Positive workspace symbol result cap; defaults to 300 and is capped by the server.")]
        int? maxResults = null,
        [Description(SymbolKindFilterParameterDescription)]
        string[]? kindFilter = null,
        [Description("Optional symbol name match mode after Roslyn LS responds: default, exact, prefix, or contains. Omit for Roslyn LS default matching.")]
        string? matchMode = null,
        [Description("Optional root-relative path prefixes used to keep only symbols located at or under those paths. This is MCP-side filtering after Roslyn LS responds.")]
        string[]? includePathPrefixes = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            query = NavigationToolOptions.ValidateSymbolQuery(query);
            var effectiveMaxResults = NavigationToolOptions.NormalizeSymbolMaxResults(maxResults);
            var parsedKindFilter = NavigationToolOptions.ParseSymbolKindFilter(kindFilter);
            var parsedMatchMode = NavigationToolOptions.ParseSymbolMatchMode(matchMode);
            var parsedIncludePathPrefixes = NavigationToolOptions.ParseIncludePathPrefixes(this.workspaceRoot, includePathPrefixes);
            var context = await this.session.PrepareReadToolAsync(cancellationToken);
            var response = await context.Handle.Client.RequestAsync(
                "workspace/symbol",
                new WorkspaceSymbolParams(query),
                WorkspaceSymbolTimeout,
                cancellationToken,
                isExpensive: true);

            var symbols = WorkspaceSymbolMapper.Map(this.workspaceRoot, response, effectiveMaxResults, parsedKindFilter, query, parsedMatchMode, parsedIncludePathPrefixes);
            var metadata = NavigationToolMetadata.Create(context.State, ToolKind.Symbols, symbols.Truncated);
            return new FindSymbolsResult(
                symbols.Items,
                symbols.TotalKnown,
                symbols.TotalUnfilteredKnown,
                symbols.Returned,
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
