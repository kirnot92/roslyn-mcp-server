using System.ComponentModel;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;
using RoslynMcpServer.Infrastructure;
using RoslynMcpServer.Lsp;
using RoslynMcpServer.Workspace;

namespace RoslynMcpServer.Mcp;

[McpServerToolType]
public sealed class NavigationTools(
    WorkspaceSession session,
    DocumentStateManager documents,
    DocumentPathMapper pathMapper)
{
    private const int MaxDocumentSymbolNodes = 1000;
    private const int MaxHoverCharacters = 20_000;
    private const int DefaultPeekContextLines = 3;
    private const int MaxPeekContextLines = 20;
    private const int DefaultPeekMaxDefinitions = 20;
    private const int MaxPeekMaxDefinitions = 100;
    private const int MaxPeekSnippetCharacters = 20_000;
    private const int DefaultReferencesMaxResults = 200;
    private const int MaxReferencesMaxResults = 1000;
    private const int DefaultImplementationsMaxResults = DefaultReferencesMaxResults;
    private const int MaxImplementationsMaxResults = MaxReferencesMaxResults;
    private const int DefaultCallHierarchyMaxResults = DefaultReferencesMaxResults;
    private const int MaxCallHierarchyMaxResults = MaxReferencesMaxResults;
    private const int MaxCallHierarchyCallSites = 20;
    private const int DefaultSymbolMaxResults = 300;
    private const int MaxSymbolMaxResults = 1000;
    private const int MinSymbolQueryLength = 2;
    private static readonly TimeSpan NavigationTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DefinitionTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReferencesTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ImplementationsTimeout = ReferencesTimeout;
    private static readonly TimeSpan CallHierarchyTimeout = ReferencesTimeout;
    private static readonly TimeSpan WorkspaceSymbolTimeout = TimeSpan.FromSeconds(30);
    private static readonly SymbolKind[] SupportedSymbolKinds = Enum.GetValues<SymbolKind>()
        .Where(kind => kind.ToMcpName() != "unknown")
        .ToArray();
    private static readonly IReadOnlyDictionary<string, SymbolKind> SymbolKindFilterValues = SupportedSymbolKinds
        .ToDictionary(kind => kind.ToMcpName(), kind => kind, StringComparer.OrdinalIgnoreCase);
    private static readonly string AllowedSymbolKindFilterValues = string.Join(
        ", ",
        SupportedSymbolKinds.Select(kind => kind.ToMcpName()));

    [McpServerTool(Name = "document_symbols")]
    [Description("Get a bounded outline of symbols in one C# file. Use before deeper navigation or edits.")]
    public async Task<object> DocumentSymbols(string file, CancellationToken cancellationToken = default)
    {
        try
        {
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

            var mappedSymbols = MapDocumentSymbols(response);
            var metadata = CreateMetadata(context.State, ToolKind.DocumentSymbols, mappedSymbols.Truncated);

            return new DocumentSymbolsResult(
                mappedSymbols.Items,
                mappedSymbols.TotalKnown,
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
    [Description("Get compiler-backed type, signature, or documentation info at a C# source location.")]
    public async Task<object> Hover(string file, int line, int column, CancellationToken cancellationToken = default)
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
    [Description("Find definition locations for a C# symbol. Use peek_definition when source context is needed.")]
    public async Task<object> GoToDefinition(string file, int line, int column, CancellationToken cancellationToken = default)
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
    [Description("Find definition locations plus bounded source snippets, avoiding a separate file-read call.")]
    public async Task<object> PeekDefinition(
        string file,
        int line,
        int column,
        int? contextLines = null,
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
    [Description("Find usages of a C# symbol. This can be expensive; check completeness and truncated before treating results as final.")]
    public async Task<object> FindReferences(
        string file,
        int line,
        int column,
        bool includeDeclaration = true,
        int? maxResults = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var effectiveMaxResults = NormalizeReferenceMaxResults(maxResults);
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

            var locations = MapLocations(response, "textDocument/references", effectiveMaxResults);
            var metadata = CreateMetadata(request.Context.State, ToolKind.References, locations.Truncated);
            return new ReferencesResult(
                locations.Items,
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

    [McpServerTool(Name = "peek_references")]
    [Description("Find reference locations plus bounded source snippets for each returned location.")]
    public async Task<object> PeekReferences(
        string file,
        int line,
        int column,
        bool includeDeclaration = true,
        int? maxResults = null,
        int? contextLines = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var effectiveMaxResults = NormalizeReferenceMaxResults(maxResults);
            var effectiveContextLines = NormalizePeekContextLines(contextLines);
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

            var locations = MapLocations(response, "textDocument/references", effectiveMaxResults);
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
    [Description("Find implementations from an interface, abstract, virtual/base member, or base type position. Concrete implementation positions may return only themselves.")]
    public async Task<object> FindImplementations(
        string file,
        int line,
        int column,
        int? maxResults = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var effectiveMaxResults = NormalizeImplementationMaxResults(maxResults);
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

            var locations = MapLocations(response, "textDocument/implementation", effectiveMaxResults);
            var metadata = CreateMetadata(request.Context.State, ToolKind.Implementations, locations.Truncated);
            return new ImplementationsResult(
                locations.Items,
                locations.TotalKnown,
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
    [Description("Get direct depth-1 incoming, outgoing, or both call relationships for a C# callable symbol.")]
    public async Task<object> GetCallHierarchy(
        string file,
        int line,
        int column,
        [Description("One of {incoming|outgoing|both}. Defaults to incoming.")]
        string direction = "incoming",
        [Description("Maximum number of direct call relationship edges to return.")]
        int? maxResults = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var parsedDirection = ParseCallHierarchyDirection(direction);
            var effectiveMaxResults = NormalizeCallHierarchyMaxResults(maxResults);
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
                    Returned: 0,
                    emptyMetadata.WorkspaceState,
                    emptyMetadata.Completeness,
                    emptyMetadata.Reason,
                    emptyMetadata.RetryAfterMs,
                    emptyMetadata.Truncated);
            }

            var edges = new List<CallHierarchyEdge>();
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
                        effectiveMaxResults,
                        edges,
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
                        effectiveMaxResults,
                        edges,
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

    [McpServerTool(Name = "find_symbols")]
    [Description("Search workspace symbols by name when you do not already have a file location. Empty results can be inconclusive while warming.")]
    public async Task<object> FindSymbols(
        string query,
        int? maxResults = null,
        [Description("Optional MCP symbol kind names such as class, interface, method, property, or field.")]
        string[]? kindFilter = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            query = ValidateSymbolQuery(query);
            var effectiveMaxResults = NormalizeSymbolMaxResults(maxResults);
            var parsedKindFilter = ParseSymbolKindFilter(kindFilter);
            var context = await session.PrepareReadToolAsync(cancellationToken).ConfigureAwait(false);
            var response = await context.Handle.Client.RequestAsync(
                "workspace/symbol",
                new WorkspaceSymbolParams(query),
                WorkspaceSymbolTimeout,
                cancellationToken,
                isExpensive: true).ConfigureAwait(false);

            var symbols = MapWorkspaceSymbols(response, effectiveMaxResults, parsedKindFilter);
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

    private async Task<PositionRequestContext> PreparePositionRequestAsync(
        string file,
        int line,
        int column,
        CancellationToken cancellationToken)
    {
        var position = PositionMapper.ToLspPosition(line, column);
        var context = await session.PrepareReadToolAsync(cancellationToken).ConfigureAwait(false);
        var document = await documents.EnsureOpenAsync(file, context.Handle.Client, cancellationToken).ConfigureAwait(false);
        documents.ValidatePosition(document, file, line, column);

        return new PositionRequestContext(context, document, position);
    }

    private LocationMapResult MapLocations(JsonElement response, string method, int? maxResults)
    {
        if (response.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return new LocationMapResult([], TotalKnown: 0, Returned: 0, Truncated: false);
        }

        var items = new List<NavigationLocation>();
        var totalKnown = 0;
        var returned = 0;

        if (response.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in response.EnumerateArray())
            {
                AddLocationIfMappable(element, method, maxResults, items, ref totalKnown, ref returned);
            }

            return new LocationMapResult(items, totalKnown, returned, maxResults.HasValue && totalKnown > returned);
        }

        if (response.ValueKind == JsonValueKind.Object)
        {
            AddLocationIfMappable(response, method, maxResults, items, ref totalKnown, ref returned);
            return new LocationMapResult(items, totalKnown, returned, maxResults.HasValue && totalKnown > returned);
        }

        throw new UserFacingException("invalid_lsp_response", $"{method} returned an unexpected response shape.");
    }

    private void AddLocationIfMappable(
        JsonElement element,
        string method,
        int? maxResults,
        List<NavigationLocation> items,
        ref int totalKnown,
        ref int returned)
    {
        var location = TryMapLocation(element, method);
        if (location is null)
        {
            return;
        }

        totalKnown++;
        if (maxResults.HasValue && returned >= maxResults.Value)
        {
            return;
        }

        items.Add(location);
        returned++;
    }

    private NavigationLocation? TryMapLocation(JsonElement element, string method)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new UserFacingException("invalid_lsp_response", $"{method} returned a malformed location.");
        }

        string? uri;
        Lsp.Range? range;
        if (element.TryGetProperty("uri", out var uriElement))
        {
            uri = uriElement.GetString();
            range = TryGetRange(element, "range");
        }
        else if (element.TryGetProperty("targetUri", out var targetUriElement))
        {
            uri = targetUriElement.GetString();
            range = TryGetRange(element, "targetRange");
        }
        else
        {
            throw new UserFacingException("invalid_lsp_response", $"{method} returned a malformed location.");
        }

        if (string.IsNullOrWhiteSpace(uri) || range is null)
        {
            throw new UserFacingException("invalid_lsp_response", $"{method} returned a malformed location.");
        }

        string relativePath;
        try
        {
            relativePath = pathMapper.UriToRelativePath(uri);
        }
        catch (UserFacingException ex) when (ex.Code is "path_outside_root" or "invalid_lsp_uri")
        {
            return null;
        }

        var mcpRange = PositionMapper.ToMcpRange(range);
        return new NavigationLocation(relativePath, mcpRange.StartLine, mcpRange.StartColumn, mcpRange);
    }

    private static int NormalizeReferenceMaxResults(int? maxResults)
    {
        if (maxResults is null)
        {
            return DefaultReferencesMaxResults;
        }

        if (maxResults.Value < 1)
        {
            throw new UserFacingException("invalid_max_results", "maxResults must be a positive integer.");
        }

        return Math.Min(maxResults.Value, MaxReferencesMaxResults);
    }

    private static int NormalizeImplementationMaxResults(int? maxResults)
    {
        if (maxResults is null)
        {
            return DefaultImplementationsMaxResults;
        }

        if (maxResults.Value < 1)
        {
            throw new UserFacingException("invalid_max_results", "maxResults must be a positive integer.");
        }

        return Math.Min(maxResults.Value, MaxImplementationsMaxResults);
    }

    private static int NormalizeCallHierarchyMaxResults(int? maxResults)
    {
        if (maxResults is null)
        {
            return DefaultCallHierarchyMaxResults;
        }

        if (maxResults.Value < 1)
        {
            throw new UserFacingException("invalid_max_results", "maxResults must be a positive integer.");
        }

        return Math.Min(maxResults.Value, MaxCallHierarchyMaxResults);
    }

    private static CallHierarchyDirection ParseCallHierarchyDirection(string direction)
    {
        var normalized = direction?.Trim();
        if (string.Equals(normalized, "incoming", StringComparison.OrdinalIgnoreCase))
        {
            return CallHierarchyDirection.Incoming;
        }

        if (string.Equals(normalized, "outgoing", StringComparison.OrdinalIgnoreCase))
        {
            return CallHierarchyDirection.Outgoing;
        }

        if (string.Equals(normalized, "both", StringComparison.OrdinalIgnoreCase))
        {
            return CallHierarchyDirection.Both;
        }

        throw new UserFacingException(
            "invalid_direction",
            "direction must be one of: incoming, outgoing, both.");
    }

    private static int NormalizePeekContextLines(int? contextLines)
    {
        if (contextLines is null)
        {
            return DefaultPeekContextLines;
        }

        if (contextLines.Value < 0)
        {
            throw new UserFacingException("invalid_context_lines", "contextLines must be a non-negative integer.");
        }

        return Math.Min(contextLines.Value, MaxPeekContextLines);
    }

    private static int NormalizePeekMaxDefinitions(int? maxDefinitions)
    {
        if (maxDefinitions is null)
        {
            return DefaultPeekMaxDefinitions;
        }

        if (maxDefinitions.Value < 1)
        {
            throw new UserFacingException("invalid_max_results", "maxDefinitions must be a positive integer.");
        }

        return Math.Min(maxDefinitions.Value, MaxPeekMaxDefinitions);
    }

    private IReadOnlyList<PreparedCallHierarchyItem> MapPreparedCallHierarchyItems(JsonElement response)
    {
        if (response.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return [];
        }

        if (response.ValueKind != JsonValueKind.Array)
        {
            throw new UserFacingException("invalid_lsp_response", "textDocument/prepareCallHierarchy returned an unexpected response shape.");
        }

        var roots = new List<PreparedCallHierarchyItem>();
        foreach (var item in response.EnumerateArray())
        {
            var symbol = TryMapCallHierarchySymbol(item);
            if (symbol is null)
            {
                continue;
            }

            roots.Add(new PreparedCallHierarchyItem(symbol, item.Clone()));
        }

        return roots;
    }

    private void AddCallHierarchyEdges(
        JsonElement response,
        CallHierarchySymbol root,
        CallHierarchyDirection direction,
        int maxResults,
        List<CallHierarchyEdge> edges,
        ref int totalKnown,
        ref int returned,
        ref bool callSitesTruncated)
    {
        if (response.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return;
        }

        if (response.ValueKind != JsonValueKind.Array)
        {
            throw new UserFacingException("invalid_lsp_response", $"{CallHierarchyMethodName(direction)} returned an unexpected response shape.");
        }

        foreach (var item in response.EnumerateArray())
        {
            var edge = TryMapCallHierarchyEdge(item, root, direction);
            if (edge is null)
            {
                continue;
            }

            totalKnown++;
            if (edge.CallSitesTruncated)
            {
                callSitesTruncated = true;
            }

            if (returned >= maxResults)
            {
                continue;
            }

            edges.Add(edge);
            returned++;
        }
    }

    private CallHierarchyEdge? TryMapCallHierarchyEdge(
        JsonElement item,
        CallHierarchySymbol root,
        CallHierarchyDirection direction)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            throw new UserFacingException("invalid_lsp_response", $"{CallHierarchyMethodName(direction)} returned a malformed call hierarchy item.");
        }

        var symbolPropertyName = direction == CallHierarchyDirection.Incoming ? "from" : "to";
        if (!item.TryGetProperty(symbolPropertyName, out var symbolElement) ||
            symbolElement.ValueKind != JsonValueKind.Object)
        {
            throw new UserFacingException("invalid_lsp_response", $"{CallHierarchyMethodName(direction)} returned a malformed call hierarchy item.");
        }

        var otherSymbol = TryMapCallHierarchySymbol(symbolElement);
        if (otherSymbol is null)
        {
            return null;
        }

        var from = direction == CallHierarchyDirection.Incoming ? otherSymbol : root;
        var to = direction == CallHierarchyDirection.Incoming ? root : otherSymbol;
        if (from.Location is null || to.Location is null)
        {
            return null;
        }

        var callSites = MapCallHierarchyCallSites(item, from.Location.File, direction);
        return new CallHierarchyEdge(
            root.Id,
            CallHierarchyDirectionName(direction),
            Depth: 1,
            from,
            to,
            callSites.Items,
            callSites.TotalKnown,
            callSites.Truncated);
    }

    private CallHierarchyCallSiteMapResult MapCallHierarchyCallSites(
        JsonElement item,
        string callerFile,
        CallHierarchyDirection direction)
    {
        if (!item.TryGetProperty("fromRanges", out var rangesElement) ||
            rangesElement.ValueKind != JsonValueKind.Array)
        {
            throw new UserFacingException("invalid_lsp_response", $"{CallHierarchyMethodName(direction)} returned malformed call site ranges.");
        }

        var items = new List<CallHierarchyCallSite>();
        var totalKnown = 0;
        foreach (var rangeElement in rangesElement.EnumerateArray())
        {
            var range = ReadCallHierarchyRange(rangeElement, direction);
            var mcpRange = PositionMapper.ToMcpRange(range);
            totalKnown++;
            if (items.Count >= MaxCallHierarchyCallSites)
            {
                continue;
            }

            items.Add(new CallHierarchyCallSite(
                callerFile,
                mcpRange.StartLine,
                mcpRange.StartColumn,
                mcpRange));
        }

        return new CallHierarchyCallSiteMapResult(items, totalKnown, totalKnown > items.Count);
    }

    private static Lsp.Range ReadCallHierarchyRange(JsonElement rangeElement, CallHierarchyDirection direction)
    {
        if (rangeElement.ValueKind != JsonValueKind.Object)
        {
            throw new UserFacingException("invalid_lsp_response", $"{CallHierarchyMethodName(direction)} returned malformed call site ranges.");
        }

        try
        {
            return rangeElement.Deserialize<Lsp.Range>(JsonOptions.Default)
                ?? throw new UserFacingException("invalid_lsp_response", $"{CallHierarchyMethodName(direction)} returned malformed call site ranges.");
        }
        catch (JsonException ex)
        {
            throw new UserFacingException("invalid_lsp_response", $"{CallHierarchyMethodName(direction)} returned malformed call site ranges.", ex);
        }
        catch (InvalidOperationException ex)
        {
            throw new UserFacingException("invalid_lsp_response", $"{CallHierarchyMethodName(direction)} returned malformed call site ranges.", ex);
        }
    }

    private CallHierarchySymbol? TryMapCallHierarchySymbol(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object ||
            !item.TryGetProperty("name", out var nameElement) ||
            !item.TryGetProperty("kind", out var kindElement) ||
            nameElement.ValueKind != JsonValueKind.String ||
            kindElement.ValueKind != JsonValueKind.Number ||
            !kindElement.TryGetInt32(out var kindValue))
        {
            return null;
        }

        var name = nameElement.GetString();
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var location = TryMapCallHierarchySymbolLocation(item);
        if (location is null)
        {
            return null;
        }

        var kind = (SymbolKind)kindValue;
        return new CallHierarchySymbol(
            CreateCallHierarchySymbolId(name, kind, location),
            name,
            kind,
            kind.ToMcpName(),
            TryGetOptionalString(item, "detail"),
            location);
    }

    private NavigationLocation? TryMapCallHierarchySymbolLocation(JsonElement item)
    {
        if (!item.TryGetProperty("uri", out var uriElement) ||
            uriElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var uri = uriElement.GetString();
        if (string.IsNullOrWhiteSpace(uri))
        {
            return null;
        }

        string relativePath;
        try
        {
            relativePath = pathMapper.UriToRelativePath(uri);
        }
        catch (UserFacingException ex) when (ex.Code is "path_outside_root" or "invalid_lsp_uri")
        {
            return null;
        }

        var range = TryGetRangeOrNull(item, "range");
        if (range is null)
        {
            return null;
        }

        var mcpRange = PositionMapper.ToMcpRange(range);
        return new NavigationLocation(relativePath, mcpRange.StartLine, mcpRange.StartColumn, mcpRange);
    }

    private static string CreateCallHierarchySymbolId(string name, SymbolKind kind, NavigationLocation location) =>
        $"{location.File}:{location.Range.StartLine}:{location.Range.StartColumn}-{location.Range.EndLine}:{location.Range.EndColumn}:{kind.ToMcpName()}:{name}";

    private static string CallHierarchyMethodName(CallHierarchyDirection direction) =>
        direction == CallHierarchyDirection.Incoming
            ? "callHierarchy/incomingCalls"
            : "callHierarchy/outgoingCalls";

    private static string CallHierarchyDirectionName(CallHierarchyDirection direction) =>
        direction == CallHierarchyDirection.Incoming ? "incoming" : "outgoing";

    private async Task<SourceSnippetReadResult> ReadSourceSnippetAsync(
        NavigationLocation location,
        int contextLines,
        string rangeDescription,
        CancellationToken cancellationToken)
    {
        string fullPath;
        try
        {
            fullPath = pathMapper.ResolveFileInput(location.File);
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

            var lines = await File.ReadAllLinesAsync(fullPath, cancellationToken).ConfigureAwait(false);
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

    private WorkspaceSymbolMapResult MapWorkspaceSymbols(
        JsonElement response,
        int maxResults,
        IReadOnlySet<SymbolKind>? kindFilter)
    {
        if (response.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return new WorkspaceSymbolMapResult([], TotalKnown: 0, TotalUnfilteredKnown: 0, Returned: 0, Truncated: false);
        }

        if (response.ValueKind != JsonValueKind.Array)
        {
            throw new UserFacingException("invalid_lsp_response", "workspace/symbol returned an unexpected response shape.");
        }

        var items = new List<WorkspaceSymbolItem>();
        var totalUnfilteredKnown = 0;
        var totalKnown = 0;
        var returned = 0;
        foreach (var item in response.EnumerateArray())
        {
            var symbol = TryMapWorkspaceSymbol(item);
            if (symbol is null)
            {
                continue;
            }

            totalUnfilteredKnown++;
            if (kindFilter is not null && !kindFilter.Contains(symbol.Kind))
            {
                continue;
            }

            totalKnown++;
            if (returned >= maxResults)
            {
                continue;
            }

            items.Add(symbol);
            returned++;
        }

        return new WorkspaceSymbolMapResult(items, totalKnown, totalUnfilteredKnown, returned, totalKnown > returned);
    }

    private WorkspaceSymbolItem? TryMapWorkspaceSymbol(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object ||
            !item.TryGetProperty("name", out var nameElement) ||
            !item.TryGetProperty("kind", out var kindElement) ||
            nameElement.ValueKind != JsonValueKind.String ||
            kindElement.ValueKind != JsonValueKind.Number ||
            !kindElement.TryGetInt32(out var kindValue))
        {
            return null;
        }

        var name = nameElement.GetString();
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var location = TryMapWorkspaceSymbolLocation(item, out var shouldInclude);
        if (!shouldInclude)
        {
            return null;
        }

        var kind = (SymbolKind)kindValue;
        return new WorkspaceSymbolItem(
            name,
            kind,
            kind.ToMcpName(),
            TryGetOptionalString(item, "containerName"),
            location);
    }

    private NavigationLocation? TryMapWorkspaceSymbolLocation(JsonElement item, out bool shouldInclude)
    {
        shouldInclude = true;
        if (!item.TryGetProperty("location", out var locationElement) ||
            locationElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (locationElement.ValueKind != JsonValueKind.Object ||
            !locationElement.TryGetProperty("uri", out var uriElement))
        {
            return null;
        }

        if (uriElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var uri = uriElement.GetString();
        if (string.IsNullOrWhiteSpace(uri))
        {
            return null;
        }

        string relativePath;
        try
        {
            relativePath = pathMapper.UriToRelativePath(uri);
        }
        catch (UserFacingException ex) when (ex.Code is "path_outside_root" or "invalid_lsp_uri")
        {
            shouldInclude = false;
            return null;
        }

        var range = TryGetRangeOrNull(locationElement, "range");
        if (range is null)
        {
            return null;
        }

        var mcpRange = PositionMapper.ToMcpRange(range);
        return new NavigationLocation(relativePath, mcpRange.StartLine, mcpRange.StartColumn, mcpRange);
    }

    private static Lsp.Range? TryGetRangeOrNull(JsonElement item, string propertyName)
    {
        if (!item.TryGetProperty(propertyName, out var rangeElement) ||
            rangeElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        try
        {
            return rangeElement.Deserialize<Lsp.Range>(JsonOptions.Default);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static string ValidateSymbolQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new UserFacingException(
                "invalid_query",
                "query must contain at least one non-whitespace character.");
        }

        var normalizedQuery = query.Trim();
        if (normalizedQuery.Length < MinSymbolQueryLength)
        {
            throw new UserFacingException(
                "invalid_query",
                $"query must contain at least {MinSymbolQueryLength} non-whitespace characters.");
        }

        return normalizedQuery;
    }

    private static int NormalizeSymbolMaxResults(int? maxResults)
    {
        if (maxResults is null)
        {
            return DefaultSymbolMaxResults;
        }

        if (maxResults.Value < 1)
        {
            throw new UserFacingException("invalid_max_results", "maxResults must be a positive integer.");
        }

        return Math.Min(maxResults.Value, MaxSymbolMaxResults);
    }

    private static IReadOnlySet<SymbolKind>? ParseSymbolKindFilter(IReadOnlyList<string>? kindFilter)
    {
        if (kindFilter is null)
        {
            return null;
        }

        if (kindFilter.Count == 0)
        {
            throw new UserFacingException(
                "invalid_kind_filter",
                $"kindFilter must contain at least one symbol kind. Allowed values: {AllowedSymbolKindFilterValues}.");
        }

        var parsedKinds = new HashSet<SymbolKind>();
        var invalidKindNames = new List<string>();
        foreach (var rawKindName in kindFilter)
        {
            var kindName = rawKindName?.Trim();
            if (string.IsNullOrEmpty(kindName) ||
                !SymbolKindFilterValues.TryGetValue(kindName, out var kind))
            {
                invalidKindNames.Add(FormatInvalidKindName(rawKindName));
                continue;
            }

            parsedKinds.Add(kind);
        }

        if (invalidKindNames.Count > 0)
        {
            throw new UserFacingException(
                "invalid_kind_filter",
                $"Unknown symbol kind(s): {string.Join(", ", invalidKindNames)}. Allowed values: {AllowedSymbolKindFilterValues}.");
        }

        return parsedKinds;
    }

    private static string FormatInvalidKindName(string? kindName) =>
        string.IsNullOrWhiteSpace(kindName) ? "<empty>" : kindName.Trim();

    private static DocumentSymbolMapResult MapDocumentSymbols(JsonElement response)
    {
        if (response.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return new DocumentSymbolMapResult([], TotalKnown: 0, Returned: 0, Truncated: false);
        }

        if (response.ValueKind != JsonValueKind.Array)
        {
            throw new UserFacingException("invalid_lsp_response", "textDocument/documentSymbol returned an unexpected response shape.");
        }

        var items = new List<DocumentSymbolItem>();
        var totalKnown = 0;
        var returned = 0;
        foreach (var item in response.EnumerateArray())
        {
            var symbol = TryMapDocumentSymbol(item, ref totalKnown, ref returned);
            if (symbol is not null)
            {
                items.Add(symbol);
            }
        }

        return new DocumentSymbolMapResult(items, totalKnown, returned, totalKnown > returned);
    }

    private static DocumentSymbolItem? TryMapDocumentSymbol(
        JsonElement item,
        ref int totalKnown,
        ref int returned)
    {
        if (item.ValueKind != JsonValueKind.Object ||
            !item.TryGetProperty("name", out var nameElement) ||
            !item.TryGetProperty("kind", out var kindElement) ||
            !kindElement.TryGetInt32(out var kindValue))
        {
            return null;
        }

        var kind = (SymbolKind)kindValue;

        var range = TryGetRange(item, "range") ?? TryGetLocationRange(item);
        if (range is null)
        {
            return null;
        }

        totalKnown++;
        var shouldReturn = returned < MaxDocumentSymbolNodes;
        if (shouldReturn)
        {
            returned++;
        }

        var selectionRange = TryGetRange(item, "selectionRange") ?? range;
        var children = new List<DocumentSymbolItem>();
        if (item.TryGetProperty("children", out var childrenElement) &&
            childrenElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in childrenElement.EnumerateArray())
            {
                var parsedChild = TryMapDocumentSymbol(child, ref totalKnown, ref returned);
                if (parsedChild is not null && shouldReturn)
                {
                    children.Add(parsedChild);
                }
            }
        }

        if (!shouldReturn)
        {
            return null;
        }

        return new DocumentSymbolItem(
            nameElement.GetString() ?? string.Empty,
            kind,
            kind.ToMcpName(),
            PositionMapper.ToMcpRange(range),
            PositionMapper.ToMcpRange(selectionRange),
            item.TryGetProperty("detail", out var detailElement) ? detailElement.GetString() : null,
            children);
    }

    private static Lsp.Range? TryGetLocationRange(JsonElement item)
    {
        if (!item.TryGetProperty("location", out var location) ||
            location.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return TryGetRange(location, "range");
    }

    private static Lsp.Range? TryGetRange(JsonElement item, string propertyName)
    {
        if (!item.TryGetProperty(propertyName, out var rangeElement) ||
            rangeElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        try
        {
            return rangeElement.Deserialize<Lsp.Range>(JsonOptions.Default);
        }
        catch (JsonException ex)
        {
            throw new UserFacingException("invalid_lsp_response", "LSP returned a malformed range.", ex);
        }
        catch (InvalidOperationException ex)
        {
            throw new UserFacingException("invalid_lsp_response", "LSP returned a malformed range.", ex);
        }
    }

    private static HoverMapResult MapHover(JsonElement response)
    {
        if (response.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return new HoverMapResult(null, null, null, Truncated: false);
        }

        if (response.ValueKind != JsonValueKind.Object)
        {
            throw new UserFacingException("invalid_lsp_response", "textDocument/hover returned an unexpected response shape.");
        }

        var contents = response.TryGetProperty("contents", out var contentsElement)
            ? MapHoverContents(contentsElement, MaxHoverCharacters)
            : new HoverContentMapResult(null, null, Truncated: false);
        var range = response.TryGetProperty("range", out var rangeElement)
            ? ReadHoverRange(rangeElement)
            : null;

        return new HoverMapResult(contents.Contents, contents.Kind, range is null ? null : PositionMapper.ToMcpRange(range), contents.Truncated);
    }

    private static Lsp.Range? ReadHoverRange(JsonElement rangeElement)
    {
        if (rangeElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (rangeElement.ValueKind != JsonValueKind.Object)
        {
            throw new UserFacingException("invalid_lsp_response", "textDocument/hover returned a malformed range.");
        }

        try
        {
            return rangeElement.Deserialize<Lsp.Range>(JsonOptions.Default);
        }
        catch (JsonException ex)
        {
            throw new UserFacingException("invalid_lsp_response", "textDocument/hover returned a malformed range.", ex);
        }
        catch (InvalidOperationException ex)
        {
            throw new UserFacingException("invalid_lsp_response", "textDocument/hover returned a malformed range.", ex);
        }
    }

    private static HoverContentMapResult MapHoverContents(JsonElement contents, int maxCharacters)
    {
        if (contents.ValueKind == JsonValueKind.String)
        {
            return LimitHoverText(contents.GetString(), "plaintext", maxCharacters);
        }

        if (contents.ValueKind == JsonValueKind.Object)
        {
            if (contents.TryGetProperty("kind", out var kindElement) &&
                contents.TryGetProperty("value", out var valueElement))
            {
                return LimitHoverText(ValueToString(valueElement), kindElement.GetString(), maxCharacters);
            }

            if (contents.TryGetProperty("language", out _) &&
                contents.TryGetProperty("value", out valueElement))
            {
                return LimitHoverText(ValueToString(valueElement), "markedString", maxCharacters);
            }
        }

        if (contents.ValueKind == JsonValueKind.Array)
        {
            var builder = new StringBuilder(Math.Min(maxCharacters, 1024));
            string? kind = null;
            var truncated = false;
            foreach (var item in contents.EnumerateArray())
            {
                if (builder.Length >= maxCharacters)
                {
                    truncated = true;
                    break;
                }

                var separatorLength = builder.Length == 0 ? 0 : Environment.NewLine.Length;
                var remaining = maxCharacters - builder.Length - separatorLength;
                if (remaining <= 0)
                {
                    truncated = true;
                    break;
                }

                var mapped = MapHoverContents(item, remaining);
                if (!string.IsNullOrEmpty(mapped.Contents))
                {
                    if (builder.Length > 0)
                    {
                        builder.AppendLine();
                    }

                    builder.Append(mapped.Contents);
                }

                kind ??= mapped.Kind;
                if (mapped.Truncated)
                {
                    truncated = true;
                    break;
                }
            }

            return new HoverContentMapResult(builder.ToString(), kind, truncated);
        }

        return LimitHoverText(contents.ToString(), null, maxCharacters);
    }

    private static HoverContentMapResult LimitHoverText(string? value, string? kind, int maxCharacters)
    {
        if (string.IsNullOrEmpty(value))
        {
            return new HoverContentMapResult(value, kind, Truncated: false);
        }

        if (value.Length <= maxCharacters)
        {
            return new HoverContentMapResult(value, kind, Truncated: false);
        }

        return new HoverContentMapResult(value[..maxCharacters], kind, Truncated: true);
    }

    private static string? ValueToString(JsonElement value) =>
        value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            ? null
            : value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : value.ToString();

    private static string? TryGetOptionalString(JsonElement item, string propertyName) =>
        item.TryGetProperty(propertyName, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

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
                    ToolKind.Symbols => "Workspace loaded with project errors; workspace symbol results may be incomplete or empty. Call get_workspace_status for load warnings.",
                    _ => "Workspace loaded with project errors; results may be incomplete."
                },
                null,
                truncated),
            WorkspaceLoadState.LspReady => new ReadToolMetadata(
                state.ToString(),
                toolKind is ToolKind.References or ToolKind.Implementations or ToolKind.CallHierarchy ? "partial" : "unknown",
                toolKind switch
                {
                    ToolKind.References => "The language server is ready, but cross-project references may be missing until workspace loading completes.",
                    ToolKind.Implementations => "The language server is ready, but cross-project implementations may be missing until workspace loading completes.",
                    ToolKind.CallHierarchy => "The language server is ready, but call hierarchy may be incomplete until workspace loading completes.",
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

    private sealed record DocumentSymbolMapResult(
        IReadOnlyList<DocumentSymbolItem> Items,
        int TotalKnown,
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
        Symbols
    }

    private enum CallHierarchyDirection
    {
        Incoming,
        Outgoing,
        Both
    }
}
