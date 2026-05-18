using System.Text.Json;
using RoslynMcpServer.Infrastructure;
using RoslynMcpServer.Lsp;

namespace RoslynMcpServer.Mcp;

public sealed partial class NavigationTools
{
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
        IReadOnlySet<SymbolKind>? kindFilter,
        int maxResults,
        List<CallHierarchyEdge> edges,
        ref int totalUnfilteredKnown,
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

            totalUnfilteredKnown++;
            if (kindFilter is not null && !kindFilter.Contains(GetCallHierarchyCounterpartKind(edge, direction)))
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

    private static SymbolKind GetCallHierarchyCounterpartKind(CallHierarchyEdge edge, CallHierarchyDirection direction) =>
        direction == CallHierarchyDirection.Incoming ? edge.From.Kind : edge.To.Kind;

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

}
