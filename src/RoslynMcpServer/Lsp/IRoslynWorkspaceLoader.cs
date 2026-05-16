using RoslynMcpServer.Workspace;

namespace RoslynMcpServer.Lsp;

public interface IRoslynWorkspaceLoader
{
    Task<RoslynWorkspaceHandle> LoadAsync(WorkspaceTarget target, CancellationToken cancellationToken);
}

public sealed class RoslynWorkspaceHandle : IAsyncDisposable
{
    private readonly IAsyncDisposable? disposable;
    private readonly Func<bool> isRunning;

    public RoslynWorkspaceHandle(WorkspaceTarget target, RoslynLanguageServerConnection connection)
        : this(target, connection.Client, connection, () => connection.IsRunning)
    {
    }

    public RoslynWorkspaceHandle(WorkspaceTarget target, ILspClient client)
        : this(target, client, disposable: null, () => true)
    {
    }

    private RoslynWorkspaceHandle(
        WorkspaceTarget target,
        ILspClient client,
        IAsyncDisposable? disposable,
        Func<bool> isRunning)
    {
        Target = target;
        Client = client;
        this.disposable = disposable;
        this.isRunning = isRunning;
    }

    public WorkspaceTarget Target { get; }
    public ILspClient Client { get; }
    public bool IsRunning => this.isRunning();
    public int PendingRequestCount => Client.PendingRequestCount;

    public ValueTask DisposeAsync() => this.disposable?.DisposeAsync() ?? ValueTask.CompletedTask;
}
