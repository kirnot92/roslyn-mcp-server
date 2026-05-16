using RoslynMcpServer.Infrastructure;
using RoslynMcpServer.Lsp;

namespace RoslynMcpServer.Workspace;

public sealed class WorkspaceSession(
    WorkspaceScanner scanner,
    PathGuard pathGuard,
    IRoslynWorkspaceLoader loader) : IAsyncDisposable
{
    private readonly SemaphoreSlim stateLock = new(1, 1);
    private WorkspaceScanResult? scanCache;
    private RoslynWorkspaceHandle? handle;
    private WorkspaceLoadState state = WorkspaceLoadState.NotLoaded;
    private string? failureCode;
    private string? failureMessage;

    public WorkspaceLoadState State => state;

    public WorkspaceScanResult ListWorkspaces(bool refresh = false, CancellationToken cancellationToken = default)
    {
        if (!refresh && scanCache is not null)
        {
            return scanCache;
        }

        scanCache = scanner.Scan(cancellationToken);
        return scanCache;
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
        var currentState = state;
        if (currentState is WorkspaceLoadState.StartingLanguageServer)
        {
            throw WorkspaceLoading();
        }

        if (currentState is WorkspaceLoadState.Failed)
        {
            throw new UserFacingException(
                "workspace_failed",
                failureMessage ?? "Workspace failed to load. Call load_solution or load_project to retry.");
        }

        if (handle is not null &&
            currentState is WorkspaceLoadState.LspReady or WorkspaceLoadState.WorkspaceWarming or WorkspaceLoadState.Ready)
        {
            return new ReadToolContext(handle, currentState);
        }

        if (!await stateLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw WorkspaceLoading();
        }

        try
        {
            currentState = state;
            if (currentState is WorkspaceLoadState.StartingLanguageServer)
            {
                throw WorkspaceLoading();
            }

            if (handle is not null &&
                currentState is WorkspaceLoadState.LspReady or WorkspaceLoadState.WorkspaceWarming or WorkspaceLoadState.Ready)
            {
                return new ReadToolContext(handle, currentState);
            }

            if (currentState is WorkspaceLoadState.Failed)
            {
                throw new UserFacingException(
                    "workspace_failed",
                    failureMessage ?? "Workspace failed to load. Call load_solution or load_project to retry.");
            }

            var target = SelectAutoLoadTarget(ListWorkspaces(refresh: false, cancellationToken));
            await LoadTargetCoreAsync(target, cancellationToken).ConfigureAwait(false);
            return new ReadToolContext(handle!, state);
        }
        finally
        {
            stateLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (handle is not null)
        {
            await handle.Client.ShutdownAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
            await handle.DisposeAsync().ConfigureAwait(false);
        }

        stateLock.Dispose();
    }

    private async Task<WorkspaceStatus> LoadAsync(
        string path,
        string[] allowedExtensions,
        WorkspaceKind requestedKind,
        CancellationToken cancellationToken)
    {
        await stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var target = CreateTarget(path, allowedExtensions, requestedKind);
            await LoadTargetCoreAsync(target, cancellationToken).ConfigureAwait(false);

            return ToStatus(ListWorkspaces(refresh: false, cancellationToken));
        }
        finally
        {
            stateLock.Release();
        }
    }

    private async Task LoadTargetCoreAsync(WorkspaceTarget target, CancellationToken cancellationToken)
    {
        var oldHandle = BeginRestart();
        await StopHandleAsync(oldHandle).ConfigureAwait(false);

        state = WorkspaceLoadState.StartingLanguageServer;
        failureCode = null;
        failureMessage = null;

        try
        {
            var handle = await loader.LoadAsync(target, cancellationToken).ConfigureAwait(false);
            this.handle = handle;
            this.handle.Client.NotificationReceived += OnNotificationReceived;
            state = WorkspaceLoadState.WorkspaceWarming;
        }
        catch (UserFacingException ex)
        {
            state = WorkspaceLoadState.Failed;
            failureCode = ex.Code;
            failureMessage = ex.Message;
            throw;
        }
        catch (Exception ex)
        {
            state = WorkspaceLoadState.Failed;
            failureCode = "workspace_failed";
            failureMessage = ex.Message;
            throw;
        }
    }

    private RoslynWorkspaceHandle? BeginRestart()
    {
        var oldHandle = handle;
        if (oldHandle is not null)
        {
            oldHandle.Client.NotificationReceived -= OnNotificationReceived;
            handle = null;
        }

        state = WorkspaceLoadState.StartingLanguageServer;
        failureCode = null;
        failureMessage = null;

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
            state = WorkspaceLoadState.Ready;
        }
    }

    private WorkspaceStatus ToStatus(WorkspaceScanResult scan) =>
        new(
            pathGuard.Root,
            state,
            handle?.Target,
            handle?.IsRunning ?? false,
            handle?.PendingRequestCount ?? 0,
            scan,
            failureCode,
            failureMessage);

    private static UserFacingException WorkspaceLoading() =>
        new(
            "workspace_loading",
            "Workspace is starting. Call get_workspace_status and retry when state is LspReady, WorkspaceWarming, or Ready.");
}

public sealed record ReadToolContext(RoslynWorkspaceHandle Handle, WorkspaceLoadState State);
