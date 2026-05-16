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
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "class C { string Name => \"\"; }");
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
        Assert.Contains("cross-project", references.Reason);
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

        Assert.Empty(Assert.IsType<FindSymbolsResult>(nullResult).Items);
        Assert.Empty(Assert.IsType<FindSymbolsResult>(emptyResult).Items);
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
        client.EnqueueResponse(CreateWorkspaceSymbols(count: 101));
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.FindSymbols("Symbol");

        var symbols = Assert.IsType<FindSymbolsResult>(result);
        Assert.Equal(101, symbols.TotalKnown);
        Assert.Equal(100, symbols.Returned);
        Assert.Equal(100, symbols.Items.Count);
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
        Assert.Equal(2, userLimited.Returned);
        Assert.True(userLimited.Truncated);

        var hardCapped = Assert.IsType<FindSymbolsResult>(hardCapResult);
        Assert.Equal(1001, hardCapped.TotalKnown);
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
    public async Task DefinitionAndReferences_DoNotExposeUrisOutsideRoot()
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
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var definitionResult = await tools.GoToDefinition("Program.cs", line: 1, column: 7);
        var referencesResult = await tools.FindReferences("Program.cs", line: 1, column: 7);

        var definition = Assert.IsType<DefinitionResult>(definitionResult);
        Assert.Single(definition.Items);
        Assert.Equal("Program.cs", definition.Items[0].File);
        var references = Assert.IsType<ReferencesResult>(referencesResult);
        Assert.Empty(references.Items);
        Assert.Equal(0, references.TotalKnown);
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

    private static object[] CreateWorkspaceSymbols(int count) =>
        Enumerable.Range(0, count)
            .Select(index => new
            {
                name = $"Symbol{index}",
                kind = (int)SymbolKind.Class
            })
            .Cast<object>()
            .ToArray();

    private static NavigationTools CreateTools(string root, WorkspaceSession session)
    {
        var guard = new PathGuard(root);
        var mapper = new DocumentPathMapper(guard);
        return new NavigationTools(session, new DocumentStateManager(CreateOptions(root), mapper), mapper);
    }

    private static WorkspaceSession CreateSession(string root, IRoslynWorkspaceLoader loader)
    {
        var guard = new PathGuard(root);
        return new WorkspaceSession(new WorkspaceScanner(CreateOptions(root), guard, gitScanner: null), guard, loader);
    }

    private static CliOptions CreateOptions(string root) =>
        new(
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
