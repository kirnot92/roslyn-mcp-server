namespace RoslynMcpServer.Workspace;

public interface IGitWorkspaceScanner
{
    WorkspaceScanResult? TryScan(CancellationToken cancellationToken = default);
}
