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

        Start with list_workspaces and get_workspace_status, then load a solution or project when needed. Prefer Roslyn semantic tools for navigation, but treat warming, unknown completeness, and truncated results as partial context.
        """;

    [McpServerResource(
        UriTemplate = ServerResourceUris.Capabilities,
        Name = "roslyn_server_capabilities",
        Title = "Roslyn MCP Server Capabilities",
        MimeType = "text/markdown")]
    [Description("Short capability summary for this Roslyn MCP server.")]
    public static string Capabilities() => """
        This server exposes read-only Roslyn context: workspace discovery, symbols, hover, definitions, references, implementations, snippets, and diagnostics.

        It does not edit, refactor, format, rename, or apply code actions.
        """;
}
