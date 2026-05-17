using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynMcpServer.Infrastructure;
using RoslynMcpServer.Workspace;

namespace RoslynMcpServer.Mcp;

[McpServerToolType]
public sealed class WorkspaceTools(WorkspaceSession session)
{
    [McpServerTool(Name = "list_workspaces")]
    [Description("Find workspace candidates. Use when no workspace is loaded or you need exact paths for load_solution/load_project.")]
    public object ListWorkspaces(
        [Description("Re-scan instead of using cached candidates.")]
        bool refresh = false)
    {
        try
        {
            return session.ListWorkspaces(refresh);
        }
        catch (UserFacingException ex)
        {
            return ToolError.FromException(ex);
        }
    }

    [McpServerTool(Name = "load_solution")]
    [Description("Select a .sln/.slnx and start Roslyn LS. Returns after LSP initialization, not full analysis.")]
    public async Task<object> LoadSolution(
        [Description("Exact root-relative path from list_workspaces, or an absolute path inside the configured root.")]
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
    [Description("Select a .csproj and start Roslyn LS. Prefer load_solution when a suitable solution exists.")]
    public async Task<object> LoadProject(
        [Description("Exact root-relative .csproj path from list_workspaces, or an absolute path inside the configured root.")]
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
    [Description("Check load/warming/error state, selected workspace, warnings, and diagnostics queue status.")]
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
