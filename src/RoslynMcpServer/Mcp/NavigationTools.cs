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
                "partial",
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

    private enum ToolKind
    {
        DocumentSymbols,
        Hover
    }
}
