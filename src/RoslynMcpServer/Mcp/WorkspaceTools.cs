using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynMcpServer.Infrastructure;
using RoslynMcpServer.Workspace;

namespace RoslynMcpServer.Mcp;

[McpServerToolType]
public sealed class WorkspaceTools(WorkspaceSession session)
{
    [McpServerTool(Name = "list_workspaces")]
    [Description("Use when you need to discover .sln, .slnx, or .csproj candidates under the configured root, especially before load_solution/load_project or when no workspace is loaded.")]
    public object ListWorkspaces(
        [Description("Optional non-negative filesystem BFS depth. When set, skips git-based discovery and scans only to this depth; useful for large repositories when looking for near-root workspace files.")]
        int? maxDepth = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return session.ListWorkspaces(maxDepth, cancellationToken);
        }
        catch (UserFacingException ex)
        {
            return ToolError.FromException(ex);
        }
    }

    [McpServerTool(Name = "load_solution")]
    [Description("Use when you need to select a specific .sln or .slnx as the Roslyn workspace. Returns after LSP initialization, not full analysis.")]
    public async Task<object> LoadSolution(
        [Description("Exact .sln/.slnx path from list_workspaces, root-relative or absolute inside the configured root.")]
        string path,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await session.LoadSolutionAsync(path, cancellationToken);
        }
        catch (UserFacingException ex)
        {
            return ToolError.FromException(ex);
        }
    }

    [McpServerTool(Name = "load_project")]
    [Description("Use when you need to select a specific .csproj as the Roslyn workspace and no suitable solution is available. Prefer load_solution when a matching solution exists.")]
    public async Task<object> LoadProject(
        [Description("Exact .csproj path from list_workspaces, root-relative or absolute inside the configured root.")]
        string path,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await session.LoadProjectAsync(path, cancellationToken);
        }
        catch (UserFacingException ex)
        {
            return ToolError.FromException(ex);
        }
    }

    [McpServerTool(Name = "get_workspace_status")]
    [Description("Use when you need to check whether a Roslyn workspace is loaded, still warming, failed, or loaded with warnings, including selected workspace and diagnostics queue status.")]
    public async Task<object> GetWorkspaceStatus(CancellationToken cancellationToken = default)
    {
        try
        {
            return await session.GetStatusAsync(cancellationToken);
        }
        catch (UserFacingException ex)
        {
            return ToolError.FromException(ex);
        }
    }
}
