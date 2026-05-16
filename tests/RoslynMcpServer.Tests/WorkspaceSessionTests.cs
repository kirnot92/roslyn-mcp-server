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
        await session.DisposeAsync();
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
    public async Task LoadSolution_WarnsWhenSelectedDirectoryHasMultipleWorkspaceFiles()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.sln"), string.Empty);
        File.WriteAllText(Path.Combine(root.Path, "Other.slnx"), string.Empty);
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), string.Empty);
        var session = CreateSession(root.Path, new FakeLoader());

        var status = await session.LoadSolutionAsync("App.sln");

        var warning = Assert.Single(status.Warnings);
        Assert.Equal("workspace_directory_ambiguous", warning.Code);
        Assert.Equal(["App.csproj", "App.sln", "Other.slnx"], warning.RelatedPaths);
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
        var options = new CliOptions(
            root,
            null,
            LogLevel.Information,
            null,
            null,
            TimeSpan.FromSeconds(60),
            6,
            TimeSpan.FromSeconds(3),
            100,
            500,
            200,
            2 * 1024 * 1024,
            16,
            2);
        var guard = new PathGuard(root);
        return new WorkspaceSession(new WorkspaceScanner(options, guard, gitScanner: null), guard, loader);
    }

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

        public bool HasReceivedNotification(string method) => this.receivedNotifications.Contains(method);

        public void RaiseNotification(string method)
        {
            this.receivedNotifications.Add(method);
            this.NotificationReceived?.Invoke(method, null);
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
