using System.Text.Json;
using RoslynMcpServer.Cli;
using RoslynMcpServer.Infrastructure;
using RoslynMcpServer.Lsp;

namespace RoslynMcpServer.Workspace;

public sealed class WorkspaceSession : IAsyncDisposable
{
    private const string ProjectInitializationCompleteMethod = "workspace/projectInitializationComplete";
    private const string PublishDiagnosticsMethod = "textDocument/publishDiagnostics";
    private const string WindowLogMessageMethod = "window/logMessage";
    private readonly WorkspaceScanner scanner;
    private readonly WorkspaceRoot workspaceRoot;
    private readonly IRoslynWorkspaceLoader loader;
    private readonly DocumentStateManager? documents;
    private readonly DiagnosticStore? diagnostics;
    private readonly DiagnosticNotificationProcessor? diagnosticNotifications;
    private readonly WorkspaceWarningCollector warningCollector;
    private readonly SemaphoreSlim stateLock = new(1, 1);
    private WorkspaceScanResult? lastWorkspaceScan;
    private RoslynWorkspaceHandle? handle;
    private Action<string, JsonElement?>? notificationHandler;
    private WorkspaceLoadState state = WorkspaceLoadState.NotLoaded;
    private string? failureCode;
    private string? failureMessage;

    public static WorkspaceSession CreateForServer(
        WorkspaceRoot workspaceRoot,
        IRoslynWorkspaceLoader loader,
        CliOptions options,
        DocumentStateManager documents,
        DiagnosticStore diagnostics)
    {
        var scanner = new WorkspaceScanner(options, workspaceRoot);
        var diagnosticNotifications = new DiagnosticNotificationProcessor(diagnostics);
        var session = new WorkspaceSession(
            scanner,
            workspaceRoot,
            loader,
            documents,
            diagnostics,
            diagnosticNotifications);
        if (!string.IsNullOrWhiteSpace(options.LoadSolutionPath))
        {
            session.MarkStartupLoadPending();
        }

        return session;
    }

    public static WorkspaceSession CreateForTest(
        WorkspaceScanner scanner,
        WorkspaceRoot workspaceRoot,
        IRoslynWorkspaceLoader loader,
        CliOptions? options = null,
        DocumentStateManager? documents = null,
        DiagnosticStore? diagnostics = null,
        DiagnosticNotificationProcessor? diagnosticNotifications = null)
    {
        var session = new WorkspaceSession(
            scanner,
            workspaceRoot,
            loader,
            documents,
            diagnostics,
            diagnosticNotifications ?? (diagnostics is null ? null : new DiagnosticNotificationProcessor(diagnostics)));

        if (!string.IsNullOrWhiteSpace(options?.LoadSolutionPath))
        {
            session.MarkStartupLoadPending();
        }

        return session;
    }

    private WorkspaceSession(
        WorkspaceScanner scanner,
        WorkspaceRoot workspaceRoot,
        IRoslynWorkspaceLoader loader,
        DocumentStateManager? documents,
        DiagnosticStore? diagnostics,
        DiagnosticNotificationProcessor? diagnosticNotifications)
    {
        this.scanner = scanner;
        this.workspaceRoot = workspaceRoot;
        this.loader = loader;
        this.documents = documents;
        this.diagnostics = diagnostics;
        this.diagnosticNotifications = diagnosticNotifications;
        this.warningCollector = new WorkspaceWarningCollector(workspaceRoot);
    }

    public WorkspaceLoadState State => this.state;

    public WorkspaceScanResult ListWorkspaces(int? maxDepth = null, CancellationToken cancellationToken = default)
    {
        this.lastWorkspaceScan = this.scanner.Scan(maxDepth, cancellationToken);
        return this.lastWorkspaceScan;
    }

    public Task<WorkspaceStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var scan = GetLastWorkspaceScanOrScan(cancellationToken);
        return Task.FromResult(ToStatus(scan));
    }

    public Task<WorkspaceStatus> LoadSolutionAsync(string path, CancellationToken cancellationToken = default) =>
        LoadAsync(path, [".sln", ".slnx"], WorkspaceKind.Solution, cancellationToken);

    public Task<WorkspaceStatus> LoadProjectAsync(string path, CancellationToken cancellationToken = default) =>
        LoadAsync(path, [".csproj"], WorkspaceKind.Project, cancellationToken);

    public void MarkStartupLoadPending()
    {
        if (this.state is not WorkspaceLoadState.NotLoaded)
        {
            return;
        }

        this.state = WorkspaceLoadState.StartingLanguageServer;
        this.failureCode = null;
        this.failureMessage = null;
    }

    public async Task MarkStartupLoadFailedAsync(UserFacingException exception)
    {
        await this.stateLock.WaitAsync();
        try
        {
            if (this.state is not WorkspaceLoadState.StartingLanguageServer)
            {
                return;
            }

            this.state = WorkspaceLoadState.Failed;
            this.failureCode = exception.Code;
            this.failureMessage = exception.Message;
        }
        finally
        {
            this.stateLock.Release();
        }
    }

    public async Task<ReadToolContext> PrepareReadToolAsync(CancellationToken cancellationToken = default)
    {
        var currentState = this.state;
        if (currentState is WorkspaceLoadState.StartingLanguageServer)
        {
            throw WorkspaceLoading();
        }

        if (currentState is WorkspaceLoadState.Failed)
        {
            throw new UserFacingException(
                "workspace_failed",
                this.failureMessage ?? "Workspace failed to load. Call load_solution or load_project to retry.");
        }

        if (this.handle is not null &&
            currentState is WorkspaceLoadState.LspReady or
                WorkspaceLoadState.WorkspaceWarming or
                WorkspaceLoadState.LoadedWithErrors or
                WorkspaceLoadState.Ready)
        {
            ThrowIfCurrentClientFaulted();
            return new ReadToolContext(this.handle, currentState);
        }

        if (!await this.stateLock.WaitAsync(0, cancellationToken))
        {
            throw WorkspaceLoading();
        }

        try
        {
            currentState = this.state;
            if (currentState is WorkspaceLoadState.StartingLanguageServer)
            {
                throw WorkspaceLoading();
            }

            if (this.handle is not null &&
                currentState is WorkspaceLoadState.LspReady or
                    WorkspaceLoadState.WorkspaceWarming or
                    WorkspaceLoadState.LoadedWithErrors or
                    WorkspaceLoadState.Ready)
            {
                ThrowIfCurrentClientFaulted();
                return new ReadToolContext(this.handle, currentState);
            }

            if (currentState is WorkspaceLoadState.Failed)
            {
                throw new UserFacingException(
                    "workspace_failed",
                    this.failureMessage ?? "Workspace failed to load. Call load_solution or load_project to retry.");
            }

            var target = SelectAutoLoadTarget(GetLastWorkspaceScanOrScan(cancellationToken));
            await LoadTargetCoreAsync(target, cancellationToken);
            return new ReadToolContext(this.handle!, this.state);
        }
        finally
        {
            this.stateLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (this.handle is not null)
        {
            await this.handle.Client.ShutdownAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
            await this.handle.DisposeAsync();
        }

        if (this.diagnosticNotifications is not null)
        {
            await this.diagnosticNotifications.DisposeAsync();
        }

        this.stateLock.Dispose();
    }

    private async Task<WorkspaceStatus> LoadAsync(
        string path,
        string[] allowedExtensions,
        WorkspaceKind requestedKind,
        CancellationToken cancellationToken)
    {
        await this.stateLock.WaitAsync(cancellationToken);
        try
        {
            var target = CreateTarget(path, allowedExtensions, requestedKind);
            await LoadTargetCoreAsync(target, cancellationToken);

            return ToStatus(GetLastWorkspaceScanOrScan(cancellationToken));
        }
        finally
        {
            this.stateLock.Release();
        }
    }

    private async Task LoadTargetCoreAsync(WorkspaceTarget target, CancellationToken cancellationToken)
    {
        var oldHandle = BeginRestart();
        await StopHandleAsync(oldHandle);

        this.state = WorkspaceLoadState.StartingLanguageServer;
        this.failureCode = null;
        this.failureMessage = null;

        try
        {
            var handle = await this.loader.LoadAsync(target, cancellationToken);
            this.handle = handle;
            var notificationGeneration = this.diagnosticNotifications?.CurrentGeneration ?? 0;
            this.notificationHandler = (method, parameters) => OnNotificationReceived(notificationGeneration, method, parameters);
            this.handle.Client.NotificationReceived += this.notificationHandler;
            this.handle.Client.Faulted += OnClientFaulted;
            this.state = WorkspaceLoadState.WorkspaceWarming;
            ApplyAlreadyReceivedNotifications(this.handle.Client);
        }
        catch (UserFacingException ex)
        {
            this.state = WorkspaceLoadState.Failed;
            this.failureCode = ex.Code;
            this.failureMessage = ex.Message;
            throw;
        }
        catch (Exception ex)
        {
            this.state = WorkspaceLoadState.Failed;
            this.failureCode = "workspace_failed";
            this.failureMessage = ex.Message;
            throw;
        }
    }

    private RoslynWorkspaceHandle? BeginRestart()
    {
        var oldHandle = this.handle;
        if (oldHandle is not null)
        {
            if (this.notificationHandler is not null)
            {
                oldHandle.Client.NotificationReceived -= this.notificationHandler;
                this.notificationHandler = null;
            }

            oldHandle.Client.Faulted -= OnClientFaulted;
            this.handle = null;
        }

        if (this.diagnosticNotifications is not null)
        {
            this.diagnosticNotifications.ResetForNewWorkspace();
        }
        else
        {
            this.diagnostics?.Clear();
        }

        this.warningCollector.Clear();
        this.state = WorkspaceLoadState.StartingLanguageServer;
        this.failureCode = null;
        this.failureMessage = null;

        return oldHandle;
    }

    private WorkspaceTarget CreateTarget(string path, string[] allowedExtensions, WorkspaceKind requestedKind)
    {
        var fullPath = this.workspaceRoot.RequireFileInsideRoot(path, allowedExtensions);
        var extension = Path.GetExtension(fullPath);
        var kind = string.Equals(extension, ".slnx", StringComparison.OrdinalIgnoreCase)
            ? WorkspaceKind.SolutionX
            : requestedKind;
        return new WorkspaceTarget(
            kind,
            fullPath,
            this.workspaceRoot.ToRelativePath(fullPath),
            this.workspaceRoot.Root,
            Path.GetDirectoryName(fullPath) ?? this.workspaceRoot.Root);
    }

    private WorkspaceScanResult GetLastWorkspaceScanOrScan(CancellationToken cancellationToken)
    {
        if (this.lastWorkspaceScan is not null)
        {
            return this.lastWorkspaceScan;
        }

        this.lastWorkspaceScan = this.scanner.Scan(cancellationToken: cancellationToken);
        return this.lastWorkspaceScan;
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
                this.workspaceRoot.Root,
                Path.GetDirectoryName(solution.FullPath) ?? this.workspaceRoot.Root);
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
                this.workspaceRoot.Root,
                Path.GetDirectoryName(project.FullPath) ?? this.workspaceRoot.Root);
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

        await handle.Client.ShutdownAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        await handle.DisposeAsync();
    }

    private void OnNotificationReceived(int generation, string method, System.Text.Json.JsonElement? parameters)
    {
        if (this.diagnosticNotifications is not null && !this.diagnosticNotifications.IsCurrentGeneration(generation))
        {
            if (string.Equals(method, PublishDiagnosticsMethod, StringComparison.Ordinal))
            {
                this.diagnosticNotifications.Enqueue(generation, parameters);
            }

            return;
        }

        if (string.Equals(method, ProjectInitializationCompleteMethod, StringComparison.Ordinal))
        {
            this.state = this.warningCollector.HasWarnings()
                ? WorkspaceLoadState.LoadedWithErrors
                : WorkspaceLoadState.Ready;
            return;
        }

        if (string.Equals(method, PublishDiagnosticsMethod, StringComparison.Ordinal))
        {
            this.diagnosticNotifications?.Enqueue(generation, parameters);
            return;
        }

        if (string.Equals(method, WindowLogMessageMethod, StringComparison.Ordinal))
        {
            if (this.warningCollector.TryRecordWorkspaceLoadWarning(parameters) &&
                this.state is WorkspaceLoadState.Ready)
            {
                this.state = WorkspaceLoadState.LoadedWithErrors;
            }
        }
    }

    private void ApplyAlreadyReceivedNotifications(ILspClient client)
    {
        if (client.HasReceivedNotification(ProjectInitializationCompleteMethod))
        {
            this.state = this.warningCollector.HasWarnings()
                ? WorkspaceLoadState.LoadedWithErrors
                : WorkspaceLoadState.Ready;
        }
    }

    private void OnClientFaulted(Exception exception) => MarkClientFaulted(exception);

    private void ThrowIfCurrentClientFaulted()
    {
        if (this.handle?.Client.IsFaulted != true)
        {
            return;
        }

        var exception = this.handle.Client.FaultException ??
            new UserFacingException(
                "lsp_connection_failed",
                "LSP connection failed. Call load_solution or load_project to restart the workspace.");
        MarkClientFaulted(exception);
        throw new UserFacingException(
            "workspace_failed",
            this.failureMessage ?? "Workspace failed. Call load_solution or load_project to retry.",
            exception);
    }

    private void MarkClientFaulted(Exception exception)
    {
        var userFacingException = exception as UserFacingException;
        this.state = WorkspaceLoadState.Failed;
        this.failureCode = userFacingException?.Code ?? "lsp_connection_failed";
        this.failureMessage = userFacingException?.Message ??
            "LSP connection failed. Call load_solution or load_project to restart the workspace.";
    }

    private WorkspaceStatus ToStatus(WorkspaceScanResult scan)
    {
        if (this.handle?.Client.IsFaulted == true)
        {
            MarkClientFaulted(this.handle.Client.FaultException ??
                new UserFacingException(
                    "lsp_connection_failed",
                    "LSP connection failed. Call load_solution or load_project to restart the workspace."));
        }

        var diagnosticQueueStatistics = this.diagnosticNotifications?.Statistics;
        return new(
            this.workspaceRoot.Root,
            this.state,
            this.handle?.Target,
            this.handle?.IsRunning ?? false,
            this.handle?.PendingRequestCount ?? 0,
            this.handle?.LastLspResponseAt,
            scan,
            this.failureCode,
            this.failureMessage,
            this.documents?.OpenDocumentCount ?? 0,
            this.diagnostics?.KnownFileCount ?? 0,
            this.diagnostics?.LastUpdatedAt,
            diagnosticQueueStatistics?.Capacity ?? 0,
            diagnosticQueueStatistics?.Pending ?? 0,
            diagnosticQueueStatistics?.Processed ?? 0,
            diagnosticQueueStatistics?.Dropped ?? 0,
            diagnosticQueueStatistics?.Stale ?? 0,
            diagnosticQueueStatistics?.OverflowPolicy,
            this.warningCollector.GetWarnings());
    }

    private static UserFacingException WorkspaceLoading() =>
        new(
            "workspace_loading",
            "Workspace is starting. Call get_workspace_status and retry when state is LspReady, WorkspaceWarming, LoadedWithErrors, or Ready.");
}

public sealed record ReadToolContext(RoslynWorkspaceHandle Handle, WorkspaceLoadState State);
