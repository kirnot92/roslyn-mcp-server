using System.Text.Json;
using RoslynMcpServer.Infrastructure;
using RoslynMcpServer.Lsp;
using RoslynMcpServer.Workspace;

namespace RoslynMcpServer.Mcp;

internal static class NavigationPositionRequests
{
    internal static async Task<PositionRequestContext> PrepareAsync(
        WorkspaceSession session,
        DocumentStateManager documents,
        string file,
        int line,
        int column,
        CancellationToken cancellationToken)
    {
        var position = PositionMapper.ToLspPosition(line, column);
        var context = await session.PrepareReadToolAsync(cancellationToken);
        var document = await documents.EnsureOpenAsync(file, context.Handle.Client, cancellationToken);
        documents.ValidatePosition(document, file, line, column);

        return new PositionRequestContext(context, document, position);
    }
}

internal static class NavigationLocationMapper
{
    internal static LocationMapResult Map(
        WorkspaceRoot workspaceRoot,
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
                    workspaceRoot,
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
                workspaceRoot,
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

    private static void AddLocationIfMappable(
        WorkspaceRoot workspaceRoot,
        JsonElement element,
        string method,
        int? maxResults,
        IReadOnlyList<string>? includePathPrefixes,
        List<NavigationLocation> items,
        ref int totalUnfilteredKnown,
        ref int totalKnown,
        ref int returned)
    {
        var location = TryMapLocation(workspaceRoot, element, method);
        if (location is null)
        {
            return;
        }

        totalUnfilteredKnown++;
        if (!NavigationPathFilters.IsIncludedByPathPrefixes(location.File, includePathPrefixes))
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

    private static NavigationLocation? TryMapLocation(WorkspaceRoot workspaceRoot, JsonElement element, string method)
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
            relativePath = workspaceRoot.UriToRelativePath(uri);
        }
        catch (UserFacingException ex) when (ex.Code is "path_outside_root" or "invalid_lsp_uri")
        {
            return null;
        }

        var mcpRange = PositionMapper.ToMcpRange(range);
        return new NavigationLocation(relativePath, mcpRange.StartLine, mcpRange.StartColumn, mcpRange);
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

internal static class NavigationPathFilters
{
    internal static bool IsIncludedByPathPrefixes(string? file, IReadOnlyList<string>? includePathPrefixes)
    {
        if (includePathPrefixes is null)
        {
            return true;
        }

        if (file is null)
        {
            return false;
        }

        return includePathPrefixes.Any(prefix => MatchesPathPrefix(file, prefix));
    }

    private static bool MatchesPathPrefix(string file, string prefix)
    {
        if (prefix == ".")
        {
            return true;
        }

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return string.Equals(file, prefix, comparison) ||
            file.StartsWith(prefix + "/", comparison);
    }
}
