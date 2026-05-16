using Microsoft.Extensions.Logging;
using RoslynMcpServer.Cli;
using RoslynMcpServer.Workspace;

namespace RoslynMcpServer.Tests;

public sealed class WorkspaceScannerTests
{
    [Fact]
    public void Scan_FindsSolutionsSolutionXAndProjects()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.sln"), string.Empty);
        File.WriteAllText(Path.Combine(root.Path, "Next.slnx"), string.Empty);
        Directory.CreateDirectory(Path.Combine(root.Path, "src", "Lib"));
        File.WriteAllText(Path.Combine(root.Path, "src", "Lib", "Lib.csproj"), string.Empty);
        Directory.CreateDirectory(Path.Combine(root.Path, "src", "Lib", "bin"));
        File.WriteAllText(Path.Combine(root.Path, "src", "Lib", "bin", "Ignored.csproj"), string.Empty);

        var scanner = CreateScanner(root.Path);

        var result = scanner.Scan();

        Assert.False(result.Truncated);
        Assert.Equal(["App.sln", "Next.slnx"], result.Solutions.Select(x => x.RelativePath).ToArray());
        Assert.Equal(["src/Lib/Lib.csproj"], result.Projects.Select(x => x.RelativePath).ToArray());
    }

    [Fact]
    public void Scan_TruncatesWhenProjectLimitIsReached()
    {
        using var root = TestRoot.Create();
        Directory.CreateDirectory(Path.Combine(root.Path, "src"));
        File.WriteAllText(Path.Combine(root.Path, "src", "One.csproj"), string.Empty);
        File.WriteAllText(Path.Combine(root.Path, "src", "Two.csproj"), string.Empty);
        var options = new CliOptions(
            root.Path,
            null,
            LogLevel.Information,
            null,
            null,
            TimeSpan.FromSeconds(60),
            6,
            TimeSpan.FromSeconds(3),
            100,
            1,
            200,
            2 * 1024 * 1024,
            16);
        var scanner = new WorkspaceScanner(options, new PathGuard(root.Path));

        var result = scanner.Scan();

        Assert.True(result.Truncated);
        Assert.Equal("project_candidate_limit", result.TruncationReason);
        Assert.Single(result.Projects);
    }

    private static WorkspaceScanner CreateScanner(string root)
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
            16);
        return new WorkspaceScanner(options, new PathGuard(root));
    }
}
