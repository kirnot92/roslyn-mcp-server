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
    public async Task GetCallHierarchy_FiltersByIncludePathPrefixesOnDirectionCounterpartBeforeMaxResults()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "class Target { void M() { } }");
        var target = CreateCallHierarchyItem("M", CreateFileUri(root.Path, "Target.cs"));
        var client = new FakeLspClient();
        client.EnqueueResponse(new[] { target });
        client.EnqueueResponse(new[]
        {
            CreateIncomingCall(CreateCallHierarchyItem("Caller1", CreateFileUri(root.Path, "src/App/Caller1.cs")), CreateRange(0, 0, 0, 1)),
            CreateIncomingCall(CreateCallHierarchyItem("CallerTests", CreateFileUri(root.Path, "tests/App.Tests/CallerTests.cs")), CreateRange(1, 0, 1, 1))
        });
        client.EnqueueResponse(new[]
        {
            CreateOutgoingCall(CreateCallHierarchyItem("Callee", CreateFileUri(root.Path, "src/App/Callee.cs")), CreateRange(2, 0, 2, 1)),
            CreateOutgoingCall(CreateCallHierarchyItem("Callee2", CreateFileUri(root.Path, "src/App2/Callee2.cs")), CreateRange(3, 0, 3, 1))
        });
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.GetCallHierarchy(
            "Program.cs",
            line: 1,
            column: 21,
            direction: "both",
            maxResults: 2,
            includePathPrefixes: new[] { "src/App" });

        var hierarchy = Assert.IsType<CallHierarchyResult>(result);
        Assert.Equal(4, hierarchy.TotalUnfilteredKnown);
        Assert.Equal(2, hierarchy.TotalKnown);
        Assert.Equal(2, hierarchy.Returned);
        Assert.False(hierarchy.Truncated);
        Assert.Equal(["Caller1", "M"], hierarchy.Edges.Select(edge => edge.From.Name));
        Assert.Equal(["M", "Callee"], hierarchy.Edges.Select(edge => edge.To.Name));
        Assert.All(client.Requests, request => Assert.DoesNotContain("includePathPrefixes", request.Params.EnumerateObject().Select(property => property.Name)));
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
        using var outside = TestRoot.Create();
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
        var invalidPathResult = await tools.GetCallHierarchy("Program.cs", line: 1, column: 21, includePathPrefixes: new[] { outside.Path });

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
        Assert.Equal("invalid_path_prefix", Assert.IsType<ToolError>(invalidPathResult).Error);
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
    public async Task GetTypeHierarchy_PrepareRequestUsesZeroBasedPositionAndIsNotExpensive()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(
            Path.Combine(root.Path, "Program.cs"),
            string.Join(Environment.NewLine, Enumerable.Repeat("// padding", 11).Append("class Derived : Base { }")));
        var client = new FakeLspClient();
        client.EnqueueResponse(Array.Empty<object>());
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.GetTypeHierarchy("Program.cs", line: 12, column: 5);

        var hierarchy = Assert.IsType<TypeHierarchyResult>(result);
        Assert.Empty(hierarchy.Roots);
        Assert.Empty(hierarchy.Edges);
        var request = Assert.Single(client.Requests);
        Assert.Equal("textDocument/prepareTypeHierarchy", request.Method);
        Assert.False(request.IsExpensive);
        Assert.Equal(11, request.Params.GetProperty("position").GetProperty("line").GetInt32());
        Assert.Equal(4, request.Params.GetProperty("position").GetProperty("character").GetInt32());
    }

    [Fact]
    public async Task GetTypeHierarchy_ReturnsEmptyWhenPrepareIsNullOrEmptyAndDoesNotCallFollowUp()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "class Derived : Base { }");
        var client = new FakeLspClient();
        client.EnqueueResponse(null);
        client.EnqueueResponse(Array.Empty<object>());
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var nullResult = await tools.GetTypeHierarchy("Program.cs", line: 1, column: 7);
        var emptyResult = await tools.GetTypeHierarchy("Program.cs", line: 1, column: 7);

        Assert.Empty(Assert.IsType<TypeHierarchyResult>(nullResult).Edges);
        Assert.Empty(Assert.IsType<TypeHierarchyResult>(emptyResult).Edges);
        Assert.Equal(["textDocument/prepareTypeHierarchy", "textDocument/prepareTypeHierarchy"], client.Requests.Select(r => r.Method));
    }

    [Fact]
    public async Task GetTypeHierarchy_MapsSupertypesEdge()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "class Derived : Base { }");
        var derived = CreateTypeHierarchyItem("Derived", CreateFileUri(root.Path, "Derived.cs"));
        var baseType = CreateTypeHierarchyItem("Base", CreateFileUri(root.Path, "Base.cs"));
        var client = new FakeLspClient();
        client.EnqueueResponse(new[] { derived });
        client.EnqueueResponse(new[] { baseType });
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.GetTypeHierarchy("Program.cs", line: 1, column: 7, maxDepth: 1);

        var hierarchy = Assert.IsType<TypeHierarchyResult>(result);
        var rootSymbol = Assert.Single(hierarchy.Roots);
        var edge = Assert.Single(hierarchy.Edges);
        Assert.Equal(rootSymbol.Id, edge.RootId);
        Assert.Equal("supertypes", edge.Direction);
        Assert.Equal(1, edge.Depth);
        Assert.Equal("Base", edge.From.Name);
        Assert.Equal("Derived", edge.To.Name);
        Assert.Equal("Base.cs", edge.From.Location.File);
        Assert.Equal("Derived.cs", edge.To.Location.File);
        Assert.Equal(1, hierarchy.TotalKnown);
        Assert.Equal(1, hierarchy.Returned);
        Assert.False(hierarchy.Truncated);
        Assert.Equal(["textDocument/prepareTypeHierarchy", "typeHierarchy/supertypes"], client.Requests.Select(r => r.Method));
        Assert.True(client.Requests[1].IsExpensive);
    }

    [Fact]
    public async Task GetTypeHierarchy_MapsSubtypesEdge()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "class Base { }");
        var baseType = CreateTypeHierarchyItem("Base", CreateFileUri(root.Path, "Base.cs"));
        var derived = CreateTypeHierarchyItem("Derived", CreateFileUri(root.Path, "Derived.cs"));
        var client = new FakeLspClient();
        client.EnqueueResponse(new[] { baseType });
        client.EnqueueResponse(new[] { derived });
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.GetTypeHierarchy("Program.cs", line: 1, column: 7, direction: "subtypes", maxDepth: 1);

        var hierarchy = Assert.IsType<TypeHierarchyResult>(result);
        var edge = Assert.Single(hierarchy.Edges);
        Assert.Equal("subtypes", edge.Direction);
        Assert.Equal("Base", edge.From.Name);
        Assert.Equal("Derived", edge.To.Name);
        Assert.Equal(["textDocument/prepareTypeHierarchy", "typeHierarchy/subtypes"], client.Requests.Select(r => r.Method));
        Assert.True(client.Requests[1].IsExpensive);
    }

    [Fact]
    public async Task GetTypeHierarchy_BothRequestsSupertypesThenSubtypesDeterministically()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "class Current : Base { }");
        var current = CreateTypeHierarchyItem("Current", CreateFileUri(root.Path, "Current.cs"));
        var baseType = CreateTypeHierarchyItem("Base", CreateFileUri(root.Path, "Base.cs"));
        var derived = CreateTypeHierarchyItem("Derived", CreateFileUri(root.Path, "Derived.cs"));
        var client = new FakeLspClient();
        client.EnqueueResponse(new[] { current });
        client.EnqueueResponse(new[] { baseType });
        client.EnqueueResponse(new[] { derived });
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.GetTypeHierarchy("Program.cs", line: 1, column: 7, direction: "both", maxDepth: 1);

        var hierarchy = Assert.IsType<TypeHierarchyResult>(result);
        Assert.Equal(["supertypes", "subtypes"], hierarchy.Edges.Select(edge => edge.Direction));
        Assert.Equal("Base", hierarchy.Edges[0].From.Name);
        Assert.Equal("Derived", hierarchy.Edges[1].To.Name);
        Assert.Equal(
            ["textDocument/prepareTypeHierarchy", "typeHierarchy/supertypes", "typeHierarchy/subtypes"],
            client.Requests.Select(r => r.Method));
    }

    [Fact]
    public async Task GetTypeHierarchy_PreservesOriginalPreparedAndFollowUpItemsInFollowUpParams()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "class Derived : Base { }");
        var range = CreateRange(1, 0, 1, 7);
        var derived = new
        {
            name = "Derived",
            kind = (int)SymbolKind.Class,
            detail = "class Derived",
            uri = CreateFileUri(root.Path, "Derived.cs"),
            range,
            selectionRange = range,
            data = new { opaque = "root" }
        };
        var baseType = new
        {
            name = "Base",
            kind = (int)SymbolKind.Class,
            detail = "class Base",
            uri = CreateFileUri(root.Path, "Base.cs"),
            range,
            selectionRange = range,
            data = new { opaque = "base" }
        };
        var client = new FakeLspClient();
        client.EnqueueResponse(new[] { derived });
        client.EnqueueResponse(new[] { baseType });
        client.EnqueueResponse(Array.Empty<object>());
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.GetTypeHierarchy("Program.cs", line: 1, column: 7, maxDepth: 2);

        Assert.IsType<TypeHierarchyResult>(result);
        Assert.Equal("Derived", client.Requests[1].Params.GetProperty("item").GetProperty("name").GetString());
        Assert.Equal("root", client.Requests[1].Params.GetProperty("item").GetProperty("data").GetProperty("opaque").GetString());
        Assert.Equal("Base", client.Requests[2].Params.GetProperty("item").GetProperty("name").GetString());
        Assert.Equal("base", client.Requests[2].Params.GetProperty("item").GetProperty("data").GetProperty("opaque").GetString());
    }

    [Fact]
    public async Task GetTypeHierarchy_RespectsMaxDepth()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "class Derived : Base { }");
        var derived = CreateTypeHierarchyItem("Derived", CreateFileUri(root.Path, "Derived.cs"));
        var baseType = CreateTypeHierarchyItem("Base", CreateFileUri(root.Path, "Base.cs"));
        var client = new FakeLspClient();
        client.EnqueueResponse(new[] { derived });
        client.EnqueueResponse(new[] { baseType });
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.GetTypeHierarchy("Program.cs", line: 1, column: 7, maxDepth: 1);

        var hierarchy = Assert.IsType<TypeHierarchyResult>(result);
        Assert.Single(hierarchy.Edges);
        Assert.Equal(["textDocument/prepareTypeHierarchy", "typeHierarchy/supertypes"], client.Requests.Select(r => r.Method));
    }

    [Fact]
    public async Task GetTypeHierarchy_RespectsMaxResultsAndSetsTruncated()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "class Derived : Base { }");
        var derived = CreateTypeHierarchyItem("Derived", CreateFileUri(root.Path, "Derived.cs"));
        var client = new FakeLspClient();
        client.EnqueueResponse(new[] { derived });
        client.EnqueueResponse(new[]
        {
            CreateTypeHierarchyItem("Base1", CreateFileUri(root.Path, "Base1.cs")),
            CreateTypeHierarchyItem("Base2", CreateFileUri(root.Path, "Base2.cs")),
            CreateTypeHierarchyItem("Base3", CreateFileUri(root.Path, "Base3.cs"))
        });
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.GetTypeHierarchy("Program.cs", line: 1, column: 7, maxDepth: 1, maxResults: 2);

        var hierarchy = Assert.IsType<TypeHierarchyResult>(result);
        Assert.Equal(3, hierarchy.TotalKnown);
        Assert.Equal(2, hierarchy.Returned);
        Assert.Equal(2, hierarchy.Edges.Count);
        Assert.True(hierarchy.Truncated);
        Assert.Equal(["Base1", "Base2"], hierarchy.Edges.Select(edge => edge.From.Name));
    }

    [Fact]
    public async Task GetTypeHierarchy_FiltersByIncludePathPrefixesBeforeMaxResults()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "class Derived : Base { }");
        var derived = CreateTypeHierarchyItem("Derived", CreateFileUri(root.Path, "Derived.cs"));
        var client = new FakeLspClient();
        client.EnqueueResponse(new[] { derived });
        client.EnqueueResponse(new[]
        {
            CreateTypeHierarchyItem("TestsBase", CreateFileUri(root.Path, "tests/App.Tests/TestsBase.cs")),
            CreateTypeHierarchyItem("AppBase", CreateFileUri(root.Path, "src/App/AppBase.cs")),
            CreateTypeHierarchyItem("AppBase2", CreateFileUri(root.Path, "src/App2/AppBase2.cs"))
        });
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.GetTypeHierarchy(
            "Program.cs",
            line: 1,
            column: 7,
            maxDepth: 1,
            maxResults: 1,
            includePathPrefixes: new[] { "src/App" });

        var hierarchy = Assert.IsType<TypeHierarchyResult>(result);
        var edge = Assert.Single(hierarchy.Edges);
        Assert.Equal("AppBase", edge.From.Name);
        Assert.Equal(3, hierarchy.TotalUnfilteredKnown);
        Assert.Equal(1, hierarchy.TotalKnown);
        Assert.Equal(1, hierarchy.Returned);
        Assert.False(hierarchy.Truncated);
    }

    [Fact]
    public async Task GetTypeHierarchy_DoesNotTraversePrefixExcludedFollowUpTypesOrForwardPrefixes()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "class Derived : Base { }");
        var derived = CreateTypeHierarchyItem("Derived", CreateFileUri(root.Path, "Derived.cs"));
        var excludedMid = CreateTypeHierarchyItem("ExcludedMid", CreateFileUri(root.Path, "tests/App.Tests/ExcludedMid.cs"));
        var includedBase = CreateTypeHierarchyItem("IncludedBase", CreateFileUri(root.Path, "src/App/IncludedBase.cs"));
        var client = new FakeLspClient();
        client.EnqueueResponse(new[] { derived });
        client.EnqueueResponse(new[] { excludedMid, includedBase });
        client.EnqueueResponse(Array.Empty<object>());
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.GetTypeHierarchy(
            "Program.cs",
            line: 1,
            column: 7,
            maxDepth: 2,
            includePathPrefixes: new[] { "src/App" });

        var hierarchy = Assert.IsType<TypeHierarchyResult>(result);
        var edge = Assert.Single(hierarchy.Edges);
        Assert.Equal("IncludedBase", edge.From.Name);
        Assert.Equal(2, hierarchy.TotalUnfilteredKnown);
        Assert.Equal(1, hierarchy.TotalKnown);
        Assert.Equal(1, hierarchy.Returned);
        Assert.Equal(
            ["textDocument/prepareTypeHierarchy", "typeHierarchy/supertypes", "typeHierarchy/supertypes"],
            client.Requests.Select(request => request.Method));
        Assert.Equal("IncludedBase", client.Requests[2].Params.GetProperty("item").GetProperty("name").GetString());
        Assert.All(client.Requests, request => Assert.DoesNotContain("includePathPrefixes", request.Params.EnumerateObject().Select(property => property.Name)));
    }

    [Fact]
    public async Task GetTypeHierarchy_MarksTruncatedWhenResultLimitSkipsSecondDirection()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "class Current : Base { }");
        var current = CreateTypeHierarchyItem("Current", CreateFileUri(root.Path, "Current.cs"));
        var baseType = CreateTypeHierarchyItem("Base", CreateFileUri(root.Path, "Base.cs"));
        var client = new FakeLspClient();
        client.EnqueueResponse(new[] { current });
        client.EnqueueResponse(new[] { baseType });
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.GetTypeHierarchy(
            "Program.cs",
            line: 1,
            column: 7,
            direction: "both",
            maxDepth: 1,
            maxResults: 1);

        var hierarchy = Assert.IsType<TypeHierarchyResult>(result);
        Assert.Equal(1, hierarchy.TotalKnown);
        Assert.Equal(1, hierarchy.Returned);
        Assert.True(hierarchy.Truncated);
        Assert.Equal(["supertypes"], hierarchy.Edges.Select(edge => edge.Direction));
        Assert.Equal(
            ["textDocument/prepareTypeHierarchy", "typeHierarchy/supertypes"],
            client.Requests.Select(r => r.Method));
    }

    [Fact]
    public async Task GetTypeHierarchy_MarksTruncatedWhenResultLimitSkipsDepthFollowUp()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "class Derived : Base { }");
        var derived = CreateTypeHierarchyItem("Derived", CreateFileUri(root.Path, "Derived.cs"));
        var baseType = CreateTypeHierarchyItem("Base", CreateFileUri(root.Path, "Base.cs"));
        var client = new FakeLspClient();
        client.EnqueueResponse(new[] { derived });
        client.EnqueueResponse(new[] { baseType });
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.GetTypeHierarchy(
            "Program.cs",
            line: 1,
            column: 7,
            maxDepth: 2,
            maxResults: 1);

        var hierarchy = Assert.IsType<TypeHierarchyResult>(result);
        Assert.Equal(1, hierarchy.TotalKnown);
        Assert.Equal(1, hierarchy.Returned);
        Assert.True(hierarchy.Truncated);
        Assert.Equal(["textDocument/prepareTypeHierarchy", "typeHierarchy/supertypes"], client.Requests.Select(r => r.Method));
    }

    [Fact]
    public async Task GetTypeHierarchy_FiltersOutsideRootNonFileAndInvalidUriItems()
    {
        using var root = TestRoot.Create();
        using var outside = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "class Derived : Base { }");
        var derived = CreateTypeHierarchyItem("Derived", CreateFileUri(root.Path, "Derived.cs"));
        var client = new FakeLspClient();
        client.EnqueueResponse(new[] { derived });
        client.EnqueueResponse(new[]
        {
            CreateTypeHierarchyItem("Outside", new Uri(Path.Combine(outside.Path, "Outside.cs")).AbsoluteUri),
            CreateTypeHierarchyItem("Remote", "https://example.test/Remote.cs"),
            CreateTypeHierarchyItem("Invalid", "not a uri"),
            CreateTypeHierarchyItem("Base", CreateFileUri(root.Path, "Base.cs"))
        });
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.GetTypeHierarchy("Program.cs", line: 1, column: 7, maxDepth: 1);

        var hierarchy = Assert.IsType<TypeHierarchyResult>(result);
        var edge = Assert.Single(hierarchy.Edges);
        Assert.Equal("Base", edge.From.Name);
        Assert.Equal(1, hierarchy.TotalKnown);
        Assert.Equal(1, hierarchy.Returned);
    }

    [Fact]
    public async Task GetTypeHierarchy_ReturnsValidationErrorsForInvalidDirectionMaxDepthAndMaxResults()
    {
        using var root = TestRoot.Create();
        using var outside = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "class Derived : Base { }");
        var client = new FakeLspClient();
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var invalidDirectionResult = await tools.GetTypeHierarchy("Program.cs", line: 1, column: 7, direction: "sideways");
        var invalidMaxDepthResult = await tools.GetTypeHierarchy("Program.cs", line: 1, column: 7, maxDepth: 0);
        var invalidMaxResultsResult = await tools.GetTypeHierarchy("Program.cs", line: 1, column: 7, maxResults: 0);
        var invalidPathResult = await tools.GetTypeHierarchy("Program.cs", line: 1, column: 7, includePathPrefixes: new[] { outside.Path });

        Assert.Equal("invalid_direction", Assert.IsType<ToolError>(invalidDirectionResult).Error);
        Assert.Equal("invalid_max_depth", Assert.IsType<ToolError>(invalidMaxDepthResult).Error);
        Assert.Equal("invalid_max_results", Assert.IsType<ToolError>(invalidMaxResultsResult).Error);
        Assert.Equal("invalid_path_prefix", Assert.IsType<ToolError>(invalidPathResult).Error);
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task GetTypeHierarchy_IncludesPartialMetadataWhileWorkspaceIsWarming()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(root.Path, "Program.cs"), "class Derived : Base { }");
        var derived = CreateTypeHierarchyItem("Derived", CreateFileUri(root.Path, "Derived.cs"));
        var client = new FakeLspClient();
        client.EnqueueResponse(new[] { derived });
        client.EnqueueResponse(Array.Empty<object>());
        await using var session = CreateSession(root.Path, new ImmediateLoader(client));
        await session.LoadProjectAsync("App.csproj");
        var tools = CreateTools(root.Path, session);

        var result = await tools.GetTypeHierarchy("Program.cs", line: 1, column: 7, maxDepth: 1);

        var hierarchy = Assert.IsType<TypeHierarchyResult>(result);
        Assert.Equal(WorkspaceLoadState.WorkspaceWarming.ToString(), hierarchy.WorkspaceState);
        Assert.Equal("partial", hierarchy.Completeness);
        Assert.Equal(ToolRetryHints.WorkspaceWarmingMs, hierarchy.RetryAfterMs);
        Assert.Contains("type hierarchy", hierarchy.Reason);
    }
}
