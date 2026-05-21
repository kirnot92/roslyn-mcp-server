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
    public object ListWorkspaces()
    {
        try
        {
            return session.ListWorkspaces();
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
            return await session.LoadSolutionAsync(path, cancellationToken).ConfigureAwait(false);
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
            return await session.LoadProjectAsync(path, cancellationToken).ConfigureAwait(false);
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
            return await session.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (UserFacingException ex)
        {
            return ToolError.FromException(ex);
        }
    }
}
