using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RoslynMcpServer.Cli;
using RoslynMcpServer.Infrastructure;
using RoslynMcpServer.Lsp;
using RoslynMcpServer.Workspace;

namespace RoslynMcpServer.Tests;

public sealed class WorkspaceSessionTests
{
    [Fact]
    public async Task LoadSolution_TransitionsToWorkspaceWarming()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.sln"), string.Empty);
        var session = CreateSession(root.Path, new FakeLoader());

        var status = await session.LoadSolutionAsync("App.sln");

        Assert.Equal(WorkspaceLoadState.WorkspaceWarming, status.State);
        Assert.Equal("App.sln", status.CurrentTarget?.RelativePath);
        Assert.Contains(ServerResourceUris.Guide, status.GuidanceResources);
        Assert.Contains(ServerResourceUris.Capabilities, status.GuidanceResources);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task WorkspaceStatus_IncludesLastLspResponseAt()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.sln"), string.Empty);
        var lastResponseAt = new DateTimeOffset(2026, 5, 17, 13, 45, 12, TimeSpan.Zero);
        var client = new NotificationRecordingClient
        {
            LastResponseAt = lastResponseAt
        };
        var session = CreateSession(root.Path, new ImmediateLoader(client));

        var status = await session.LoadSolutionAsync("App.sln");

        Assert.Equal(lastResponseAt, status.LastLspResponseAt);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task StartupSolutionLoader_LoadsConfiguredSolutionInBackground()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.sln"), string.Empty);
        await using var session = CreateSession(root.Path, new ImmediateLoader(new NotificationRecordingClient()));
        var service = new StartupSolutionLoader(
            CreateOptions(root.Path, loadSolutionPath: "App.sln"),
            session,
            NullLogger<StartupSolutionLoader>.Instance);

        await service.StartAsync(CancellationToken.None);
        var status = await WaitForStateAsync(session, WorkspaceLoadState.WorkspaceWarming);

        Assert.Equal(WorkspaceLoadState.WorkspaceWarming, status.State);
        Assert.Equal("App.sln", status.CurrentTarget?.RelativePath);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task LoadStartupSolution_InvalidExtensionRecordsFailure()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        await using var session = CreateSession(root.Path, new ImmediateLoader(new NotificationRecordingClient()));

        var ex = await Assert.ThrowsAsync<UserFacingException>(() => session.LoadStartupSolutionAsync("App.csproj"));
        var status = await session.GetStatusAsync();

        Assert.Equal("invalid_workspace_file", ex.Code);
        Assert.Equal(WorkspaceLoadState.Failed, status.State);
        Assert.Equal("invalid_workspace_file", status.FailureCode);
    }

    [Fact]
    public async Task LoadStartupSolution_OutsideRootRecordsFailure()
    {
        using var root = TestRoot.Create();
        using var outside = TestRoot.Create();
        var solutionPath = Path.Combine(outside.Path, "Outside.sln");
        File.WriteAllText(solutionPath, string.Empty);
        await using var session = CreateSession(root.Path, new ImmediateLoader(new NotificationRecordingClient()));

        var ex = await Assert.ThrowsAsync<UserFacingException>(() => session.LoadStartupSolutionAsync(solutionPath));
        var status = await session.GetStatusAsync();

        Assert.Equal("path_outside_root", ex.Code);
        Assert.Equal(WorkspaceLoadState.Failed, status.State);
        Assert.Equal("path_outside_root", status.FailureCode);
    }

    [Fact]
    public async Task StartupSolutionLoader_ReadToolPreparationReturnsWorkspaceLoadingWhileLanguageServerStarts()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.sln"), string.Empty);
        var loader = new BlockingLoader();
        await using var session = CreateSession(root.Path, loader);
        var service = new StartupSolutionLoader(
            CreateOptions(root.Path, loadSolutionPath: "App.sln"),
            session,
            NullLogger<StartupSolutionLoader>.Instance);

        await service.StartAsync(CancellationToken.None);
        await loader.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var ex = await Assert.ThrowsAsync<UserFacingException>(() => session.PrepareReadToolAsync());

        Assert.Equal("workspace_loading", ex.Code);

        loader.Release.SetResult();
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task LoadSolution_TransitionsToReadyWhenInitializationNotificationArrivedDuringLoad()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.sln"), string.Empty);
        var client = new NotificationRecordingClient("workspace/projectInitializationComplete");
        var session = CreateSession(root.Path, new ImmediateLoader(client));

        var status = await session.LoadSolutionAsync("App.sln");

        Assert.Equal(WorkspaceLoadState.Ready, status.State);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task LoadSolution_TransitionsToReadyWhenInitializationNotificationArrivesAfterLoad()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.sln"), string.Empty);
        var client = new NotificationRecordingClient();
        var session = CreateSession(root.Path, new ImmediateLoader(client));

        var loadingStatus = await session.LoadSolutionAsync("App.sln");
        client.RaiseNotification("workspace/projectInitializationComplete");
        var readyStatus = await session.GetStatusAsync();

        Assert.Equal(WorkspaceLoadState.WorkspaceWarming, loadingStatus.State);
        Assert.Equal(WorkspaceLoadState.Ready, readyStatus.State);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task LoadSolution_TransitionsToLoadedWithErrorsWhenProjectLoadErrorArrivesBeforeInitializationComplete()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.sln"), string.Empty);
        var projectDirectory = Path.Combine(root.Path, "src", "App");
        Directory.CreateDirectory(projectDirectory);
        var projectPath = Path.Combine(projectDirectory, "App.csproj");
        File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var client = new NotificationRecordingClient();
        var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadSolutionAsync("App.sln");

        client.RaiseNotification(
            "window/logMessage",
            new
            {
                type = 1,
                message = $"""
                    [solution/open] [LanguageServerProjectLoader] Error while loading {projectPath}: Exception: A compatible .NET SDK was not found.

                    Requested SDK version: 11.0.100-preview.3.26207.106
                    global.json file: {Path.Combine(root.Path, "global.json")}
                    """
            });
        client.RaiseNotification("workspace/projectInitializationComplete");
        var status = await session.GetStatusAsync();

        Assert.Equal(WorkspaceLoadState.LoadedWithErrors, status.State);
        var warning = Assert.Single(status.Warnings);
        Assert.Equal("workspace_project_load_failed", warning.Code);
        Assert.Equal(["src/App/App.csproj"], warning.RelatedPaths);
        Assert.Contains("compatible .NET SDK", warning.Message);
        Assert.Contains("11.0.100-preview.3.26207.106", warning.Message);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task LoadProject_RecordsFailureWhenLoaderFails()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), string.Empty);
        var session = CreateSession(root.Path, new FailingLoader());

        var ex = await Assert.ThrowsAsync<UserFacingException>(() => session.LoadProjectAsync("App.csproj"));
        var status = await session.GetStatusAsync();

        Assert.Equal("roslyn_language_server_not_found", ex.Code);
        Assert.Equal(WorkspaceLoadState.Failed, status.State);
        Assert.Equal("roslyn_language_server_not_found", status.FailureCode);
    }

    [Fact]
    public async Task LoadSolution_DoesNotWarnWhenSelectedDirectoryHasMultipleWorkspaceFiles()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.sln"), string.Empty);
        File.WriteAllText(Path.Combine(root.Path, "Other.slnx"), string.Empty);
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), string.Empty);
        var session = CreateSession(root.Path, new FakeLoader());

        var status = await session.LoadSolutionAsync("App.sln");

        Assert.Empty(status.Warnings);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task ClientFault_TransitionsWorkspaceStatusToFailed()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.sln"), string.Empty);
        var client = new FaultableClient();
        var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadSolutionAsync("App.sln");

        client.Fail(new UserFacingException("invalid_lsp_response", "LSP message body exceeded 32 bytes."));
        var status = await session.GetStatusAsync();

        Assert.Equal(WorkspaceLoadState.Failed, status.State);
        Assert.Equal("invalid_lsp_response", status.FailureCode);
        Assert.Contains("32 bytes", status.FailureMessage);
        await session.DisposeAsync();
    }

    private static WorkspaceSession CreateSession(string root, IRoslynWorkspaceLoader loader)
    {
        var options = CreateOptions(root, loadSolutionPath: null);
        var guard = new PathGuard(root);
        return WorkspaceSession.CreateForTest(
            new WorkspaceScanner(options, guard, gitScanner: null),
            guard,
            loader);
    }

    private static async Task<WorkspaceStatus> WaitForStateAsync(
        WorkspaceSession session,
        WorkspaceLoadState expectedState)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2);
        WorkspaceStatus status;
        do
        {
            status = await session.GetStatusAsync();
            if (status.State == expectedState)
            {
                return status;
            }

            await Task.Delay(10);
        }
        while (DateTimeOffset.UtcNow < deadline);

        return status;
    }

    private static CliOptions CreateOptions(string root, string? loadSolutionPath) =>
        new(
            root,
            null,
            loadSolutionPath,
            LogLevel.Information,
            null,
            null,
            TimeSpan.FromSeconds(60),
            100,
            500,
            200,
            2 * 1024 * 1024,
            16,
            2);

    private sealed class FakeLoader : IRoslynWorkspaceLoader
    {
        public Task<RoslynWorkspaceHandle> LoadAsync(WorkspaceTarget target, CancellationToken cancellationToken)
        {
            var process = Process.Start(new ProcessStartInfo("dotnet")
            {
                ArgumentList = { "--info" },
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            })!;
            var client = new LspClient(
                new MemoryStream(),
                new MemoryStream(),
                16,
                2,
                NullLogger<LspClient>.Instance);
            return Task.FromResult(new RoslynWorkspaceHandle(target, new RoslynLanguageServerConnection(process, client)));
        }
    }

    private sealed class FailingLoader : IRoslynWorkspaceLoader
    {
        public Task<RoslynWorkspaceHandle> LoadAsync(WorkspaceTarget target, CancellationToken cancellationToken) =>
            throw new UserFacingException("roslyn_language_server_not_found", RoslynLanguageServerLocator.InstallMessage);
    }

    private sealed class ImmediateLoader(ILspClient client) : IRoslynWorkspaceLoader
    {
        public Task<RoslynWorkspaceHandle> LoadAsync(WorkspaceTarget target, CancellationToken cancellationToken) =>
            Task.FromResult(new RoslynWorkspaceHandle(target, client));
    }

    private sealed class BlockingLoader : IRoslynWorkspaceLoader
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<RoslynWorkspaceHandle> LoadAsync(WorkspaceTarget target, CancellationToken cancellationToken)
        {
            Started.SetResult();
            await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new RoslynWorkspaceHandle(target, new NotificationRecordingClient());
        }
    }

    private sealed class FaultableClient : ILspClient
    {
        public event Action<string, JsonElement?>? NotificationReceived
        {
            add { }
            remove { }
        }

        public event Action<Exception>? Faulted;

        public int PendingRequestCount => 0;
        public bool IsFaulted { get; private set; }
        public Exception? FaultException { get; private set; }

        public void Fail(Exception exception)
        {
            FaultException = exception;
            IsFaulted = true;
            Faulted?.Invoke(exception);
        }

        public Task<JsonElement> RequestAsync(
            string method,
            object? parameters,
            TimeSpan timeout,
            CancellationToken cancellationToken,
            bool isExpensive = false) =>
            Task.FromException<JsonElement>(FaultException ?? new InvalidOperationException("No response configured."));

        public Task NotifyAsync(string method, object? parameters, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task ShutdownAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class NotificationRecordingClient(params string[] initialNotifications) : ILspClient
    {
        private readonly HashSet<string> receivedNotifications = new(initialNotifications, StringComparer.Ordinal);

        public event Action<string, JsonElement?>? NotificationReceived;

        public int PendingRequestCount => 0;
        public DateTimeOffset? LastResponseAt { get; set; }

        public bool HasReceivedNotification(string method) => this.receivedNotifications.Contains(method);

        public void RaiseNotification(string method, object? parameters = null)
        {
            this.receivedNotifications.Add(method);
            var element = parameters is null
                ? (JsonElement?)null
                : JsonSerializer.SerializeToElement(parameters, JsonOptions.Default);
            this.NotificationReceived?.Invoke(method, element);
        }

        public Task<JsonElement> RequestAsync(
            string method,
            object? parameters,
            TimeSpan timeout,
            CancellationToken cancellationToken,
            bool isExpensive = false) =>
            Task.FromException<JsonElement>(new InvalidOperationException("No response configured."));

        public Task NotifyAsync(string method, object? parameters, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task ShutdownAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
