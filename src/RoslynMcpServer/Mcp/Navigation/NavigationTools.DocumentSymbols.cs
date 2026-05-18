using System.Text.Json;
using RoslynMcpServer.Infrastructure;
using RoslynMcpServer.Lsp;

namespace RoslynMcpServer.Mcp;

public sealed partial class NavigationTools
{
    private static DocumentSymbolMapResult MapDocumentSymbols(JsonElement response, IReadOnlySet<SymbolKind>? kindFilter)
    {
        if (response.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return new DocumentSymbolMapResult([], TotalKnown: 0, TotalUnfilteredKnown: 0, Returned: 0, Truncated: false);
        }

        if (response.ValueKind != JsonValueKind.Array)
        {
            throw new UserFacingException("invalid_lsp_response", "textDocument/documentSymbol returned an unexpected response shape.");
        }

        var items = new List<DocumentSymbolItem>();
        var totalUnfilteredKnown = 0;
        var totalKnown = 0;
        foreach (var item in response.EnumerateArray())
        {
            var symbol = TryMapDocumentSymbol(item, kindFilter, ref totalUnfilteredKnown, ref totalKnown);
            if (symbol is not null)
            {
                items.Add(symbol);
            }
        }

        var returned = 0;
        var limitedItems = LimitDocumentSymbols(items, ref returned);

        return new DocumentSymbolMapResult(limitedItems, totalKnown, totalUnfilteredKnown, returned, totalKnown > returned);
    }

    private static DocumentSymbolItem? TryMapDocumentSymbol(
        JsonElement item,
        IReadOnlySet<SymbolKind>? kindFilter,
        ref int totalUnfilteredKnown,
        ref int totalKnown)
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

        totalUnfilteredKnown++;

        var selectionRange = TryGetRange(item, "selectionRange") ?? range;
        var children = new List<DocumentSymbolItem>();
        if (item.TryGetProperty("children", out var childrenElement) &&
            childrenElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in childrenElement.EnumerateArray())
            {
                var parsedChild = TryMapDocumentSymbol(child, kindFilter, ref totalUnfilteredKnown, ref totalKnown);
                if (parsedChild is not null)
                {
                    children.Add(parsedChild);
                }
            }
        }

        if (kindFilter is not null && !kindFilter.Contains(kind) && children.Count == 0)
        {
            return null;
        }

        totalKnown++;

        return new DocumentSymbolItem(
            nameElement.GetString() ?? string.Empty,
            kind,
            kind.ToMcpName(),
            PositionMapper.ToMcpRange(range),
            PositionMapper.ToMcpRange(selectionRange),
            item.TryGetProperty("detail", out var detailElement) ? detailElement.GetString() : null,
            children);
    }

    private static IReadOnlyList<DocumentSymbolItem> LimitDocumentSymbols(
        IReadOnlyList<DocumentSymbolItem> items,
        ref int returned)
    {
        var limitedItems = new List<DocumentSymbolItem>();
        foreach (var item in items)
        {
            var limitedItem = LimitDocumentSymbol(item, ref returned);
            if (limitedItem is null)
            {
                break;
            }

            limitedItems.Add(limitedItem);
        }

        return limitedItems;
    }

    private static DocumentSymbolItem? LimitDocumentSymbol(DocumentSymbolItem item, ref int returned)
    {
        if (returned >= MaxDocumentSymbolNodes)
        {
            return null;
        }

        returned++;
        var limitedChildren = new List<DocumentSymbolItem>();
        foreach (var child in item.Children)
        {
            var limitedChild = LimitDocumentSymbol(child, ref returned);
            if (limitedChild is null)
            {
                break;
            }

            limitedChildren.Add(limitedChild);
        }

        return item with { Children = limitedChildren };
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
}
