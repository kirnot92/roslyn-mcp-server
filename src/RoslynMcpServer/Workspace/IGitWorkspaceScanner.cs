namespace RoslynMcpServer.Workspace;

public interface IGitWorkspaceScanner
{
    WorkspaceScanResult? TryScan(CancellationToken cancellationToken = default);

    WorkspaceScanResult? TryScan(TimeSpan budget, CancellationToken cancellationToken = default);
}
