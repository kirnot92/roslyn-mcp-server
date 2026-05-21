using System.Text.Json;
using Microsoft.Extensions.Logging;
using RoslynMcpServer.Cli;
using RoslynMcpServer.Lsp;
using RoslynMcpServer.Workspace;

namespace RoslynMcpServer.Tests;

public sealed class RoslynWorkspaceLoaderTests
{
    [Theory]
    [InlineData(WorkspaceKind.Solution, "App.sln")]
    [InlineData(WorkspaceKind.SolutionX, "App.slnx")]
    public async Task LoadAsync_SendsSolutionOpenNotificationForSolutionTargets(WorkspaceKind kind, string fileName)
    {
        using var root = TestRoot.Create();
        var targetPath = Path.Combine(root.Path, fileName);
        File.WriteAllText(targetPath, string.Empty);
        var client = new FakeLspClient();
        client.EnqueueResponse(new { capabilities = new { } });
        var process = new FakeLanguageServerProcess(client);
        var loader = RoslynWorkspaceLoader.CreateForTest(CreateOptions(root.Path), process.Start);
        var target = CreateTarget(kind, targetPath, root.Path);

        await using var handle = await loader.LoadAsync(target, CancellationToken.None);

        Assert.Same(client, handle.Client);
        Assert.Same(target, Assert.Single(process.StartedTargets));
        var initializeRequest = Assert.Single(client.Requests);
        Assert.Equal("initialize", initializeRequest.Method);
        Assert.Equal("en-US", initializeRequest.Params.GetProperty("locale").GetString());
        Assert.Collection(
            client.Notifications,
            notification => Assert.Equal("initialized", notification.Method),
            notification =>
            {
                Assert.Equal("solution/open", notification.Method);
                Assert.Equal(ToFileUri(targetPath), notification.Params.GetProperty("solution").GetString());
            });
    }

    [Fact]
    public async Task LoadAsync_SendsProjectOpenNotificationForProjectTarget()
    {
        using var root = TestRoot.Create();
        var targetPath = Path.Combine(root.Path, "App.csproj");
        File.WriteAllText(targetPath, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var client = new FakeLspClient();
        client.EnqueueResponse(new { capabilities = new { } });
        var process = new FakeLanguageServerProcess(client);
        var loader = RoslynWorkspaceLoader.CreateForTest(CreateOptions(root.Path), process.Start);
        var target = CreateTarget(WorkspaceKind.Project, targetPath, root.Path);

        await using var handle = await loader.LoadAsync(target, CancellationToken.None);

        Assert.Same(client, handle.Client);
        Assert.Same(target, Assert.Single(process.StartedTargets));
        var initializeRequest = Assert.Single(client.Requests);
        Assert.Equal("initialize", initializeRequest.Method);
        Assert.Equal("en-US", initializeRequest.Params.GetProperty("locale").GetString());
        Assert.Collection(
            client.Notifications,
            notification => Assert.Equal("initialized", notification.Method),
            notification =>
            {
                Assert.Equal("project/open", notification.Method);
                var projects = notification.Params.GetProperty("projects").EnumerateArray().ToArray();
                var project = Assert.Single(projects);
                Assert.Equal(ToFileUri(targetPath), project.GetString());
            });
    }

    private static WorkspaceTarget CreateTarget(WorkspaceKind kind, string fullPath, string root) =>
        new(kind, fullPath, Path.GetFileName(fullPath), root, Path.GetDirectoryName(fullPath) ?? root);

    private static CliOptions CreateOptions(string root) =>
        new(
            root,
            null,
            null,
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

    private static string ToFileUri(string path) => new Uri(Path.GetFullPath(path)).AbsoluteUri;

    private sealed class FakeLanguageServerProcess(ILspClient client)
    {
        public List<WorkspaceTarget> StartedTargets { get; } = [];

        public RoslynWorkspaceHandle Start(WorkspaceTarget target)
        {
            StartedTargets.Add(target);
            return new RoslynWorkspaceHandle(target, client);
        }
    }

    private sealed class FakeLspClient : ILspClient
    {
        private readonly Queue<JsonElement> responses = new();

        public event Action<string, JsonElement?>? NotificationReceived
        {
            add { }
            remove { }
        }

        public int PendingRequestCount => 0;
        public List<(string Method, JsonElement Params)> Notifications { get; } = [];
        public List<(string Method, JsonElement Params)> Requests { get; } = [];

        public void EnqueueResponse(object? response) =>
            this.responses.Enqueue(JsonSerializer.SerializeToElement(response, JsonOptions.Default));

        public Task<JsonElement> RequestAsync(
            string method,
            object? parameters,
            TimeSpan timeout,
            CancellationToken cancellationToken,
            bool isExpensive = false)
        {
            Requests.Add((method, JsonSerializer.SerializeToElement(parameters, JsonOptions.Default)));
            return Task.FromResult(this.responses.Dequeue());
        }

        public Task NotifyAsync(string method, object? parameters, CancellationToken cancellationToken)
        {
            Notifications.Add((method, JsonSerializer.SerializeToElement(parameters, JsonOptions.Default)));
            return Task.CompletedTask;
        }

        public Task ShutdownAsync(TimeSpan timeout, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
