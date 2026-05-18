using System.Text;
using System.Text.Json;
using RoslynMcpServer.Infrastructure;
using RoslynMcpServer.Lsp;

namespace RoslynMcpServer.Mcp;

public sealed partial class NavigationTools
{
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
}
