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
        Assert.Equal(TimeSpan.FromSeconds(10), request.Timeout);
        Assert.False(request.Params.GetProperty("context").GetProperty("includeDeclaration").GetBoolean());
    }

    [Fact]
    public async Task FindReferences_UsesCustomTimeout()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "class C { }");
        var client = new FakeLspClient();
        client.EnqueueResponse(Array.Empty<Lsp.Location>());
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.FindReferences("Program.cs", line: 1, column: 7, timeoutSec: 45);

        Assert.IsType<ReferencesResult>(result);
        Assert.Equal(TimeSpan.FromSeconds(45), Assert.Single(client.Requests).Timeout);
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
    public async Task FindReferences_FiltersByIncludePathPrefixesBeforeMaxResults()
    {
        using var root = TestRoot.Create();
        Directory.CreateDirectory(Path.Combine(root.Path, "src", "App", "Services"));
        Directory.CreateDirectory(Path.Combine(root.Path, "src", "App2"));
        Directory.CreateDirectory(Path.Combine(root.Path, "tests", "App.Tests"));
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "class C { }");
        var client = new FakeLspClient();
        client.EnqueueResponse(new[]
        {
            new Lsp.Location(CreateFileUri(root.Path, "src/App/Calculator.cs"), CreateRange(0, 0, 0, 1)),
            new Lsp.Location(CreateFileUri(root.Path, "src/App/Services/CalculatorService.cs"), CreateRange(1, 0, 1, 1)),
            new Lsp.Location(CreateFileUri(root.Path, "src/App2/Calculator.cs"), CreateRange(2, 0, 2, 1)),
            new Lsp.Location(CreateFileUri(root.Path, "tests/App.Tests/CalculatorTests.cs"), CreateRange(3, 0, 3, 1))
        });
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.FindReferences(
            "Program.cs",
            line: 1,
            column: 7,
            maxResults: 1,
            includePathPrefixes: new[] { "src/App" });

        var references = Assert.IsType<ReferencesResult>(result);
        var item = Assert.Single(references.Items);
        Assert.Equal("src/App/Calculator.cs", item.File);
        Assert.Equal(4, references.TotalUnfilteredKnown);
        Assert.Equal(2, references.TotalKnown);
        Assert.Equal(1, references.Returned);
        Assert.True(references.Truncated);
    }

    [Fact]
    public async Task FindReferences_IncludePathPrefixesNormalizesSlashStylesAndSupportsRootPrefix()
    {
        using var root = TestRoot.Create();
        Directory.CreateDirectory(Path.Combine(root.Path, "src", "App"));
        Directory.CreateDirectory(Path.Combine(root.Path, "tests", "App.Tests"));
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "class C { }");
        var client = new FakeLspClient();
        var locations = new[]
        {
            new Lsp.Location(CreateFileUri(root.Path, "src/App/Calculator.cs"), CreateRange(0, 0, 0, 1)),
            new Lsp.Location(CreateFileUri(root.Path, "tests/App.Tests/CalculatorTests.cs"), CreateRange(1, 0, 1, 1))
        };
        client.EnqueueResponse(locations);
        client.EnqueueResponse(locations);
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var slashResult = await tools.FindReferences(
            "Program.cs",
            line: 1,
            column: 7,
            includePathPrefixes: new[] { "src\\App\\" });
        var rootResult = await tools.FindReferences(
            "Program.cs",
            line: 1,
            column: 7,
            includePathPrefixes: new[] { "." });

        var slashReferences = Assert.IsType<ReferencesResult>(slashResult);
        var slashItem = Assert.Single(slashReferences.Items);
        Assert.Equal("src/App/Calculator.cs", slashItem.File);
        Assert.Equal(2, slashReferences.TotalUnfilteredKnown);
        Assert.Equal(1, slashReferences.TotalKnown);

        var rootReferences = Assert.IsType<ReferencesResult>(rootResult);
        Assert.Equal(["src/App/Calculator.cs", "tests/App.Tests/CalculatorTests.cs"], rootReferences.Items.Select(item => item.File));
        Assert.Equal(2, rootReferences.TotalUnfilteredKnown);
        Assert.Equal(2, rootReferences.TotalKnown);
    }

    [Fact]
    public async Task FindReferences_ReturnsValidationErrorForInvalidIncludePathPrefixes()
    {
        using var root = TestRoot.Create();
        using var outside = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "class C { }");
        var client = new FakeLspClient();
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var emptyArrayResult = await tools.FindReferences("Program.cs", line: 1, column: 7, includePathPrefixes: Array.Empty<string>());
        var emptyEntryResult = await tools.FindReferences("Program.cs", line: 1, column: 7, includePathPrefixes: new[] { "src/App", " " });
        var outsideResult = await tools.FindReferences("Program.cs", line: 1, column: 7, includePathPrefixes: new[] { outside.Path });

        var emptyArrayError = Assert.IsType<ToolError>(emptyArrayResult);
        var emptyEntryError = Assert.IsType<ToolError>(emptyEntryResult);
        var outsideError = Assert.IsType<ToolError>(outsideResult);
        Assert.Equal("invalid_path_prefix", emptyArrayError.Error);
        Assert.Equal("invalid_path_prefix", emptyEntryError.Error);
        Assert.Equal("invalid_path_prefix", outsideError.Error);
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task FindReferences_DoesNotSendIncludePathPrefixesToRoslynLs()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "class C { }");
        var client = new FakeLspClient();
        client.EnqueueResponse(Array.Empty<Lsp.Location>());
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.FindReferences("Program.cs", line: 1, column: 7, includePathPrefixes: new[] { "src" });

        Assert.IsType<ReferencesResult>(result);
        var request = Assert.Single(client.Requests);
        Assert.Equal("textDocument/references", request.Method);
        Assert.Equal(["textDocument", "position", "context"], request.Params.EnumerateObject().Select(property => property.Name));
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
        Assert.Equal(TimeSpan.FromSeconds(10), request.Timeout);
        Assert.False(request.Params.GetProperty("context").GetProperty("includeDeclaration").GetBoolean());
    }

    [Fact]
    public async Task PeekReferences_UsesCustomTimeout()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "class C { void M() { } }");
        var client = new FakeLspClient();
        client.EnqueueResponse(Array.Empty<Lsp.Location>());
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.PeekReferences("Program.cs", line: 1, column: 16, timeoutSec: 45);

        Assert.IsType<PeekReferencesResult>(result);
        Assert.Equal(TimeSpan.FromSeconds(45), Assert.Single(client.Requests).Timeout);
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
    public async Task PeekReferences_FiltersByIncludePathPrefixesBeforeReadingSnippets()
    {
        using var root = TestRoot.Create();
        Directory.CreateDirectory(Path.Combine(root.Path, "src", "App"));
        Directory.CreateDirectory(Path.Combine(root.Path, "src", "App2"));
        Directory.CreateDirectory(Path.Combine(root.Path, "tests", "App.Tests"));
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "c");
        File.WriteAllText(Path.Combine(root.Path, "src", "App", "Calculator.cs"), "abc");
        File.WriteAllText(Path.Combine(root.Path, "src", "App2", "Calculator.cs"), "123456");
        var client = new FakeLspClient();
        client.EnqueueResponse(new[]
        {
            new Lsp.Location(CreateFileUri(root.Path, "src/App/Calculator.cs"), CreateRange(0, 0, 0, 1)),
            new Lsp.Location(CreateFileUri(root.Path, "src/App2/Calculator.cs"), CreateRange(0, 0, 0, 1)),
            new Lsp.Location(CreateFileUri(root.Path, "tests/App.Tests/MissingCalculatorTests.cs"), CreateRange(0, 0, 0, 1))
        });
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session, maxDocumentBytes: 5);

        var result = await tools.PeekReferences(
            "Program.cs",
            line: 1,
            column: 1,
            contextLines: 0,
            includePathPrefixes: new[] { "src/App" });

        var peek = Assert.IsType<PeekReferencesResult>(result);
        var item = Assert.Single(peek.Items);
        Assert.Equal("src/App/Calculator.cs", item.File);
        Assert.NotNull(item.Snippet);
        Assert.Null(item.SnippetError);
        Assert.Equal(3, peek.TotalUnfilteredKnown);
        Assert.Equal(1, peek.TotalKnown);
        Assert.Equal(1, peek.Returned);
        Assert.False(peek.Truncated);
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
    public async Task PeekReferences_ReturnsValidationErrorForInvalidIncludePathPrefixes()
    {
        using var root = TestRoot.Create();
        using var outside = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "class C { void M() { } }");
        var client = new FakeLspClient();
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.PeekReferences(
            "Program.cs",
            line: 1,
            column: 16,
            includePathPrefixes: new[] { outside.Path });

        var error = Assert.IsType<ToolError>(result);
        Assert.Equal("invalid_path_prefix", error.Error);
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
    public async Task FindImplementations_FiltersByIncludePathPrefixesBeforeMaxResults()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "interface I { }");
        var client = new FakeLspClient();
        client.EnqueueResponse(new[]
        {
            new Lsp.Location(CreateFileUri(root.Path, "src/App/Calculator.cs"), CreateRange(0, 0, 0, 1)),
            new Lsp.Location(CreateFileUri(root.Path, "tests/App.Tests/CalculatorTests.cs"), CreateRange(1, 0, 1, 1)),
            new Lsp.Location(CreateFileUri(root.Path, "src/App/Services/CalculatorService.cs"), CreateRange(2, 0, 2, 1)),
            new Lsp.Location(CreateFileUri(root.Path, "src/App2/Calculator.cs"), CreateRange(3, 0, 3, 1))
        });
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.FindImplementations(
            "Program.cs",
            line: 1,
            column: 11,
            maxResults: 1,
            includePathPrefixes: new[] { "src\\App\\" });

        var implementations = Assert.IsType<ImplementationsResult>(result);
        var item = Assert.Single(implementations.Items);
        Assert.Equal("src/App/Calculator.cs", item.File);
        Assert.Equal(4, implementations.TotalUnfilteredKnown);
        Assert.Equal(2, implementations.TotalKnown);
        Assert.Equal(1, implementations.Returned);
        Assert.True(implementations.Truncated);
        var request = Assert.Single(client.Requests);
        Assert.Equal("textDocument/implementation", request.Method);
        Assert.Equal(["textDocument", "position"], request.Params.EnumerateObject().Select(property => property.Name));
    }

    [Fact]
    public async Task FindImplementations_IncludePathPrefixesSupportsRootPrefix()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "interface I { }");
        var client = new FakeLspClient();
        client.EnqueueResponse(new[]
        {
            new Lsp.Location(CreateFileUri(root.Path, "src/App/Calculator.cs"), CreateRange(0, 0, 0, 1)),
            new Lsp.Location(CreateFileUri(root.Path, "tests/App.Tests/CalculatorTests.cs"), CreateRange(1, 0, 1, 1))
        });
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.FindImplementations(
            "Program.cs",
            line: 1,
            column: 11,
            includePathPrefixes: new[] { "." });

        var implementations = Assert.IsType<ImplementationsResult>(result);
        Assert.Equal(["src/App/Calculator.cs", "tests/App.Tests/CalculatorTests.cs"], implementations.Items.Select(item => item.File));
        Assert.Equal(2, implementations.TotalUnfilteredKnown);
        Assert.Equal(2, implementations.TotalKnown);
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
    public async Task FindImplementations_ReturnsValidationErrorForInvalidIncludePathPrefixes()
    {
        using var root = TestRoot.Create();
        using var outside = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "interface I { }");
        var client = new FakeLspClient();
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var emptyArrayResult = await tools.FindImplementations("Program.cs", line: 1, column: 11, includePathPrefixes: Array.Empty<string>());
        var emptyEntryResult = await tools.FindImplementations("Program.cs", line: 1, column: 11, includePathPrefixes: new[] { "src/App", " " });
        var outsideResult = await tools.FindImplementations("Program.cs", line: 1, column: 11, includePathPrefixes: new[] { outside.Path });

        Assert.Equal("invalid_path_prefix", Assert.IsType<ToolError>(emptyArrayResult).Error);
        Assert.Equal("invalid_path_prefix", Assert.IsType<ToolError>(emptyEntryResult).Error);
        Assert.Equal("invalid_path_prefix", Assert.IsType<ToolError>(outsideResult).Error);
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
}
