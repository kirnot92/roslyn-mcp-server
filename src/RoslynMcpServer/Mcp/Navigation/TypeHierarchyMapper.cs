using System.Text.Json;
using RoslynMcpServer.Infrastructure;
using RoslynMcpServer.Lsp;
using RoslynMcpServer.Workspace;
using static RoslynMcpServer.Mcp.NavigationToolLimits;

namespace RoslynMcpServer.Mcp;

internal static class TypeHierarchyMapper
{
    internal static IReadOnlyList<PreparedTypeHierarchyItem> MapPreparedItems(
        DocumentPathMapper pathMapper,
        JsonElement response)
    {
        if (response.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return [];
        }

        if (response.ValueKind != JsonValueKind.Array)
        {
            throw new UserFacingException("invalid_lsp_response", "textDocument/prepareTypeHierarchy returned an unexpected response shape.");
        }

        var roots = new List<PreparedTypeHierarchyItem>();
        foreach (var item in response.EnumerateArray())
        {
            var symbol = MapTypeHierarchySymbol(pathMapper, item, "textDocument/prepareTypeHierarchy");
            if (symbol is null)
            {
                continue;
            }

            roots.Add(new PreparedTypeHierarchyItem(symbol, item.Clone()));
        }

        return roots;
    }

    internal static async Task TraverseAsync(
        DocumentPathMapper pathMapper,
        ILspClient client,
        PreparedTypeHierarchyItem root,
        TypeHierarchyDirection direction,
        int maxDepth,
        int maxResults,
        IReadOnlyList<string>? includePathPrefixes,
        List<TypeHierarchyEdge> edges,
        HashSet<string> visitedEdges,
        TypeHierarchyTraversalState state,
        CancellationToken cancellationToken)
    {
        var visitedFollowUps = new HashSet<string>(StringComparer.Ordinal)
        {
            CreateTypeHierarchyFollowUpKey(root.Symbol.Id, direction)
        };
        var queue = new Queue<TypeHierarchyQueueItem>();
        queue.Enqueue(new TypeHierarchyQueueItem(root.Symbol, root.OriginalItem, Depth: 0));

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current.Depth >= maxDepth)
            {
                continue;
            }

            if (state.Returned >= maxResults)
            {
                state.HitResultLimit = true;
                continue;
            }

            var response = await client.RequestAsync(
                TypeHierarchyMethodName(direction),
                new { item = current.OriginalItem },
                TypeHierarchyTimeout,
                cancellationToken,
                isExpensive: true);

            var nextItems = MapTypeHierarchyFollowUpItems(pathMapper, response, direction);
            foreach (var next in nextItems)
            {
                var edge = CreateTypeHierarchyEdge(root.Symbol, current.Symbol, next.Symbol, direction, current.Depth + 1);
                if (!visitedEdges.Add(CreateTypeHierarchyEdgeKey(edge)))
                {
                    continue;
                }

                state.TotalUnfilteredKnown++;
                if (!NavigationPathFilters.IsIncludedByPathPrefixes(next.Symbol.Location.File, includePathPrefixes))
                {
                    continue;
                }

                state.TotalKnown++;
                if (state.Returned < maxResults)
                {
                    edges.Add(edge);
                    state.Returned++;
                }

                if (state.Returned >= maxResults)
                {
                    if (current.Depth + 1 < maxDepth)
                    {
                        state.HitResultLimit = true;
                    }

                    continue;
                }

                var followUpKey = CreateTypeHierarchyFollowUpKey(next.Symbol.Id, direction);
                if (visitedFollowUps.Add(followUpKey))
                {
                    queue.Enqueue(new TypeHierarchyQueueItem(next.Symbol, next.OriginalItem, current.Depth + 1));
                }
            }
        }
    }

    private static IReadOnlyList<PreparedTypeHierarchyItem> MapTypeHierarchyFollowUpItems(
        DocumentPathMapper pathMapper,
        JsonElement response,
        TypeHierarchyDirection direction)
    {
        if (response.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return [];
        }

        if (response.ValueKind != JsonValueKind.Array)
        {
            throw new UserFacingException("invalid_lsp_response", $"{TypeHierarchyMethodName(direction)} returned an unexpected response shape.");
        }

        var items = new List<PreparedTypeHierarchyItem>();
        foreach (var item in response.EnumerateArray())
        {
            var symbol = MapTypeHierarchySymbol(pathMapper, item, TypeHierarchyMethodName(direction));
            if (symbol is null)
            {
                continue;
            }

            items.Add(new PreparedTypeHierarchyItem(symbol, item.Clone()));
        }

        return items;
    }

    private static TypeHierarchySymbol? MapTypeHierarchySymbol(
        DocumentPathMapper pathMapper,
        JsonElement item,
        string method)
    {
        if (item.ValueKind != JsonValueKind.Object ||
            !item.TryGetProperty("name", out var nameElement) ||
            !item.TryGetProperty("kind", out var kindElement) ||
            !item.TryGetProperty("uri", out var uriElement) ||
            nameElement.ValueKind != JsonValueKind.String ||
            kindElement.ValueKind != JsonValueKind.Number ||
            uriElement.ValueKind != JsonValueKind.String ||
            !kindElement.TryGetInt32(out var kindValue))
        {
            throw new UserFacingException("invalid_lsp_response", $"{method} returned a malformed type hierarchy item.");
        }

        var name = nameElement.GetString();
        var uri = uriElement.GetString();
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(uri))
        {
            throw new UserFacingException("invalid_lsp_response", $"{method} returned a malformed type hierarchy item.");
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

        var range = TryGetRangeOrNull(item, "range")
            ?? throw new UserFacingException("invalid_lsp_response", $"{method} returned a malformed type hierarchy item.");
        var selectionRange = TryGetRangeOrNull(item, "selectionRange")
            ?? throw new UserFacingException("invalid_lsp_response", $"{method} returned a malformed type hierarchy item.");
        var mcpRange = PositionMapper.ToMcpRange(range);
        var mcpSelectionRange = PositionMapper.ToMcpRange(selectionRange);
        var location = new NavigationLocation(relativePath, mcpRange.StartLine, mcpRange.StartColumn, mcpRange);
        var kind = (SymbolKind)kindValue;

        return new TypeHierarchySymbol(
            CreateTypeHierarchySymbolId(name, kind, location),
            name,
            kind,
            kind.ToMcpName(),
            TryGetOptionalString(item, "detail"),
            location,
            mcpSelectionRange);
    }

    private static TypeHierarchyEdge CreateTypeHierarchyEdge(
        TypeHierarchySymbol root,
        TypeHierarchySymbol current,
        TypeHierarchySymbol next,
        TypeHierarchyDirection direction,
        int depth)
    {
        var from = direction == TypeHierarchyDirection.Supertypes ? next : current;
        var to = direction == TypeHierarchyDirection.Supertypes ? current : next;
        return new TypeHierarchyEdge(root.Id, TypeHierarchyDirectionName(direction), depth, from, to);
    }

    private static string CreateTypeHierarchySymbolId(string name, SymbolKind kind, NavigationLocation location) =>
        $"{location.File}:{location.Range.StartLine}:{location.Range.StartColumn}-{location.Range.EndLine}:{location.Range.EndColumn}:{kind.ToMcpName()}:{name}";

    private static string CreateTypeHierarchyEdgeKey(TypeHierarchyEdge edge) =>
        $"{edge.RootId}:{edge.Direction}:{edge.From.Id}->{edge.To.Id}";

    private static string CreateTypeHierarchyFollowUpKey(string symbolId, TypeHierarchyDirection direction) =>
        $"{TypeHierarchyDirectionName(direction)}:{symbolId}";

    private static string TypeHierarchyMethodName(TypeHierarchyDirection direction) =>
        direction == TypeHierarchyDirection.Supertypes
            ? "typeHierarchy/supertypes"
            : "typeHierarchy/subtypes";

    private static string TypeHierarchyDirectionName(TypeHierarchyDirection direction) =>
        direction == TypeHierarchyDirection.Supertypes ? "supertypes" : "subtypes";

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

    private static string? TryGetOptionalString(JsonElement item, string propertyName) =>
        item.TryGetProperty(propertyName, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
}
