using RoslynMcpServer.Lsp;

namespace RoslynMcpServer.Mcp;

public sealed partial class NavigationTools
{
    private const string FileParameterDescription = "Root-relative C# file path under the configured root; absolute paths inside root are also accepted.";
    private const string LineParameterDescription = "1-based source line.";
    private const string ColumnParameterDescription = "1-based source column.";
    private const int MaxDocumentSymbolNodes = 1000;
    private const int MaxHoverCharacters = 20_000;
    private const int DefaultPeekContextLines = 3;
    private const int MaxPeekContextLines = 20;
    private const int DefaultPeekMaxDefinitions = 20;
    private const int MaxPeekMaxDefinitions = 100;
    private const int MaxPeekSnippetCharacters = 20_000;
    private const int DefaultReferencesMaxResults = 200;
    private const int MaxReferencesMaxResults = 1000;
    private const int DefaultImplementationsMaxResults = DefaultReferencesMaxResults;
    private const int MaxImplementationsMaxResults = MaxReferencesMaxResults;
    private const int DefaultCallHierarchyMaxResults = DefaultReferencesMaxResults;
    private const int MaxCallHierarchyMaxResults = MaxReferencesMaxResults;
    private const int MaxCallHierarchyCallSites = 20;
    private const int DefaultTypeHierarchyMaxDepth = 2;
    private const int MaxTypeHierarchyMaxDepth = 5;
    private const int DefaultTypeHierarchyMaxResults = DefaultReferencesMaxResults;
    private const int MaxTypeHierarchyMaxResults = MaxReferencesMaxResults;
    private const int DefaultSymbolMaxResults = 300;
    private const int MaxSymbolMaxResults = 1000;
    private const int MinSymbolQueryLength = 2;
    private static readonly TimeSpan NavigationTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DefinitionTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReferencesTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ImplementationsTimeout = ReferencesTimeout;
    private static readonly TimeSpan CallHierarchyTimeout = ReferencesTimeout;
    private static readonly TimeSpan TypeHierarchyTimeout = ReferencesTimeout;
    private static readonly TimeSpan WorkspaceSymbolTimeout = TimeSpan.FromSeconds(30);
    private static readonly SymbolKind[] SupportedSymbolKinds = Enum.GetValues<SymbolKind>()
        .Where(kind => kind.ToMcpName() != "unknown")
        .ToArray();
    private static readonly IReadOnlyDictionary<string, SymbolKind> SymbolKindFilterValues = SupportedSymbolKinds
        .ToDictionary(kind => kind.ToMcpName(), kind => kind, StringComparer.OrdinalIgnoreCase);
    private static readonly string AllowedSymbolKindFilterValues = string.Join(
        ", ",
        SupportedSymbolKinds.Select(kind => kind.ToMcpName()));
    private static readonly SymbolKind[] SupportedCallHierarchySymbolKinds =
    [
        SymbolKind.Method,
        SymbolKind.Constructor,
        SymbolKind.Property,
        SymbolKind.Event,
        SymbolKind.Operator,
        SymbolKind.Field
    ];
    private static readonly IReadOnlyDictionary<string, SymbolKind> CallHierarchyKindFilterValues = SupportedCallHierarchySymbolKinds
        .ToDictionary(kind => kind.ToMcpName(), kind => kind, StringComparer.OrdinalIgnoreCase);
    private static readonly string AllowedCallHierarchyKindFilterValues = string.Join(
        ", ",
        SupportedCallHierarchySymbolKinds.Select(kind => kind.ToMcpName()));
}
