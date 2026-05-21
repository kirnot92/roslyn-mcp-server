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
}
