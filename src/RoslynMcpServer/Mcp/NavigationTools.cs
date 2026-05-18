using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynMcpServer.Infrastructure;
using RoslynMcpServer.Lsp;
using RoslynMcpServer.Workspace;

namespace RoslynMcpServer.Mcp;

[McpServerToolType]
public sealed partial class NavigationTools(
    WorkspaceSession session,
    DocumentStateManager documents,
    DocumentPathMapper pathMapper)
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
        CancellationToken cancellationToken = default)
    {
        try
        {
            var parsedKindFilter = ParseSymbolKindFilter(kindFilter);
            var normalizedQuery = NormalizeDocumentSymbolQuery(query);
            var effectiveMaxResults = NormalizeDocumentSymbolMaxResults(maxResults);
            var context = await session.PrepareReadToolAsync(cancellationToken).ConfigureAwait(false);
            var document = await documents.EnsureOpenAsync(file, context.Handle.Client, cancellationToken).ConfigureAwait(false);
            var response = await context.Handle.Client.RequestAsync(
                "textDocument/documentSymbol",
                new
                {
                    textDocument = new TextDocumentIdentifier(document.Uri)
                },
                NavigationTimeout,
                cancellationToken).ConfigureAwait(false);

            var mappedSymbols = MapDocumentSymbols(response, parsedKindFilter, normalizedQuery, effectiveMaxResults);
            var metadata = CreateMetadata(context.State, ToolKind.DocumentSymbols, mappedSymbols.Truncated);

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
            var request = await PreparePositionRequestAsync(file, line, column, cancellationToken).ConfigureAwait(false);
            var response = await request.Context.Handle.Client.RequestAsync(
                "textDocument/hover",
                new
                {
                    textDocument = new TextDocumentIdentifier(request.Document.Uri),
                    position = request.Position
                },
                NavigationTimeout,
                cancellationToken).ConfigureAwait(false);

            var hover = MapHover(response);
            var metadata = CreateMetadata(request.Context.State, ToolKind.Hover, hover.Truncated);
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
            var request = await PreparePositionRequestAsync(file, line, column, cancellationToken).ConfigureAwait(false);
            var response = await request.Context.Handle.Client.RequestAsync(
                "textDocument/definition",
                new
                {
                    textDocument = new TextDocumentIdentifier(request.Document.Uri),
                    position = request.Position
                },
                DefinitionTimeout,
                cancellationToken).ConfigureAwait(false);

            var locations = MapLocations(response, "textDocument/definition", maxResults: null);
            var metadata = CreateMetadata(request.Context.State, ToolKind.Definition, truncated: false);
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
            var effectiveContextLines = NormalizePeekContextLines(contextLines);
            var effectiveMaxDefinitions = NormalizePeekMaxDefinitions(maxDefinitions);
            var request = await PreparePositionRequestAsync(file, line, column, cancellationToken).ConfigureAwait(false);
            var response = await request.Context.Handle.Client.RequestAsync(
                "textDocument/definition",
                new
                {
                    textDocument = new TextDocumentIdentifier(request.Document.Uri),
                    position = request.Position
                },
                DefinitionTimeout,
                cancellationToken).ConfigureAwait(false);

            var locations = MapLocations(response, "textDocument/definition", effectiveMaxDefinitions);
            var items = new List<PeekDefinitionItem>(locations.Items.Count);
            foreach (var location in locations.Items)
            {
                var snippet = await ReadSourceSnippetAsync(location, effectiveContextLines, "Definition", cancellationToken).ConfigureAwait(false);
                items.Add(new PeekDefinitionItem(
                    location.File,
                    location.Line,
                    location.Column,
                    location.Range,
                    snippet.Snippet,
                    snippet.Error));
            }

            var metadata = CreateMetadata(request.Context.State, ToolKind.Definition, locations.Truncated);
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
        CancellationToken cancellationToken = default)
    {
        try
        {
            var effectiveMaxResults = NormalizeReferenceMaxResults(maxResults);
            var parsedIncludePathPrefixes = ParseIncludePathPrefixes(includePathPrefixes);
            var request = await PreparePositionRequestAsync(file, line, column, cancellationToken).ConfigureAwait(false);
            var response = await request.Context.Handle.Client.RequestAsync(
                "textDocument/references",
                new ReferenceParams(
                    new TextDocumentIdentifier(request.Document.Uri),
                    request.Position,
                    new ReferenceContext(includeDeclaration)),
                ReferencesTimeout,
                cancellationToken,
                isExpensive: true).ConfigureAwait(false);

            var locations = MapLocations(response, "textDocument/references", effectiveMaxResults, parsedIncludePathPrefixes);
            var metadata = CreateMetadata(request.Context.State, ToolKind.References, locations.Truncated);
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
        CancellationToken cancellationToken = default)
    {
        try
        {
            var effectiveMaxResults = NormalizeReferenceMaxResults(maxResults);
            var effectiveContextLines = NormalizePeekContextLines(contextLines);
            var parsedIncludePathPrefixes = ParseIncludePathPrefixes(includePathPrefixes);
            var request = await PreparePositionRequestAsync(file, line, column, cancellationToken).ConfigureAwait(false);
            var response = await request.Context.Handle.Client.RequestAsync(
                "textDocument/references",
                new ReferenceParams(
                    new TextDocumentIdentifier(request.Document.Uri),
                    request.Position,
                    new ReferenceContext(includeDeclaration)),
                ReferencesTimeout,
                cancellationToken,
                isExpensive: true).ConfigureAwait(false);

            var locations = MapLocations(response, "textDocument/references", effectiveMaxResults, parsedIncludePathPrefixes);
            var items = new List<PeekReferenceItem>(locations.Items.Count);
            foreach (var location in locations.Items)
            {
                var snippet = await ReadSourceSnippetAsync(location, effectiveContextLines, "Reference", cancellationToken).ConfigureAwait(false);
                items.Add(new PeekReferenceItem(
                    location.File,
                    location.Line,
                    location.Column,
                    location.Range,
                    snippet.Snippet,
                    snippet.Error));
            }

            var metadata = CreateMetadata(request.Context.State, ToolKind.References, locations.Truncated);
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
            var effectiveMaxResults = NormalizeImplementationMaxResults(maxResults);
            var parsedIncludePathPrefixes = ParseIncludePathPrefixes(includePathPrefixes);
            var request = await PreparePositionRequestAsync(file, line, column, cancellationToken).ConfigureAwait(false);
            var response = await request.Context.Handle.Client.RequestAsync(
                "textDocument/implementation",
                new
                {
                    textDocument = new TextDocumentIdentifier(request.Document.Uri),
                    position = request.Position
                },
                ImplementationsTimeout,
                cancellationToken,
                isExpensive: true).ConfigureAwait(false);

            var locations = MapLocations(response, "textDocument/implementation", effectiveMaxResults, parsedIncludePathPrefixes);
            var metadata = CreateMetadata(request.Context.State, ToolKind.Implementations, locations.Truncated);
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

    [McpServerTool(Name = "get_call_hierarchy")]
    [Description("Use when you have an exact C# callable position, such as a method, constructor, property accessor, event, or operator, and need direct depth-1 incoming callers, outgoing callees, or both. This is not recursive; kindFilter and includePathPrefixes reduce returned edges after Roslyn LS responds, not request cost.")]
    public async Task<object> GetCallHierarchy(
        [Description(FileParameterDescription)]
        string file,
        [Description(LineParameterDescription)]
        int line,
        [Description(ColumnParameterDescription)]
        int column,
        [Description("Call hierarchy direction: incoming callers, outgoing callees, or both; defaults to incoming.")]
        string direction = "incoming",
        [Description("Positive call hierarchy edge cap; defaults to 200 and is capped by the server.")]
        int? maxResults = null,
        [Description(CallHierarchyKindFilterParameterDescription)]
        string[]? kindFilter = null,
        [Description("Optional root-relative path prefixes used to keep only edges whose direction-specific counterpart is at or under those paths. This is MCP-side filtering after Roslyn LS responds.")]
        string[]? includePathPrefixes = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var parsedDirection = ParseCallHierarchyDirection(direction);
            var effectiveMaxResults = NormalizeCallHierarchyMaxResults(maxResults);
            var parsedKindFilter = ParseCallHierarchyKindFilter(kindFilter);
            var parsedIncludePathPrefixes = ParseIncludePathPrefixes(includePathPrefixes);
            var request = await PreparePositionRequestAsync(file, line, column, cancellationToken).ConfigureAwait(false);
            var prepareResponse = await request.Context.Handle.Client.RequestAsync(
                "textDocument/prepareCallHierarchy",
                new
                {
                    textDocument = new TextDocumentIdentifier(request.Document.Uri),
                    position = request.Position
                },
                CallHierarchyTimeout,
                cancellationToken).ConfigureAwait(false);

            var roots = MapPreparedCallHierarchyItems(prepareResponse);
            if (roots.Count == 0)
            {
                var emptyMetadata = CreateMetadata(request.Context.State, ToolKind.CallHierarchy, truncated: false);
                return new CallHierarchyResult(
                    [],
                    [],
                    TotalKnown: 0,
                    TotalUnfilteredKnown: 0,
                    Returned: 0,
                    emptyMetadata.WorkspaceState,
                    emptyMetadata.Completeness,
                    emptyMetadata.Reason,
                    emptyMetadata.RetryAfterMs,
                    emptyMetadata.Truncated);
            }

            var edges = new List<CallHierarchyEdge>();
            var totalUnfilteredKnown = 0;
            var totalKnown = 0;
            var returned = 0;
            var callSitesTruncated = false;
            foreach (var root in roots)
            {
                if (parsedDirection is CallHierarchyDirection.Incoming or CallHierarchyDirection.Both)
                {
                    var incomingResponse = await request.Context.Handle.Client.RequestAsync(
                        "callHierarchy/incomingCalls",
                        new { item = root.OriginalItem },
                        CallHierarchyTimeout,
                        cancellationToken,
                        isExpensive: true).ConfigureAwait(false);
                    AddCallHierarchyEdges(
                        incomingResponse,
                        root.Symbol,
                        CallHierarchyDirection.Incoming,
                        parsedKindFilter,
                        parsedIncludePathPrefixes,
                        effectiveMaxResults,
                        edges,
                        ref totalUnfilteredKnown,
                        ref totalKnown,
                        ref returned,
                        ref callSitesTruncated);
                }

                if (parsedDirection is CallHierarchyDirection.Outgoing or CallHierarchyDirection.Both)
                {
                    var outgoingResponse = await request.Context.Handle.Client.RequestAsync(
                        "callHierarchy/outgoingCalls",
                        new { item = root.OriginalItem },
                        CallHierarchyTimeout,
                        cancellationToken,
                        isExpensive: true).ConfigureAwait(false);
                    AddCallHierarchyEdges(
                        outgoingResponse,
                        root.Symbol,
                        CallHierarchyDirection.Outgoing,
                        parsedKindFilter,
                        parsedIncludePathPrefixes,
                        effectiveMaxResults,
                        edges,
                        ref totalUnfilteredKnown,
                        ref totalKnown,
                        ref returned,
                        ref callSitesTruncated);
                }
            }

            var truncated = totalKnown > returned || callSitesTruncated;
            var metadata = CreateMetadata(request.Context.State, ToolKind.CallHierarchy, truncated);
            return new CallHierarchyResult(
                roots.Select(root => root.Symbol).ToArray(),
                edges,
                totalKnown,
                totalUnfilteredKnown,
                returned,
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

    [McpServerTool(Name = "get_type_hierarchy")]
    [Description("Use when you have an exact C# type position and need base type, derived type, or interface implementation hierarchy. Traverses LSP type hierarchy edges breadth-first up to maxDepth. includePathPrefixes narrows returned and traversed follow-up types after Roslyn LS responds.")]
    public async Task<object> GetTypeHierarchy(
        [Description(FileParameterDescription)]
        string file,
        [Description(LineParameterDescription)]
        int line,
        [Description(ColumnParameterDescription)]
        int column,
        [Description("Type hierarchy direction: supertypes, subtypes, or both; defaults to supertypes.")]
        string direction = "supertypes",
        [Description("Positive BFS traversal depth; defaults to 2 and is capped at 5.")]
        int? maxDepth = null,
        [Description("Positive type hierarchy edge cap; defaults to 200 and is capped by the server.")]
        int? maxResults = null,
        [Description("Optional root-relative path prefixes used to keep only follow-up type hierarchy edges whose discovered type is at or under those paths. Excluded follow-up types are not traversed further.")]
        string[]? includePathPrefixes = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var parsedDirection = ParseTypeHierarchyDirection(direction);
            var effectiveMaxDepth = NormalizeTypeHierarchyMaxDepth(maxDepth);
            var effectiveMaxResults = NormalizeTypeHierarchyMaxResults(maxResults);
            var parsedIncludePathPrefixes = ParseIncludePathPrefixes(includePathPrefixes);
            var request = await PreparePositionRequestAsync(file, line, column, cancellationToken).ConfigureAwait(false);
            var prepareResponse = await request.Context.Handle.Client.RequestAsync(
                "textDocument/prepareTypeHierarchy",
                new
                {
                    textDocument = new TextDocumentIdentifier(request.Document.Uri),
                    position = request.Position
                },
                TypeHierarchyTimeout,
                cancellationToken).ConfigureAwait(false);

            var roots = MapPreparedTypeHierarchyItems(prepareResponse);
            if (roots.Count == 0)
            {
                var emptyMetadata = CreateMetadata(request.Context.State, ToolKind.TypeHierarchy, truncated: false);
                return new TypeHierarchyResult(
                    [],
                    [],
                    TotalKnown: 0,
                    TotalUnfilteredKnown: 0,
                    Returned: 0,
                    emptyMetadata.WorkspaceState,
                    emptyMetadata.Completeness,
                    emptyMetadata.Reason,
                    emptyMetadata.RetryAfterMs,
                    emptyMetadata.Truncated);
            }

            var edges = new List<TypeHierarchyEdge>();
            var traversalState = new TypeHierarchyTraversalState();
            var visitedEdges = new HashSet<string>(StringComparer.Ordinal);
            var followUpDirections = GetTypeHierarchyTraversalDirections(parsedDirection);
            foreach (var traversalDirection in followUpDirections)
            {
                foreach (var root in roots)
                {
                    await TraverseTypeHierarchyAsync(
                        request.Context.Handle.Client,
                        root,
                        traversalDirection,
                        effectiveMaxDepth,
                        effectiveMaxResults,
                        parsedIncludePathPrefixes,
                        edges,
                        visitedEdges,
                        traversalState,
                        cancellationToken).ConfigureAwait(false);
                }
            }

            var truncated = traversalState.TotalKnown > traversalState.Returned || traversalState.HitResultLimit;
            var metadata = CreateMetadata(request.Context.State, ToolKind.TypeHierarchy, truncated);
            return new TypeHierarchyResult(
                roots.Select(root => root.Symbol).ToArray(),
                edges,
                traversalState.TotalKnown,
                traversalState.TotalUnfilteredKnown,
                traversalState.Returned,
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
            query = ValidateSymbolQuery(query);
            var effectiveMaxResults = NormalizeSymbolMaxResults(maxResults);
            var parsedKindFilter = ParseSymbolKindFilter(kindFilter);
            var parsedMatchMode = ParseSymbolMatchMode(matchMode);
            var parsedIncludePathPrefixes = ParseIncludePathPrefixes(includePathPrefixes);
            var context = await session.PrepareReadToolAsync(cancellationToken).ConfigureAwait(false);
            var response = await context.Handle.Client.RequestAsync(
                "workspace/symbol",
                new WorkspaceSymbolParams(query),
                WorkspaceSymbolTimeout,
                cancellationToken,
                isExpensive: true).ConfigureAwait(false);

            var symbols = MapWorkspaceSymbols(response, effectiveMaxResults, parsedKindFilter, query, parsedMatchMode, parsedIncludePathPrefixes);
            var metadata = CreateMetadata(context.State, ToolKind.Symbols, symbols.Truncated);
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
