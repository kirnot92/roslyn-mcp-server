using System.Text.Json;
using Microsoft.Extensions.Logging;
using RoslynMcpServer.Cli;
using RoslynMcpServer.Lsp;
using RoslynMcpServer.Mcp;
using RoslynMcpServer.Workspace;
using Lsp = RoslynMcpServer.Lsp;

namespace RoslynMcpServer.Tests;

public sealed partial class NavigationToolsTests
{
    private static async Task<object> ExecuteHoverWithResponse(object? response)
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "class C { }");
        var client = new FakeLspClient();
        client.EnqueueResponse(response);
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        return await tools.Hover("Program.cs", line: 1, column: 1);
    }

    private static async Task<object> ExecuteDefinitionWithResponse(object? response)
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "class C { }");
        var client = new FakeLspClient();
        client.EnqueueResponse(response);
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        return await tools.GoToDefinition("Program.cs", line: 1, column: 1);
    }

    private static async Task<object> ExecuteImplementationsWithResponse(object? response)
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "interface I { }");
        var client = new FakeLspClient();
        client.EnqueueResponse(response);
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        return await tools.FindImplementations("Program.cs", line: 1, column: 1);
    }

    private static async Task<object> ExecuteFindSymbolsWithResponse(object? response)
    {
        var (result, _) = await ExecuteFindSymbolsRequestAsync(response);
        return result;
    }

    private static async Task<(object Result, FakeLspClient Client)> ExecuteFindSymbolsRequestAsync(
        object? response,
        string query = "Symbol",
        int? maxResults = null,
        string[]? kindFilter = null,
        string? matchMode = null,
        string[]? includePathPrefixes = null)
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var client = new FakeLspClient();
        client.EnqueueResponse(response);
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.FindSymbols(query, maxResults, kindFilter, matchMode, includePathPrefixes);
        return (result, client);
    }

    private static string CreateFileUri(string root, string relativePath) =>
        new Uri(Path.Combine(root, relativePath)).AbsoluteUri;

    private static Lsp.Range CreateRange(int startLine, int startColumn, int endLine, int endColumn) =>
        new(new Position(startLine, startColumn), new Position(endLine, endColumn));

    private static object CreateCallHierarchyItem(
        string name,
        string uri,
        SymbolKind kind = SymbolKind.Method,
        Lsp.Range? range = null,
        string? detail = null)
    {
        var effectiveRange = range ?? CreateRange(0, 0, 0, 1);
        return new
        {
            name,
            kind = (int)kind,
            detail,
            uri,
            range = effectiveRange,
            selectionRange = effectiveRange
        };
    }

    private static object CreateTypeHierarchyItem(
        string name,
        string uri,
        SymbolKind kind = SymbolKind.Class,
        Lsp.Range? range = null,
        string? detail = null)
    {
        var effectiveRange = range ?? CreateRange(0, 0, 0, 1);
        return new
        {
            name,
            kind = (int)kind,
            detail,
            uri,
            range = effectiveRange,
            selectionRange = effectiveRange
        };
    }

    private static object CreateIncomingCall(object from, params Lsp.Range[] fromRanges) =>
        new
        {
            from,
            fromRanges
        };

    private static object CreateOutgoingCall(object to, params Lsp.Range[] fromRanges) =>
        new
        {
            to,
            fromRanges
        };

    private static Lsp.Range[] CreateCallSiteRanges(int count) =>
        Enumerable.Range(0, count)
            .Select(index => CreateRange(index, 0, index, 1))
            .ToArray();

    private static object[] CreateWorkspaceSymbols(int count) =>
        Enumerable.Range(0, count)
            .Select(index => new
            {
                name = $"Symbol{index}",
                kind = (int)SymbolKind.Class
            })
            .Cast<object>()
            .ToArray();

    private static object CreateWorkspaceSymbol(string name, SymbolKind kind) =>
        new
        {
            name,
            kind = (int)kind
        };

    private static object CreateWorkspaceSymbol(string root, string relativePath, string name, SymbolKind kind) =>
        new
        {
            name,
            kind = (int)kind,
            location = new Lsp.Location(
                CreateFileUri(root, relativePath),
                new Lsp.Range(new Position(0, 0), new Position(0, 1)))
        };

    private static Lsp.Location[] CreateImplementationLocations(string root, int count)
    {
        var uri = new Uri(Path.Combine(root, "Program.cs")).AbsoluteUri;
        return Enumerable.Range(0, count)
            .Select(index => new Lsp.Location(
                uri,
                new Lsp.Range(new Position(index, 0), new Position(index, 1))))
            .ToArray();
    }

    private static NavigationTools CreateTools(string root, WorkspaceSession session, long maxDocumentBytes = 2 * 1024 * 1024)
    {
        var workspaceRoot = new WorkspaceRoot(root);
        return new NavigationTools(session, new OpenDocumentManager(CreateOptions(root, maxDocumentBytes: maxDocumentBytes), workspaceRoot), workspaceRoot);
    }

    private static WorkspaceSession CreateSession(string root, IRoslynWorkspaceLoader loader)
    {
        var workspaceRoot = new WorkspaceRoot(root);
        return WorkspaceSession.CreateForTest(
            new WorkspaceScanner(CreateOptions(root), workspaceRoot, gitScanner: null),
            workspaceRoot,
            loader);
    }

    private static WorkspaceSession CreateStartupLoadSession(
        string root,
        IRoslynWorkspaceLoader loader,
        string loadSolutionPath)
    {
        var workspaceRoot = new WorkspaceRoot(root);
        var options = CreateOptions(root) with { LoadSolutionPath = loadSolutionPath };
        return WorkspaceSession.CreateForTest(
            new WorkspaceScanner(options, workspaceRoot, gitScanner: null),
            workspaceRoot,
            loader,
            options);
    }

    private static CliOptions CreateOptions(string root, long maxDocumentBytes = 2 * 1024 * 1024) =>
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
            maxDocumentBytes,
            16,
            2);

    private sealed class ImmediateLoader(ILspClient client) : IRoslynWorkspaceLoader
    {
        public Task<RoslynWorkspaceHandle> LoadAsync(WorkspaceTarget target, CancellationToken cancellationToken) =>
            Task.FromResult(new RoslynWorkspaceHandle(target, client));
    }

    private sealed class SequentialLoader(params ILspClient[] clients) : IRoslynWorkspaceLoader
    {
        private readonly Queue<ILspClient> remainingClients = new(clients);

        public Task<RoslynWorkspaceHandle> LoadAsync(WorkspaceTarget target, CancellationToken cancellationToken) =>
            Task.FromResult(new RoslynWorkspaceHandle(target, this.remainingClients.Dequeue()));
    }

    private sealed class BlockingLoader : IRoslynWorkspaceLoader
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<RoslynWorkspaceHandle> LoadAsync(WorkspaceTarget target, CancellationToken cancellationToken)
        {
            Started.SetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new RoslynWorkspaceHandle(target, new FakeLspClient());
        }
    }

    private sealed class ThrowingLoader : IRoslynWorkspaceLoader
    {
        public Task<RoslynWorkspaceHandle> LoadAsync(WorkspaceTarget target, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Loader should not be called.");
    }

    private sealed class FakeLspClient : ILspClient
    {
        private readonly Queue<JsonElement> responses = new();

        public event Action<string, JsonElement?>? NotificationReceived;

        public List<(string Method, JsonElement Params)> Notifications { get; } = [];
        public List<(string Method, JsonElement Params, TimeSpan Timeout, bool IsExpensive)> Requests { get; } = [];
        public List<string> Events { get; } = [];
        public int PendingRequestCount => 0;
        public TaskCompletionSource? ShutdownStarted { get; init; }
        public TaskCompletionSource? ReleaseShutdown { get; init; }

        public void EnqueueResponse(object? response) =>
            this.responses.Enqueue(JsonSerializer.SerializeToElement(response, JsonOptions.Default));

        public Task<JsonElement> RequestAsync(
            string method,
            object? parameters,
            TimeSpan timeout,
            CancellationToken cancellationToken,
            bool isExpensive = false)
        {
            Events.Add($"request:{method}");
            Requests.Add((method, JsonSerializer.SerializeToElement(parameters, JsonOptions.Default), timeout, isExpensive));
            return Task.FromResult(this.responses.Dequeue());
        }

        public Task NotifyAsync(string method, object? parameters, CancellationToken cancellationToken)
        {
            Events.Add($"notify:{method}");
            Notifications.Add((method, JsonSerializer.SerializeToElement(parameters, JsonOptions.Default)));
            return Task.CompletedTask;
        }

        public async Task ShutdownAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            ShutdownStarted?.SetResult();
            if (ReleaseShutdown is not null)
            {
                await ReleaseShutdown.Task.WaitAsync(timeout, cancellationToken);
            }
        }

        public void RaiseNotification(string method, JsonElement? parameters = null) =>
            NotificationReceived?.Invoke(method, parameters);
    }
}
