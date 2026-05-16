using RoslynMcpServer.Workspace;

namespace RoslynMcpServer.Lsp;

public interface IRoslynWorkspaceLoader
{
    Task<RoslynWorkspaceHandle> LoadAsync(WorkspaceTarget target, CancellationToken cancellationToken);
}

public sealed class RoslynWorkspaceHandle(
    WorkspaceTarget target,
    RoslynLanguageServerConnection connection) : IAsyncDisposable
{
    public WorkspaceTarget Target { get; } = target;
    public RoslynLanguageServerConnection Connection { get; } = connection;
    public LspClient Client => Connection.Client;
    public bool IsRunning => Connection.IsRunning;
    public int PendingRequestCount => Client.PendingRequestCount;

    public ValueTask DisposeAsync() => Connection.DisposeAsync();
}
