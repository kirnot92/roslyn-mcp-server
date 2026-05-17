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
    private const int DefaultSymbolMaxResults = 300;
    private const int MaxSymbolMaxResults = 1000;
    private const int MinSymbolQueryLength = 2;
    private static readonly TimeSpan NavigationTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DefinitionTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReferencesTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ImplementationsTimeout = ReferencesTimeout;
    private static readonly TimeSpan WorkspaceSymbolTimeout = TimeSpan.FromSeconds(30);

    [McpServerTool(Name = "document_symbols")]
    [Description("Return document symbols for a C# source file.")]
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
    [Description("Return hover information for a C# source location. line and column are 1-based.")]
    public async Task<object> Hover(string file, int line, int column, CancellationToken cancellationToken = default)
    {
        try
        {
            var position = PositionMapper.ToLspPosition(line, column);
            var context = await session.PrepareReadToolAsync(cancellationToken).ConfigureAwait(false);
            var document = await documents.EnsureOpenAsync(file, context.Handle.Client, cancellationToken).ConfigureAwait(false);
            var response = await context.Handle.Client.RequestAsync(
                "textDocument/hover",
                new
                {
                    textDocument = new TextDocumentIdentifier(document.Uri),
                    position
                },
                NavigationTimeout,
                cancellationToken).ConfigureAwait(false);

            var hover = MapHover(response);
            var metadata = CreateMetadata(context.State, ToolKind.Hover, hover.Truncated);
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
    [Description("Return definition locations for a C# source location. line and column are 1-based.")]
    public async Task<object> GoToDefinition(string file, int line, int column, CancellationToken cancellationToken = default)
    {
        try
        {
            var position = PositionMapper.ToLspPosition(line, column);
            var context = await session.PrepareReadToolAsync(cancellationToken).ConfigureAwait(false);
            var document = await documents.EnsureOpenAsync(file, context.Handle.Client, cancellationToken).ConfigureAwait(false);
            var response = await context.Handle.Client.RequestAsync(
                "textDocument/definition",
                new
                {
                    textDocument = new TextDocumentIdentifier(document.Uri),
                    position
                },
                DefinitionTimeout,
                cancellationToken).ConfigureAwait(false);

            var locations = MapLocations(response, "textDocument/definition", maxResults: null);
            var metadata = CreateMetadata(context.State, ToolKind.Definition, truncated: false);
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
    [Description("Return definition locations and source snippets for a C# source location. line and column are 1-based.")]
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
            var position = PositionMapper.ToLspPosition(line, column);
            var context = await session.PrepareReadToolAsync(cancellationToken).ConfigureAwait(false);
            var document = await documents.EnsureOpenAsync(file, context.Handle.Client, cancellationToken).ConfigureAwait(false);
            var response = await context.Handle.Client.RequestAsync(
                "textDocument/definition",
                new
                {
                    textDocument = new TextDocumentIdentifier(document.Uri),
                    position
                },
                DefinitionTimeout,
                cancellationToken).ConfigureAwait(false);

            var locations = MapLocations(response, "textDocument/definition", effectiveMaxDefinitions);
            var items = new List<PeekDefinitionItem>(locations.Items.Count);
            foreach (var location in locations.Items)
            {
                var snippet = await ReadSourceSnippetAsync(location, effectiveContextLines, cancellationToken).ConfigureAwait(false);
                items.Add(new PeekDefinitionItem(
                    location.File,
                    location.Line,
                    location.Column,
                    location.Range,
                    snippet.Snippet,
                    snippet.Error));
            }

            var metadata = CreateMetadata(context.State, ToolKind.Definition, locations.Truncated);
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
    [Description("Return references for a C# source location. line and column are 1-based.")]
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
            var position = PositionMapper.ToLspPosition(line, column);
            var context = await session.PrepareReadToolAsync(cancellationToken).ConfigureAwait(false);
            var document = await documents.EnsureOpenAsync(file, context.Handle.Client, cancellationToken).ConfigureAwait(false);
            var response = await context.Handle.Client.RequestAsync(
                "textDocument/references",
                new ReferenceParams(
                    new TextDocumentIdentifier(document.Uri),
                    position,
                    new ReferenceContext(includeDeclaration)),
                ReferencesTimeout,
                cancellationToken,
                isExpensive: true).ConfigureAwait(false);

            var locations = MapLocations(response, "textDocument/references", effectiveMaxResults);
            var metadata = CreateMetadata(context.State, ToolKind.References, locations.Truncated);
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

    [McpServerTool(Name = "find_implementations")]
    [Description("Return implementation locations for a C# contract position.")]
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
            var position = PositionMapper.ToLspPosition(line, column);
            var context = await session.PrepareReadToolAsync(cancellationToken).ConfigureAwait(false);
            var document = await documents.EnsureOpenAsync(file, context.Handle.Client, cancellationToken).ConfigureAwait(false);
            var response = await context.Handle.Client.RequestAsync(
                "textDocument/implementation",
                new
                {
                    textDocument = new TextDocumentIdentifier(document.Uri),
                    position
                },
                ImplementationsTimeout,
                cancellationToken,
                isExpensive: true).ConfigureAwait(false);

            var locations = MapLocations(response, "textDocument/implementation", effectiveMaxResults);
            var metadata = CreateMetadata(context.State, ToolKind.Implementations, locations.Truncated);
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

    [McpServerTool(Name = "find_symbols")]
    [Description("Search workspace symbols by name.")]
    public async Task<object> FindSymbols(
        string query,
        int? maxResults = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            query = ValidateSymbolQuery(query);
            var effectiveMaxResults = NormalizeSymbolMaxResults(maxResults);
            var context = await session.PrepareReadToolAsync(cancellationToken).ConfigureAwait(false);
            var response = await context.Handle.Client.RequestAsync(
                "workspace/symbol",
                new WorkspaceSymbolParams(query),
                WorkspaceSymbolTimeout,
                cancellationToken,
                isExpensive: true).ConfigureAwait(false);

            var symbols = MapWorkspaceSymbols(response, effectiveMaxResults);
            var metadata = CreateMetadata(context.State, ToolKind.Symbols, symbols.Truncated);
            return new FindSymbolsResult(
                symbols.Items,
                symbols.TotalKnown,
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

    private async Task<SourceSnippetReadResult> ReadSourceSnippetAsync(
        NavigationLocation location,
        int contextLines,
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
                        $"Definition range is outside the readable source file: {location.File}"));
            }

            var startLine = Math.Max(location.Range.StartLine - contextLines, 1);
            var requestedEndLine = Math.Min(location.Range.EndLine + contextLines, lines.Length);
            if (requestedEndLine < startLine)
            {
                return new SourceSnippetReadResult(
                    null,
                    new SourceSnippetError(
                        "invalid_range",
                        $"Definition range is outside the readable source file: {location.File}"));
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
                new SourceSnippetError("file_read_error", $"Could not read definition source: {ex.Message}"));
        }
        catch (UnauthorizedAccessException ex)
        {
            return new SourceSnippetReadResult(
                null,
                new SourceSnippetError("file_read_error", $"Could not read definition source: {ex.Message}"));
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

    private WorkspaceSymbolMapResult MapWorkspaceSymbols(JsonElement response, int maxResults)
    {
        if (response.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return new WorkspaceSymbolMapResult([], TotalKnown: 0, Returned: 0, Truncated: false);
        }

        if (response.ValueKind != JsonValueKind.Array)
        {
            throw new UserFacingException("invalid_lsp_response", "workspace/symbol returned an unexpected response shape.");
        }

        var items = new List<WorkspaceSymbolItem>();
        var totalKnown = 0;
        var returned = 0;
        foreach (var item in response.EnumerateArray())
        {
            var symbol = TryMapWorkspaceSymbol(item);
            if (symbol is null)
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

        return new WorkspaceSymbolMapResult(items, totalKnown, returned, totalKnown > returned);
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
                    ToolKind.Symbols => "Workspace loaded with project errors; workspace symbol results may be incomplete or empty. Call get_workspace_status for load warnings.",
                    _ => "Workspace loaded with project errors; results may be incomplete."
                },
                null,
                truncated),
            WorkspaceLoadState.LspReady => new ReadToolMetadata(
                state.ToString(),
                toolKind is ToolKind.References or ToolKind.Implementations ? "partial" : "unknown",
                toolKind switch
                {
                    ToolKind.References => "The language server is ready, but cross-project references may be missing until workspace loading completes.",
                    ToolKind.Implementations => "The language server is ready, but cross-project implementations may be missing until workspace loading completes.",
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
        int Returned,
        bool Truncated);

    private enum ToolKind
    {
        DocumentSymbols,
        Hover,
        Definition,
        References,
        Implementations,
        Symbols
    }
}
