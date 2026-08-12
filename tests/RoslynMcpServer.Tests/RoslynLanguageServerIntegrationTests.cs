using System.Diagnostics;
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
    // Each request can take 30 seconds, so leave room for one full retry after an initial timeout.
    private static readonly TimeSpan RoslynSettleTimeout = TimeSpan.FromSeconds(65);
    private static readonly TimeSpan RoslynSettleRetryDelay = TimeSpan.FromMilliseconds(500);

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
            public interface ICalculator
            {
                int Add(int left, int right);
            }

            public class Calculator : ICalculator
            {
                public int Add(int left, int right) => left + right;
            }

            public class Consumer
            {
                public int Use()
                {
                    ICalculator calculator = new Calculator();
                    return calculator.Add(1, 2);
                }
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
        try
        {
            _ = RoslynLanguageServerProcess.LocateExecutable(options);
        }
        catch (UserFacingException ex) when (ex.Code == "roslyn_language_server_not_found")
        {
            throw SkipException.ForSkip(RoslynLanguageServerProcess.InstallMessage);
        }

        var workspaceRoot = new WorkspaceRoot(root.Path);
        var documents = new OpenDocumentManager(options, workspaceRoot);
        var diagnosticStore = new DiagnosticStore(workspaceRoot, new SystemClock());
        var session = WorkspaceSession.CreateForTest(
            new WorkspaceScanner(options, workspaceRoot, new GitWorkspaceScanner(options, workspaceRoot)),
            workspaceRoot,
            RoslynWorkspaceLoader.CreateForServer(
                options,
                NullLogger<RoslynLanguageServerProcess>.Instance,
                NullLoggerFactory.Instance),
            documents: documents,
            diagnostics: diagnosticStore);
        await using var disposeSession = session;

        var tools = new NavigationTools(session, documents, workspaceRoot);
        await session.LoadProjectAsync("Sample.csproj");

        var symbolsResult = await tools.DocumentSymbols("Calculator.cs");
        var symbols = Assert.IsType<DocumentSymbolsResult>(symbolsResult);
        Assert.Contains(symbols.Items, item => item.Name.Contains("Calculator", StringComparison.Ordinal));

        var hoverResult = await tools.Hover("Calculator.cs", line: 4, column: 16);
        Assert.IsNotType<ToolError>(hoverResult);

        var definitionResult = await tools.GoToDefinition("Calculator.cs", line: 16, column: 39);
        var definition = Assert.IsType<DefinitionResult>(definitionResult);
        Assert.Contains(definition.Items, item => item.File == "Calculator.cs" && item.Line == 7);

        var referencesResult = await tools.FindReferences("Calculator.cs", line: 9, column: 17);
        var references = Assert.IsType<ReferencesResult>(referencesResult);
        Assert.Contains(references.Items, item => item.File == "Calculator.cs");

        var peekReferencesResult = await tools.PeekReferences("Calculator.cs", line: 9, column: 17, contextLines: 1);
        var peekReferences = Assert.IsType<PeekReferencesResult>(peekReferencesResult);
        Assert.Contains(
            peekReferences.Items,
            item => item.File == "Calculator.cs" &&
                item.Snippet is not null &&
                item.Snippet.Text.Contains("Add", StringComparison.Ordinal));

        var implementations = await WaitForImplementationAsync(
            tools,
            "Calculator.cs",
            line: 4,
            column: 9,
            item => item.File == "Calculator.cs" && item.Line == 9);
        Assert.Contains(implementations.Items, item => item.File == "Calculator.cs" && item.Line == 9);

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
            null,
            LogLevel.Information,
            null,
            null,
            TimeSpan.FromSeconds(60),
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
        using var settleCts = new CancellationTokenSource(RoslynSettleTimeout);
        var stopwatch = Stopwatch.StartNew();
        var attempts = 0;
        object? lastResult = null;

        try
        {
            while (true)
            {
                attempts++;
                lastResult = await tools.FindSymbols(query, cancellationToken: settleCts.Token);
                if (lastResult is FindSymbolsResult symbols && symbols.Items.Any(predicate))
                {
                    return symbols;
                }

                await Task.Delay(RoslynSettleRetryDelay, settleCts.Token);
            }
        }
        catch (OperationCanceledException) when (settleCts.IsCancellationRequested)
        {
        }

        throw new XunitException(
            $"Expected workspace symbol '{query}' after {attempts} attempts over {stopwatch.Elapsed.TotalSeconds:F1}s. " +
            $"Last completed result: {lastResult}");
    }

    private static async Task<ImplementationsResult> WaitForImplementationAsync(
        NavigationTools tools,
        string file,
        int line,
        int column,
        Func<NavigationLocation, bool> predicate)
    {
        using var settleCts = new CancellationTokenSource(RoslynSettleTimeout);
        var stopwatch = Stopwatch.StartNew();
        var attempts = 0;
        object? lastResult = null;

        try
        {
            while (true)
            {
                attempts++;
                lastResult = await tools.FindImplementations(
                    file,
                    line,
                    column,
                    cancellationToken: settleCts.Token);
                if (lastResult is ImplementationsResult implementations && implementations.Items.Any(predicate))
                {
                    return implementations;
                }

                await Task.Delay(RoslynSettleRetryDelay, settleCts.Token);
            }
        }
        catch (OperationCanceledException) when (settleCts.IsCancellationRequested)
        {
        }

        throw new XunitException(
            $"Expected implementation for {file}:{line}:{column} after {attempts} attempts over " +
            $"{stopwatch.Elapsed.TotalSeconds:F1}s. Last completed result: {lastResult}");
    }
}
