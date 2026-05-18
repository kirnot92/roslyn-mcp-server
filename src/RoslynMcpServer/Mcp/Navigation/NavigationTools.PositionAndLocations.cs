using System.Text.Json;
using RoslynMcpServer.Infrastructure;
using RoslynMcpServer.Lsp;
using RoslynMcpServer.Workspace;

namespace RoslynMcpServer.Mcp;

public sealed partial class NavigationTools
{
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

    private LocationMapResult MapLocations(
        JsonElement response,
        string method,
        int? maxResults,
        IReadOnlyList<string>? includePathPrefixes = null)
    {
        if (response.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return new LocationMapResult([], TotalKnown: 0, TotalUnfilteredKnown: 0, Returned: 0, Truncated: false);
        }

        var items = new List<NavigationLocation>();
        var totalUnfilteredKnown = 0;
        var totalKnown = 0;
        var returned = 0;

        if (response.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in response.EnumerateArray())
            {
                AddLocationIfMappable(
                    element,
                    method,
                    maxResults,
                    includePathPrefixes,
                    items,
                    ref totalUnfilteredKnown,
                    ref totalKnown,
                    ref returned);
            }

            return new LocationMapResult(items, totalKnown, totalUnfilteredKnown, returned, maxResults.HasValue && totalKnown > returned);
        }

        if (response.ValueKind == JsonValueKind.Object)
        {
            AddLocationIfMappable(
                response,
                method,
                maxResults,
                includePathPrefixes,
                items,
                ref totalUnfilteredKnown,
                ref totalKnown,
                ref returned);
            return new LocationMapResult(items, totalKnown, totalUnfilteredKnown, returned, maxResults.HasValue && totalKnown > returned);
        }

        throw new UserFacingException("invalid_lsp_response", $"{method} returned an unexpected response shape.");
    }

    private void AddLocationIfMappable(
        JsonElement element,
        string method,
        int? maxResults,
        IReadOnlyList<string>? includePathPrefixes,
        List<NavigationLocation> items,
        ref int totalUnfilteredKnown,
        ref int totalKnown,
        ref int returned)
    {
        var location = TryMapLocation(element, method);
        if (location is null)
        {
            return;
        }

        totalUnfilteredKnown++;
        if (!IsIncludedByPathPrefixes(location.File, includePathPrefixes))
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

}
