using System.Text.Json;
using RoslynMcpServer.Infrastructure;
using RoslynMcpServer.Lsp;
using RoslynMcpServer.Workspace;
using Lsp = RoslynMcpServer.Lsp;

namespace RoslynMcpServer.Tests;

public sealed class DiagnosticStoreTests
{
    [Fact]
    public void PublishDiagnosticsNotification_IsStored()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "class C { }");
        var clock = new FakeClock(new DateTimeOffset(2026, 5, 17, 1, 0, 0, TimeSpan.Zero));
        var store = CreateStore(root.Path, clock);

        var updated = store.TryUpdateFromPublishDiagnostics(Publish(root.Path, "Program.cs", Diagnostic("boom", DiagnosticSeverity.Error)));

        Assert.True(updated);
        var snapshot = store.GetFile("Program.cs", severity: null);
        Assert.NotNull(snapshot);
        Assert.Equal(clock.UtcNow, snapshot.LastUpdatedAt);
        var diagnostic = Assert.Single(snapshot.Diagnostics);
        Assert.Equal("boom", diagnostic.Message);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
    }

    [Fact]
    public void SameFileDiagnostics_AreReplacedByNewNotification()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "class C { }");
        var store = CreateStore(root.Path);

        store.TryUpdateFromPublishDiagnostics(Publish(root.Path, "Program.cs", Diagnostic("old", DiagnosticSeverity.Error)));
        store.TryUpdateFromPublishDiagnostics(Publish(root.Path, "Program.cs", Diagnostic("new", DiagnosticSeverity.Warning)));

        var diagnostic = Assert.Single(store.GetFile("Program.cs", severity: null)!.Diagnostics);
        Assert.Equal("new", diagnostic.Message);
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
    }

    [Fact]
    public void EmptyDiagnosticsNotification_ClearsExistingDiagnostics()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "class C { }");
        var store = CreateStore(root.Path);

        store.TryUpdateFromPublishDiagnostics(Publish(root.Path, "Program.cs", Diagnostic("old", DiagnosticSeverity.Error)));
        store.TryUpdateFromPublishDiagnostics(Publish(root.Path, "Program.cs"));

        var snapshot = store.GetFile("Program.cs", severity: null);
        Assert.NotNull(snapshot);
        Assert.Empty(snapshot.Diagnostics);
        Assert.Equal(1, store.KnownFileCount);
    }

    [Fact]
    public void SeverityFilter_IsApplied()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "class C { }");
        var store = CreateStore(root.Path);

        store.TryUpdateFromPublishDiagnostics(
            Publish(
                root.Path,
                "Program.cs",
                Diagnostic("error", DiagnosticSeverity.Error),
                Diagnostic("warning", DiagnosticSeverity.Warning)));

        var snapshot = store.GetFile("Program.cs", DiagnosticSeverity.Warning);

        var diagnostic = Assert.Single(snapshot!.Diagnostics);
        Assert.Equal("warning", diagnostic.Message);
    }

    [Fact]
    public void PerFileDiagnosticCap_PreservesTotalKnownAndTruncationMetadata()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "class C { }");
        var store = CreateStore(root.Path);
        var diagnostics = Enumerable
            .Range(0, DiagnosticStore.DefaultMaxDiagnosticsPerFile + 1)
            .Select(i => Diagnostic($"diag{i}", DiagnosticSeverity.Error))
            .ToArray();

        store.TryUpdateFromPublishDiagnostics(Publish(root.Path, "Program.cs", diagnostics));

        var snapshot = store.GetFile("Program.cs", severity: null);
        Assert.NotNull(snapshot);
        Assert.Equal(DiagnosticStore.DefaultMaxDiagnosticsPerFile + 1, snapshot.TotalKnown);
        Assert.Equal(DiagnosticStore.DefaultMaxDiagnosticsPerFile, snapshot.Diagnostics.Count);
        Assert.True(snapshot.Truncated);
    }

    [Fact]
    public void MalformedDiagnosticElements_AreInspectedOnlyUpToCap()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "class C { }");
        var store = CreateStore(root.Path);
        var invalidDiagnostics = Enumerable
            .Range(0, DiagnosticStore.DefaultMaxDiagnosticsInspectedPerPublish + 1)
            .Select(_ => new { invalid = true })
            .Cast<object>()
            .ToArray();

        var updated = store.TryUpdateFromPublishDiagnostics(Publish(root.Path, "Program.cs", invalidDiagnostics));

        Assert.True(updated);
        var snapshot = store.GetFile("Program.cs", severity: null);
        Assert.NotNull(snapshot);
        Assert.Equal(0, snapshot.TotalKnown);
        Assert.Empty(snapshot.Diagnostics);
        Assert.True(snapshot.Truncated);
    }

    [Fact]
    public void CacheEvictsOldestEntriesWhenFileLimitIsExceeded()
    {
        using var root = TestRoot.Create();
        var clock = new FakeClock(new DateTimeOffset(2026, 5, 17, 1, 0, 0, TimeSpan.Zero));
        var store = CreateStore(root.Path, clock);

        for (var i = 0; i < DiagnosticStore.DefaultMaxDiagnosticFiles + 1; i++)
        {
            var file = $"File{i}.cs";
            File.WriteAllText(Path.Combine(root.Path, file), "class C { }");
            store.TryUpdateFromPublishDiagnostics(Publish(root.Path, file, Diagnostic($"diag{i}", DiagnosticSeverity.Error)));
            clock.Advance(TimeSpan.FromMilliseconds(1));
        }

        Assert.Equal(DiagnosticStore.DefaultMaxDiagnosticFiles, store.KnownFileCount);
        Assert.Null(store.GetFile("File0.cs", severity: null));
        Assert.NotNull(store.GetFile("File1.cs", severity: null));
    }

    [Fact]
    public void SummaryCountsReflectRetainedDiagnostics()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "class C { }");
        var store = CreateStore(root.Path);

        store.TryUpdateFromPublishDiagnostics(
            Publish(
                root.Path,
                "Program.cs",
                Diagnostic("error", DiagnosticSeverity.Error),
                Diagnostic("warning", DiagnosticSeverity.Warning),
                Diagnostic("info", DiagnosticSeverity.Information),
                Diagnostic("hint", DiagnosticSeverity.Hint),
                DiagnosticWithoutSeverity("unknown")));

        var counts = store.GetSeverityCounts();

        Assert.Equal(1, counts.Error);
        Assert.Equal(1, counts.Warning);
        Assert.Equal(1, counts.Information);
        Assert.Equal(1, counts.Hint);
        Assert.Equal(1, counts.Unknown);
    }

    [Fact]
    public void RootOutsideAndNonFileUriDiagnostics_AreIgnored()
    {
        using var root = TestRoot.Create();
        using var outside = TestRoot.Create();
        File.WriteAllText(Path.Combine(outside.Path, "Outside.cs"), "class C { }");
        var store = CreateStore(root.Path);

        var outsideUpdated = store.TryUpdateFromPublishDiagnostics(PublishUri(new Uri(Path.Combine(outside.Path, "Outside.cs")).AbsoluteUri, Diagnostic("outside", DiagnosticSeverity.Error)));
        var nonFileUpdated = store.TryUpdateFromPublishDiagnostics(PublishUri("https://example.test/Program.cs", Diagnostic("remote", DiagnosticSeverity.Error)));

        Assert.False(outsideUpdated);
        Assert.False(nonFileUpdated);
        Assert.Equal(0, store.KnownFileCount);
    }

    [Fact]
    public void UnsupportedUserFacingPathDiagnostics_AreIgnored()
    {
        using var root = TestRoot.Create();
        using var outside = TestRoot.Create();
        File.WriteAllText(Path.Combine(outside.Path, "Outside.cs"), "class C { }");
        var linkPath = Path.Combine(root.Path, "link");

        try
        {
            Directory.CreateSymbolicLink(linkPath, outside.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }

        var store = CreateStore(root.Path);
        var updated = store.TryUpdateFromPublishDiagnostics(
            PublishUri(new Uri(Path.Combine(linkPath, "Outside.cs")).AbsoluteUri, Diagnostic("outside", DiagnosticSeverity.Error)));

        Assert.False(updated);
        Assert.Equal(0, store.KnownFileCount);
    }

    [Fact]
    public void MalformedDiagnosticsPayload_DoesNotThrowRawException()
    {
        using var root = TestRoot.Create();
        var store = CreateStore(root.Path);
        var malformed = JsonSerializer.SerializeToElement(new
        {
            uri = 123,
            diagnostics = "not diagnostics"
        }, JsonOptions.Default);

        var updated = store.TryUpdateFromPublishDiagnostics(malformed);

        Assert.False(updated);
        Assert.Equal(0, store.KnownFileCount);
    }

    [Fact]
    public void PublishStorm_DoesNotExceedCacheEntryLimit()
    {
        using var root = TestRoot.Create();
        var store = CreateStore(root.Path);

        for (var i = 0; i < DiagnosticStore.DefaultMaxDiagnosticFiles + 50; i++)
        {
            var file = $"Storm{i}.cs";
            File.WriteAllText(Path.Combine(root.Path, file), "class C { }");
            store.TryUpdateFromPublishDiagnostics(Publish(root.Path, file, Diagnostic("diag", DiagnosticSeverity.Warning)));
        }

        Assert.Equal(DiagnosticStore.DefaultMaxDiagnosticFiles, store.KnownFileCount);
    }

    private static DiagnosticStore CreateStore(string root) =>
        CreateStore(root, new FakeClock(DateTimeOffset.UtcNow));

    private static DiagnosticStore CreateStore(string root, IClock clock) =>
        new(new WorkspaceRoot(root), clock);

    private static JsonElement Publish(string root, string file, params object[] diagnostics) =>
        PublishUri(new Uri(Path.Combine(root, file)).AbsoluteUri, diagnostics);

    private static JsonElement PublishUri(string uri, params object[] diagnostics) =>
        JsonSerializer.SerializeToElement(new
        {
            uri,
            diagnostics
        }, JsonOptions.Default);

    private static object Diagnostic(string message, DiagnosticSeverity severity) =>
        new
        {
            range = new Lsp.Range(new Position(0, 1), new Position(0, 4)),
            severity = (int)severity,
            code = "CS0001",
            source = "csharp",
            message
        };

    private static object DiagnosticWithoutSeverity(string message) =>
        new
        {
            range = new Lsp.Range(new Position(0, 1), new Position(0, 4)),
            message
        };

    private sealed class FakeClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = utcNow;

        public void Advance(TimeSpan amount) => UtcNow += amount;
    }
}
