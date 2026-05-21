using System.Text.Json;
using Microsoft.Extensions.Logging;
using RoslynMcpServer.Cli;
using RoslynMcpServer.Infrastructure;
using RoslynMcpServer.Lsp;
using RoslynMcpServer.Workspace;

namespace RoslynMcpServer.Tests;

public sealed class DocumentStateManagerTests
{
    [Fact]
    public async Task EnsureOpenAsync_SendsDidOpenOnFirstAccess()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "class C { }");
        var client = new FakeLspClient();
        var manager = CreateManager(root.Path);

        await manager.EnsureOpenAsync("Program.cs", client);

        var notification = Assert.Single(client.Notifications);
        Assert.Equal("textDocument/didOpen", notification.Method);
        Assert.Equal("csharp", notification.Params.GetProperty("textDocument").GetProperty("languageId").GetString());
        Assert.Equal("class C { }", notification.Params.GetProperty("textDocument").GetProperty("text").GetString());
    }

    [Fact]
    public async Task EnsureOpenAsync_SendsDidChangeWhenTimestampOrLengthChanges()
    {
        using var root = TestRoot.Create();
        var file = Path.Combine(root.Path, "Program.cs");
        File.WriteAllText(file, "class C { }");
        var client = new FakeLspClient();
        var manager = CreateManager(root.Path);

        await manager.EnsureOpenAsync("Program.cs", client);
        File.WriteAllText(file, "class C { void M() { } }");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(1));
        await manager.EnsureOpenAsync("Program.cs", client);

        Assert.Equal(["textDocument/didOpen", "textDocument/didChange"], client.Notifications.Select(n => n.Method));
        var change = client.Notifications[1].Params;
        Assert.Equal(2, change.GetProperty("textDocument").GetProperty("version").GetInt32());
        Assert.Contains("void M", change.GetProperty("contentChanges")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task EnsureOpenAsync_ClosesLeastRecentlyUsedDocumentWhenLimitIsExceeded()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "One.cs"), "class One { }");
        File.WriteAllText(Path.Combine(root.Path, "Two.cs"), "class Two { }");
        var client = new FakeLspClient();
        var manager = CreateManager(root.Path, maxOpenDocuments: 1);

        await manager.EnsureOpenAsync("One.cs", client);
        await manager.EnsureOpenAsync("Two.cs", client);

        Assert.Equal(["textDocument/didOpen", "textDocument/didOpen", "textDocument/didClose"], client.Notifications.Select(n => n.Method));
        Assert.EndsWith("/One.cs", client.Notifications[2].Params.GetProperty("textDocument").GetProperty("uri").GetString());
    }

    [Fact]
    public async Task EnsureOpenAsync_RejectsFilesOverMaxDocumentBytes()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "Large.cs"), "123456");
        var manager = CreateManager(root.Path, maxDocumentBytes: 5);

        var ex = await Assert.ThrowsAsync<UserFacingException>(() =>
            manager.EnsureOpenAsync("Large.cs", new FakeLspClient()));

        Assert.Equal("document_too_large", ex.Code);
    }

    [Fact]
    public async Task EnsureOpenAsync_TracksLineLengthsForCrLfLfAndTrailingNewline()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "alpha\r\n\r\nomega\n");
        var manager = CreateManager(root.Path);

        var state = await manager.EnsureOpenAsync("Program.cs", new FakeLspClient());

        Assert.Equal(4, state.LineCount);
        Assert.Equal(new[] { 5, 0, 5, 0 }, state.LineMap.LineLengths);
    }

    [Fact]
    public async Task EnsureOpenAsync_DoesNotOpenSameWindowsPathCasingTwice()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "class C { }");
        var client = new FakeLspClient();
        var manager = CreateManager(root.Path);

        await manager.EnsureOpenAsync("Program.cs", client);
        await manager.EnsureOpenAsync(OperatingSystem.IsWindows() ? "program.cs" : "Program.cs", client);

        Assert.Single(client.Notifications);
        Assert.Equal("textDocument/didOpen", client.Notifications[0].Method);
    }

    private static DocumentStateManager CreateManager(
        string root,
        int maxOpenDocuments = 200,
        long maxDocumentBytes = 2 * 1024 * 1024)
    {
        var options = new CliOptions(
            root,
            null,
            null,
            LogLevel.Information,
            null,
            null,
            TimeSpan.FromSeconds(60),
            100,
            500,
            maxOpenDocuments,
            maxDocumentBytes,
            16,
            2);
        var workspaceRoot = new WorkspaceRoot(root);
        return new DocumentStateManager(options, workspaceRoot);
    }

    private sealed class FakeLspClient : ILspClient
    {
        public event Action<string, JsonElement?>? NotificationReceived;

        public List<(string Method, JsonElement Params)> Notifications { get; } = [];

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
            Notifications.Add((method, JsonSerializer.SerializeToElement(parameters, JsonOptions.Default)));
            return Task.CompletedTask;
        }

        public Task ShutdownAsync(TimeSpan timeout, CancellationToken cancellationToken) => Task.CompletedTask;

        public void RaiseNotification(string method, JsonElement? parameters = null) =>
            NotificationReceived?.Invoke(method, parameters);
    }
}
