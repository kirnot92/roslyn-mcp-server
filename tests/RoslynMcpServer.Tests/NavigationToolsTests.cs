using System.Text.Json;
using Microsoft.Extensions.Logging;
using RoslynMcpServer.Cli;
using RoslynMcpServer.Lsp;
using RoslynMcpServer.Mcp;
using RoslynMcpServer.Workspace;
using Lsp = RoslynMcpServer.Lsp;

namespace RoslynMcpServer.Tests;

public sealed class NavigationToolsTests
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
        Assert.Single(symbols.Items);
        Assert.Equal(1, symbols.Items[0].Range.StartLine);
        Assert.Equal(1, symbols.Items[0].Range.StartColumn);
        Assert.Equal(7, symbols.Items[0].SelectionRange.StartColumn);
        Assert.Equal(["textDocument/didOpen"], client.Notifications.Select(n => n.Method));
        Assert.Equal("textDocument/documentSymbol", Assert.Single(client.Requests).Method);
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
        Assert.Equal(WorkspaceLoadState.WorkspaceWarming, session.State);
    }

    [Fact]
    public async Task Hover_ConvertsOneBasedInputToZeroBasedLspPositionAndMapsResponse()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(
            Path.Combine(root.Path, "Program.cs"),
            string.Join(
                Environment.NewLine,
                Enumerable.Repeat("// padding", 11).Append("class C { string Name => \"\"; }")));
        var client = new FakeLspClient();
        client.EnqueueResponse(new
        {
            contents = new
            {
                kind = "markdown",
                value = "string C.Name"
            },
            range = new Lsp.Range(new Position(0, 17), new Position(0, 21))
        });
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.Hover("Program.cs", line: 12, column: 5);

        var hover = Assert.IsType<HoverResult>(result);
        Assert.Equal("string C.Name", hover.Contents);
        Assert.Equal("markdown", hover.Kind);
        Assert.Equal(1, hover.Range?.StartLine);
        Assert.Equal(18, hover.Range?.StartColumn);

        var request = Assert.Single(client.Requests);
        Assert.Equal("textDocument/hover", request.Method);
        Assert.Equal(11, request.Params.GetProperty("position").GetProperty("line").GetInt32());
        Assert.Equal(4, request.Params.GetProperty("position").GetProperty("character").GetInt32());
    }

    [Fact]
    public async Task Hover_ReturnsPositionOutOfRangeWhenLineExceedsFileLineCount()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "class C { }\nclass D { }");
        var client = new FakeLspClient();
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.Hover("Program.cs", line: 3, column: 1);

        var error = Assert.IsType<ToolError>(result);
        Assert.Equal("position_out_of_range", error.Error);
        Assert.Contains("line 3", error.Message);
        Assert.Contains("column 1", error.Message);
        Assert.Contains("1..2", error.Message);
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task Hover_ReturnsEmptyResultForNullResponse()
    {
        var result = await ExecuteHoverWithResponse(null);

        var hover = Assert.IsType<HoverResult>(result);
        Assert.Null(hover.Contents);
        Assert.Null(hover.Kind);
        Assert.Null(hover.Range);
    }

    [Fact]
    public async Task Hover_ReturnsEmptyResultWhenContentsAreMissing()
    {
        var result = await ExecuteHoverWithResponse(new { });

        var hover = Assert.IsType<HoverResult>(result);
        Assert.Null(hover.Contents);
    }

    [Fact]
    public async Task Hover_ReturnsInvalidLspResponseForNonObjectResponse()
    {
        var result = await ExecuteHoverWithResponse("unexpected");

        var error = Assert.IsType<ToolError>(result);
        Assert.Equal("invalid_lsp_response", error.Error);
    }

    [Fact]
    public async Task Hover_ReturnsInvalidLspResponseForMalformedRange()
    {
        var result = await ExecuteHoverWithResponse(new
        {
            contents = "text",
            range = "not a range"
        });

        var error = Assert.IsType<ToolError>(result);
        Assert.Equal("invalid_lsp_response", error.Error);
    }

    [Fact]
    public async Task GoToDefinition_MapsSingleLocationToRootRelativeOneBasedLocation()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "class C { }");
        var client = new FakeLspClient();
        client.EnqueueResponse(new Lsp.Location(
            new Uri(Path.Combine(root.Path, "Program.cs")).AbsoluteUri,
            new Lsp.Range(new Position(1, 2), new Position(1, 5))));
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.GoToDefinition("Program.cs", line: 1, column: 7);

        var definition = Assert.IsType<DefinitionResult>(result);
        var item = Assert.Single(definition.Items);
        Assert.Equal("Program.cs", item.File);
        Assert.Equal(2, item.Line);
        Assert.Equal(3, item.Column);
        Assert.Equal(2, item.Range.StartLine);
        Assert.Equal(3, item.Range.StartColumn);
    }

    [Fact]
    public async Task GoToDefinition_ReturnsPositionOutOfRangeWhenColumnExceedsLineEnd()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "class C { }");
        var client = new FakeLspClient();
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.GoToDefinition("Program.cs", line: 1, column: 13);

        var error = Assert.IsType<ToolError>(result);
        Assert.Equal("position_out_of_range", error.Error);
        Assert.Contains("line 1", error.Message);
        Assert.Contains("column 13", error.Message);
        Assert.Contains("1..12", error.Message);
        Assert.Contains("line length 11", error.Message);
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task GoToDefinition_MapsLocationLinkUsingTargetRange()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "class C { }");
        var client = new FakeLspClient();
        client.EnqueueResponse(new
        {
            targetUri = new Uri(Path.Combine(root.Path, "Program.cs")).AbsoluteUri,
            targetRange = new Lsp.Range(new Position(4, 8), new Position(4, 14)),
            targetSelectionRange = new Lsp.Range(new Position(4, 20), new Position(4, 21))
        });
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.GoToDefinition("Program.cs", line: 1, column: 7);

        var definition = Assert.IsType<DefinitionResult>(result);
        var item = Assert.Single(definition.Items);
        Assert.Equal(5, item.Line);
        Assert.Equal(9, item.Column);
    }

    [Fact]
    public async Task GoToDefinition_ReturnsEmptyResultForNullOrEmptyResponse()
    {
        var nullResult = await ExecuteDefinitionWithResponse(null);
        var emptyResult = await ExecuteDefinitionWithResponse(Array.Empty<Lsp.Location>());

        Assert.Empty(Assert.IsType<DefinitionResult>(nullResult).Items);
        Assert.Empty(Assert.IsType<DefinitionResult>(emptyResult).Items);
    }

    [Fact]
    public async Task PeekDefinition_ReturnsLocationAndSourceSnippet()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(
            Path.Combine(root.Path, "Program.cs"),
            string.Join(Environment.NewLine, Enumerable.Repeat("// caller", 8).Append("class Caller { }")));
        File.WriteAllText(Path.Combine(root.Path, "Target.cs"), """
            namespace Sample;
            public class Target
            {
                public void Method()
                {
                }
            }
            """);
        var client = new FakeLspClient();
        client.EnqueueResponse(new Lsp.Location(
            new Uri(Path.Combine(root.Path, "Target.cs")).AbsoluteUri,
            new Lsp.Range(new Position(3, 16), new Position(3, 22))));
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.PeekDefinition("Program.cs", line: 9, column: 4, contextLines: 1);

        var peek = Assert.IsType<PeekDefinitionResult>(result);
        Assert.Equal(1, peek.TotalKnown);
        Assert.Equal(1, peek.Returned);
        Assert.False(peek.Truncated);
        var item = Assert.Single(peek.Items);
        Assert.Equal("Target.cs", item.File);
        Assert.Equal(4, item.Line);
        Assert.Equal(17, item.Column);
        Assert.Null(item.SnippetError);
        Assert.NotNull(item.Snippet);
        Assert.Equal(3, item.Snippet.StartLine);
        Assert.Equal(5, item.Snippet.EndLine);
        Assert.Contains("public void Method()", item.Snippet.Text);
        Assert.False(item.Snippet.Truncated);

        var request = Assert.Single(client.Requests);
        Assert.Equal("textDocument/definition", request.Method);
        Assert.Equal(8, request.Params.GetProperty("position").GetProperty("line").GetInt32());
        Assert.Equal(3, request.Params.GetProperty("position").GetProperty("character").GetInt32());
    }

    [Fact]
    public async Task PeekDefinition_ReturnsPositionOutOfRangeWhenLineExceedsFileLineCount()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "class Caller { }\nclass Other { }");
        var client = new FakeLspClient();
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.PeekDefinition("Program.cs", line: 9999, column: 1);

        var error = Assert.IsType<ToolError>(result);
        Assert.Equal("position_out_of_range", error.Error);
        Assert.Contains("line 9999", error.Message);
        Assert.Contains("column 1", error.Message);
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task PeekDefinition_AppliesMaxDefinitionAndContextLineLimits()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "class Caller { }");
        File.WriteAllText(Path.Combine(root.Path, "Target.cs"), """
            line 1
            line 2
            line 3
            line 4
            """);
        var uri = new Uri(Path.Combine(root.Path, "Target.cs")).AbsoluteUri;
        var client = new FakeLspClient();
        client.EnqueueResponse(new[]
        {
            new Lsp.Location(uri, new Lsp.Range(new Position(0, 0), new Position(0, 1))),
            new Lsp.Location(uri, new Lsp.Range(new Position(1, 0), new Position(1, 1))),
            new Lsp.Location(uri, new Lsp.Range(new Position(2, 0), new Position(2, 1)))
        });
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.PeekDefinition(
            "Program.cs",
            line: 1,
            column: 7,
            contextLines: 0,
            maxDefinitions: 2);

        var peek = Assert.IsType<PeekDefinitionResult>(result);
        Assert.Equal(3, peek.TotalKnown);
        Assert.Equal(2, peek.Returned);
        Assert.True(peek.Truncated);
        Assert.Equal(2, peek.Items.Count);
        Assert.All(peek.Items, item =>
        {
            Assert.NotNull(item.Snippet);
            Assert.Equal(item.Line, item.Snippet.StartLine);
            Assert.Equal(item.Line, item.Snippet.EndLine);
            Assert.False(item.Snippet.Truncated);
        });
    }

    [Fact]
    public async Task PeekDefinition_ReturnsPerItemSnippetErrorWhenTargetFileExceedsDocumentLimit()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "c");
        File.WriteAllText(Path.Combine(root.Path, "Target.cs"), "123456");
        var client = new FakeLspClient();
        client.EnqueueResponse(new Lsp.Location(
            new Uri(Path.Combine(root.Path, "Target.cs")).AbsoluteUri,
            new Lsp.Range(new Position(0, 0), new Position(0, 1))));
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session, maxDocumentBytes: 5);

        var result = await tools.PeekDefinition("Program.cs", line: 1, column: 1);

        var peek = Assert.IsType<PeekDefinitionResult>(result);
        var item = Assert.Single(peek.Items);
        Assert.Null(item.Snippet);
        Assert.NotNull(item.SnippetError);
        Assert.Equal("document_too_large", item.SnippetError.Error);
    }

    [Fact]
    public async Task PeekDefinition_TruncatesLongSnippetTextPerItem()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "class Caller { }");
        File.WriteAllText(Path.Combine(root.Path, "Target.cs"), new string('x', 20_010));
        var client = new FakeLspClient();
        client.EnqueueResponse(new Lsp.Location(
            new Uri(Path.Combine(root.Path, "Target.cs")).AbsoluteUri,
            new Lsp.Range(new Position(0, 0), new Position(0, 1))));
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.PeekDefinition("Program.cs", line: 1, column: 7, contextLines: 0);

        var peek = Assert.IsType<PeekDefinitionResult>(result);
        Assert.False(peek.Truncated);
        var item = Assert.Single(peek.Items);
        Assert.NotNull(item.Snippet);
        Assert.Equal(20_000, item.Snippet.Text.Length);
        Assert.True(item.Snippet.Truncated);
        Assert.Null(item.SnippetError);
    }

    [Fact]
    public async Task PeekDefinition_ReturnsPerItemSnippetErrorForOutOfRangeDefinitionLine()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "class Caller { }");
        File.WriteAllText(Path.Combine(root.Path, "Target.cs"), "class Target { }");
        var client = new FakeLspClient();
        client.EnqueueResponse(new Lsp.Location(
            new Uri(Path.Combine(root.Path, "Target.cs")).AbsoluteUri,
            new Lsp.Range(new Position(9, 0), new Position(9, 1))));
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.PeekDefinition("Program.cs", line: 1, column: 7);

        var peek = Assert.IsType<PeekDefinitionResult>(result);
        var item = Assert.Single(peek.Items);
        Assert.Null(item.Snippet);
        Assert.NotNull(item.SnippetError);
        Assert.Equal("invalid_range", item.SnippetError.Error);
    }

    [Fact]
    public async Task PeekDefinition_ReturnsValidationErrorForInvalidMaxDefinitions()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "class Caller { }");
        var client = new FakeLspClient();
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.PeekDefinition("Program.cs", line: 1, column: 7, maxDefinitions: 0);

        var error = Assert.IsType<ToolError>(result);
        Assert.Equal("invalid_max_results", error.Error);
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task PeekDefinition_ReturnsValidationErrorForNegativeContextLines()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "class Caller { }");
        var client = new FakeLspClient();
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.PeekDefinition("Program.cs", line: 1, column: 7, contextLines: -1);

        var error = Assert.IsType<ToolError>(result);
        Assert.Equal("invalid_context_lines", error.Error);
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task FindReferences_PassesIncludeDeclarationInLspContext()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "class C { }");
        var client = new FakeLspClient();
        client.EnqueueResponse(Array.Empty<Lsp.Location>());
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.FindReferences("Program.cs", line: 1, column: 7, includeDeclaration: false);

        Assert.IsType<ReferencesResult>(result);
        var request = Assert.Single(client.Requests);
        Assert.Equal("textDocument/references", request.Method);
        Assert.True(request.IsExpensive);
        Assert.False(request.Params.GetProperty("context").GetProperty("includeDeclaration").GetBoolean());
    }

    [Fact]
    public async Task FindReferences_TruncatesLargeResponseAndReportsMetadata()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "class C { }");
        var uri = new Uri(Path.Combine(root.Path, "Program.cs")).AbsoluteUri;
        var client = new FakeLspClient();
        client.EnqueueResponse(new[]
        {
            new Lsp.Location(uri, new Lsp.Range(new Position(0, 0), new Position(0, 1))),
            new Lsp.Location(uri, new Lsp.Range(new Position(1, 0), new Position(1, 1))),
            new Lsp.Location(uri, new Lsp.Range(new Position(2, 0), new Position(2, 1)))
        });
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.FindReferences("Program.cs", line: 1, column: 7, maxResults: 2);

        var references = Assert.IsType<ReferencesResult>(result);
        Assert.Equal(3, references.TotalKnown);
        Assert.Equal(2, references.Returned);
        Assert.True(references.Truncated);
        Assert.Equal(2, references.Items.Count);
    }

    [Fact]
    public async Task FindReferences_IncludesPartialMetadataWhileWorkspaceIsWarming()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "class C { }");
        var client = new FakeLspClient();
        client.EnqueueResponse(Array.Empty<Lsp.Location>());
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.FindReferences("Program.cs", line: 1, column: 7);

        var references = Assert.IsType<ReferencesResult>(result);
        Assert.Equal(WorkspaceLoadState.WorkspaceWarming.ToString(), references.WorkspaceState);
        Assert.Equal("partial", references.Completeness);
        Assert.Equal(ToolRetryHints.WorkspaceWarmingMs, references.RetryAfterMs);
        Assert.Contains("cross-project", references.Reason);
    }

    [Fact]
    public async Task FindReferences_AllowsEmptyLineColumnOneAndLineEndPosition()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "alpha\r\n\r\nomega");
        var client = new FakeLspClient();
        client.EnqueueResponse(Array.Empty<Lsp.Location>());
        client.EnqueueResponse(Array.Empty<Lsp.Location>());
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var emptyLineResult = await tools.FindReferences("Program.cs", line: 2, column: 1);
        var lineEndResult = await tools.GoToDefinition("Program.cs", line: 1, column: 6);

        Assert.IsType<ReferencesResult>(emptyLineResult);
        Assert.IsType<DefinitionResult>(lineEndResult);
        Assert.Collection(
            client.Requests,
            request =>
            {
                Assert.Equal("textDocument/references", request.Method);
                Assert.Equal(1, request.Params.GetProperty("position").GetProperty("line").GetInt32());
                Assert.Equal(0, request.Params.GetProperty("position").GetProperty("character").GetInt32());
            },
            request =>
            {
                Assert.Equal("textDocument/definition", request.Method);
                Assert.Equal(0, request.Params.GetProperty("position").GetProperty("line").GetInt32());
                Assert.Equal(5, request.Params.GetProperty("position").GetProperty("character").GetInt32());
            });
    }

    [Fact]
    public async Task FindReferences_ReturnsPositionOutOfRangeForColumnTwoOnEmptyLine()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "alpha\n\nomega");
        var client = new FakeLspClient();
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.FindReferences("Program.cs", line: 2, column: 2);

        var error = Assert.IsType<ToolError>(result);
        Assert.Equal("position_out_of_range", error.Error);
        Assert.Contains("line 2", error.Message);
        Assert.Contains("column 2", error.Message);
        Assert.Contains("1..1", error.Message);
        Assert.Contains("line length 0", error.Message);
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task PeekReferences_PassesIncludeDeclarationAndReturnsSnippets()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "class C { void M() { } }");
        File.WriteAllText(Path.Combine(root.Path, "References.cs"), """
            class Uses
            {
                void One() => new C().M();
                void Two() => new C().M();
            }
            """);
        var client = new FakeLspClient();
        client.EnqueueResponse(new[]
        {
            new Lsp.Location(
                new Uri(Path.Combine(root.Path, "References.cs")).AbsoluteUri,
                new Lsp.Range(new Position(2, 25), new Position(2, 26)))
        });
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.PeekReferences(
            "Program.cs",
            line: 1,
            column: 16,
            includeDeclaration: false,
            contextLines: 1);

        var peek = Assert.IsType<PeekReferencesResult>(result);
        Assert.Equal(1, peek.TotalKnown);
        Assert.Equal(1, peek.Returned);
        Assert.False(peek.Truncated);
        var item = Assert.Single(peek.Items);
        Assert.Equal("References.cs", item.File);
        Assert.Equal(3, item.Line);
        Assert.Equal(26, item.Column);
        Assert.Null(item.SnippetError);
        Assert.NotNull(item.Snippet);
        Assert.Equal(2, item.Snippet.StartLine);
        Assert.Equal(4, item.Snippet.EndLine);
        Assert.Contains("void One() => new C().M();", item.Snippet.Text);
        Assert.False(item.Snippet.Truncated);

        var request = Assert.Single(client.Requests);
        Assert.Equal("textDocument/references", request.Method);
        Assert.True(request.IsExpensive);
        Assert.False(request.Params.GetProperty("context").GetProperty("includeDeclaration").GetBoolean());
    }

    [Fact]
    public async Task PeekReferences_TruncatesLargeResponseAndReportsMetadata()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "class C { void M() { } }");
        File.WriteAllText(Path.Combine(root.Path, "References.cs"), """
            line 1
            line 2
            line 3
            """);
        var uri = new Uri(Path.Combine(root.Path, "References.cs")).AbsoluteUri;
        var client = new FakeLspClient();
        client.EnqueueResponse(new[]
        {
            new Lsp.Location(uri, new Lsp.Range(new Position(0, 0), new Position(0, 1))),
            new Lsp.Location(uri, new Lsp.Range(new Position(1, 0), new Position(1, 1))),
            new Lsp.Location(uri, new Lsp.Range(new Position(2, 0), new Position(2, 1)))
        });
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.PeekReferences("Program.cs", line: 1, column: 16, maxResults: 2, contextLines: 0);

        var peek = Assert.IsType<PeekReferencesResult>(result);
        Assert.Equal(3, peek.TotalKnown);
        Assert.Equal(2, peek.Returned);
        Assert.True(peek.Truncated);
        Assert.Equal(2, peek.Items.Count);
        Assert.All(peek.Items, item =>
        {
            Assert.NotNull(item.Snippet);
            Assert.Equal(item.Line, item.Snippet.StartLine);
            Assert.Equal(item.Line, item.Snippet.EndLine);
            Assert.False(item.Snippet.Truncated);
        });
    }

    [Fact]
    public async Task PeekReferences_IncludesPartialMetadataWhileWorkspaceIsWarming()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "class C { void M() { } }");
        var client = new FakeLspClient();
        client.EnqueueResponse(Array.Empty<Lsp.Location>());
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.PeekReferences("Program.cs", line: 1, column: 16);

        var peek = Assert.IsType<PeekReferencesResult>(result);
        Assert.Equal(WorkspaceLoadState.WorkspaceWarming.ToString(), peek.WorkspaceState);
        Assert.Equal("partial", peek.Completeness);
        Assert.Equal(ToolRetryHints.WorkspaceWarmingMs, peek.RetryAfterMs);
        Assert.Contains("cross-project", peek.Reason);
    }

    [Fact]
    public async Task PeekReferences_ReturnsValidationErrorForInvalidMaxResults()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "class C { void M() { } }");
        var client = new FakeLspClient();
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.PeekReferences("Program.cs", line: 1, column: 16, maxResults: 0);

        var error = Assert.IsType<ToolError>(result);
        Assert.Equal("invalid_max_results", error.Error);
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task PeekReferences_ReturnsValidationErrorForNegativeContextLines()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "class C { void M() { } }");
        var client = new FakeLspClient();
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.PeekReferences("Program.cs", line: 1, column: 16, contextLines: -1);

        var error = Assert.IsType<ToolError>(result);
        Assert.Equal("invalid_context_lines", error.Error);
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task PeekReferences_ReturnsPerItemSnippetErrorWhenTargetFileExceedsDocumentLimit()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "c");
        File.WriteAllText(Path.Combine(root.Path, "References.cs"), "123456");
        var client = new FakeLspClient();
        client.EnqueueResponse(new Lsp.Location(
            new Uri(Path.Combine(root.Path, "References.cs")).AbsoluteUri,
            new Lsp.Range(new Position(0, 0), new Position(0, 1))));
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session, maxDocumentBytes: 5);

        var result = await tools.PeekReferences("Program.cs", line: 1, column: 1);

        var peek = Assert.IsType<PeekReferencesResult>(result);
        var item = Assert.Single(peek.Items);
        Assert.Null(item.Snippet);
        Assert.NotNull(item.SnippetError);
        Assert.Equal("document_too_large", item.SnippetError.Error);
    }

    [Fact]
    public async Task PeekReferences_TruncatesLongSnippetTextPerItem()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "class C { void M() { } }");
        File.WriteAllText(Path.Combine(root.Path, "References.cs"), new string('x', 20_010));
        var client = new FakeLspClient();
        client.EnqueueResponse(new Lsp.Location(
            new Uri(Path.Combine(root.Path, "References.cs")).AbsoluteUri,
            new Lsp.Range(new Position(0, 0), new Position(0, 1))));
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.PeekReferences("Program.cs", line: 1, column: 16, contextLines: 0);

        var peek = Assert.IsType<PeekReferencesResult>(result);
        Assert.False(peek.Truncated);
        var item = Assert.Single(peek.Items);
        Assert.NotNull(item.Snippet);
        Assert.Equal(20_000, item.Snippet.Text.Length);
        Assert.True(item.Snippet.Truncated);
        Assert.Null(item.SnippetError);
    }

    [Fact]
    public async Task FindImplementations_UsesImplementationMethodAndConvertsPosition()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(
            Path.Combine(root.Path, "Program.cs"),
            string.Join(Environment.NewLine, Enumerable.Repeat("// padding", 11).Append("interface I { }")));
        var client = new FakeLspClient();
        client.EnqueueResponse(Array.Empty<Lsp.Location>());
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.FindImplementations("Program.cs", line: 12, column: 5);

        var implementations = Assert.IsType<ImplementationsResult>(result);
        Assert.Contains("contract positions", implementations.UsageHint);
        Assert.Contains("concrete", implementations.UsageHint);
        var request = Assert.Single(client.Requests);
        Assert.Equal("textDocument/implementation", request.Method);
        Assert.True(request.IsExpensive);
        Assert.Equal(11, request.Params.GetProperty("position").GetProperty("line").GetInt32());
        Assert.Equal(4, request.Params.GetProperty("position").GetProperty("character").GetInt32());
    }

    [Fact]
    public async Task FindImplementations_ReturnsPositionOutOfRangeWhenColumnExceedsLineEnd()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "interface I { }");
        var client = new FakeLspClient();
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.FindImplementations("Program.cs", line: 1, column: 17);

        var error = Assert.IsType<ToolError>(result);
        Assert.Equal("position_out_of_range", error.Error);
        Assert.Contains("line 1", error.Message);
        Assert.Contains("column 17", error.Message);
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task FindImplementations_MapsSingleLocationToRootRelativeOneBasedLocation()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "interface I { }");
        var client = new FakeLspClient();
        client.EnqueueResponse(new Lsp.Location(
            new Uri(Path.Combine(root.Path, "Program.cs")).AbsoluteUri,
            new Lsp.Range(new Position(2, 4), new Position(2, 9))));
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.FindImplementations("Program.cs", line: 1, column: 11);

        var implementations = Assert.IsType<ImplementationsResult>(result);
        var item = Assert.Single(implementations.Items);
        Assert.Equal("Program.cs", item.File);
        Assert.Equal(3, item.Line);
        Assert.Equal(5, item.Column);
        Assert.Equal(3, item.Range.StartLine);
        Assert.Equal(5, item.Range.StartColumn);
    }

    [Fact]
    public async Task FindImplementations_MapsLocationLinkUsingTargetRange()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "interface I { }");
        var client = new FakeLspClient();
        client.EnqueueResponse(new
        {
            targetUri = new Uri(Path.Combine(root.Path, "Program.cs")).AbsoluteUri,
            targetRange = new Lsp.Range(new Position(4, 8), new Position(4, 14)),
            targetSelectionRange = new Lsp.Range(new Position(4, 20), new Position(4, 21))
        });
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.FindImplementations("Program.cs", line: 1, column: 11);

        var implementations = Assert.IsType<ImplementationsResult>(result);
        var item = Assert.Single(implementations.Items);
        Assert.Equal(5, item.Line);
        Assert.Equal(9, item.Column);
    }

    [Fact]
    public async Task FindImplementations_ReturnsEmptyResultForNullOrEmptyResponse()
    {
        var nullResult = await ExecuteImplementationsWithResponse(null);
        var emptyResult = await ExecuteImplementationsWithResponse(Array.Empty<Lsp.Location>());

        var nullImplementations = Assert.IsType<ImplementationsResult>(nullResult);
        Assert.Empty(nullImplementations.Items);
        Assert.Equal(0, nullImplementations.TotalKnown);
        Assert.Equal(0, nullImplementations.Returned);
        Assert.False(nullImplementations.Truncated);

        var emptyImplementations = Assert.IsType<ImplementationsResult>(emptyResult);
        Assert.Empty(emptyImplementations.Items);
        Assert.Equal(0, emptyImplementations.TotalKnown);
        Assert.Equal(0, emptyImplementations.Returned);
        Assert.False(emptyImplementations.Truncated);
    }

    [Fact]
    public async Task FindImplementations_TruncatesLargeResponseAndReportsMetadata()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "interface I { }");
        var uri = new Uri(Path.Combine(root.Path, "Program.cs")).AbsoluteUri;
        var client = new FakeLspClient();
        client.EnqueueResponse(new[]
        {
            new Lsp.Location(uri, new Lsp.Range(new Position(0, 0), new Position(0, 1))),
            new Lsp.Location(uri, new Lsp.Range(new Position(1, 0), new Position(1, 1))),
            new Lsp.Location(uri, new Lsp.Range(new Position(2, 0), new Position(2, 1)))
        });
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.FindImplementations("Program.cs", line: 1, column: 11, maxResults: 2);

        var implementations = Assert.IsType<ImplementationsResult>(result);
        Assert.Equal(3, implementations.TotalKnown);
        Assert.Equal(2, implementations.Returned);
        Assert.True(implementations.Truncated);
        Assert.Equal(2, implementations.Items.Count);
    }

    [Fact]
    public async Task FindImplementations_DoesNotExposeOutsideRootOrInvalidUriResults()
    {
        using var root = TestRoot.Create();
        using var outside = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "interface I { }");
        File.WriteAllText(Path.Combine(outside.Path, "Outside.cs"), "class Outside { }");
        var insideUri = new Uri(Path.Combine(root.Path, "Program.cs")).AbsoluteUri;
        var outsideUri = new Uri(Path.Combine(outside.Path, "Outside.cs")).AbsoluteUri;
        var client = new FakeLspClient();
        client.EnqueueResponse(new object[]
        {
            new Lsp.Location(outsideUri, new Lsp.Range(new Position(0, 0), new Position(0, 1))),
            new Lsp.Location("https://example.test/Remote.cs", new Lsp.Range(new Position(1, 0), new Position(1, 1))),
            new
            {
                uri = "not a uri",
                range = new Lsp.Range(new Position(2, 0), new Position(2, 1))
            },
            new Lsp.Location(insideUri, new Lsp.Range(new Position(3, 4), new Position(3, 9)))
        });
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.FindImplementations("Program.cs", line: 1, column: 11);

        var implementations = Assert.IsType<ImplementationsResult>(result);
        var item = Assert.Single(implementations.Items);
        Assert.Equal("Program.cs", item.File);
        Assert.Equal(4, item.Line);
        Assert.Equal(5, item.Column);
        Assert.Equal(1, implementations.TotalKnown);
        Assert.Equal(1, implementations.Returned);
        Assert.False(implementations.Truncated);
        Assert.DoesNotContain(implementations.Items, implementation => implementation.File.Contains("Outside", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(implementations.Items, implementation => implementation.File.StartsWith("http", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task FindImplementations_TruncatesAtDefaultLimit()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "interface I { }");
        var client = new FakeLspClient();
        client.EnqueueResponse(CreateImplementationLocations(root.Path, count: 201));
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.FindImplementations("Program.cs", line: 1, column: 11);

        var implementations = Assert.IsType<ImplementationsResult>(result);
        Assert.Equal(201, implementations.TotalKnown);
        Assert.Equal(200, implementations.Returned);
        Assert.Equal(200, implementations.Items.Count);
        Assert.True(implementations.Truncated);
    }

    [Fact]
    public async Task FindImplementations_ClampsUserMaxResultsToHardCap()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "interface I { }");
        var client = new FakeLspClient();
        client.EnqueueResponse(CreateImplementationLocations(root.Path, count: 1001));
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.FindImplementations("Program.cs", line: 1, column: 11, maxResults: 5000);

        var implementations = Assert.IsType<ImplementationsResult>(result);
        Assert.Equal(1001, implementations.TotalKnown);
        Assert.Equal(1000, implementations.Returned);
        Assert.Equal(1000, implementations.Items.Count);
        Assert.True(implementations.Truncated);
    }

    [Fact]
    public async Task FindImplementations_ReturnsValidationErrorForInvalidMaxResults()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "interface I { }");
        var client = new FakeLspClient();
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.FindImplementations("Program.cs", line: 1, column: 11, maxResults: 0);

        var error = Assert.IsType<ToolError>(result);
        Assert.Equal("invalid_max_results", error.Error);
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task FindImplementations_IncludesPartialMetadataWhileWorkspaceIsWarming()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "interface I { }");
        var client = new FakeLspClient();
        client.EnqueueResponse(Array.Empty<Lsp.Location>());
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.FindImplementations("Program.cs", line: 1, column: 11);

        var implementations = Assert.IsType<ImplementationsResult>(result);
        Assert.Equal(WorkspaceLoadState.WorkspaceWarming.ToString(), implementations.WorkspaceState);
        Assert.Equal("partial", implementations.Completeness);
        Assert.Equal(ToolRetryHints.WorkspaceWarmingMs, implementations.RetryAfterMs);
        Assert.Contains("implementations", implementations.Reason);
    }

    [Fact]
    public async Task GetCallHierarchy_PrepareRequestUsesZeroBasedPositionAndIsNotExpensive()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(
            Path.Combine(root.Path, "Program.cs"),
            string.Join(Environment.NewLine, Enumerable.Repeat("// padding", 11).Append("class C { void M() { } }")));
        var client = new FakeLspClient();
        client.EnqueueResponse(Array.Empty<object>());
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.GetCallHierarchy("Program.cs", line: 12, column: 5);

        var hierarchy = Assert.IsType<CallHierarchyResult>(result);
        Assert.Empty(hierarchy.Roots);
        Assert.Empty(hierarchy.Edges);
        var request = Assert.Single(client.Requests);
        Assert.Equal("textDocument/prepareCallHierarchy", request.Method);
        Assert.False(request.IsExpensive);
        Assert.Equal(11, request.Params.GetProperty("position").GetProperty("line").GetInt32());
        Assert.Equal(4, request.Params.GetProperty("position").GetProperty("character").GetInt32());
    }

    [Fact]
    public async Task GetCallHierarchy_ReturnsEmptyWhenPrepareIsNullOrEmptyAndDoesNotCallFollowUp()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "class C { void M() { } }");
        var client = new FakeLspClient();
        client.EnqueueResponse(null);
        client.EnqueueResponse(Array.Empty<object>());
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var nullResult = await tools.GetCallHierarchy("Program.cs", line: 1, column: 16);
        var emptyResult = await tools.GetCallHierarchy("Program.cs", line: 1, column: 16);

        var nullHierarchy = Assert.IsType<CallHierarchyResult>(nullResult);
        var emptyHierarchy = Assert.IsType<CallHierarchyResult>(emptyResult);
        Assert.Empty(nullHierarchy.Roots);
        Assert.Empty(nullHierarchy.Edges);
        Assert.Empty(emptyHierarchy.Roots);
        Assert.Empty(emptyHierarchy.Edges);
        Assert.Equal(["textDocument/prepareCallHierarchy", "textDocument/prepareCallHierarchy"], client.Requests.Select(r => r.Method));
    }

    [Fact]
    public async Task GetCallHierarchy_MapsIncomingCallerToRootAndCallSites()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "class Target { void M() { } }");
        var targetUri = CreateFileUri(root.Path, "Target.cs");
        var callerUri = CreateFileUri(root.Path, "Caller.cs");
        var target = CreateCallHierarchyItem("M", targetUri, range: CreateRange(1, 4, 1, 12), detail: "void Target.M()");
        var caller = CreateCallHierarchyItem("Call", callerUri, range: CreateRange(3, 8, 3, 16), detail: "void Caller.Call()");
        var client = new FakeLspClient();
        client.EnqueueResponse(new[] { target });
        client.EnqueueResponse(new[]
        {
            CreateIncomingCall(
                caller,
                CreateRange(4, 20, 4, 23),
                CreateRange(5, 12, 5, 15))
        });
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.GetCallHierarchy("Program.cs", line: 1, column: 21);

        var hierarchy = Assert.IsType<CallHierarchyResult>(result);
        var rootSymbol = Assert.Single(hierarchy.Roots);
        var edge = Assert.Single(hierarchy.Edges);
        Assert.Equal(rootSymbol.Id, edge.RootId);
        Assert.Equal("incoming", edge.Direction);
        Assert.Equal(1, edge.Depth);
        Assert.Equal("Call", edge.From.Name);
        Assert.Equal("M", edge.To.Name);
        Assert.Equal("Caller.cs", edge.From.Location?.File);
        Assert.Equal("Target.cs", edge.To.Location?.File);
        Assert.Equal(2, edge.TotalCallSites);
        Assert.False(edge.CallSitesTruncated);
        Assert.Collection(
            edge.CallSites,
            site =>
            {
                Assert.Equal("Caller.cs", site.File);
                Assert.Equal(5, site.Line);
                Assert.Equal(21, site.Column);
                Assert.Equal(5, site.Range.StartLine);
                Assert.Equal(21, site.Range.StartColumn);
            },
            site =>
            {
                Assert.Equal("Caller.cs", site.File);
                Assert.Equal(6, site.Line);
                Assert.Equal(13, site.Column);
            });
        Assert.Equal(1, hierarchy.TotalKnown);
        Assert.Equal(1, hierarchy.TotalUnfilteredKnown);
        Assert.Equal(1, hierarchy.Returned);
        Assert.False(hierarchy.Truncated);
        Assert.Equal(["textDocument/prepareCallHierarchy", "callHierarchy/incomingCalls"], client.Requests.Select(r => r.Method));
        Assert.True(client.Requests[1].IsExpensive);
    }

    [Fact]
    public async Task GetCallHierarchy_MapsOutgoingRootToCallee()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "class Target { void M() { } }");
        var targetUri = CreateFileUri(root.Path, "Target.cs");
        var calleeUri = CreateFileUri(root.Path, "Callee.cs");
        var target = CreateCallHierarchyItem("M", targetUri, range: CreateRange(1, 4, 1, 12));
        var callee = CreateCallHierarchyItem("Other", calleeUri, range: CreateRange(6, 8, 6, 16));
        var client = new FakeLspClient();
        client.EnqueueResponse(new[] { target });
        client.EnqueueResponse(new[]
        {
            CreateOutgoingCall(callee, CreateRange(2, 18, 2, 23))
        });
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.GetCallHierarchy("Program.cs", line: 1, column: 21, direction: "outgoing");

        var hierarchy = Assert.IsType<CallHierarchyResult>(result);
        var rootSymbol = Assert.Single(hierarchy.Roots);
        var edge = Assert.Single(hierarchy.Edges);
        Assert.Equal(rootSymbol.Id, edge.RootId);
        Assert.Equal("outgoing", edge.Direction);
        Assert.Equal("M", edge.From.Name);
        Assert.Equal("Other", edge.To.Name);
        Assert.Equal("Target.cs", edge.From.Location?.File);
        Assert.Equal("Callee.cs", edge.To.Location?.File);
        var callSite = Assert.Single(edge.CallSites);
        Assert.Equal("Target.cs", callSite.File);
        Assert.Equal(3, callSite.Line);
        Assert.Equal(19, callSite.Column);
        Assert.Equal(["textDocument/prepareCallHierarchy", "callHierarchy/outgoingCalls"], client.Requests.Select(r => r.Method));
        Assert.True(client.Requests[1].IsExpensive);
    }

    [Fact]
    public async Task GetCallHierarchy_BothRequestsIncomingThenOutgoingAndReturnsDeterministicEdges()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "class Target { void M() { } }");
        var target = CreateCallHierarchyItem("M", CreateFileUri(root.Path, "Target.cs"));
        var caller = CreateCallHierarchyItem("Caller", CreateFileUri(root.Path, "Caller.cs"));
        var callee = CreateCallHierarchyItem("Callee", CreateFileUri(root.Path, "Callee.cs"));
        var client = new FakeLspClient();
        client.EnqueueResponse(new[] { target });
        client.EnqueueResponse(new[] { CreateIncomingCall(caller, CreateRange(2, 0, 2, 1)) });
        client.EnqueueResponse(new[] { CreateOutgoingCall(callee, CreateRange(3, 0, 3, 1)) });
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.GetCallHierarchy("Program.cs", line: 1, column: 21, direction: "both");

        var hierarchy = Assert.IsType<CallHierarchyResult>(result);
        Assert.Equal(["incoming", "outgoing"], hierarchy.Edges.Select(edge => edge.Direction));
        Assert.Equal("Caller", hierarchy.Edges[0].From.Name);
        Assert.Equal("Callee", hierarchy.Edges[1].To.Name);
        Assert.Equal(
            ["textDocument/prepareCallHierarchy", "callHierarchy/incomingCalls", "callHierarchy/outgoingCalls"],
            client.Requests.Select(r => r.Method));
        Assert.True(client.Requests[1].IsExpensive);
        Assert.True(client.Requests[2].IsExpensive);
    }

    [Fact]
    public async Task GetCallHierarchy_MethodKindFilterExcludesFieldEdges()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "class Target { void M() { } }");
        var target = CreateCallHierarchyItem("M", CreateFileUri(root.Path, "Target.cs"));
        var method = CreateCallHierarchyItem("Call", CreateFileUri(root.Path, "Method.cs"), SymbolKind.Method);
        var field = CreateCallHierarchyItem("Value", CreateFileUri(root.Path, "Field.cs"), SymbolKind.Field);
        var client = new FakeLspClient();
        client.EnqueueResponse(new[] { target });
        client.EnqueueResponse(new[]
        {
            CreateOutgoingCall(method, CreateRange(1, 0, 1, 1)),
            CreateOutgoingCall(field, CreateRange(2, 0, 2, 1))
        });
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.GetCallHierarchy(
            "Program.cs",
            line: 1,
            column: 21,
            direction: "outgoing",
            kindFilter: new[] { "method" });

        var hierarchy = Assert.IsType<CallHierarchyResult>(result);
        var edge = Assert.Single(hierarchy.Edges);
        Assert.Equal("Call", edge.To.Name);
        Assert.Equal(SymbolKind.Method, edge.To.Kind);
        Assert.Equal(2, hierarchy.TotalUnfilteredKnown);
        Assert.Equal(1, hierarchy.TotalKnown);
        Assert.Equal(1, hierarchy.Returned);
        Assert.False(hierarchy.Truncated);
        Assert.False(client.Requests[0].Params.TryGetProperty("kindFilter", out _));
        Assert.False(client.Requests[1].Params.TryGetProperty("kindFilter", out _));
    }

    [Fact]
    public async Task GetCallHierarchy_FieldKindFilterReturnsOnlyFieldEdges()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "class Target { void M() { } }");
        var target = CreateCallHierarchyItem("M", CreateFileUri(root.Path, "Target.cs"));
        var method = CreateCallHierarchyItem("Call", CreateFileUri(root.Path, "Method.cs"), SymbolKind.Method);
        var field = CreateCallHierarchyItem("Value", CreateFileUri(root.Path, "Field.cs"), SymbolKind.Field);
        var client = new FakeLspClient();
        client.EnqueueResponse(new[] { target });
        client.EnqueueResponse(new[]
        {
            CreateOutgoingCall(method, CreateRange(1, 0, 1, 1)),
            CreateOutgoingCall(field, CreateRange(2, 0, 2, 1))
        });
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.GetCallHierarchy(
            "Program.cs",
            line: 1,
            column: 21,
            direction: "outgoing",
            kindFilter: new[] { "field" });

        var hierarchy = Assert.IsType<CallHierarchyResult>(result);
        var edge = Assert.Single(hierarchy.Edges);
        Assert.Equal("Value", edge.To.Name);
        Assert.Equal(SymbolKind.Field, edge.To.Kind);
        Assert.Equal(2, hierarchy.TotalUnfilteredKnown);
        Assert.Equal(1, hierarchy.TotalKnown);
        Assert.Equal(1, hierarchy.Returned);
    }

    [Fact]
    public async Task GetCallHierarchy_BothKindFilterUsesDirectionCounterpartSymbols()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "class Target { int P => 1; }");
        var target = CreateCallHierarchyItem("P", CreateFileUri(root.Path, "Target.cs"), SymbolKind.Property);
        var incomingProperty = CreateCallHierarchyItem("CallerProperty", CreateFileUri(root.Path, "CallerProperty.cs"), SymbolKind.Property);
        var incomingMethod = CreateCallHierarchyItem("CallerMethod", CreateFileUri(root.Path, "CallerMethod.cs"), SymbolKind.Method);
        var outgoingProperty = CreateCallHierarchyItem("CalleeProperty", CreateFileUri(root.Path, "CalleeProperty.cs"), SymbolKind.Property);
        var outgoingMethod = CreateCallHierarchyItem("CalleeMethod", CreateFileUri(root.Path, "CalleeMethod.cs"), SymbolKind.Method);
        var client = new FakeLspClient();
        client.EnqueueResponse(new[] { target });
        client.EnqueueResponse(new[]
        {
            CreateIncomingCall(incomingProperty, CreateRange(1, 0, 1, 1)),
            CreateIncomingCall(incomingMethod, CreateRange(2, 0, 2, 1))
        });
        client.EnqueueResponse(new[]
        {
            CreateOutgoingCall(outgoingProperty, CreateRange(3, 0, 3, 1)),
            CreateOutgoingCall(outgoingMethod, CreateRange(4, 0, 4, 1))
        });
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.GetCallHierarchy(
            "Program.cs",
            line: 1,
            column: 21,
            direction: "both",
            kindFilter: new[] { "property" });

        var hierarchy = Assert.IsType<CallHierarchyResult>(result);
        Assert.Equal(4, hierarchy.TotalUnfilteredKnown);
        Assert.Equal(2, hierarchy.TotalKnown);
        Assert.Equal(2, hierarchy.Returned);
        Assert.Equal(["incoming", "outgoing"], hierarchy.Edges.Select(edge => edge.Direction));
        Assert.Equal("CallerProperty", hierarchy.Edges[0].From.Name);
        Assert.Equal("P", hierarchy.Edges[0].To.Name);
        Assert.Equal("P", hierarchy.Edges[1].From.Name);
        Assert.Equal("CalleeProperty", hierarchy.Edges[1].To.Name);
    }

    [Fact]
    public async Task GetCallHierarchy_PreservesOriginalPreparedItemInFollowUpParams()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "class Target { void M() { } }");
        var range = CreateRange(1, 4, 1, 12);
        var target = new
        {
            name = "M",
            kind = (int)SymbolKind.Method,
            detail = "void Target.M()",
            uri = CreateFileUri(root.Path, "Target.cs"),
            range,
            selectionRange = range,
            data = new
            {
                opaque = "keep",
                nested = new
                {
                    value = 42
                }
            }
        };
        var client = new FakeLspClient();
        client.EnqueueResponse(new[] { target });
        client.EnqueueResponse(Array.Empty<object>());
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.GetCallHierarchy("Program.cs", line: 1, column: 21);

        Assert.IsType<CallHierarchyResult>(result);
        var followUpItem = client.Requests[1].Params.GetProperty("item");
        Assert.Equal("M", followUpItem.GetProperty("name").GetString());
        Assert.Equal("void Target.M()", followUpItem.GetProperty("detail").GetString());
        Assert.Equal("keep", followUpItem.GetProperty("data").GetProperty("opaque").GetString());
        Assert.Equal(42, followUpItem.GetProperty("data").GetProperty("nested").GetProperty("value").GetInt32());
    }

    [Fact]
    public async Task GetCallHierarchy_FiltersEdgesWithOutsideRootNonFileOrInvalidFromAndToUris()
    {
        using var root = TestRoot.Create();
        using var outside = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "class Target { void M() { } }");
        var target = CreateCallHierarchyItem("M", CreateFileUri(root.Path, "Target.cs"));
        var validCaller = CreateCallHierarchyItem("ValidCaller", CreateFileUri(root.Path, "Caller.cs"));
        var validCallee = CreateCallHierarchyItem("ValidCallee", CreateFileUri(root.Path, "Callee.cs"));
        var outsideSymbol = CreateCallHierarchyItem("Outside", new Uri(Path.Combine(outside.Path, "Outside.cs")).AbsoluteUri);
        var nonFileSymbol = CreateCallHierarchyItem("Remote", "https://example.test/Remote.cs");
        var invalidUriSymbol = CreateCallHierarchyItem("Invalid", "not a uri");
        var client = new FakeLspClient();
        client.EnqueueResponse(new[] { target });
        client.EnqueueResponse(new[]
        {
            CreateIncomingCall(outsideSymbol, CreateRange(0, 0, 0, 1)),
            CreateIncomingCall(nonFileSymbol, CreateRange(0, 0, 0, 1)),
            CreateIncomingCall(invalidUriSymbol, CreateRange(0, 0, 0, 1)),
            CreateIncomingCall(validCaller, CreateRange(1, 0, 1, 1))
        });
        client.EnqueueResponse(new[]
        {
            CreateOutgoingCall(outsideSymbol, CreateRange(0, 0, 0, 1)),
            CreateOutgoingCall(nonFileSymbol, CreateRange(0, 0, 0, 1)),
            CreateOutgoingCall(invalidUriSymbol, CreateRange(0, 0, 0, 1)),
            CreateOutgoingCall(validCallee, CreateRange(2, 0, 2, 1))
        });
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.GetCallHierarchy("Program.cs", line: 1, column: 21, direction: "both");

        var hierarchy = Assert.IsType<CallHierarchyResult>(result);
        Assert.Equal(2, hierarchy.TotalKnown);
        Assert.Equal(2, hierarchy.TotalUnfilteredKnown);
        Assert.Equal(2, hierarchy.Returned);
        Assert.Equal(["ValidCaller", "M"], hierarchy.Edges.Select(edge => edge.From.Name));
        Assert.Equal(["M", "ValidCallee"], hierarchy.Edges.Select(edge => edge.To.Name));
        Assert.DoesNotContain(hierarchy.Edges, edge => edge.From.Name is "Outside" or "Remote" or "Invalid");
        Assert.DoesNotContain(hierarchy.Edges, edge => edge.To.Name is "Outside" or "Remote" or "Invalid");
    }

    [Fact]
    public async Task GetCallHierarchy_TruncatesByEdgeCount()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "class Target { void M() { } }");
        var target = CreateCallHierarchyItem("M", CreateFileUri(root.Path, "Target.cs"));
        var client = new FakeLspClient();
        client.EnqueueResponse(new[] { target });
        client.EnqueueResponse(new[]
        {
            CreateIncomingCall(CreateCallHierarchyItem("Caller1", CreateFileUri(root.Path, "Caller1.cs")), CreateRange(0, 0, 0, 1)),
            CreateIncomingCall(CreateCallHierarchyItem("Caller2", CreateFileUri(root.Path, "Caller2.cs")), CreateRange(1, 0, 1, 1)),
            CreateIncomingCall(CreateCallHierarchyItem("Caller3", CreateFileUri(root.Path, "Caller3.cs")), CreateRange(2, 0, 2, 1))
        });
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.GetCallHierarchy("Program.cs", line: 1, column: 21, maxResults: 2);

        var hierarchy = Assert.IsType<CallHierarchyResult>(result);
        Assert.Equal(3, hierarchy.TotalKnown);
        Assert.Equal(2, hierarchy.Returned);
        Assert.Equal(2, hierarchy.Edges.Count);
        Assert.True(hierarchy.Truncated);
        Assert.Equal(["Caller1", "Caller2"], hierarchy.Edges.Select(edge => edge.From.Name));
    }

    [Fact]
    public async Task GetCallHierarchy_AppliesMaxResultsAndCountsAfterKindFiltering()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "class Target { void M() { } }");
        var target = CreateCallHierarchyItem("M", CreateFileUri(root.Path, "Target.cs"));
        var client = new FakeLspClient();
        client.EnqueueResponse(new[] { target });
        client.EnqueueResponse(new[]
        {
            CreateIncomingCall(CreateCallHierarchyItem("Field1", CreateFileUri(root.Path, "Field1.cs"), SymbolKind.Field), CreateRange(0, 0, 0, 1)),
            CreateIncomingCall(CreateCallHierarchyItem("Method1", CreateFileUri(root.Path, "Method1.cs"), SymbolKind.Method), CreateRange(1, 0, 1, 1)),
            CreateIncomingCall(CreateCallHierarchyItem("Field2", CreateFileUri(root.Path, "Field2.cs"), SymbolKind.Field), CreateRange(2, 0, 2, 1)),
            CreateIncomingCall(CreateCallHierarchyItem("Method2", CreateFileUri(root.Path, "Method2.cs"), SymbolKind.Method), CreateRange(3, 0, 3, 1)),
            CreateIncomingCall(CreateCallHierarchyItem("Method3", CreateFileUri(root.Path, "Method3.cs"), SymbolKind.Method), CreateRange(4, 0, 4, 1))
        });
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.GetCallHierarchy(
            "Program.cs",
            line: 1,
            column: 21,
            maxResults: 2,
            kindFilter: new[] { "method" });

        var hierarchy = Assert.IsType<CallHierarchyResult>(result);
        Assert.Equal(5, hierarchy.TotalUnfilteredKnown);
        Assert.Equal(3, hierarchy.TotalKnown);
        Assert.Equal(2, hierarchy.Returned);
        Assert.True(hierarchy.Truncated);
        Assert.Equal(["Method1", "Method2"], hierarchy.Edges.Select(edge => edge.From.Name));
    }

    [Fact]
    public async Task GetCallHierarchy_TruncatesCallSitesAndSetsMetadata()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "class Target { void M() { } }");
        var target = CreateCallHierarchyItem("M", CreateFileUri(root.Path, "Target.cs"));
        var caller = CreateCallHierarchyItem("Caller", CreateFileUri(root.Path, "Caller.cs"));
        var client = new FakeLspClient();
        client.EnqueueResponse(new[] { target });
        client.EnqueueResponse(new[] { CreateIncomingCall(caller, CreateCallSiteRanges(25)) });
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.GetCallHierarchy("Program.cs", line: 1, column: 21);

        var hierarchy = Assert.IsType<CallHierarchyResult>(result);
        var edge = Assert.Single(hierarchy.Edges);
        Assert.Equal(25, edge.TotalCallSites);
        Assert.Equal(20, edge.CallSites.Count);
        Assert.True(edge.CallSitesTruncated);
        Assert.Equal(1, hierarchy.TotalKnown);
        Assert.Equal(1, hierarchy.Returned);
        Assert.True(hierarchy.Truncated);
    }

    [Fact]
    public async Task GetCallHierarchy_ReturnsValidationErrorsForInvalidDirectionMaxResultsAndKindFilter()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "class Target { void M() { } }");
        var client = new FakeLspClient();
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var invalidDirectionResult = await tools.GetCallHierarchy("Program.cs", line: 1, column: 21, direction: "sideways");
        var invalidMaxResultsResult = await tools.GetCallHierarchy("Program.cs", line: 1, column: 21, maxResults: 0);
        var invalidKindResult = await tools.GetCallHierarchy("Program.cs", line: 1, column: 21, kindFilter: new[] { "class" });
        var emptyKindResult = await tools.GetCallHierarchy("Program.cs", line: 1, column: 21, kindFilter: Array.Empty<string>());

        var invalidDirection = Assert.IsType<ToolError>(invalidDirectionResult);
        Assert.Equal("invalid_direction", invalidDirection.Error);
        var invalidMaxResults = Assert.IsType<ToolError>(invalidMaxResultsResult);
        Assert.Equal("invalid_max_results", invalidMaxResults.Error);
        var invalidKind = Assert.IsType<ToolError>(invalidKindResult);
        Assert.Equal("invalid_kind_filter", invalidKind.Error);
        Assert.Contains("class", invalidKind.Message);
        Assert.Contains("method", invalidKind.Message);
        Assert.Contains("field", invalidKind.Message);
        var emptyKind = Assert.IsType<ToolError>(emptyKindResult);
        Assert.Equal("invalid_kind_filter", emptyKind.Error);
        Assert.Contains("at least one", emptyKind.Message);
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task GetCallHierarchy_IncludesPartialMetadataWhileWorkspaceIsWarming()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "class Target { void M() { } }");
        var target = CreateCallHierarchyItem("M", CreateFileUri(root.Path, "Target.cs"));
        var client = new FakeLspClient();
        client.EnqueueResponse(new[] { target });
        client.EnqueueResponse(Array.Empty<object>());
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.GetCallHierarchy("Program.cs", line: 1, column: 21);

        var hierarchy = Assert.IsType<CallHierarchyResult>(result);
        Assert.Equal(WorkspaceLoadState.WorkspaceWarming.ToString(), hierarchy.WorkspaceState);
        Assert.Equal("partial", hierarchy.Completeness);
        Assert.Equal(ToolRetryHints.WorkspaceWarmingMs, hierarchy.RetryAfterMs);
        Assert.Contains("call hierarchy", hierarchy.Reason);
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

    [Fact]
    public async Task DefinitionReferencesAndPeekReferences_DoNotExposeUrisOutsideRoot()
    {
        using var root = TestRoot.Create();
        using var outside = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "class C { }");
        File.WriteAllText(Path.Combine(outside.Path, "Outside.cs"), "class Outside { }");
        var insideUri = new Uri(Path.Combine(root.Path, "Program.cs")).AbsoluteUri;
        var outsideUri = new Uri(Path.Combine(outside.Path, "Outside.cs")).AbsoluteUri;
        var client = new FakeLspClient();
        client.EnqueueResponse(new[]
        {
            new Lsp.Location(outsideUri, new Lsp.Range(new Position(0, 0), new Position(0, 1))),
            new Lsp.Location(insideUri, new Lsp.Range(new Position(0, 1), new Position(0, 2)))
        });
        client.EnqueueResponse(new[]
        {
            new Lsp.Location(outsideUri, new Lsp.Range(new Position(0, 0), new Position(0, 1)))
        });
        client.EnqueueResponse(new[]
        {
            new Lsp.Location(outsideUri, new Lsp.Range(new Position(0, 0), new Position(0, 1))),
            new Lsp.Location(insideUri, new Lsp.Range(new Position(0, 6), new Position(0, 7)))
        });
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var definitionResult = await tools.GoToDefinition("Program.cs", line: 1, column: 7);
        var referencesResult = await tools.FindReferences("Program.cs", line: 1, column: 7);
        var peekReferencesResult = await tools.PeekReferences("Program.cs", line: 1, column: 7, contextLines: 0);

        var definition = Assert.IsType<DefinitionResult>(definitionResult);
        Assert.Single(definition.Items);
        Assert.Equal("Program.cs", definition.Items[0].File);
        var references = Assert.IsType<ReferencesResult>(referencesResult);
        Assert.Empty(references.Items);
        Assert.Equal(0, references.TotalKnown);
        var peekReferences = Assert.IsType<PeekReferencesResult>(peekReferencesResult);
        var peekItem = Assert.Single(peekReferences.Items);
        Assert.Equal("Program.cs", peekItem.File);
        Assert.NotNull(peekItem.Snippet);
        Assert.Equal(1, peekReferences.TotalKnown);
    }

    [Fact]
    public async Task LocationBasedRequests_SyncDocumentBeforeLspRequest()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "class C { }");
        var client = new FakeLspClient();
        client.EnqueueResponse(Array.Empty<Lsp.Location>());
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.GoToDefinition("Program.cs", line: 1, column: 7);

        Assert.IsType<DefinitionResult>(result);
        Assert.Equal(["notify:textDocument/didOpen", "request:textDocument/definition"], client.Events);
    }

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
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var client = new FakeLspClient();
        client.EnqueueResponse(response);
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        return await tools.FindSymbols("Symbol");
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
        var guard = new PathGuard(root);
        var mapper = new DocumentPathMapper(guard);
        return new NavigationTools(session, new DocumentStateManager(CreateOptions(root, maxDocumentBytes: maxDocumentBytes), mapper), mapper);
    }

    private static WorkspaceSession CreateSession(string root, IRoslynWorkspaceLoader loader)
    {
        var guard = new PathGuard(root);
        return new WorkspaceSession(new WorkspaceScanner(CreateOptions(root), guard, gitScanner: null), guard, loader);
    }

    private static WorkspaceSession CreateStartupLoadSession(
        string root,
        IRoslynWorkspaceLoader loader,
        string loadSolutionPath)
    {
        var guard = new PathGuard(root);
        var options = CreateOptions(root) with { LoadSolutionPath = loadSolutionPath };
        return new WorkspaceSession(
            new WorkspaceScanner(options, guard, gitScanner: null),
            guard,
            loader,
            options,
            documents: null,
            diagnostics: null);
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
            6,
            TimeSpan.FromSeconds(3),
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
            await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
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
        public List<(string Method, JsonElement Params, bool IsExpensive)> Requests { get; } = [];
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
            Requests.Add((method, JsonSerializer.SerializeToElement(parameters, JsonOptions.Default), isExpensive));
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
                await ReleaseShutdown.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            }
        }

        public void RaiseNotification(string method, JsonElement? parameters = null) =>
            NotificationReceived?.Invoke(method, parameters);
    }
}
