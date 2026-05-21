using RoslynMcpServer.Lsp;

namespace RoslynMcpServer.Mcp;

internal static class NavigationToolLimits
{
    internal const string FileParameterDescription = "Root-relative C# file path under the configured root; absolute paths inside root are also accepted.";
    internal const string LineParameterDescription = "1-based source line.";
    internal const string ColumnParameterDescription = "1-based source column.";
    internal const string SymbolKindFilterParameterDescription = "Optional MCP symbol kind names to keep, such as class, interface, method, property, field, enum, enumMember, constructor, event, operator, struct, or typeParameter.";
    internal const string CallHierarchyKindFilterParameterDescription = "Optional edge counterpart MCP symbol kind names to keep, such as method, constructor, property, event, operator, or field.";
    internal const int DefaultDocumentSymbolMaxResults = 1000;
    internal const int MaxDocumentSymbolMaxResults = 1000;
    internal const int MaxHoverCharacters = 20_000;
    internal const int DefaultPeekContextLines = 3;
    internal const int MaxPeekContextLines = 20;
    internal const int DefaultPeekMaxDefinitions = 20;
    internal const int MaxPeekMaxDefinitions = 100;
    internal const int MaxPeekSnippetCharacters = 20_000;
    internal const int DefaultReferencesMaxResults = 200;
    internal const int MaxReferencesMaxResults = 1000;
    internal const int DefaultImplementationsMaxResults = DefaultReferencesMaxResults;
    internal const int MaxImplementationsMaxResults = MaxReferencesMaxResults;
    internal const int DefaultCallHierarchyMaxResults = DefaultReferencesMaxResults;
    internal const int MaxCallHierarchyMaxResults = MaxReferencesMaxResults;
    internal const int MaxCallHierarchyCallSites = 20;
    internal const int DefaultTypeHierarchyMaxDepth = 2;
    internal const int MaxTypeHierarchyMaxDepth = 5;
    internal const int DefaultTypeHierarchyMaxResults = DefaultReferencesMaxResults;
    internal const int MaxTypeHierarchyMaxResults = MaxReferencesMaxResults;
    internal const int DefaultSymbolMaxResults = 300;
    internal const int MaxSymbolMaxResults = 1000;
    internal const int MinSymbolQueryLength = 2;
    internal const int DefaultConfigurableTimeoutSeconds = 10;
    internal const int MaxConfigurableTimeoutSeconds = 120;
    internal static readonly TimeSpan NavigationTimeout = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan DefinitionTimeout = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan ReferencesTimeout = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan ImplementationsTimeout = ReferencesTimeout;
    internal static readonly TimeSpan CallHierarchyTimeout = ReferencesTimeout;
    internal static readonly TimeSpan TypeHierarchyTimeout = ReferencesTimeout;
    internal static readonly TimeSpan WorkspaceSymbolTimeout = TimeSpan.FromSeconds(30);
    internal static readonly SymbolKind[] SupportedSymbolKinds = Enum.GetValues<SymbolKind>()
        .Where(kind => kind.ToMcpName() != "unknown")
        .ToArray();
    internal static readonly IReadOnlyDictionary<string, SymbolKind> SymbolKindFilterValues = SupportedSymbolKinds
        .ToDictionary(kind => kind.ToMcpName(), kind => kind, StringComparer.OrdinalIgnoreCase);
    internal static readonly string AllowedSymbolKindFilterValues = string.Join(
        ", ",
        SupportedSymbolKinds.Select(kind => kind.ToMcpName()));
    internal static readonly SymbolKind[] SupportedCallHierarchySymbolKinds =
    [
        SymbolKind.Method,
        SymbolKind.Constructor,
        SymbolKind.Property,
        SymbolKind.Event,
        SymbolKind.Operator,
        SymbolKind.Field
    ];
    internal static readonly IReadOnlyDictionary<string, SymbolKind> CallHierarchyKindFilterValues = SupportedCallHierarchySymbolKinds
        .ToDictionary(kind => kind.ToMcpName(), kind => kind, StringComparer.OrdinalIgnoreCase);
    internal static readonly string AllowedCallHierarchyKindFilterValues = string.Join(
        ", ",
        SupportedCallHierarchySymbolKinds.Select(kind => kind.ToMcpName()));
}
