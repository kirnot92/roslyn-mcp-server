using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RoslynMcpServer.Cli;
using RoslynMcpServer.Infrastructure;
using RoslynMcpServer.Lsp;
using RoslynMcpServer.Workspace;

namespace RoslynMcpServer.Tests;

public sealed class WorkspaceSessionTests
{
    [Fact]
    public async Task LoadSolution_TransitionsToWorkspaceWarming()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.sln"), string.Empty);
        var session = CreateSession(root.Path, new FakeLoader());

        var status = await session.LoadSolutionAsync("App.sln");

        Assert.Equal(WorkspaceLoadState.WorkspaceWarming, status.State);
        Assert.Equal("App.sln", status.CurrentTarget?.RelativePath);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task LoadProject_RecordsFailureWhenLoaderFails()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), string.Empty);
        var session = CreateSession(root.Path, new FailingLoader());

        var ex = await Assert.ThrowsAsync<UserFacingException>(() => session.LoadProjectAsync("App.csproj"));
        var status = await session.GetStatusAsync();

        Assert.Equal("roslyn_language_server_not_found", ex.Code);
        Assert.Equal(WorkspaceLoadState.Failed, status.State);
        Assert.Equal("roslyn_language_server_not_found", status.FailureCode);
    }

    private static WorkspaceSession CreateSession(string root, IRoslynWorkspaceLoader loader)
    {
        var options = new CliOptions(
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
        var guard = new PathGuard(root);
        return new WorkspaceSession(new WorkspaceScanner(options, guard), guard, loader);
    }

    private sealed class FakeLoader : IRoslynWorkspaceLoader
    {
        public Task<RoslynWorkspaceHandle> LoadAsync(WorkspaceTarget target, CancellationToken cancellationToken)
        {
            var process = Process.Start(new ProcessStartInfo("dotnet")
            {
                ArgumentList = { "--info" },
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            })!;
            var client = new LspClient(
                new MemoryStream(),
                new MemoryStream(),
                16,
                2,
                NullLogger<LspClient>.Instance);
            return Task.FromResult(new RoslynWorkspaceHandle(target, new RoslynLanguageServerConnection(process, client)));
        }
    }

    private sealed class FailingLoader : IRoslynWorkspaceLoader
    {
        public Task<RoslynWorkspaceHandle> LoadAsync(WorkspaceTarget target, CancellationToken cancellationToken) =>
            throw new UserFacingException("roslyn_language_server_not_found", RoslynLanguageServerLocator.InstallMessage);
    }
}
