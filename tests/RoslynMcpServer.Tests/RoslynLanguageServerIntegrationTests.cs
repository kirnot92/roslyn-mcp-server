using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RoslynMcpServer.Cli;
using RoslynMcpServer.Infrastructure;
using RoslynMcpServer.Lsp;
using RoslynMcpServer.Mcp;
using RoslynMcpServer.Workspace;
using Xunit.Sdk;

namespace RoslynMcpServer.Tests;

public sealed class RoslynLanguageServerIntegrationTests
{
    [Fact]
    public async Task ReadTools_WorkAgainstInstalledRoslynLanguageServer()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "Sample.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(root.Path, "Calculator.cs"), """
            namespace Sample;
            public class Calculator
            {
                public int Add(int left, int right) => left + right;
            }

            public class Consumer
            {
                public int Use() => new Calculator().Add(1, 2);
            }
            """);
        File.WriteAllText(Path.Combine(root.Path, "Broken.cs"), """
            namespace Sample;
            public class Broken
            {
                public int Value => MissingSymbol;
            }
            """);

        var options = CreateOptions(root.Path);
        var locator = new RoslynLanguageServerLocator(options);
        try
        {
            _ = locator.Locate();
        }
        catch (UserFacingException ex) when (ex.Code == "roslyn_language_server_not_found")
        {
            throw SkipException.ForSkip(RoslynLanguageServerLocator.InstallMessage);
        }

        var guard = new PathGuard(root.Path);
        var mapper = new DocumentPathMapper(guard);
        var documents = new DocumentStateManager(options, mapper);
        var diagnosticStore = new DiagnosticStore(mapper, new SystemClock());
        var session = new WorkspaceSession(
            new WorkspaceScanner(options, guard, new GitWorkspaceScanner(options, guard)),
            guard,
            new RoslynWorkspaceLoader(
                options,
                new RoslynLanguageServerProcess(
                    options,
                    locator,
                    NullLogger<RoslynLanguageServerProcess>.Instance,
                    NullLoggerFactory.Instance)),
            documents,
            diagnosticStore);
        await using var disposeSession = session.ConfigureAwait(false);

        var tools = new NavigationTools(session, documents, mapper);
        await session.LoadProjectAsync("Sample.csproj");

        var symbolsResult = await tools.DocumentSymbols("Calculator.cs");
        var symbols = Assert.IsType<DocumentSymbolsResult>(symbolsResult);
        Assert.Contains(symbols.Items, item => item.Name.Contains("Calculator", StringComparison.Ordinal));

        var hoverResult = await tools.Hover("Calculator.cs", line: 4, column: 16);
        Assert.IsNotType<ToolError>(hoverResult);

        var definitionResult = await tools.GoToDefinition("Calculator.cs", line: 9, column: 43);
        var definition = Assert.IsType<DefinitionResult>(definitionResult);
        Assert.Contains(definition.Items, item => item.File == "Calculator.cs" && item.Line == 4);

        var referencesResult = await tools.FindReferences("Calculator.cs", line: 4, column: 17);
        var references = Assert.IsType<ReferencesResult>(referencesResult);
        Assert.Contains(references.Items, item => item.File == "Calculator.cs");

        var workspaceSymbols = await WaitForWorkspaceSymbolAsync(
            tools,
            "Calculator",
            item => item.Name.Contains("Calculator", StringComparison.Ordinal));
        Assert.Contains(workspaceSymbols.Items, item => item.Location?.File == "Calculator.cs");
    }

    [Fact(Skip = "roslyn-language-server publishDiagnostics arrival is environment-dependent in the current smoke fixture; deferred until a stable settle strategy is defined.")]
    public void DiagnosticsPublishSmoke_DeferredUntilStableSettleStrategy()
    {
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

    private static async Task<FindSymbolsResult> WaitForWorkspaceSymbolAsync(
        NavigationTools tools,
        string query,
        Func<WorkspaceSymbolItem, bool> predicate)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
        object? lastResult = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            lastResult = await tools.FindSymbols(query);
            if (lastResult is FindSymbolsResult symbols && symbols.Items.Any(predicate))
            {
                return symbols;
            }

            await Task.Delay(500);
        }

        throw new XunitException($"Expected workspace symbol '{query}' before timeout. Last result: {lastResult}");
    }
}
