using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynMcpServer.Infrastructure;

namespace RoslynMcpServer.Mcp;

[McpServerResourceType]
public static class ServerResources
{
    [McpServerResource(
        UriTemplate = ServerResourceUris.Guide,
        Name = "roslyn_server_guide",
        Title = "Roslyn MCP Server Guide",
        MimeType = "text/markdown")]
    [Description("Short guidance for using this Roslyn MCP server.")]
    public static string Guide() => """
        Roslyn MCP Server is a best-effort, read-only C# context provider.

        Start with list_workspaces and get_workspace_status, then load a solution or project when needed. Prefer Roslyn semantic tools for navigation. Use find_symbols when you know a symbol name but not its file; pass matchMode: "exact", "prefix", or "contains" when Roslyn LS default matching is too broad, and includePathPrefixes when a large repository search should stay inside known root-relative directories. Use get_call_hierarchy on callable positions when you need direct depth-1 incoming callers, outgoing callees, or both. For method-only call graph reviews, pass kindFilter: ["method"]; include "constructor" or "property" only when those edges are relevant.

        Treat warming, unknown completeness, and truncated results as partial context.
        """;

    [McpServerResource(
        UriTemplate = ServerResourceUris.Capabilities,
        Name = "roslyn_server_capabilities",
        Title = "Roslyn MCP Server Capabilities",
        MimeType = "text/markdown")]
    [Description("Short capability summary for this Roslyn MCP server.")]
    public static string Capabilities() => """
        This server exposes read-only Roslyn context: workspace discovery, symbols, hover, definitions, references, implementations, call hierarchy, snippets, and diagnostics.

        It does not edit, refactor, format, rename, or apply code actions.
        """;
}
