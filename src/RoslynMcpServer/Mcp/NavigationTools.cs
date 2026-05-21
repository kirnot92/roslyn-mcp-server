using ModelContextProtocol.Server;
using RoslynMcpServer.Workspace;

namespace RoslynMcpServer.Mcp;

[McpServerToolType]
public sealed partial class NavigationTools
{
    private readonly WorkspaceSession session;
    private readonly OpenDocumentManager documents;
    private readonly WorkspaceRoot workspaceRoot;

    public NavigationTools(
        WorkspaceSession session,
        OpenDocumentManager documents,
        WorkspaceRoot workspaceRoot)
    {
        this.session = session;
        this.documents = documents;
        this.workspaceRoot = workspaceRoot;
    }
}
