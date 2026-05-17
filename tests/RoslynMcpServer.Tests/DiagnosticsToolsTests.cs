using System.Text.Json;
using Microsoft.Extensions.Logging;
using RoslynMcpServer.Cli;
using RoslynMcpServer.Infrastructure;
using RoslynMcpServer.Lsp;
using RoslynMcpServer.Mcp;
using RoslynMcpServer.Workspace;
using Lsp = RoslynMcpServer.Lsp;

namespace RoslynMcpServer.Tests;

public sealed class DiagnosticsToolsTests
{
    [Fact]
    public async Task FileSpecificDiagnostics_ReturnRootRelativePathAndOneBasedRange()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "class C { }");
        var client = new FakeLspClient();
        var (session, tools, store) = CreateLoadedTools(root.Path, client);
        store.TryUpdateFromPublishDiagnostics(Publish(root.Path, "Program.cs", Diagnostic("boom", DiagnosticSeverity.Error)));

        var result = await tools.Diagnostics(file: "Program.cs");

        var diagnostics = Assert.IsType<DiagnosticsResult>(result);
        var item = Assert.Single(diagnostics.Items);
        Assert.Equal("Program.cs", item.File);
        Assert.Equal("error", item.Severity);
        Assert.Equal(2, item.Range.StartLine);
        Assert.Equal(3, item.Range.StartColumn);
        Assert.Equal(2, item.Line);
        Assert.Equal(3, item.Column);
        Assert.Equal(["textDocument/didOpen"], client.Notifications.Select(notification => notification.Method));
        await session.DisposeAsync();
    }

    [Fact]
    public async Task FileSpecificDiagnostics_EnsuresDocumentIsOpenBeforeReadingStore()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "class C { }");
        var client = new FakeLspClient();
        var (session, tools, store) = CreateLoadedTools(root.Path, client);
        store.TryUpdateFromPublishDiagnostics(Publish(root.Path, "Program.cs", Diagnostic("boom", DiagnosticSeverity.Warning)));

        var result = await tools.Diagnostics(file: "Program.cs");

        Assert.IsType<DiagnosticsResult>(result);
        Assert.Equal(["notify:textDocument/didOpen"], client.Events);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task FileSpecificDiagnosticsWithoutPublish_ReturnsUnknownCompleteness()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "class C { }");
        var (session, tools, _) = CreateLoadedTools(root.Path, new FakeLspClient());

        var result = await tools.Diagnostics(file: "Program.cs");

        var diagnostics = Assert.IsType<DiagnosticsResult>(result);
        Assert.Empty(diagnostics.Items);
        Assert.Equal("unknown", diagnostics.Completeness);
        Assert.Equal(ToolRetryHints.WorkspaceWarmingMs, diagnostics.RetryAfterMs);
        Assert.Contains("No textDocument/publishDiagnostics", diagnostics.Reason);
        Assert.Null(diagnostics.LastUpdatedAt);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task FileSpecificDiagnosticsWithoutPublish_DoesNotReturnOtherFileLastUpdatedAt()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "A.cs"), "class A { }");
        File.WriteAllText(Path.Combine(root.Path, "B.cs"), "class B { }");
        var (session, tools, store) = CreateLoadedTools(root.Path, new FakeLspClient());
        store.TryUpdateFromPublishDiagnostics(Publish(root.Path, "A.cs", Diagnostic("boom", DiagnosticSeverity.Error)));

        var result = await tools.Diagnostics(file: "B.cs");

        var diagnostics = Assert.IsType<DiagnosticsResult>(result);
        Assert.Empty(diagnostics.Items);
        Assert.Equal("unknown", diagnostics.Completeness);
        Assert.Null(diagnostics.LastUpdatedAt);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task FileSpecificDiagnostics_ReportsCacheCapTruncation()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "class C { }");
        var (session, tools, store) = CreateLoadedTools(root.Path, new FakeLspClient());
        var diagnosticsPayload = Enumerable
            .Range(0, DiagnosticStore.DefaultMaxDiagnosticsPerFile + 1)
            .Select(i => Diagnostic($"diag{i}", DiagnosticSeverity.Error))
            .ToArray();
        store.TryUpdateFromPublishDiagnostics(Publish(root.Path, "Program.cs", diagnosticsPayload));

        var result = await tools.Diagnostics(file: "Program.cs", maxResults: 1000);

        var diagnostics = Assert.IsType<DiagnosticsResult>(result);
        Assert.Equal(DiagnosticStore.DefaultMaxDiagnosticsPerFile + 1, diagnostics.TotalKnown);
        Assert.Equal(DiagnosticStore.DefaultMaxDiagnosticsPerFile, diagnostics.Returned);
        Assert.True(diagnostics.Truncated);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task WorkspaceScope_TruncatesAtDefaultLimitAndReportsMetadata()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var client = new FakeLspClient();
        var (session, tools, store) = CreateLoadedTools(root.Path, client);
        for (var i = 0; i < 201; i++)
        {
            var file = $"File{i}.cs";
            File.WriteAllText(Path.Combine(root.Path, file), "class C { }");
            store.TryUpdateFromPublishDiagnostics(Publish(root.Path, file, Diagnostic($"diag{i}", DiagnosticSeverity.Error)));
        }

        var result = await tools.Diagnostics(scope: "workspace");

        var diagnostics = Assert.IsType<DiagnosticsResult>(result);
        Assert.Equal(201, diagnostics.TotalKnown);
        Assert.Equal(200, diagnostics.Returned);
        Assert.True(diagnostics.Truncated);
        Assert.NotNull(diagnostics.LastUpdatedAt);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task WorkspaceScope_AppliesUserMaxResultsAndServerHardCap()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var (session, tools, store) = CreateLoadedTools(root.Path, new FakeLspClient());
        for (var i = 0; i < 1001; i++)
        {
            var file = $"File{i}.cs";
            File.WriteAllText(Path.Combine(root.Path, file), "class C { }");
            store.TryUpdateFromPublishDiagnostics(Publish(root.Path, file, Diagnostic($"diag{i}", DiagnosticSeverity.Warning)));
        }

        var userLimitedResult = await tools.Diagnostics(scope: "workspace", maxResults: 2);
        var hardCappedResult = await tools.Diagnostics(scope: "workspace", maxResults: 5000);

        var userLimited = Assert.IsType<DiagnosticsResult>(userLimitedResult);
        Assert.Equal(1000, userLimited.TotalKnown);
        Assert.Equal(2, userLimited.Returned);
        Assert.True(userLimited.Truncated);

        var hardCapped = Assert.IsType<DiagnosticsResult>(hardCappedResult);
        Assert.Equal(1000, hardCapped.TotalKnown);
        Assert.Equal(1000, hardCapped.Returned);
        Assert.False(hardCapped.Truncated);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task SeverityFilter_IsAppliedByDiagnosticsTool()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "class C { }");
        var (session, tools, store) = CreateLoadedTools(root.Path, new FakeLspClient());
        store.TryUpdateFromPublishDiagnostics(
            Publish(
                root.Path,
                "Program.cs",
                Diagnostic("error", DiagnosticSeverity.Error),
                Diagnostic("warning", DiagnosticSeverity.Warning)));

        var result = await tools.Diagnostics(file: "Program.cs", severity: "warning");

        var diagnostics = Assert.IsType<DiagnosticsResult>(result);
        var item = Assert.Single(diagnostics.Items);
        Assert.Equal("warning", item.Severity);
        Assert.Equal("warning", item.Message);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task WorkspaceStatus_ReturnsOpenDocumentsAndDiagnosticMetadata()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "class C { }");
        var (session, tools, store) = CreateLoadedTools(root.Path, new FakeLspClient());
        store.TryUpdateFromPublishDiagnostics(Publish(root.Path, "Program.cs", Diagnostic("boom", DiagnosticSeverity.Error)));

        await tools.Diagnostics(file: "Program.cs");
        var status = await session.GetStatusAsync();

        Assert.Equal(1, status.OpenDocumentCount);
        Assert.Equal(1, status.KnownDiagnosticsFileCount);
        Assert.NotNull(status.LastDiagnosticUpdateAt);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task Diagnostics_ReturnsWorkspaceLoadingWhileLanguageServerStarts()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var loader = new BlockingLoader();
        var session = CreateSession(root.Path, loader, CreateDocumentState(root.Path), CreateStore(root.Path));
        var tools = CreateTools(root.Path, session, CreateDocumentState(root.Path), CreateStore(root.Path));

        var loadTask = session.LoadProjectAsync("App.csproj");
        await loader.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var result = await tools.Diagnostics(scope: "workspace");

        var error = Assert.IsType<ToolError>(result);
        Assert.Equal("workspace_loading", error.Error);
        Assert.Equal(WorkspaceLoadState.StartingLanguageServer.ToString(), error.WorkspaceState);

        loader.Release.SetResult();
        await loadTask.WaitAsync(TimeSpan.FromSeconds(2));
        await session.DisposeAsync();
    }

    [Fact]
    public async Task WorkspaceDiagnosticsWhileWarming_ReturnsPartialMetadata()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var (session, tools, _) = CreateLoadedTools(root.Path, new FakeLspClient());

        var result = await tools.Diagnostics(scope: "workspace");

        var diagnostics = Assert.IsType<DiagnosticsResult>(result);
        Assert.Equal(WorkspaceLoadState.WorkspaceWarming.ToString(), diagnostics.WorkspaceState);
        Assert.Equal("partial", diagnostics.Completeness);
        Assert.Equal(ToolRetryHints.WorkspaceWarmingMs, diagnostics.RetryAfterMs);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task PublishDiagnosticsNotificationThroughSession_UpdatesStore()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "class C { }");
        var client = new FakeLspClient();
        var (session, tools, store) = CreateLoadedTools(root.Path, client);

        client.RaiseNotification("textDocument/publishDiagnostics", Publish(root.Path, "Program.cs", Diagnostic("boom", DiagnosticSeverity.Error)));
        var result = await tools.Diagnostics(file: "Program.cs");

        Assert.Equal(1, store.KnownFileCount);
        var diagnostics = Assert.IsType<DiagnosticsResult>(result);
        Assert.Single(diagnostics.Items);
        await session.DisposeAsync();
    }

    private static (WorkspaceSession Session, DiagnosticsTools Tools, DiagnosticStore Store) CreateLoadedTools(
        string root,
        FakeLspClient client)
    {
        var documents = CreateDocumentState(root);
        var store = CreateStore(root);
        var session = CreateSession(root, new ImmediateLoader(client), documents, store);
        session.LoadProjectAsync("App.csproj").GetAwaiter().GetResult();
        return (session, CreateTools(root, session, documents, store), store);
    }

    private static DiagnosticsTools CreateTools(
        string root,
        WorkspaceSession session,
        DocumentStateManager documents,
        DiagnosticStore store)
    {
        var mapper = new DocumentPathMapper(new PathGuard(root));
        return new DiagnosticsTools(session, documents, mapper, store);
    }

    private static WorkspaceSession CreateSession(
        string root,
        IRoslynWorkspaceLoader loader,
        DocumentStateManager documents,
        DiagnosticStore store)
    {
        var guard = new PathGuard(root);
        return new WorkspaceSession(new WorkspaceScanner(CreateOptions(root), guard, gitScanner: null), guard, loader, documents, store);
    }

    private static DocumentStateManager CreateDocumentState(string root)
    {
        var guard = new PathGuard(root);
        return new DocumentStateManager(CreateOptions(root), new DocumentPathMapper(guard));
    }

    private static DiagnosticStore CreateStore(string root)
    {
        var guard = new PathGuard(root);
        return new DiagnosticStore(new DocumentPathMapper(guard), new FakeClock());
    }

    private static CliOptions CreateOptions(string root) =>
        new(
            root,
            null,
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

    private static JsonElement Publish(string root, string file, params object[] diagnostics) =>
        JsonSerializer.SerializeToElement(new
        {
            uri = new Uri(Path.Combine(root, file)).AbsoluteUri,
            diagnostics
        }, JsonOptions.Default);

    private static object Diagnostic(string message, DiagnosticSeverity severity) =>
        new
        {
            range = new Lsp.Range(new Position(1, 2), new Position(1, 5)),
            severity = (int)severity,
            code = "CS0001",
            source = "csharp",
            message
        };

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 5, 17, 1, 0, 0, TimeSpan.Zero);
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
            return new RoslynWorkspaceHandle(target, new FakeLspClient());
        }
    }

    private sealed class FakeLspClient : ILspClient
    {
        public event Action<string, JsonElement?>? NotificationReceived;

        public List<(string Method, JsonElement Params)> Notifications { get; } = [];
        public List<string> Events { get; } = [];
        public int PendingRequestCount => 0;

        public Task<JsonElement> RequestAsync(
            string method,
            object? parameters,
            TimeSpan timeout,
            CancellationToken cancellationToken,
            bool isExpensive = false) =>
            throw new NotSupportedException();

        public Task NotifyAsync(string method, object? parameters, CancellationToken cancellationToken)
        {
            Events.Add($"notify:{method}");
            Notifications.Add((method, JsonSerializer.SerializeToElement(parameters, JsonOptions.Default)));
            return Task.CompletedTask;
        }

        public Task ShutdownAsync(TimeSpan timeout, CancellationToken cancellationToken) => Task.CompletedTask;

        public void RaiseNotification(string method, JsonElement? parameters = null) =>
            NotificationReceived?.Invoke(method, parameters);
    }
}
