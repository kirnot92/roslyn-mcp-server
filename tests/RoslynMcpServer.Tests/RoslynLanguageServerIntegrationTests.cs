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
    public async Task DocumentSymbolsAndHover_WorkAgainstInstalledRoslynLanguageServer()
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
        var session = new WorkspaceSession(
            new WorkspaceScanner(options, guard),
            guard,
            new RoslynWorkspaceLoader(
                options,
                new RoslynLanguageServerProcess(
                    options,
                    locator,
                    NullLogger<RoslynLanguageServerProcess>.Instance,
                    NullLoggerFactory.Instance)));
        await using var disposeSession = session.ConfigureAwait(false);

        var mapper = new DocumentPathMapper(guard);
        var tools = new NavigationTools(session, new DocumentStateManager(options, mapper));
        await session.LoadProjectAsync("Sample.csproj");

        var symbolsResult = await tools.DocumentSymbols("Calculator.cs");
        var symbols = Assert.IsType<DocumentSymbolsResult>(symbolsResult);
        Assert.Contains(symbols.Items, item => item.Name.Contains("Calculator", StringComparison.Ordinal));

        var hoverResult = await tools.Hover("Calculator.cs", line: 4, column: 16);
        Assert.IsNotType<ToolError>(hoverResult);
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
            16);
}
