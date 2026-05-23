using System.Text.Json;
using RoslynMcpServer.Infrastructure;
using RoslynMcpServer.Lsp;
using RoslynMcpServer.Workspace;
using Lsp = RoslynMcpServer.Lsp;

namespace RoslynMcpServer.Tests;

public sealed class DiagnosticNotificationProcessorTests
{
    [Fact]
    public async Task BackgroundProcessor_ProcessesQueuedDiagnostics()
    {
        using var root = TestRoot.Create();
        var store = CreateStore(root.Path);
        await using var processor = DiagnosticNotificationProcessor.CreateForServer(store);

        Assert.True(processor.Enqueue(processor.CurrentGeneration, Publish(root.Path, "Program.cs", Diagnostic("boom", DiagnosticSeverity.Error))));
        await WaitForConditionAsync(() => store.KnownFileCount == 1);

        var snapshot = store.GetFile("Program.cs", severity: null);
        Assert.NotNull(snapshot);
        Assert.Equal("boom", Assert.Single(snapshot.Diagnostics).Message);
        Assert.Equal(0, processor.Statistics.Pending);
        Assert.True(processor.Statistics.Processed >= 1);
    }

    [Fact]
    public async Task QueueOverflow_DropsNewestNotificationAndReportsDroppedCount()
    {
        using var root = TestRoot.Create();
        var store = CreateStore(root.Path);
        await using var processor = DiagnosticNotificationProcessor.CreateForTest(store, capacity: 1, startAutomatically: false);

        Assert.True(processor.Enqueue(processor.CurrentGeneration, Publish(root.Path, "A.cs", Diagnostic("first", DiagnosticSeverity.Error))));
        Assert.False(processor.Enqueue(processor.CurrentGeneration, Publish(root.Path, "B.cs", Diagnostic("latest", DiagnosticSeverity.Warning))));

        Assert.Equal(1, processor.Statistics.Pending);
        Assert.Equal(1, processor.Statistics.Dropped);
        Assert.Equal(DiagnosticNotificationProcessor.DropNewestWhenFullPolicy, processor.Statistics.OverflowPolicy);

        processor.Start();
        await WaitForConditionAsync(() => store.KnownFileCount == 1);

        Assert.NotNull(store.GetFile("A.cs", severity: null));
        Assert.Null(store.GetFile("B.cs", severity: null));
    }

    [Fact]
    public async Task QueueOverflow_WhenRepeated_KeepsOldestPendingNotifications()
    {
        using var root = TestRoot.Create();
        var store = CreateStore(root.Path);
        await using var processor = DiagnosticNotificationProcessor.CreateForTest(store, capacity: 2, startAutomatically: false);

        Assert.True(processor.Enqueue(processor.CurrentGeneration, Publish(root.Path, "A.cs", Diagnostic("first", DiagnosticSeverity.Error))));
        Assert.True(processor.Enqueue(processor.CurrentGeneration, Publish(root.Path, "B.cs", Diagnostic("second", DiagnosticSeverity.Warning))));
        Assert.False(processor.Enqueue(processor.CurrentGeneration, Publish(root.Path, "C.cs", Diagnostic("third", DiagnosticSeverity.Information))));
        Assert.False(processor.Enqueue(processor.CurrentGeneration, Publish(root.Path, "D.cs", Diagnostic("fourth", DiagnosticSeverity.Hint))));

        Assert.Equal(2, processor.Statistics.Pending);
        Assert.Equal(2, processor.Statistics.Dropped);

        processor.Start();
        await WaitForConditionAsync(() => store.KnownFileCount == 2);

        Assert.NotNull(store.GetFile("A.cs", severity: null));
        Assert.NotNull(store.GetFile("B.cs", severity: null));
        Assert.Null(store.GetFile("C.cs", severity: null));
        Assert.Null(store.GetFile("D.cs", severity: null));
    }

    [Fact]
    public async Task ResetForNewWorkspace_DiscardsQueuedDiagnosticsFromPreviousGeneration()
    {
        using var root = TestRoot.Create();
        var store = CreateStore(root.Path);
        await using var processor = DiagnosticNotificationProcessor.CreateForTest(store, capacity: 10, startAutomatically: false);

        var oldGeneration = processor.CurrentGeneration;
        Assert.True(processor.Enqueue(oldGeneration, Publish(root.Path, "Old.cs", Diagnostic("old", DiagnosticSeverity.Error))));
        processor.ResetForNewWorkspace();
        processor.Start();
        await Task.Delay(50);

        Assert.Equal(0, store.KnownFileCount);
        Assert.True(processor.Statistics.Stale >= 1);
        Assert.Equal(0, processor.Statistics.Dropped);

        Assert.True(processor.Enqueue(processor.CurrentGeneration, Publish(root.Path, "New.cs", Diagnostic("new", DiagnosticSeverity.Warning))));
        await WaitForConditionAsync(() => store.KnownFileCount == 1);

        Assert.Null(store.GetFile("Old.cs", severity: null));
        Assert.NotNull(store.GetFile("New.cs", severity: null));
    }

    [Fact]
    public async Task StaleIncomingNotification_IsNotEnqueuedOrDropped()
    {
        using var root = TestRoot.Create();
        var store = CreateStore(root.Path);
        var processor = DiagnosticNotificationProcessor.CreateForTest(store, capacity: 1, startAutomatically: false);

        try
        {
            var oldGeneration = processor.CurrentGeneration;
            processor.ResetForNewWorkspace();

            Assert.False(processor.Enqueue(oldGeneration, Publish(root.Path, "Old.cs", Diagnostic("old", DiagnosticSeverity.Error))));

            Assert.Equal(0, processor.Statistics.Pending);
            Assert.Equal(0, processor.Statistics.Dropped);
            Assert.True(processor.Statistics.Stale >= 1);
            Assert.Equal(0, store.KnownFileCount);
        }
        finally
        {
            await processor.DisposeAsync();
        }
    }

    [Fact]
    public async Task Dispose_CompletesWithoutHanging()
    {
        using var root = TestRoot.Create();
        var processor = DiagnosticNotificationProcessor.CreateForServer(CreateStore(root.Path));

        Assert.True(processor.Enqueue(processor.CurrentGeneration, Publish(root.Path, "Program.cs", Diagnostic("boom", DiagnosticSeverity.Error))));
        await processor.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static DiagnosticStore CreateStore(string root)
    {
        var workspaceRoot = new WorkspaceRoot(root);
        return new DiagnosticStore(workspaceRoot, new FakeClock());
    }

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

    private static async Task WaitForConditionAsync(Func<bool> predicate)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (predicate())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.True(predicate());
    }

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 5, 17, 1, 0, 0, TimeSpan.Zero);
    }
}
