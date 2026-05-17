using System.Text.Json;
using RoslynMcpServer.Infrastructure;
using RoslynMcpServer.Lsp;

namespace RoslynMcpServer.Workspace;

public sealed class WorkspaceSession : IAsyncDisposable
{
    private const string ProjectInitializationCompleteMethod = "workspace/projectInitializationComplete";
    private const string PublishDiagnosticsMethod = "textDocument/publishDiagnostics";
    private const string WindowLogMessageMethod = "window/logMessage";
    private const string ProjectLoaderErrorToken = "[LanguageServerProjectLoader] Error while loading ";
    private const int MaxWorkspaceWarnings = 50;
    private readonly WorkspaceScanner scanner;
    private readonly PathGuard pathGuard;
    private readonly IRoslynWorkspaceLoader loader;
    private readonly DocumentStateManager? documents;
    private readonly DiagnosticStore? diagnostics;
    private readonly DiagnosticNotificationProcessor? diagnosticNotifications;
    private readonly SemaphoreSlim stateLock = new(1, 1);
    private readonly object warningsLock = new();
    private readonly List<WorkspaceWarning> workspaceWarnings = [];
    private WorkspaceScanResult? scanCache;
    private RoslynWorkspaceHandle? handle;
    private WorkspaceLoadState state = WorkspaceLoadState.NotLoaded;
    private string? failureCode;
    private string? failureMessage;

    public WorkspaceSession(
        WorkspaceScanner scanner,
        PathGuard pathGuard,
        IRoslynWorkspaceLoader loader,
        DocumentStateManager? documents,
        DiagnosticStore? diagnostics)
        : this(
            scanner,
            pathGuard,
            loader,
            documents,
            diagnostics,
            diagnostics is null ? null : new DiagnosticNotificationProcessor(diagnostics))
    {
    }

    public WorkspaceSession(
        WorkspaceScanner scanner,
        PathGuard pathGuard,
        IRoslynWorkspaceLoader loader,
        DocumentStateManager? documents,
        DiagnosticStore? diagnostics,
        DiagnosticNotificationProcessor? diagnosticNotifications)
    {
        this.scanner = scanner;
        this.pathGuard = pathGuard;
        this.loader = loader;
        this.documents = documents;
        this.diagnostics = diagnostics;
        this.diagnosticNotifications = diagnosticNotifications;
    }

    public WorkspaceSession(
        WorkspaceScanner scanner,
        PathGuard pathGuard,
        IRoslynWorkspaceLoader loader)
        : this(scanner, pathGuard, loader, documents: null, diagnostics: null)
    {
    }

    public WorkspaceLoadState State => this.state;

    public WorkspaceScanResult ListWorkspaces(bool refresh = false, CancellationToken cancellationToken = default)
    {
        if (!refresh && this.scanCache is not null)
        {
            return this.scanCache;
        }

        this.scanCache = this.scanner.Scan(cancellationToken);
        return this.scanCache;
    }

    public Task<WorkspaceStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var scan = ListWorkspaces(refresh: false, cancellationToken);
        return Task.FromResult(ToStatus(scan));
    }

    public Task<WorkspaceStatus> LoadSolutionAsync(string path, CancellationToken cancellationToken = default) =>
        LoadAsync(path, [".sln", ".slnx"], WorkspaceKind.Solution, recordValidationFailure: false, cancellationToken);

    public Task<WorkspaceStatus> LoadStartupSolutionAsync(string path, CancellationToken cancellationToken = default) =>
        LoadAsync(path, [".sln", ".slnx"], WorkspaceKind.Solution, recordValidationFailure: true, cancellationToken);

    public Task<WorkspaceStatus> LoadProjectAsync(string path, CancellationToken cancellationToken = default) =>
        LoadAsync(path, [".csproj"], WorkspaceKind.Project, recordValidationFailure: false, cancellationToken);

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

        if (!await this.stateLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
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

            var target = SelectAutoLoadTarget(ListWorkspaces(refresh: false, cancellationToken));
            await LoadTargetCoreAsync(target, cancellationToken).ConfigureAwait(false);
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
            await this.handle.Client.ShutdownAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
            await this.handle.DisposeAsync().ConfigureAwait(false);
        }

        if (this.diagnosticNotifications is not null)
        {
            await this.diagnosticNotifications.DisposeAsync().ConfigureAwait(false);
        }

        this.stateLock.Dispose();
    }

    private async Task<WorkspaceStatus> LoadAsync(
        string path,
        string[] allowedExtensions,
        WorkspaceKind requestedKind,
        bool recordValidationFailure,
        CancellationToken cancellationToken)
    {
        await this.stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            WorkspaceTarget target;
            try
            {
                target = CreateTarget(path, allowedExtensions, requestedKind);
            }
            catch (UserFacingException ex) when (recordValidationFailure)
            {
                this.state = WorkspaceLoadState.Failed;
                this.failureCode = ex.Code;
                this.failureMessage = ex.Message;
                throw;
            }

            await LoadTargetCoreAsync(target, cancellationToken).ConfigureAwait(false);

            return ToStatus(ListWorkspaces(refresh: false, cancellationToken));
        }
        finally
        {
            this.stateLock.Release();
        }
    }

    private async Task LoadTargetCoreAsync(WorkspaceTarget target, CancellationToken cancellationToken)
    {
        var oldHandle = BeginRestart();
        await StopHandleAsync(oldHandle).ConfigureAwait(false);

        this.state = WorkspaceLoadState.StartingLanguageServer;
        this.failureCode = null;
        this.failureMessage = null;

        try
        {
            var handle = await this.loader.LoadAsync(target, cancellationToken).ConfigureAwait(false);
            this.handle = handle;
            this.handle.Client.NotificationReceived += OnNotificationReceived;
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
            oldHandle.Client.NotificationReceived -= OnNotificationReceived;
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

        ClearWorkspaceWarnings();
        this.state = WorkspaceLoadState.StartingLanguageServer;
        this.failureCode = null;
        this.failureMessage = null;

        return oldHandle;
    }

    private WorkspaceTarget CreateTarget(string path, string[] allowedExtensions, WorkspaceKind requestedKind)
    {
        var fullPath = this.pathGuard.RequireFileInsideRoot(path, allowedExtensions);
        var extension = Path.GetExtension(fullPath);
        var kind = string.Equals(extension, ".slnx", StringComparison.OrdinalIgnoreCase)
            ? WorkspaceKind.SolutionX
            : requestedKind;
        return new WorkspaceTarget(
            kind,
            fullPath,
            this.pathGuard.ToRelativePath(fullPath),
            this.pathGuard.Root,
            Path.GetDirectoryName(fullPath) ?? this.pathGuard.Root);
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
                this.pathGuard.Root,
                Path.GetDirectoryName(solution.FullPath) ?? this.pathGuard.Root);
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
                this.pathGuard.Root,
                Path.GetDirectoryName(project.FullPath) ?? this.pathGuard.Root);
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
        if (string.Equals(method, ProjectInitializationCompleteMethod, StringComparison.Ordinal))
        {
            this.state = HasWorkspaceWarnings()
                ? WorkspaceLoadState.LoadedWithErrors
                : WorkspaceLoadState.Ready;
            return;
        }

        if (string.Equals(method, PublishDiagnosticsMethod, StringComparison.Ordinal))
        {
            this.diagnosticNotifications?.Enqueue(parameters);
            return;
        }

        if (string.Equals(method, WindowLogMessageMethod, StringComparison.Ordinal))
        {
            TryRecordWorkspaceLoadWarning(parameters);
        }
    }

    private void ApplyAlreadyReceivedNotifications(ILspClient client)
    {
        if (client.HasReceivedNotification(ProjectInitializationCompleteMethod))
        {
            this.state = HasWorkspaceWarnings()
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
            this.pathGuard.Root,
            this.state,
            this.handle?.Target,
            this.handle?.IsRunning ?? false,
            this.handle?.PendingRequestCount ?? 0,
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
            GetWorkspaceWarnings());
    }

    private void TryRecordWorkspaceLoadWarning(JsonElement? parameters)
    {
        if (parameters is null ||
            parameters.Value.ValueKind != JsonValueKind.Object ||
            !parameters.Value.TryGetProperty("message", out var messageElement) ||
            messageElement.ValueKind != JsonValueKind.String)
        {
            return;
        }

        var message = messageElement.GetString();
        if (string.IsNullOrWhiteSpace(message) ||
            !message.Contains(ProjectLoaderErrorToken, StringComparison.Ordinal))
        {
            return;
        }

        var relatedPaths = TryGetLoadErrorRelativePath(message) is { } path
            ? new[] { path }
            : [];
        var warning = new WorkspaceWarning(
            "workspace_project_load_failed",
            BuildWorkspaceLoadWarningMessage(message),
            relatedPaths);

        AddWorkspaceWarning(warning);
        if (this.state is WorkspaceLoadState.Ready)
        {
            this.state = WorkspaceLoadState.LoadedWithErrors;
        }
    }

    private void AddWorkspaceWarning(WorkspaceWarning warning)
    {
        lock (this.warningsLock)
        {
            if (this.workspaceWarnings.Any(existing =>
                string.Equals(existing.Code, warning.Code, StringComparison.Ordinal) &&
                existing.RelatedPaths.SequenceEqual(warning.RelatedPaths, StringComparer.OrdinalIgnoreCase)))
            {
                return;
            }

            if (this.workspaceWarnings.Count >= MaxWorkspaceWarnings)
            {
                return;
            }

            this.workspaceWarnings.Add(warning);
        }
    }

    private bool HasWorkspaceWarnings()
    {
        lock (this.warningsLock)
        {
            return this.workspaceWarnings.Count > 0;
        }
    }

    private IReadOnlyList<WorkspaceWarning> GetWorkspaceWarnings()
    {
        lock (this.warningsLock)
        {
            return this.workspaceWarnings.ToArray();
        }
    }

    private void ClearWorkspaceWarnings()
    {
        lock (this.warningsLock)
        {
            this.workspaceWarnings.Clear();
        }
    }

    private string? TryGetLoadErrorRelativePath(string message)
    {
        var tokenIndex = message.IndexOf(ProjectLoaderErrorToken, StringComparison.Ordinal);
        if (tokenIndex < 0)
        {
            return null;
        }

        var start = tokenIndex + ProjectLoaderErrorToken.Length;
        var suffix = message[start..];
        string[] extensions = [".csproj", ".slnx", ".sln"];
        foreach (var extension in extensions)
        {
            var extensionIndex = suffix.IndexOf(extension, StringComparison.OrdinalIgnoreCase);
            if (extensionIndex < 0)
            {
                continue;
            }

            var path = suffix[..(extensionIndex + extension.Length)];
            try
            {
                var fullPath = Path.GetFullPath(path);
                return this.pathGuard.IsInsideRoot(fullPath)
                    ? this.pathGuard.ToRelativePath(fullPath)
                    : path;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return path;
            }
        }

        return null;
    }

    private static string BuildWorkspaceLoadWarningMessage(string message)
    {
        const string sdkMissing = "A compatible .NET SDK was not found.";
        if (message.Contains(sdkMissing, StringComparison.OrdinalIgnoreCase))
        {
            var requestedSdk = TryExtractLineValue(message, "Requested SDK version:");
            var globalJson = TryExtractLineValue(message, "global.json file:");
            var details = new List<string>
            {
                "Roslyn LS failed to load a project because a compatible .NET SDK was not found."
            };

            if (!string.IsNullOrWhiteSpace(requestedSdk))
            {
                details.Add($"Requested SDK version: {requestedSdk}.");
            }

            if (!string.IsNullOrWhiteSpace(globalJson))
            {
                details.Add($"global.json: {globalJson}.");
            }

            return string.Join(' ', details);
        }

        return "Roslyn LS reported a project load error. Read-tool results may be incomplete; inspect server logs for the full load error.";
    }

    private static string? TryExtractLineValue(string message, string prefix)
    {
        using var reader = new StringReader(message);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            line = line.Trim();
            if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return line[prefix.Length..].Trim();
            }
        }

        return null;
    }

    private static UserFacingException WorkspaceLoading() =>
        new(
            "workspace_loading",
            "Workspace is starting. Call get_workspace_status and retry when state is LspReady, WorkspaceWarming, LoadedWithErrors, or Ready.");
}

public sealed record ReadToolContext(RoslynWorkspaceHandle Handle, WorkspaceLoadState State);
