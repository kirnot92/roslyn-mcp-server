using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynMcpServer.Infrastructure;
using RoslynMcpServer.Workspace;

namespace RoslynMcpServer.Mcp;

[McpServerToolType]
public sealed class WorkspaceTools(WorkspaceSession session)
{
    [McpServerTool(Name = "list_workspaces")]
    [Description("List .sln, .slnx, and .csproj workspace candidates under the configured root.")]
    public object ListWorkspaces(bool refresh = false)
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
    [Description("Load a .sln or .slnx workspace into roslyn-language-server.")]
    public async Task<object> LoadSolution(string path, CancellationToken cancellationToken = default)
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
    [Description("Load a .csproj workspace into roslyn-language-server.")]
    public async Task<object> LoadProject(string path, CancellationToken cancellationToken = default)
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
    [Description("Get current Roslyn workspace load state and discovered workspace candidates.")]
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

public sealed record ToolError(string Error, string Message)
{
    public static ToolError FromException(UserFacingException exception) => new(exception.Code, exception.Message);
}
