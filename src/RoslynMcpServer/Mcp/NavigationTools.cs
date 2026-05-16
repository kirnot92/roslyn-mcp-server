using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using RoslynMcpServer.Infrastructure;
using RoslynMcpServer.Lsp;
using RoslynMcpServer.Workspace;

namespace RoslynMcpServer.Mcp;

[McpServerToolType]
public sealed class NavigationTools(
    WorkspaceSession session,
    DocumentStateManager documents)
{
    private const int MaxDocumentSymbolNodes = 1000;
    private const int MaxHoverCharacters = 20_000;
    private static readonly TimeSpan NavigationTimeout = TimeSpan.FromSeconds(10);

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

            var symbols = ParseDocumentSymbols(response);
            var totalKnown = CountSymbols(symbols);
            var returned = 0;
            var items = MapDocumentSymbols(symbols, ref returned);
            var metadata = CreateMetadata(context.State, ToolKind.DocumentSymbols, truncated: totalKnown > returned);

            return new DocumentSymbolsResult(
                items,
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

            var (contents, kind, range) = MapHover(response);
            var truncated = contents?.Length > MaxHoverCharacters;
            if (truncated)
            {
                contents = contents![..MaxHoverCharacters];
            }

            var metadata = CreateMetadata(context.State, ToolKind.Hover, truncated);
            return new HoverResult(
                contents,
                kind,
                range,
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

    private static IReadOnlyList<DocumentSymbolItem> MapDocumentSymbols(
        IReadOnlyList<DocumentSymbol> symbols,
        ref int returned)
    {
        var items = new List<DocumentSymbolItem>();
        foreach (var symbol in symbols)
        {
            if (returned >= MaxDocumentSymbolNodes)
            {
                break;
            }

            returned++;
            var children = symbol.Children is null
                ? []
                : MapDocumentSymbols(symbol.Children, ref returned);
            items.Add(new DocumentSymbolItem(
                symbol.Name,
                symbol.Kind,
                ToMcpSymbolKindName(symbol.Kind),
                PositionMapper.ToMcpRange(symbol.Range),
                PositionMapper.ToMcpRange(symbol.SelectionRange),
                symbol.Detail,
                children));
        }

        return items;
    }

    private static IReadOnlyList<DocumentSymbol> ParseDocumentSymbols(JsonElement response)
    {
        if (response.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return [];
        }

        if (response.ValueKind != JsonValueKind.Array)
        {
            throw new UserFacingException("invalid_lsp_response", "textDocument/documentSymbol returned an unexpected response shape.");
        }

        var symbols = new List<DocumentSymbol>();
        foreach (var item in response.EnumerateArray())
        {
            var symbol = ParseDocumentSymbol(item);
            if (symbol is not null)
            {
                symbols.Add(symbol);
            }
        }

        return symbols;
    }

    private static DocumentSymbol? ParseDocumentSymbol(JsonElement item)
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

        var selectionRange = TryGetRange(item, "selectionRange") ?? range;
        IReadOnlyList<DocumentSymbol>? children = null;
        if (item.TryGetProperty("children", out var childrenElement) &&
            childrenElement.ValueKind == JsonValueKind.Array)
        {
            var parsedChildren = new List<DocumentSymbol>();
            foreach (var child in childrenElement.EnumerateArray())
            {
                var parsedChild = ParseDocumentSymbol(child);
                if (parsedChild is not null)
                {
                    parsedChildren.Add(parsedChild);
                }
            }

            children = parsedChildren;
        }

        return new DocumentSymbol(
            nameElement.GetString() ?? string.Empty,
            kind,
            range,
            selectionRange,
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

        return rangeElement.Deserialize<Lsp.Range>(JsonOptions.Default);
    }

    private static int CountSymbols(IReadOnlyList<DocumentSymbol> symbols)
    {
        var count = 0;
        foreach (var symbol in symbols)
        {
            count++;
            if (symbol.Children is not null)
            {
                count += CountSymbols(symbol.Children);
            }
        }

        return count;
    }

    private static (string? Contents, string? Kind, McpRange? Range) MapHover(JsonElement response)
    {
        if (response.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return (null, null, null);
        }

        var contents = response.TryGetProperty("contents", out var contentsElement)
            ? MapHoverContents(contentsElement)
            : (Contents: (string?)null, Kind: (string?)null);
        var range = response.TryGetProperty("range", out var rangeElement)
            ? rangeElement.Deserialize<Lsp.Range>(JsonOptions.Default)
            : null;

        return (contents.Contents, contents.Kind, range is null ? null : PositionMapper.ToMcpRange(range));
    }

    private static (string? Contents, string? Kind) MapHoverContents(JsonElement contents)
    {
        if (contents.ValueKind == JsonValueKind.String)
        {
            return (contents.GetString(), "plaintext");
        }

        if (contents.ValueKind == JsonValueKind.Object)
        {
            if (contents.TryGetProperty("kind", out var kindElement) &&
                contents.TryGetProperty("value", out var valueElement))
            {
                return (valueElement.GetString(), kindElement.GetString());
            }

            if (contents.TryGetProperty("language", out _) &&
                contents.TryGetProperty("value", out valueElement))
            {
                return (valueElement.GetString(), "markedString");
            }
        }

        if (contents.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            string? kind = null;
            foreach (var item in contents.EnumerateArray())
            {
                var mapped = MapHoverContents(item);
                if (!string.IsNullOrEmpty(mapped.Contents))
                {
                    parts.Add(mapped.Contents);
                }

                kind ??= mapped.Kind;
            }

            return (string.Join(Environment.NewLine, parts), kind);
        }

        return (contents.ToString(), null);
    }

    private static ReadToolMetadata CreateMetadata(WorkspaceLoadState state, ToolKind toolKind, bool truncated) =>
        state switch
        {
            WorkspaceLoadState.Ready => new ReadToolMetadata(
                state.ToString(),
                "complete",
                null,
                null,
                truncated),
            WorkspaceLoadState.WorkspaceWarming => new ReadToolMetadata(
                state.ToString(),
                toolKind is ToolKind.DocumentSymbols ? "partial" : "partial",
                toolKind is ToolKind.DocumentSymbols
                    ? "Workspace is still warming; symbols from projects not loaded yet may be missing."
                    : "Workspace is still warming; hover may not include complete semantic information.",
                2000,
                truncated),
            WorkspaceLoadState.LspReady => new ReadToolMetadata(
                state.ToString(),
                "unknown",
                "The language server is ready, but workspace completeness is not known yet.",
                2000,
                truncated),
            _ => new ReadToolMetadata(
                state.ToString(),
                "unknown",
                null,
                null,
                truncated)
        };

    private static string ToMcpSymbolKindName(SymbolKind kind) =>
        kind switch
        {
            _ when !System.Enum.IsDefined(kind) => "unknown",
            // Keep multi-word LSP names in camelCase for stable MCP output.
            SymbolKind.EnumMember => "enumMember",
            SymbolKind.TypeParameter => "typeParameter",
            _ => kind.ToString().ToLowerInvariant()
        };

    private enum ToolKind
    {
        DocumentSymbols,
        Hover
    }
}
