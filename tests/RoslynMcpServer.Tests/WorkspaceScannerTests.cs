using Microsoft.Extensions.Logging;
using RoslynMcpServer.Cli;
using RoslynMcpServer.Infrastructure;
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
            null,
            LogLevel.Information,
            null,
            null,
            TimeSpan.FromSeconds(60),
            100,
            1,
            200,
            2 * 1024 * 1024,
            16,
            2);
        var scanner = new WorkspaceScanner(options, new WorkspaceRoot(root.Path), gitScanner: null);

        var result = scanner.Scan();

        Assert.True(result.Truncated);
        Assert.Equal("project_candidate_limit", result.TruncationReason);
        Assert.Single(result.Projects);
    }

    [Fact]
    public void Scan_StopsWhenSolutionAndProjectLimitsAreBothReached()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.sln"), string.Empty);
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), string.Empty);
        Directory.CreateDirectory(Path.Combine(root.Path, "src"));
        File.WriteAllText(Path.Combine(root.Path, "src", "Other.sln"), string.Empty);
        File.WriteAllText(Path.Combine(root.Path, "src", "Other.csproj"), string.Empty);
        var options = new CliOptions(
            root.Path,
            null,
            null,
            LogLevel.Information,
            null,
            null,
            TimeSpan.FromSeconds(60),
            1,
            1,
            200,
            2 * 1024 * 1024,
            16,
            2);
        var scanner = new WorkspaceScanner(options, new WorkspaceRoot(root.Path), gitScanner: null);

        var result = scanner.Scan();

        Assert.True(result.Truncated);
        Assert.Equal("candidate_limit", result.TruncationReason);
        Assert.Equal(["App.sln"], result.Solutions.Select(x => x.RelativePath).ToArray());
        Assert.Equal(["App.csproj"], result.Projects.Select(x => x.RelativePath).ToArray());
    }

    [Fact]
    public void Scan_RejectsNegativeMaxDepth()
    {
        using var root = TestRoot.Create();
        var scanner = CreateScanner(root.Path);

        var ex = Assert.Throws<UserFacingException>(() => scanner.Scan(maxDepth: -1));

        Assert.Equal("invalid_max_depth", ex.Code);
    }

    private static WorkspaceScanner CreateScanner(string root)
    {
        var options = new CliOptions(
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
        return new WorkspaceScanner(options, new WorkspaceRoot(root), gitScanner: null);
    }
}
