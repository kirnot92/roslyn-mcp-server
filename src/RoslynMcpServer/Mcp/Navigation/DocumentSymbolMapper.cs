using System.Text.Json;
using RoslynMcpServer.Infrastructure;
using RoslynMcpServer.Lsp;

namespace RoslynMcpServer.Mcp;

internal static class DocumentSymbolMapper
{
    internal static DocumentSymbolMapResult Map(
        JsonElement response,
        IReadOnlySet<SymbolKind>? kindFilter,
        string? query,
        int maxResults)
    {
        if (response.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return new DocumentSymbolMapResult([], TotalKnown: 0, TotalUnfilteredKnown: 0, Returned: 0, Truncated: false);
        }

        if (response.ValueKind != JsonValueKind.Array)
        {
            throw new UserFacingException("invalid_lsp_response", "textDocument/documentSymbol returned an unexpected response shape.");
        }

        var items = new List<MappedDocumentSymbol>();
        var totalUnfilteredKnown = 0;
        var totalKnown = 0;
        var ordinal = 0;
        foreach (var item in response.EnumerateArray())
        {
            var symbol = TryMapDocumentSymbol(item, kindFilter, query, ordinal++, ref totalUnfilteredKnown, ref totalKnown);
            if (symbol is not null)
            {
                items.Add(symbol);
            }
        }

        var sortedItems = SortDocumentSymbols(items, sortByRelevance: query is not null);
        var documentSymbolItems = new List<DocumentSymbolItem>(sortedItems.Count);
        foreach (var item in sortedItems)
        {
            documentSymbolItems.Add(ToDocumentSymbolItem(item));
        }

        var returned = 0;
        var limitedItems = LimitDocumentSymbols(documentSymbolItems, maxResults, ref returned);

        return new DocumentSymbolMapResult(limitedItems, totalKnown, totalUnfilteredKnown, returned, totalKnown > returned);
    }

    private static MappedDocumentSymbol? TryMapDocumentSymbol(
        JsonElement item,
        IReadOnlySet<SymbolKind>? kindFilter,
        string? query,
        int ordinal,
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

        var name = nameElement.GetString() ?? string.Empty;
        var selectionRange = TryGetRange(item, "selectionRange") ?? range;
        var children = new List<MappedDocumentSymbol>();
        if (item.TryGetProperty("children", out var childrenElement) &&
            childrenElement.ValueKind == JsonValueKind.Array)
        {
            var childOrdinal = 0;
            foreach (var child in childrenElement.EnumerateArray())
            {
                var parsedChild = TryMapDocumentSymbol(child, kindFilter, query, childOrdinal++, ref totalUnfilteredKnown, ref totalKnown);
                if (parsedChild is not null)
                {
                    children.Add(parsedChild);
                }
            }
        }

        var matchesSelf = MatchesDocumentSymbolFilters(name, kind, kindFilter, query);
        if (!matchesSelf && children.Count == 0)
        {
            return null;
        }

        totalKnown++;

        return new MappedDocumentSymbol(
            name,
            kind,
            kind.ToMcpName(),
            PositionMapper.ToMcpRange(range),
            PositionMapper.ToMcpRange(selectionRange),
            item.TryGetProperty("detail", out var detailElement) ? detailElement.GetString() : null,
            children,
            query is null ? 0 : GetDocumentSymbolRelevance(name, query, matchesSelf, children),
            ordinal);
    }

    private static bool MatchesDocumentSymbolFilters(
        string name,
        SymbolKind kind,
        IReadOnlySet<SymbolKind>? kindFilter,
        string? query)
    {
        if (kindFilter is not null && !kindFilter.Contains(kind))
        {
            return false;
        }

        return query is null || name.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<MappedDocumentSymbol> SortDocumentSymbols(
        IReadOnlyList<MappedDocumentSymbol> items,
        bool sortByRelevance)
    {
        var sortedItems = new List<MappedDocumentSymbol>(items.Count);
        foreach (var item in items)
        {
            sortedItems.Add(item with { Children = SortDocumentSymbols(item.Children, sortByRelevance) });
        }

        sortedItems.Sort((left, right) => CompareDocumentSymbols(left, right, sortByRelevance));
        return sortedItems;
    }

    private static int CompareDocumentSymbols(
        MappedDocumentSymbol left,
        MappedDocumentSymbol right,
        bool sortByRelevance)
    {
        if (sortByRelevance)
        {
            var relevanceComparison = left.Relevance.CompareTo(right.Relevance);
            if (relevanceComparison != 0)
            {
                return relevanceComparison;
            }
        }

        var sourceComparison = CompareDocumentSymbolSourceOrder(left, right);
        return sourceComparison != 0
            ? sourceComparison
            : left.Ordinal.CompareTo(right.Ordinal);
    }

    private static int CompareDocumentSymbolSourceOrder(MappedDocumentSymbol left, MappedDocumentSymbol right)
    {
        var startLineComparison = left.Range.StartLine.CompareTo(right.Range.StartLine);
        if (startLineComparison != 0)
        {
            return startLineComparison;
        }

        var startColumnComparison = left.Range.StartColumn.CompareTo(right.Range.StartColumn);
        if (startColumnComparison != 0)
        {
            return startColumnComparison;
        }

        var endLineComparison = left.Range.EndLine.CompareTo(right.Range.EndLine);
        return endLineComparison != 0
            ? endLineComparison
            : left.Range.EndColumn.CompareTo(right.Range.EndColumn);
    }

    private static int GetDocumentSymbolRelevance(
        string name,
        string query,
        bool matchesSelf,
        IReadOnlyList<MappedDocumentSymbol> children)
    {
        var relevance = matchesSelf ? GetSymbolNameRelevance(name, query) : int.MaxValue;
        foreach (var child in children)
        {
            relevance = Math.Min(relevance, child.Relevance);
        }

        return relevance;
    }

    private static int GetSymbolNameRelevance(string name, string query)
    {
        if (string.Equals(name, query, StringComparison.Ordinal))
        {
            return 0;
        }

        if (string.Equals(name, query, StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        return 3;
    }

    private static DocumentSymbolItem ToDocumentSymbolItem(MappedDocumentSymbol symbol)
    {
        var children = new List<DocumentSymbolItem>(symbol.Children.Count);
        foreach (var child in symbol.Children)
        {
            children.Add(ToDocumentSymbolItem(child));
        }

        return new DocumentSymbolItem(
            symbol.Name,
            symbol.Kind,
            symbol.KindName,
            symbol.Range,
            symbol.SelectionRange,
            symbol.Detail,
            children);
    }

    private static IReadOnlyList<DocumentSymbolItem> LimitDocumentSymbols(
        IReadOnlyList<DocumentSymbolItem> items,
        int maxResults,
        ref int returned)
    {
        var limitedItems = new List<DocumentSymbolItem>();
        foreach (var item in items)
        {
            var limitedItem = LimitDocumentSymbol(item, maxResults, ref returned);
            if (limitedItem is null)
            {
                break;
            }

            limitedItems.Add(limitedItem);
        }

        return limitedItems;
    }

    private static DocumentSymbolItem? LimitDocumentSymbol(DocumentSymbolItem item, int maxResults, ref int returned)
    {
        if (returned >= maxResults)
        {
            return null;
        }

        returned++;
        var limitedChildren = new List<DocumentSymbolItem>();
        foreach (var child in item.Children)
        {
            var limitedChild = LimitDocumentSymbol(child, maxResults, ref returned);
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

    private sealed record MappedDocumentSymbol(
        string Name,
        SymbolKind Kind,
        string KindName,
        McpRange Range,
        McpRange SelectionRange,
        string? Detail,
        IReadOnlyList<MappedDocumentSymbol> Children,
        int Relevance,
        int Ordinal);
}
