using RoslynMcpServer.Infrastructure;
using RoslynMcpServer.Lsp;

namespace RoslynMcpServer.Workspace;

public sealed class WorkspaceSession(
    WorkspaceScanner scanner,
    PathGuard pathGuard,
    IRoslynWorkspaceLoader loader) : IAsyncDisposable
{
    private readonly SemaphoreSlim _stateLock = new(1, 1);
    private WorkspaceScanResult? _scanCache;
    private RoslynWorkspaceHandle? _handle;
    private WorkspaceLoadState _state = WorkspaceLoadState.NotLoaded;
    private string? _failureCode;
    private string? _failureMessage;

    public WorkspaceLoadState State => _state;

    public WorkspaceScanResult ListWorkspaces(bool refresh = false, CancellationToken cancellationToken = default)
    {
        if (!refresh && _scanCache is not null)
        {
            return _scanCache;
        }

        _scanCache = scanner.Scan(cancellationToken);
        return _scanCache;
    }

    public Task<WorkspaceStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var scan = ListWorkspaces(refresh: false, cancellationToken);
        return Task.FromResult(ToStatus(scan));
    }

    public Task<WorkspaceStatus> LoadSolutionAsync(string path, CancellationToken cancellationToken = default) =>
        LoadAsync(path, [".sln", ".slnx"], WorkspaceKind.Solution, cancellationToken);

    public Task<WorkspaceStatus> LoadProjectAsync(string path, CancellationToken cancellationToken = default) =>
        LoadAsync(path, [".csproj"], WorkspaceKind.Project, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_handle is not null)
        {
            await _handle.Client.ShutdownAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
            await _handle.DisposeAsync().ConfigureAwait(false);
        }

        _stateLock.Dispose();
    }

    private async Task<WorkspaceStatus> LoadAsync(
        string path,
        string[] allowedExtensions,
        WorkspaceKind requestedKind,
        CancellationToken cancellationToken)
    {
        await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var fullPath = pathGuard.RequireFileInsideRoot(path, allowedExtensions);
            var extension = Path.GetExtension(fullPath);
            var kind = string.Equals(extension, ".slnx", StringComparison.OrdinalIgnoreCase)
                ? WorkspaceKind.SolutionX
                : requestedKind;
            var target = new WorkspaceTarget(
                kind,
                fullPath,
                pathGuard.ToRelativePath(fullPath),
                pathGuard.Root,
                Path.GetDirectoryName(fullPath) ?? pathGuard.Root);

            await StopCurrentAsync().ConfigureAwait(false);
            _state = WorkspaceLoadState.StartingLanguageServer;
            _failureCode = null;
            _failureMessage = null;

            try
            {
                var handle = await loader.LoadAsync(target, cancellationToken).ConfigureAwait(false);
                _handle = handle;
                _handle.Client.NotificationReceived += OnNotificationReceived;
                _state = WorkspaceLoadState.WorkspaceWarming;
            }
            catch (UserFacingException ex)
            {
                _state = WorkspaceLoadState.Failed;
                _failureCode = ex.Code;
                _failureMessage = ex.Message;
                throw;
            }
            catch (Exception ex)
            {
                _state = WorkspaceLoadState.Failed;
                _failureCode = "workspace_failed";
                _failureMessage = ex.Message;
                throw;
            }

            return ToStatus(ListWorkspaces(refresh: false, cancellationToken));
        }
        finally
        {
            _stateLock.Release();
        }
    }

    private async Task StopCurrentAsync()
    {
        if (_handle is null)
        {
            return;
        }

        _handle.Client.NotificationReceived -= OnNotificationReceived;
        await _handle.Client.ShutdownAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
        await _handle.DisposeAsync().ConfigureAwait(false);
        _handle = null;
    }

    private void OnNotificationReceived(string method, System.Text.Json.JsonElement? parameters)
    {
        if (string.Equals(method, "workspace/projectInitializationComplete", StringComparison.Ordinal))
        {
            _state = WorkspaceLoadState.Ready;
        }
    }

    private WorkspaceStatus ToStatus(WorkspaceScanResult scan) =>
        new(
            pathGuard.Root,
            _state,
            _handle?.Target,
            _handle?.IsRunning ?? false,
            _handle?.PendingRequestCount ?? 0,
            scan,
            _failureCode,
            _failureMessage);
}
