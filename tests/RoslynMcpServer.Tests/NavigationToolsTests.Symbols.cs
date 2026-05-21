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
    [Fact]
    public async Task DocumentSymbols_ReturnsWorkspaceLoadingWhileLanguageServerStarts()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var loader = new BlockingLoader();
        await using var session = CreateSession(root.Path, loader);
        var tools = CreateTools(root.Path, session);

        var loadTask = session.LoadProjectAsync("App.csproj");
        await loader.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var result = await tools.DocumentSymbols("Program.cs");

        var error = Assert.IsType<ToolError>(result);
        Assert.Equal("workspace_loading", error.Error);
        Assert.Equal(WorkspaceLoadState.StartingLanguageServer.ToString(), error.WorkspaceState);

        loader.Release.SetResult();
        await loadTask.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task DocumentSymbols_ReturnsWorkspaceLoadingWhileExistingLanguageServerShutsDownForReload()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Other.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "class C { }");
        var oldClient = new FakeLspClient
        {
            ShutdownStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            ReleaseShutdown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var newClient = new FakeLspClient();
        await using var session = CreateSession(root.Path, new SequentialLoader(oldClient, newClient));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var reloadTask = session.LoadProjectAsync("Other.csproj");
        await oldClient.ShutdownStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var result = await tools.DocumentSymbols("Program.cs");

        var error = Assert.IsType<ToolError>(result);
        Assert.Equal("workspace_loading", error.Error);
        Assert.Empty(oldClient.Requests);

        oldClient.ReleaseShutdown.SetResult();
        await reloadTask.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task DocumentSymbols_ReturnsWorkspaceNotLoadedWhenMultipleSolutionsExist()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "One.sln"), string.Empty);
        File.WriteAllText(Path.Combine(root.Path, "Two.sln"), string.Empty);
        await using var session = CreateSession(root.Path, new ThrowingLoader());
        var tools = CreateTools(root.Path, session);

        var result = await tools.DocumentSymbols("Program.cs");

        var error = Assert.IsType<ToolError>(result);
        Assert.Equal("workspace_not_loaded", error.Error);
    }

    [Fact]
    public async Task DocumentSymbols_MapsFakeLspResponseAndIncludesWarmingMetadata()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "class C { void M() { } }");
        var client = new FakeLspClient();
        client.EnqueueResponse(new[]
        {
            new DocumentSymbol(
                "C",
                SymbolKind.Class,
                new Lsp.Range(new Position(0, 0), new Position(0, 26)),
                new Lsp.Range(new Position(0, 6), new Position(0, 7)),
                Children:
                [
                    new DocumentSymbol(
                        "M",
                        SymbolKind.Method,
                        new Lsp.Range(new Position(0, 10), new Position(0, 24)),
                        new Lsp.Range(new Position(0, 15), new Position(0, 16)))
                ])
        });
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.DocumentSymbols("Program.cs");

        var symbols = Assert.IsType<DocumentSymbolsResult>(result);
        Assert.Equal(WorkspaceLoadState.WorkspaceWarming.ToString(), symbols.WorkspaceState);
        Assert.Equal("partial", symbols.Completeness);
        Assert.Equal(ToolRetryHints.WorkspaceWarmingMs, symbols.RetryAfterMs);
        Assert.False(symbols.Truncated);
        Assert.Equal(2, symbols.TotalKnown);
        Assert.Equal(2, symbols.TotalUnfilteredKnown);
        Assert.Single(symbols.Items);
        Assert.Equal(1, symbols.Items[0].Range.StartLine);
        Assert.Equal(1, symbols.Items[0].Range.StartColumn);
        Assert.Equal(7, symbols.Items[0].SelectionRange.StartColumn);
        Assert.Equal(["textDocument/didOpen"], client.Notifications.Select(n => n.Method));
        var request = Assert.Single(client.Requests);
        Assert.Equal("textDocument/documentSymbol", request.Method);
        Assert.Equal(TimeSpan.FromSeconds(10), request.Timeout);
    }

    [Fact]
    public async Task DocumentSymbols_KindFilterRetainsAncestorsAndPrunesNonMatchingChildren()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "class C { void M() { } string P => \"\"; class Nested { void N() { } } }");
        var client = new FakeLspClient();
        client.EnqueueResponse(new[]
        {
            new DocumentSymbol(
                "C",
                SymbolKind.Class,
                new Lsp.Range(new Position(0, 0), new Position(0, 72)),
                new Lsp.Range(new Position(0, 6), new Position(0, 7)),
                Children:
                [
                    new DocumentSymbol(
                        "M",
                        SymbolKind.Method,
                        new Lsp.Range(new Position(0, 10), new Position(0, 24)),
                        new Lsp.Range(new Position(0, 15), new Position(0, 16))),
                    new DocumentSymbol(
                        "P",
                        SymbolKind.Property,
                        new Lsp.Range(new Position(0, 25), new Position(0, 39)),
                        new Lsp.Range(new Position(0, 32), new Position(0, 33))),
                    new DocumentSymbol(
                        "Nested",
                        SymbolKind.Class,
                        new Lsp.Range(new Position(0, 41), new Position(0, 70)),
                        new Lsp.Range(new Position(0, 47), new Position(0, 53)),
                        Children:
                        [
                            new DocumentSymbol(
                                "N",
                                SymbolKind.Method,
                                new Lsp.Range(new Position(0, 56), new Position(0, 68)),
                                new Lsp.Range(new Position(0, 61), new Position(0, 62)))
                        ])
                ])
        });
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.DocumentSymbols("Program.cs", kindFilter: ["method"]);

        var symbols = Assert.IsType<DocumentSymbolsResult>(result);
        Assert.False(symbols.Truncated);
        Assert.Equal(4, symbols.TotalKnown);
        Assert.Equal(5, symbols.TotalUnfilteredKnown);
        Assert.Equal(4, symbols.Returned);
        var rootSymbol = Assert.Single(symbols.Items);
        Assert.Equal("C", rootSymbol.Name);
        Assert.Equal(SymbolKind.Class, rootSymbol.Kind);
        Assert.Equal(["M", "Nested"], rootSymbol.Children.Select(child => child.Name));
        Assert.Equal(SymbolKind.Method, rootSymbol.Children[0].Kind);
        Assert.Equal(SymbolKind.Class, rootSymbol.Children[1].Kind);
        var nestedMethod = Assert.Single(rootSymbol.Children[1].Children);
        Assert.Equal("N", nestedMethod.Name);
        Assert.Equal(SymbolKind.Method, nestedMethod.Kind);
    }

    [Fact]
    public async Task DocumentSymbols_QueryFiltersByNameAndRetainsAncestors()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "class Parser { void ParseInput() { } void ReportError() { } }");
        var client = new FakeLspClient();
        client.EnqueueResponse(new[]
        {
            new DocumentSymbol(
                "Parser",
                SymbolKind.Class,
                new Lsp.Range(new Position(0, 0), new Position(0, 62)),
                new Lsp.Range(new Position(0, 6), new Position(0, 12)),
                Children:
                [
                    new DocumentSymbol(
                        "ParseInput",
                        SymbolKind.Method,
                        new Lsp.Range(new Position(0, 15), new Position(0, 35)),
                        new Lsp.Range(new Position(0, 20), new Position(0, 30))),
                    new DocumentSymbol(
                        "ReportError",
                        SymbolKind.Method,
                        new Lsp.Range(new Position(0, 36), new Position(0, 58)),
                        new Lsp.Range(new Position(0, 41), new Position(0, 52)))
                ])
        });
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.DocumentSymbols("Program.cs", query: "parse");

        var symbols = Assert.IsType<DocumentSymbolsResult>(result);
        Assert.False(symbols.Truncated);
        Assert.Equal(2, symbols.TotalKnown);
        Assert.Equal(3, symbols.TotalUnfilteredKnown);
        Assert.Equal(2, symbols.Returned);
        var rootSymbol = Assert.Single(symbols.Items);
        Assert.Equal("Parser", rootSymbol.Name);
        var child = Assert.Single(rootSymbol.Children);
        Assert.Equal("ParseInput", child.Name);
    }

    [Fact]
    public async Task DocumentSymbols_QueryCombinesWithKindFilter()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "class Parser { void ParseInput() { } string ParseState => \"\"; }");
        var client = new FakeLspClient();
        client.EnqueueResponse(new[]
        {
            new DocumentSymbol(
                "Parser",
                SymbolKind.Class,
                new Lsp.Range(new Position(0, 0), new Position(0, 64)),
                new Lsp.Range(new Position(0, 6), new Position(0, 12)),
                Children:
                [
                    new DocumentSymbol(
                        "ParseInput",
                        SymbolKind.Method,
                        new Lsp.Range(new Position(0, 15), new Position(0, 35)),
                        new Lsp.Range(new Position(0, 20), new Position(0, 30))),
                    new DocumentSymbol(
                        "ParseState",
                        SymbolKind.Property,
                        new Lsp.Range(new Position(0, 36), new Position(0, 60)),
                        new Lsp.Range(new Position(0, 43), new Position(0, 53)))
                ])
        });
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.DocumentSymbols("Program.cs", kindFilter: ["method"], query: "parse");

        var symbols = Assert.IsType<DocumentSymbolsResult>(result);
        Assert.Equal(2, symbols.TotalKnown);
        Assert.Equal(3, symbols.TotalUnfilteredKnown);
        var rootSymbol = Assert.Single(symbols.Items);
        var child = Assert.Single(rootSymbol.Children);
        Assert.Equal("ParseInput", child.Name);
        Assert.Equal(SymbolKind.Method, child.Kind);
    }

    [Fact]
    public async Task DocumentSymbols_BlankQueryIsIgnored()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "class C { void M() { } }");
        var client = new FakeLspClient();
        client.EnqueueResponse(new[]
        {
            new DocumentSymbol(
                "C",
                SymbolKind.Class,
                new Lsp.Range(new Position(0, 0), new Position(0, 24)),
                new Lsp.Range(new Position(0, 6), new Position(0, 7)),
                Children:
                [
                    new DocumentSymbol(
                        "M",
                        SymbolKind.Method,
                        new Lsp.Range(new Position(0, 10), new Position(0, 22)),
                        new Lsp.Range(new Position(0, 15), new Position(0, 16)))
                ])
        });
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.DocumentSymbols("Program.cs", query: "   ");

        var symbols = Assert.IsType<DocumentSymbolsResult>(result);
        Assert.Equal(2, symbols.TotalKnown);
        Assert.Equal(2, symbols.TotalUnfilteredKnown);
        Assert.Equal(2, symbols.Returned);
        Assert.Single(symbols.Items);
    }

    [Fact]
    public async Task DocumentSymbols_MaxResultsLimitsReturnedAndSetsTruncated()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "class C { void M() { } void N() { } }");
        var client = new FakeLspClient();
        client.EnqueueResponse(new[]
        {
            new DocumentSymbol(
                "C",
                SymbolKind.Class,
                new Lsp.Range(new Position(0, 0), new Position(0, 39)),
                new Lsp.Range(new Position(0, 6), new Position(0, 7)),
                Children:
                [
                    new DocumentSymbol(
                        "M",
                        SymbolKind.Method,
                        new Lsp.Range(new Position(0, 10), new Position(0, 22)),
                        new Lsp.Range(new Position(0, 15), new Position(0, 16))),
                    new DocumentSymbol(
                        "N",
                        SymbolKind.Method,
                        new Lsp.Range(new Position(0, 23), new Position(0, 35)),
                        new Lsp.Range(new Position(0, 28), new Position(0, 29)))
                ])
        });
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.DocumentSymbols("Program.cs", maxResults: 2);

        var symbols = Assert.IsType<DocumentSymbolsResult>(result);
        Assert.True(symbols.Truncated);
        Assert.Equal(3, symbols.TotalKnown);
        Assert.Equal(3, symbols.TotalUnfilteredKnown);
        Assert.Equal(2, symbols.Returned);
        var rootSymbol = Assert.Single(symbols.Items);
        Assert.Equal("C", rootSymbol.Name);
        var child = Assert.Single(rootSymbol.Children);
        Assert.Equal("M", child.Name);
    }

    [Fact]
    public async Task DocumentSymbols_InvalidMaxResultsReturnsToolError()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "class C { }");
        await using var session = CreateSession(root.Path, new ThrowingLoader());
        var tools = CreateTools(root.Path, session);

        var result = await tools.DocumentSymbols("Program.cs", maxResults: 0);

        var error = Assert.IsType<ToolError>(result);
        Assert.Equal("invalid_max_results", error.Error);
    }

    [Fact]
    public async Task DocumentSymbols_UsesCustomTimeout()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "class C { }");
        var client = new FakeLspClient();
        client.EnqueueResponse(Array.Empty<DocumentSymbol>());
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.DocumentSymbols("Program.cs", timeoutSec: 45);

        Assert.IsType<DocumentSymbolsResult>(result);
        Assert.Equal(TimeSpan.FromSeconds(45), Assert.Single(client.Requests).Timeout);
    }

    [Fact]
    public async Task DocumentSymbols_InvalidTimeoutReturnsToolError()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "class C { }");
        await using var session = CreateSession(root.Path, new ThrowingLoader());
        var tools = CreateTools(root.Path, session);

        var result = await tools.DocumentSymbols("Program.cs", timeoutSec: 0);

        var error = Assert.IsType<ToolError>(result);
        Assert.Equal("invalid_timeout", error.Error);
    }

    [Fact]
    public async Task DocumentSymbols_InvalidKindFilterReturnsToolError()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "class C { }");
        await using var session = CreateSession(root.Path, new ThrowingLoader());
        var tools = CreateTools(root.Path, session);

        var result = await tools.DocumentSymbols("Program.cs", kindFilter: ["nope"]);

        var error = Assert.IsType<ToolError>(result);
        Assert.Equal("invalid_kind_filter", error.Error);
    }

    [Fact]
    public async Task DocumentSymbols_AutoLoadsSingleProjectWhenWorkspaceIsNotLoaded()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "class C { }");
        var client = new FakeLspClient();
        client.EnqueueResponse(Array.Empty<DocumentSymbol>());
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        var tools = CreateTools(root.Path, session);

        var result = await tools.DocumentSymbols("Program.cs");

        var symbols = Assert.IsType<DocumentSymbolsResult>(result);
        Assert.Equal(WorkspaceLoadState.WorkspaceWarming.ToString(), symbols.WorkspaceState);
        Assert.Equal(0, symbols.TotalUnfilteredKnown);
        Assert.Equal(WorkspaceLoadState.WorkspaceWarming, session.State);
    }

    [Fact]
    public async Task FindSymbols_ReturnsValidationErrorForEmptyQuery()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var client = new FakeLspClient();
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.FindSymbols("   ");

        var error = Assert.IsType<ToolError>(result);
        Assert.Equal("invalid_query", error.Error);
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task FindSymbols_ReturnsValidationErrorForTooShortQuery()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var client = new FakeLspClient();
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.FindSymbols("a");

        var error = Assert.IsType<ToolError>(result);
        Assert.Equal("invalid_query", error.Error);
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task FindSymbols_PassesQueryToWorkspaceSymbolRequestAndMarksItExpensive()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var client = new FakeLspClient();
        client.EnqueueResponse(Array.Empty<object>());
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.FindSymbols("Calc");

        Assert.IsType<FindSymbolsResult>(result);
        var request = Assert.Single(client.Requests);
        Assert.Equal("workspace/symbol", request.Method);
        Assert.True(request.IsExpensive);
        Assert.Equal("Calc", request.Params.GetProperty("query").GetString());
    }

    [Fact]
    public async Task FindSymbols_TrimsQueryBeforeWorkspaceSymbolRequest()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var client = new FakeLspClient();
        client.EnqueueResponse(Array.Empty<object>());
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.FindSymbols("  Calc  ");

        Assert.IsType<FindSymbolsResult>(result);
        var request = Assert.Single(client.Requests);
        Assert.Equal("Calc", request.Params.GetProperty("query").GetString());
    }

    [Fact]
    public async Task FindSymbols_ReturnsEmptyResultForNullOrEmptyResponse()
    {
        var nullResult = await ExecuteFindSymbolsWithResponse(null);
        var emptyResult = await ExecuteFindSymbolsWithResponse(Array.Empty<object>());

        var nullSymbols = Assert.IsType<FindSymbolsResult>(nullResult);
        var emptySymbols = Assert.IsType<FindSymbolsResult>(emptyResult);
        Assert.Empty(nullSymbols.Items);
        Assert.Equal(0, nullSymbols.TotalUnfilteredKnown);
        Assert.Empty(emptySymbols.Items);
        Assert.Equal(0, emptySymbols.TotalUnfilteredKnown);
    }

    [Fact]
    public async Task FindSymbols_FiltersBySingleKind()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var client = new FakeLspClient();
        client.EnqueueResponse(new object[]
        {
            CreateWorkspaceSymbol("Calculator", SymbolKind.Class),
            CreateWorkspaceSymbol("Calculate", SymbolKind.Method),
            CreateWorkspaceSymbol("CalculatorValue", SymbolKind.Field)
        });
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.FindSymbols("Calc", kindFilter: new[] { "class" });

        var symbols = Assert.IsType<FindSymbolsResult>(result);
        var item = Assert.Single(symbols.Items);
        Assert.Equal("Calculator", item.Name);
        Assert.Equal(SymbolKind.Class, item.Kind);
        Assert.Equal(3, symbols.TotalUnfilteredKnown);
        Assert.Equal(1, symbols.TotalKnown);
        Assert.Equal(1, symbols.Returned);
        Assert.False(symbols.Truncated);
        var request = Assert.Single(client.Requests);
        Assert.Equal("Calc", request.Params.GetProperty("query").GetString());
        Assert.False(request.Params.TryGetProperty("kindFilter", out _));
    }

    [Fact]
    public async Task FindSymbols_FiltersByMultipleKinds()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var client = new FakeLspClient();
        client.EnqueueResponse(new object[]
        {
            CreateWorkspaceSymbol("Calculator", SymbolKind.Class),
            CreateWorkspaceSymbol("ICalculator", SymbolKind.Interface),
            CreateWorkspaceSymbol("Calculate", SymbolKind.Method),
            CreateWorkspaceSymbol("CalculatorValue", SymbolKind.Field)
        });
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.FindSymbols("Calc", kindFilter: new[] { "interface", "field" });

        var symbols = Assert.IsType<FindSymbolsResult>(result);
        Assert.Collection(
            symbols.Items,
            item => Assert.Equal(SymbolKind.Interface, item.Kind),
            item => Assert.Equal(SymbolKind.Field, item.Kind));
        Assert.Equal(4, symbols.TotalUnfilteredKnown);
        Assert.Equal(2, symbols.TotalKnown);
        Assert.Equal(2, symbols.Returned);
    }

    [Fact]
    public async Task FindSymbols_KindFilterIsCaseInsensitive()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var client = new FakeLspClient();
        client.EnqueueResponse(new object[]
        {
            CreateWorkspaceSymbol("Calculator", SymbolKind.Class),
            CreateWorkspaceSymbol("Calculate", SymbolKind.Method),
            CreateWorkspaceSymbol("T", SymbolKind.TypeParameter)
        });
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.FindSymbols("Calc", kindFilter: new[] { "ClAsS", "TYPEPARAMETER" });

        var symbols = Assert.IsType<FindSymbolsResult>(result);
        Assert.Collection(
            symbols.Items,
            item => Assert.Equal(SymbolKind.Class, item.Kind),
            item => Assert.Equal(SymbolKind.TypeParameter, item.Kind));
        Assert.Equal(3, symbols.TotalUnfilteredKnown);
        Assert.Equal(2, symbols.TotalKnown);
    }

    [Fact]
    public async Task FindSymbols_AppliesMaxResultsAfterKindFiltering()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var client = new FakeLspClient();
        client.EnqueueResponse(new object[]
        {
            CreateWorkspaceSymbol("Calculate", SymbolKind.Method),
            CreateWorkspaceSymbol("Calculator", SymbolKind.Class),
            CreateWorkspaceSymbol("CalculatorValue", SymbolKind.Field),
            CreateWorkspaceSymbol("CalculatorService", SymbolKind.Class)
        });
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.FindSymbols("Calc", maxResults: 1, kindFilter: new[] { "class" });

        var symbols = Assert.IsType<FindSymbolsResult>(result);
        var item = Assert.Single(symbols.Items);
        Assert.Equal("Calculator", item.Name);
        Assert.Equal(4, symbols.TotalUnfilteredKnown);
        Assert.Equal(2, symbols.TotalKnown);
        Assert.Equal(1, symbols.Returned);
        Assert.True(symbols.Truncated);
    }

    [Fact]
    public async Task FindSymbols_DefaultKeepsRoslynLsResultsThatContainsWouldFilterOut()
    {
        var response = new object[]
        {
            CreateWorkspaceSymbol("Version", SymbolKind.Property),
            CreateWorkspaceSymbol("SessionManager", SymbolKind.Class)
        };

        var (defaultResult, _) = await ExecuteFindSymbolsRequestAsync(response, query: "Session", matchMode: null);
        var (containsResult, _) = await ExecuteFindSymbolsRequestAsync(response, query: "Session", matchMode: "contains");

        var defaultSymbols = Assert.IsType<FindSymbolsResult>(defaultResult);
        Assert.Collection(
            defaultSymbols.Items,
            item => Assert.Equal("Version", item.Name),
            item => Assert.Equal("SessionManager", item.Name));
        Assert.Equal(2, defaultSymbols.TotalUnfilteredKnown);
        Assert.Equal(2, defaultSymbols.TotalKnown);

        var containsSymbols = Assert.IsType<FindSymbolsResult>(containsResult);
        var item = Assert.Single(containsSymbols.Items);
        Assert.Equal("SessionManager", item.Name);
        Assert.Equal(2, containsSymbols.TotalUnfilteredKnown);
        Assert.Equal(1, containsSymbols.TotalKnown);
    }

    [Fact]
    public async Task FindSymbols_ExactMatchModeKeepsOnlyExactNames()
    {
        var (result, _) = await ExecuteFindSymbolsRequestAsync(
            new object[]
            {
                CreateWorkspaceSymbol("calc", SymbolKind.Class),
                CreateWorkspaceSymbol("Calculator", SymbolKind.Class),
                CreateWorkspaceSymbol("MyCalc", SymbolKind.Class)
            },
            query: "Calc",
            matchMode: "exact");

        var symbols = Assert.IsType<FindSymbolsResult>(result);
        var item = Assert.Single(symbols.Items);
        Assert.Equal("calc", item.Name);
        Assert.Equal(3, symbols.TotalUnfilteredKnown);
        Assert.Equal(1, symbols.TotalKnown);
        Assert.Equal(1, symbols.Returned);
    }

    [Fact]
    public async Task FindSymbols_PrefixMatchModeKeepsOnlyPrefixNames()
    {
        var (result, _) = await ExecuteFindSymbolsRequestAsync(
            new object[]
            {
                CreateWorkspaceSymbol("Calc", SymbolKind.Class),
                CreateWorkspaceSymbol("Calculator", SymbolKind.Class),
                CreateWorkspaceSymbol("MyCalc", SymbolKind.Class),
                CreateWorkspaceSymbol("Version", SymbolKind.Property)
            },
            query: "Calc",
            matchMode: "prefix");

        var symbols = Assert.IsType<FindSymbolsResult>(result);
        Assert.Collection(
            symbols.Items,
            item => Assert.Equal("Calc", item.Name),
            item => Assert.Equal("Calculator", item.Name));
        Assert.Equal(4, symbols.TotalUnfilteredKnown);
        Assert.Equal(2, symbols.TotalKnown);
    }

    [Fact]
    public async Task FindSymbols_ContainsMatchModeKeepsOnlyContainingNames()
    {
        var (result, _) = await ExecuteFindSymbolsRequestAsync(
            new object[]
            {
                CreateWorkspaceSymbol("Calc", SymbolKind.Class),
                CreateWorkspaceSymbol("Calculator", SymbolKind.Class),
                CreateWorkspaceSymbol("MyCalc", SymbolKind.Class),
                CreateWorkspaceSymbol("Version", SymbolKind.Property)
            },
            query: "Calc",
            matchMode: "contains");

        var symbols = Assert.IsType<FindSymbolsResult>(result);
        Assert.Collection(
            symbols.Items,
            item => Assert.Equal("Calc", item.Name),
            item => Assert.Equal("Calculator", item.Name),
            item => Assert.Equal("MyCalc", item.Name));
        Assert.Equal(4, symbols.TotalUnfilteredKnown);
        Assert.Equal(3, symbols.TotalKnown);
    }

    [Fact]
    public async Task FindSymbols_MatchModeTrimsAndParsesCaseInsensitively()
    {
        var (result, _) = await ExecuteFindSymbolsRequestAsync(
            new object[]
            {
                CreateWorkspaceSymbol("Calculator", SymbolKind.Class),
                CreateWorkspaceSymbol("MyCalculator", SymbolKind.Class)
            },
            query: "Calc",
            matchMode: " PrEfIx ");

        var symbols = Assert.IsType<FindSymbolsResult>(result);
        var item = Assert.Single(symbols.Items);
        Assert.Equal("Calculator", item.Name);
    }

    [Fact]
    public async Task FindSymbols_AppliesMatchModeAfterKindFilter()
    {
        var (result, _) = await ExecuteFindSymbolsRequestAsync(
            new object[]
            {
                CreateWorkspaceSymbol("Calc", SymbolKind.Method),
                CreateWorkspaceSymbol("CalcService", SymbolKind.Class),
                CreateWorkspaceSymbol("MyCalc", SymbolKind.Class),
                CreateWorkspaceSymbol("CalcOptions", SymbolKind.Field)
            },
            query: "Calc",
            kindFilter: new[] { "class" },
            matchMode: "prefix");

        var symbols = Assert.IsType<FindSymbolsResult>(result);
        var item = Assert.Single(symbols.Items);
        Assert.Equal("CalcService", item.Name);
        Assert.Equal(4, symbols.TotalUnfilteredKnown);
        Assert.Equal(1, symbols.TotalKnown);
        Assert.Equal(1, symbols.Returned);
    }

    [Fact]
    public async Task FindSymbols_AppliesMaxResultsAfterMatchMode()
    {
        var (result, _) = await ExecuteFindSymbolsRequestAsync(
            new object[]
            {
                CreateWorkspaceSymbol("CalcOne", SymbolKind.Class),
                CreateWorkspaceSymbol("Version", SymbolKind.Property),
                CreateWorkspaceSymbol("CalcTwo", SymbolKind.Class),
                CreateWorkspaceSymbol("CalcThree", SymbolKind.Class)
            },
            query: "Calc",
            maxResults: 1,
            matchMode: "prefix");

        var symbols = Assert.IsType<FindSymbolsResult>(result);
        var item = Assert.Single(symbols.Items);
        Assert.Equal("CalcOne", item.Name);
        Assert.Equal(4, symbols.TotalUnfilteredKnown);
        Assert.Equal(3, symbols.TotalKnown);
        Assert.Equal(1, symbols.Returned);
        Assert.True(symbols.Truncated);
    }

    [Fact]
    public async Task FindSymbols_FiltersByIncludePathPrefixes()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var client = new FakeLspClient();
        client.EnqueueResponse(new object[]
        {
            CreateWorkspaceSymbol(root.Path, "src/App/Calculator.cs", "Calculator", SymbolKind.Class),
            CreateWorkspaceSymbol(root.Path, "src/App/Services/CalculatorService.cs", "CalculatorService", SymbolKind.Class),
            CreateWorkspaceSymbol(root.Path, "src/App2/Calculator.cs", "CalculatorClone", SymbolKind.Class),
            CreateWorkspaceSymbol(root.Path, "tests/App.Tests/CalculatorTests.cs", "CalculatorTests", SymbolKind.Class)
        });
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.FindSymbols("Calc", includePathPrefixes: new[] { "src/App" });

        var symbols = Assert.IsType<FindSymbolsResult>(result);
        Assert.Collection(
            symbols.Items,
            item => Assert.Equal("src/App/Calculator.cs", item.Location?.File),
            item => Assert.Equal("src/App/Services/CalculatorService.cs", item.Location?.File));
        Assert.Equal(4, symbols.TotalUnfilteredKnown);
        Assert.Equal(2, symbols.TotalKnown);
        Assert.Equal(2, symbols.Returned);
        Assert.False(symbols.Truncated);
    }

    [Fact]
    public async Task FindSymbols_AppliesMaxResultsAfterIncludePathPrefixes()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var client = new FakeLspClient();
        client.EnqueueResponse(new object[]
        {
            CreateWorkspaceSymbol(root.Path, "tests/App.Tests/CalculatorTests.cs", "CalculatorTests", SymbolKind.Class),
            CreateWorkspaceSymbol(root.Path, "src/App/Calculator.cs", "Calculator", SymbolKind.Class),
            CreateWorkspaceSymbol(root.Path, "src/App/CalculatorService.cs", "CalculatorService", SymbolKind.Class)
        });
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.FindSymbols("Calc", maxResults: 1, includePathPrefixes: new[] { "src/App" });

        var symbols = Assert.IsType<FindSymbolsResult>(result);
        var item = Assert.Single(symbols.Items);
        Assert.Equal("Calculator", item.Name);
        Assert.Equal(3, symbols.TotalUnfilteredKnown);
        Assert.Equal(2, symbols.TotalKnown);
        Assert.Equal(1, symbols.Returned);
        Assert.True(symbols.Truncated);
    }

    [Fact]
    public async Task FindSymbols_IncludePathPrefixesNormalizesSlashStyles()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var client = new FakeLspClient();
        client.EnqueueResponse(new object[]
        {
            CreateWorkspaceSymbol(root.Path, "src/App/Calculator.cs", "Calculator", SymbolKind.Class),
            CreateWorkspaceSymbol(root.Path, "tests/App.Tests/CalculatorTests.cs", "CalculatorTests", SymbolKind.Class)
        });
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.FindSymbols("Calc", includePathPrefixes: new[] { "src\\App\\" });

        var symbols = Assert.IsType<FindSymbolsResult>(result);
        var item = Assert.Single(symbols.Items);
        Assert.Equal("src/App/Calculator.cs", item.Location?.File);
        Assert.Equal(1, symbols.TotalKnown);
    }

    [Fact]
    public async Task FindSymbols_IncludePathPrefixesExcludesLocationlessSymbols()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var client = new FakeLspClient();
        client.EnqueueResponse(new object[]
        {
            CreateWorkspaceSymbol("Locationless", SymbolKind.Class),
            CreateWorkspaceSymbol(root.Path, "src/App/Calculator.cs", "Calculator", SymbolKind.Class)
        });
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.FindSymbols("Calc", includePathPrefixes: new[] { "src/App" });

        var symbols = Assert.IsType<FindSymbolsResult>(result);
        var item = Assert.Single(symbols.Items);
        Assert.Equal("Calculator", item.Name);
        Assert.Equal(2, symbols.TotalUnfilteredKnown);
        Assert.Equal(1, symbols.TotalKnown);
    }

    [Fact]
    public async Task FindSymbols_ReturnsValidationErrorForInvalidIncludePathPrefixes()
    {
        using var root = TestRoot.Create();
        using var outside = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var client = new FakeLspClient();
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var emptyArrayResult = await tools.FindSymbols("Calc", includePathPrefixes: Array.Empty<string>());
        var emptyEntryResult = await tools.FindSymbols("Calc", includePathPrefixes: new[] { "src/App", " " });
        var outsideResult = await tools.FindSymbols("Calc", includePathPrefixes: new[] { outside.Path });

        var emptyArrayError = Assert.IsType<ToolError>(emptyArrayResult);
        var emptyEntryError = Assert.IsType<ToolError>(emptyEntryResult);
        var outsideError = Assert.IsType<ToolError>(outsideResult);
        Assert.Equal("invalid_path_prefix", emptyArrayError.Error);
        Assert.Equal("invalid_path_prefix", emptyEntryError.Error);
        Assert.Equal("invalid_path_prefix", outsideError.Error);
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task FindSymbols_DoesNotSendMcpSideFiltersToRoslynLs()
    {
        var (result, client) = await ExecuteFindSymbolsRequestAsync(
            Array.Empty<object>(),
            query: "Calc",
            kindFilter: new[] { "class" },
            matchMode: "exact",
            includePathPrefixes: new[] { "src" });

        Assert.IsType<FindSymbolsResult>(result);
        var request = Assert.Single(client.Requests);
        Assert.Equal("workspace/symbol", request.Method);
        Assert.Equal(["query"], request.Params.EnumerateObject().Select(property => property.Name));
        Assert.Equal("Calc", request.Params.GetProperty("query").GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("banana")]
    [InlineData("1")]
    public async Task FindSymbols_ReturnsValidationErrorForInvalidMatchMode(string matchMode)
    {
        var (result, client) = await ExecuteFindSymbolsRequestAsync(
            Array.Empty<object>(),
            query: "Calc",
            matchMode: matchMode);

        var error = Assert.IsType<ToolError>(result);
        Assert.Equal("invalid_match_mode", error.Error);
        Assert.Contains("default", error.Message);
        Assert.Contains("exact", error.Message);
        Assert.Contains("prefix", error.Message);
        Assert.Contains("contains", error.Message);
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task FindSymbols_ReturnsValidationErrorForUnknownKindFilter()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var client = new FakeLspClient();
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.FindSymbols("Calc", kindFilter: new[] { "banana" });

        var error = Assert.IsType<ToolError>(result);
        Assert.Equal("invalid_kind_filter", error.Error);
        Assert.Contains("banana", error.Message);
        Assert.Contains("Allowed values", error.Message);
        Assert.Contains("class", error.Message);
        Assert.Contains("enumMember", error.Message);
        Assert.Contains("typeParameter", error.Message);
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task FindSymbols_ReturnsValidationErrorForEmptyKindFilter()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var client = new FakeLspClient();
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.FindSymbols("Calc", kindFilter: Array.Empty<string>());

        var error = Assert.IsType<ToolError>(result);
        Assert.Equal("invalid_kind_filter", error.Error);
        Assert.Contains("at least one", error.Message);
        Assert.Contains("Allowed values", error.Message);
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task FindSymbols_MapsSymbolInformationToRootRelativeOneBasedLocation()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "class Calculator { }");
        var client = new FakeLspClient();
        client.EnqueueResponse(new[]
        {
            new
            {
                name = "Calculator",
                kind = (int)SymbolKind.Class,
                containerName = "Sample",
                location = new Lsp.Location(
                    new Uri(Path.Combine(root.Path, "Program.cs")).AbsoluteUri,
                    new Lsp.Range(new Position(2, 4), new Position(2, 14)))
            }
        });
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.FindSymbols("Calculator");

        var symbols = Assert.IsType<FindSymbolsResult>(result);
        var item = Assert.Single(symbols.Items);
        Assert.Equal("Calculator", item.Name);
        Assert.Equal(SymbolKind.Class, item.Kind);
        Assert.Equal("class", item.KindName);
        Assert.Equal("Sample", item.ContainerName);
        Assert.Equal("Program.cs", item.Location?.File);
        Assert.Equal(3, item.Location?.Line);
        Assert.Equal(5, item.Location?.Column);
        Assert.Equal(3, item.Location?.Range.StartLine);
        Assert.Equal(5, item.Location?.Range.StartColumn);
    }

    [Fact]
    public async Task FindSymbols_MapsWorkspaceSymbolVariantWithLocation()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "class Calculator { }");
        var client = new FakeLspClient();
        client.EnqueueResponse(new[]
        {
            new
            {
                name = "Add",
                kind = (int)SymbolKind.Method,
                location = new
                {
                    uri = new Uri(Path.Combine(root.Path, "Program.cs")).AbsoluteUri,
                    range = new Lsp.Range(new Position(5, 8), new Position(5, 11))
                }
            }
        });
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.FindSymbols("Add");

        var symbols = Assert.IsType<FindSymbolsResult>(result);
        var item = Assert.Single(symbols.Items);
        Assert.Equal("Add", item.Name);
        Assert.Equal("method", item.KindName);
        Assert.Equal("Program.cs", item.Location?.File);
        Assert.Equal(6, item.Location?.Line);
        Assert.Equal(9, item.Location?.Column);
    }

    [Fact]
    public async Task FindSymbols_ToleratesMissingOrUnsupportedLocation()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var client = new FakeLspClient();
        client.EnqueueResponse(new object[]
        {
            new
            {
                name = "NoLocation",
                kind = (int)SymbolKind.Class
            },
            new
            {
                name = "UriOnly",
                kind = (int)SymbolKind.Class,
                location = new
                {
                    uri = new Uri(Path.Combine(root.Path, "Program.cs")).AbsoluteUri
                }
            },
            new
            {
                name = "MalformedLocation",
                kind = (int)SymbolKind.Class,
                location = "not a location"
            }
        });
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.FindSymbols("Location");

        var symbols = Assert.IsType<FindSymbolsResult>(result);
        Assert.Equal(3, symbols.Items.Count);
        Assert.All(symbols.Items, item => Assert.Null(item.Location));
    }

    [Fact]
    public async Task FindSymbols_ToleratesMalformedSymbolFields()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var client = new FakeLspClient();
        client.EnqueueResponse(new object[]
        {
            new
            {
                name = 123,
                kind = (int)SymbolKind.Class
            },
            new
            {
                name = "BadKind",
                kind = "class"
            },
            new
            {
                name = "BadUri",
                kind = (int)SymbolKind.Class,
                location = new
                {
                    uri = 123,
                    range = new Lsp.Range(new Position(0, 0), new Position(0, 1))
                }
            },
            new
            {
                name = "BadContainer",
                kind = (int)SymbolKind.Class,
                containerName = 123
            }
        });
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.FindSymbols("Bad");

        var symbols = Assert.IsType<FindSymbolsResult>(result);
        Assert.Collection(
            symbols.Items,
            item =>
            {
                Assert.Equal("BadUri", item.Name);
                Assert.Null(item.Location);
            },
            item =>
            {
                Assert.Equal("BadContainer", item.Name);
                Assert.Null(item.ContainerName);
            });
    }

    [Fact]
    public async Task FindSymbols_DoesNotExposeOutsideRootOrNonFileUriResults()
    {
        using var root = TestRoot.Create();
        using var outside = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var insideUri = new Uri(Path.Combine(root.Path, "Program.cs")).AbsoluteUri;
        var outsideUri = new Uri(Path.Combine(outside.Path, "Outside.cs")).AbsoluteUri;
        var client = new FakeLspClient();
        client.EnqueueResponse(new[]
        {
            new
            {
                name = "Outside",
                kind = (int)SymbolKind.Class,
                location = new Lsp.Location(outsideUri, new Lsp.Range(new Position(0, 0), new Position(0, 1)))
            },
            new
            {
                name = "Remote",
                kind = (int)SymbolKind.Class,
                location = new Lsp.Location("https://example.test/Remote.cs", new Lsp.Range(new Position(0, 0), new Position(0, 1)))
            },
            new
            {
                name = "Inside",
                kind = (int)SymbolKind.Class,
                location = new Lsp.Location(insideUri, new Lsp.Range(new Position(0, 0), new Position(0, 1)))
            }
        });
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.FindSymbols("Class");

        var symbols = Assert.IsType<FindSymbolsResult>(result);
        var item = Assert.Single(symbols.Items);
        Assert.Equal("Inside", item.Name);
        Assert.Equal("Program.cs", item.Location?.File);
        Assert.Equal(1, symbols.TotalKnown);
        Assert.Equal(1, symbols.Returned);
        Assert.False(symbols.Truncated);
    }

    [Fact]
    public async Task FindSymbols_TruncatesLargeResponseAtDefaultLimit()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var client = new FakeLspClient();
        client.EnqueueResponse(CreateWorkspaceSymbols(count: 301));
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.FindSymbols("Symbol");

        var symbols = Assert.IsType<FindSymbolsResult>(result);
        Assert.Equal(301, symbols.TotalKnown);
        Assert.Equal(301, symbols.TotalUnfilteredKnown);
        Assert.Equal(300, symbols.Returned);
        Assert.Equal(300, symbols.Items.Count);
        Assert.True(symbols.Truncated);
    }

    [Fact]
    public async Task FindSymbols_AppliesUserMaxResultsAndServerHardCap()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var client = new FakeLspClient();
        client.EnqueueResponse(CreateWorkspaceSymbols(count: 4));
        client.EnqueueResponse(CreateWorkspaceSymbols(count: 1001));
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var userLimitedResult = await tools.FindSymbols("Symbol", maxResults: 2);
        var hardCapResult = await tools.FindSymbols("Symbol", maxResults: 5000);

        var userLimited = Assert.IsType<FindSymbolsResult>(userLimitedResult);
        Assert.Equal(4, userLimited.TotalKnown);
        Assert.Equal(4, userLimited.TotalUnfilteredKnown);
        Assert.Equal(2, userLimited.Returned);
        Assert.True(userLimited.Truncated);

        var hardCapped = Assert.IsType<FindSymbolsResult>(hardCapResult);
        Assert.Equal(1001, hardCapped.TotalKnown);
        Assert.Equal(1001, hardCapped.TotalUnfilteredKnown);
        Assert.Equal(1000, hardCapped.Returned);
        Assert.True(hardCapped.Truncated);
    }

    [Fact]
    public async Task FindSymbols_IncludesPartialReasonForEmptyResultWhileWorkspaceIsWarming()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var client = new FakeLspClient();
        client.EnqueueResponse(Array.Empty<object>());
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.FindSymbols("Missing");

        var symbols = Assert.IsType<FindSymbolsResult>(result);
        Assert.Empty(symbols.Items);
        Assert.Equal(WorkspaceLoadState.WorkspaceWarming.ToString(), symbols.WorkspaceState);
        Assert.Equal("partial", symbols.Completeness);
        Assert.Contains("symbol index", symbols.Reason);
        Assert.Equal(0, symbols.TotalKnown);
        Assert.Equal(0, symbols.TotalUnfilteredKnown);
        Assert.Equal(0, symbols.Returned);
        Assert.False(symbols.Truncated);
    }

    [Fact]
    public async Task FindSymbols_ReturnsWorkspaceLoadingWhileLanguageServerStarts()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var loader = new BlockingLoader();
        await using var session = CreateSession(root.Path, loader);
        var tools = CreateTools(root.Path, session);

        var loadTask = session.LoadProjectAsync("App.csproj");
        await loader.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var result = await tools.FindSymbols("Program");

        var error = Assert.IsType<ToolError>(result);
        Assert.Equal("workspace_loading", error.Error);
        Assert.Equal(WorkspaceLoadState.StartingLanguageServer.ToString(), error.WorkspaceState);

        loader.Release.SetResult();
        await loadTask.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task FindSymbols_ReturnsWorkspaceLoadingWhenStartupLoadIsPending()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.sln"), string.Empty);
        await using var session = CreateStartupLoadSession(root.Path, new ThrowingLoader(), "App.sln");
        var tools = CreateTools(root.Path, session);

        var result = await tools.FindSymbols("Program");

        var error = Assert.IsType<ToolError>(result);
        Assert.Equal("workspace_loading", error.Error);
        Assert.Equal(WorkspaceLoadState.StartingLanguageServer.ToString(), error.WorkspaceState);
    }
}
