using RoslynMcpServer.Infrastructure;
using RoslynMcpServer.Lsp;

namespace RoslynMcpServer.Mcp;

public sealed partial class NavigationTools
{
    private static string? NormalizeDocumentSymbolQuery(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        return query.Trim();
    }

    private static int NormalizeDocumentSymbolMaxResults(int? maxResults)
    {
        if (maxResults is null)
        {
            return DefaultDocumentSymbolMaxResults;
        }

        if (maxResults.Value < 1)
        {
            throw new UserFacingException("invalid_max_results", "maxResults must be a positive integer.");
        }

        return Math.Min(maxResults.Value, MaxDocumentSymbolMaxResults);
    }

    private static int NormalizeReferenceMaxResults(int? maxResults)
    {
        if (maxResults is null)
        {
            return DefaultReferencesMaxResults;
        }

        if (maxResults.Value < 1)
        {
            throw new UserFacingException("invalid_max_results", "maxResults must be a positive integer.");
        }

        return Math.Min(maxResults.Value, MaxReferencesMaxResults);
    }

    private static TimeSpan NormalizeConfigurableTimeout(int? timeoutSec)
    {
        if (timeoutSec is null)
        {
            return TimeSpan.FromSeconds(DefaultConfigurableTimeoutSeconds);
        }

        if (timeoutSec.Value < 1)
        {
            throw new UserFacingException("invalid_timeout", "timeoutSec must be a positive integer number of seconds.");
        }

        return TimeSpan.FromSeconds(Math.Min(timeoutSec.Value, MaxConfigurableTimeoutSeconds));
    }

    private static int NormalizeImplementationMaxResults(int? maxResults)
    {
        if (maxResults is null)
        {
            return DefaultImplementationsMaxResults;
        }

        if (maxResults.Value < 1)
        {
            throw new UserFacingException("invalid_max_results", "maxResults must be a positive integer.");
        }

        return Math.Min(maxResults.Value, MaxImplementationsMaxResults);
    }

    private static int NormalizeCallHierarchyMaxResults(int? maxResults)
    {
        if (maxResults is null)
        {
            return DefaultCallHierarchyMaxResults;
        }

        if (maxResults.Value < 1)
        {
            throw new UserFacingException("invalid_max_results", "maxResults must be a positive integer.");
        }

        return Math.Min(maxResults.Value, MaxCallHierarchyMaxResults);
    }

    private static int NormalizeTypeHierarchyMaxDepth(int? maxDepth)
    {
        if (maxDepth is null)
        {
            return DefaultTypeHierarchyMaxDepth;
        }

        if (maxDepth.Value < 1)
        {
            throw new UserFacingException("invalid_max_depth", "maxDepth must be a positive integer.");
        }

        return Math.Min(maxDepth.Value, MaxTypeHierarchyMaxDepth);
    }

    private static int NormalizeTypeHierarchyMaxResults(int? maxResults)
    {
        if (maxResults is null)
        {
            return DefaultTypeHierarchyMaxResults;
        }

        if (maxResults.Value < 1)
        {
            throw new UserFacingException("invalid_max_results", "maxResults must be a positive integer.");
        }

        return Math.Min(maxResults.Value, MaxTypeHierarchyMaxResults);
    }

    private static CallHierarchyDirection ParseCallHierarchyDirection(string direction)
    {
        var normalized = direction?.Trim();
        if (string.Equals(normalized, "incoming", StringComparison.OrdinalIgnoreCase))
        {
            return CallHierarchyDirection.Incoming;
        }

        if (string.Equals(normalized, "outgoing", StringComparison.OrdinalIgnoreCase))
        {
            return CallHierarchyDirection.Outgoing;
        }

        if (string.Equals(normalized, "both", StringComparison.OrdinalIgnoreCase))
        {
            return CallHierarchyDirection.Both;
        }

        throw new UserFacingException(
            "invalid_direction",
            "direction must be one of: incoming, outgoing, both.");
    }

    private static TypeHierarchyDirection ParseTypeHierarchyDirection(string direction)
    {
        var normalized = direction?.Trim();
        if (string.Equals(normalized, "supertypes", StringComparison.OrdinalIgnoreCase))
        {
            return TypeHierarchyDirection.Supertypes;
        }

        if (string.Equals(normalized, "subtypes", StringComparison.OrdinalIgnoreCase))
        {
            return TypeHierarchyDirection.Subtypes;
        }

        if (string.Equals(normalized, "both", StringComparison.OrdinalIgnoreCase))
        {
            return TypeHierarchyDirection.Both;
        }

        throw new UserFacingException(
            "invalid_direction",
            "direction must be one of: supertypes, subtypes, both.");
    }

    private static IReadOnlyList<TypeHierarchyDirection> GetTypeHierarchyTraversalDirections(TypeHierarchyDirection direction) =>
        direction switch
        {
            TypeHierarchyDirection.Supertypes => [TypeHierarchyDirection.Supertypes],
            TypeHierarchyDirection.Subtypes => [TypeHierarchyDirection.Subtypes],
            TypeHierarchyDirection.Both => [TypeHierarchyDirection.Supertypes, TypeHierarchyDirection.Subtypes],
            _ => []
        };

    private static int NormalizePeekContextLines(int? contextLines)
    {
        if (contextLines is null)
        {
            return DefaultPeekContextLines;
        }

        if (contextLines.Value < 0)
        {
            throw new UserFacingException("invalid_context_lines", "contextLines must be a non-negative integer.");
        }

        return Math.Min(contextLines.Value, MaxPeekContextLines);
    }

    private static int NormalizePeekMaxDefinitions(int? maxDefinitions)
    {
        if (maxDefinitions is null)
        {
            return DefaultPeekMaxDefinitions;
        }

        if (maxDefinitions.Value < 1)
        {
            throw new UserFacingException("invalid_max_results", "maxDefinitions must be a positive integer.");
        }

        return Math.Min(maxDefinitions.Value, MaxPeekMaxDefinitions);
    }

    private static string ValidateSymbolQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new UserFacingException(
                "invalid_query",
                "query must contain at least one non-whitespace character.");
        }

        var normalizedQuery = query.Trim();
        if (normalizedQuery.Length < MinSymbolQueryLength)
        {
            throw new UserFacingException(
                "invalid_query",
                $"query must contain at least {MinSymbolQueryLength} non-whitespace characters.");
        }

        return normalizedQuery;
    }

    private static int NormalizeSymbolMaxResults(int? maxResults)
    {
        if (maxResults is null)
        {
            return DefaultSymbolMaxResults;
        }

        if (maxResults.Value < 1)
        {
            throw new UserFacingException("invalid_max_results", "maxResults must be a positive integer.");
        }

        return Math.Min(maxResults.Value, MaxSymbolMaxResults);
    }

    private static IReadOnlySet<SymbolKind>? ParseSymbolKindFilter(IReadOnlyList<string>? kindFilter) =>
        ParseKindFilter(kindFilter, SymbolKindFilterValues, AllowedSymbolKindFilterValues);

    private static SymbolMatchMode ParseSymbolMatchMode(string? matchMode)
    {
        if (matchMode is null)
        {
            return SymbolMatchMode.Default;
        }

        var normalized = matchMode.Trim();
        if (string.Equals(normalized, "default", StringComparison.OrdinalIgnoreCase))
        {
            return SymbolMatchMode.Default;
        }

        if (string.Equals(normalized, "exact", StringComparison.OrdinalIgnoreCase))
        {
            return SymbolMatchMode.Exact;
        }

        if (string.Equals(normalized, "prefix", StringComparison.OrdinalIgnoreCase))
        {
            return SymbolMatchMode.Prefix;
        }

        if (string.Equals(normalized, "contains", StringComparison.OrdinalIgnoreCase))
        {
            return SymbolMatchMode.Contains;
        }

        throw new UserFacingException(
            "invalid_match_mode",
            "matchMode must be one of: default, exact, prefix, contains.");
    }

    private IReadOnlyList<string>? ParseIncludePathPrefixes(IReadOnlyList<string>? includePathPrefixes)
    {
        if (includePathPrefixes is null)
        {
            return null;
        }

        if (includePathPrefixes.Count == 0)
        {
            throw new UserFacingException(
                "invalid_path_prefix",
                "includePathPrefixes must contain at least one path prefix.");
        }

        var prefixes = new List<string>(includePathPrefixes.Count);
        foreach (var rawPrefix in includePathPrefixes)
        {
            if (string.IsNullOrWhiteSpace(rawPrefix))
            {
                throw new UserFacingException(
                    "invalid_path_prefix",
                    "includePathPrefixes must not contain empty path prefixes.");
            }

            prefixes.Add(pathMapper.NormalizePathPrefix(rawPrefix.Trim()));
        }

        return prefixes;
    }

    private static IReadOnlySet<SymbolKind>? ParseCallHierarchyKindFilter(IReadOnlyList<string>? kindFilter) =>
        ParseKindFilter(kindFilter, CallHierarchyKindFilterValues, AllowedCallHierarchyKindFilterValues);

    private static IReadOnlySet<SymbolKind>? ParseKindFilter(
        IReadOnlyList<string>? kindFilter,
        IReadOnlyDictionary<string, SymbolKind> allowedValues,
        string allowedValueNames)
    {
        if (kindFilter is null)
        {
            return null;
        }

        if (kindFilter.Count == 0)
        {
            throw new UserFacingException(
                "invalid_kind_filter",
                $"kindFilter must contain at least one symbol kind. Allowed values: {allowedValueNames}.");
        }

        var parsedKinds = new HashSet<SymbolKind>();
        var invalidKindNames = new List<string>();
        foreach (var rawKindName in kindFilter)
        {
            var kindName = rawKindName?.Trim();
            if (string.IsNullOrEmpty(kindName) ||
                !allowedValues.TryGetValue(kindName, out var kind))
            {
                invalidKindNames.Add(FormatInvalidKindName(rawKindName));
                continue;
            }

            parsedKinds.Add(kind);
        }

        if (invalidKindNames.Count > 0)
        {
            throw new UserFacingException(
                "invalid_kind_filter",
                $"Unknown symbol kind(s): {string.Join(", ", invalidKindNames)}. Allowed values: {allowedValueNames}.");
        }

        return parsedKinds;
    }

    private static string FormatInvalidKindName(string? kindName) =>
        string.IsNullOrWhiteSpace(kindName) ? "<empty>" : kindName.Trim();
}
