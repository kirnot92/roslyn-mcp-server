using System.Text.Json;
using RoslynMcpServer.Infrastructure;
using RoslynMcpServer.Lsp;
using RoslynMcpServer.Workspace;

namespace RoslynMcpServer.Mcp;

internal static class WorkspaceSymbolMapper
{
    internal static WorkspaceSymbolMapResult Map(
        DocumentPathMapper pathMapper,
        JsonElement response,
        int maxResults,
        IReadOnlySet<SymbolKind>? kindFilter,
        string query,
        SymbolMatchMode matchMode,
        IReadOnlyList<string>? includePathPrefixes)
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
            var symbol = TryMapWorkspaceSymbol(pathMapper, item);
            if (symbol is null)
            {
                continue;
            }

            totalUnfilteredKnown++;
            if (kindFilter is not null && !kindFilter.Contains(symbol.Kind))
            {
                continue;
            }

            if (!MatchesSymbolName(symbol.Name, query, matchMode))
            {
                continue;
            }

            if (!NavigationPathFilters.IsIncludedByPathPrefixes(symbol.Location?.File, includePathPrefixes))
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

    private static bool MatchesSymbolName(string name, string query, SymbolMatchMode matchMode) =>
        matchMode switch
        {
            SymbolMatchMode.Default => true,
            SymbolMatchMode.Exact => string.Equals(name, query, StringComparison.OrdinalIgnoreCase),
            SymbolMatchMode.Prefix => name.StartsWith(query, StringComparison.OrdinalIgnoreCase),
            SymbolMatchMode.Contains => name.Contains(query, StringComparison.OrdinalIgnoreCase),
            _ => false
        };

    private static WorkspaceSymbolItem? TryMapWorkspaceSymbol(DocumentPathMapper pathMapper, JsonElement item)
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

        var location = TryMapWorkspaceSymbolLocation(pathMapper, item, out var shouldInclude);
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

    private static NavigationLocation? TryMapWorkspaceSymbolLocation(
        DocumentPathMapper pathMapper,
        JsonElement item,
        out bool shouldInclude)
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

    private static string? TryGetOptionalString(JsonElement item, string propertyName) =>
        item.TryGetProperty(propertyName, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
}
