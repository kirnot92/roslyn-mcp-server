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

    public async Task<ReadToolContext> PrepareReadToolAsync(CancellationToken cancellationToken = default)
    {
        var currentState = _state;
        if (currentState is WorkspaceLoadState.StartingLanguageServer)
        {
            throw WorkspaceLoading();
        }

        if (currentState is WorkspaceLoadState.Failed)
        {
            throw new UserFacingException(
                "workspace_failed",
                _failureMessage ?? "Workspace failed to load. Call load_solution or load_project to retry.");
        }

        if (_handle is not null &&
            currentState is WorkspaceLoadState.LspReady or WorkspaceLoadState.WorkspaceWarming or WorkspaceLoadState.Ready)
        {
            return new ReadToolContext(_handle, currentState);
        }

        if (!await _stateLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw WorkspaceLoading();
        }

        try
        {
            currentState = _state;
            if (currentState is WorkspaceLoadState.StartingLanguageServer)
            {
                throw WorkspaceLoading();
            }

            if (_handle is not null &&
                currentState is WorkspaceLoadState.LspReady or WorkspaceLoadState.WorkspaceWarming or WorkspaceLoadState.Ready)
            {
                return new ReadToolContext(_handle, currentState);
            }

            if (currentState is WorkspaceLoadState.Failed)
            {
                throw new UserFacingException(
                    "workspace_failed",
                    _failureMessage ?? "Workspace failed to load. Call load_solution or load_project to retry.");
            }

            var target = SelectAutoLoadTarget(ListWorkspaces(refresh: false, cancellationToken));
            await LoadTargetCoreAsync(target, cancellationToken).ConfigureAwait(false);
            return new ReadToolContext(_handle!, _state);
        }
        finally
        {
            _stateLock.Release();
        }
    }

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
            var target = CreateTarget(path, allowedExtensions, requestedKind);
            await LoadTargetCoreAsync(target, cancellationToken).ConfigureAwait(false);

            return ToStatus(ListWorkspaces(refresh: false, cancellationToken));
        }
        finally
        {
            _stateLock.Release();
        }
    }

    private async Task LoadTargetCoreAsync(WorkspaceTarget target, CancellationToken cancellationToken)
    {
        var oldHandle = BeginRestart();
        await StopHandleAsync(oldHandle).ConfigureAwait(false);

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
    }

    private RoslynWorkspaceHandle? BeginRestart()
    {
        var oldHandle = _handle;
        if (oldHandle is not null)
        {
            oldHandle.Client.NotificationReceived -= OnNotificationReceived;
            _handle = null;
        }

        _state = WorkspaceLoadState.StartingLanguageServer;
        _failureCode = null;
        _failureMessage = null;

        return oldHandle;
    }

    private WorkspaceTarget CreateTarget(string path, string[] allowedExtensions, WorkspaceKind requestedKind)
    {
        var fullPath = pathGuard.RequireFileInsideRoot(path, allowedExtensions);
        var extension = Path.GetExtension(fullPath);
        var kind = string.Equals(extension, ".slnx", StringComparison.OrdinalIgnoreCase)
            ? WorkspaceKind.SolutionX
            : requestedKind;
        return new WorkspaceTarget(
            kind,
            fullPath,
            pathGuard.ToRelativePath(fullPath),
            pathGuard.Root,
            Path.GetDirectoryName(fullPath) ?? pathGuard.Root);
    }

    private WorkspaceTarget SelectAutoLoadTarget(WorkspaceScanResult scan)
    {
        if (scan.Solutions.Count == 1)
        {
            var solution = scan.Solutions[0];
            return new WorkspaceTarget(
                solution.Kind,
                solution.FullPath,
                solution.RelativePath,
                pathGuard.Root,
                Path.GetDirectoryName(solution.FullPath) ?? pathGuard.Root);
        }

        if (scan.Solutions.Count > 1)
        {
            throw new UserFacingException(
                "workspace_not_loaded",
                "Multiple solutions were found. Call load_solution with one of:" +
                Environment.NewLine +
                string.Join(Environment.NewLine, scan.Solutions.Select(candidate => $"- {candidate.RelativePath}")));
        }

        if (scan.Projects.Count == 1)
        {
            var project = scan.Projects[0];
            return new WorkspaceTarget(
                project.Kind,
                project.FullPath,
                project.RelativePath,
                pathGuard.Root,
                Path.GetDirectoryName(project.FullPath) ?? pathGuard.Root);
        }

        if (scan.Projects.Count > 1)
        {
            throw new UserFacingException(
                "workspace_not_loaded",
                "Multiple projects were found and no solution is loaded. Call load_project with an explicit .csproj path.");
        }

        throw new UserFacingException(
            "workspace_not_loaded",
            "No workspace is loaded and no .sln, .slnx, or .csproj candidate was found.");
    }

    private async Task StopHandleAsync(RoslynWorkspaceHandle? handle)
    {
        if (handle is null)
        {
            return;
        }

        await handle.Client.ShutdownAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
        await handle.DisposeAsync().ConfigureAwait(false);
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

    private static UserFacingException WorkspaceLoading() =>
        new(
            "workspace_loading",
            "Workspace is starting. Call get_workspace_status and retry when state is LspReady, WorkspaceWarming, or Ready.");
}

public sealed record ReadToolContext(RoslynWorkspaceHandle Handle, WorkspaceLoadState State);
