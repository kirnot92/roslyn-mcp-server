using Microsoft.Extensions.Logging;
using RoslynMcpServer.Cli;
using RoslynMcpServer.Infrastructure;
using RoslynMcpServer.Lsp;

namespace RoslynMcpServer.Tests;

public sealed class RoslynLanguageServerLocatorTests
{
    [Fact]
    public void InstallMessage_IncludesNextActionsAndRuntimeRisk()
    {
        Assert.Contains("dotnet tool install --global roslyn-language-server --prerelease", RoslynLanguageServerLocator.InstallMessage);
        Assert.Contains("does not bundle roslyn-language-server", RoslynLanguageServerLocator.InstallMessage);
        Assert.Contains("PATH", RoslynLanguageServerLocator.InstallMessage);
        Assert.Contains("--roslyn-language-server <path>", RoslynLanguageServerLocator.InstallMessage);
        Assert.Contains(".NET 10", RoslynLanguageServerLocator.InstallMessage);
        Assert.Contains("prerelease", RoslynLanguageServerLocator.InstallMessage);
    }

    [Fact]
    public void Locate_ReportsConfiguredPathWhenExplicitPathIsMissing()
    {
        using var root = TestRoot.Create();
        var missingPath = Path.Combine(root.Path, "missing", "roslyn-language-server.exe");
        var locator = new RoslynLanguageServerLocator(CreateOptions(root.Path, missingPath));

        var ex = Assert.Throws<UserFacingException>(() => locator.Locate());

        Assert.Equal("roslyn_language_server_not_found", ex.Code);
        Assert.Contains(Path.GetFullPath(missingPath), ex.Message);
        Assert.Contains("Configured path was not found", ex.Message);
        Assert.Contains("Fix the path passed to --roslyn-language-server", ex.Message);
        Assert.Contains("dotnet tool install --global roslyn-language-server --prerelease", ex.Message);
        Assert.Contains("PATH", ex.Message);
    }

    private static CliOptions CreateOptions(string root, string? languageServerPath) =>
        new(
            root,
            languageServerPath,
            LogLevel.Information,
            null,
            null,
            TimeSpan.FromSeconds(60),
            6,
            TimeSpan.FromSeconds(3),
            100,
            1000,
            200,
            2 * 1024 * 1024,
            16,
            2);
}
