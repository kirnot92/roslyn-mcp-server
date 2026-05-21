using System.Diagnostics;
using Microsoft.Extensions.Logging;
using RoslynMcpServer.Cli;
using RoslynMcpServer.Workspace;

namespace RoslynMcpServer.Tests;

public sealed class GitWorkspaceScannerTests
{
    [Fact]
    public void TryScan_UsesGitIgnoreAndIncludesUntrackedCandidates()
    {
        if (!GitIsAvailable())
        {
            return;
        }

        using var root = TestRoot.Create();
        RunGit(root.Path, "init");
        File.WriteAllText(Path.Combine(root.Path, ".gitignore"), "bin/\nignored/\n");
        File.WriteAllText(Path.Combine(root.Path, "App.sln"), string.Empty);
        Directory.CreateDirectory(Path.Combine(root.Path, "src", "Lib"));
        File.WriteAllText(Path.Combine(root.Path, "src", "Lib", "Lib.csproj"), string.Empty);
        Directory.CreateDirectory(Path.Combine(root.Path, "bin"));
        File.WriteAllText(Path.Combine(root.Path, "bin", "Ignored.csproj"), string.Empty);
        Directory.CreateDirectory(Path.Combine(root.Path, "ignored"));
        File.WriteAllText(Path.Combine(root.Path, "ignored", "Ignored.sln"), string.Empty);

        var scanner = new GitWorkspaceScanner(CreateOptions(root.Path), new WorkspaceRoot(root.Path));

        var result = scanner.TryScan();

        Assert.NotNull(result);
        Assert.False(result.Truncated);
        Assert.Equal(["App.sln"], result.Solutions.Select(x => x.RelativePath).ToArray());
        Assert.Equal(["src/Lib/Lib.csproj"], result.Projects.Select(x => x.RelativePath).ToArray());
    }

    [Fact]
    public void TryScan_ReturnsNullOutsideGitWorkTree()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.sln"), string.Empty);
        var scanner = new GitWorkspaceScanner(CreateOptions(root.Path), new WorkspaceRoot(root.Path));

        var result = scanner.TryScan();

        Assert.Null(result);
    }

    [Fact]
    public void TryScan_ReturnsWorkspaceCandidatesWhenManyNonWorkspaceFilesExist()
    {
        if (!GitIsAvailable())
        {
            return;
        }

        using var root = TestRoot.Create();
        RunGit(root.Path, "init");
        Directory.CreateDirectory(Path.Combine(root.Path, "noise"));
        for (var i = 0; i < 2000; i++)
        {
            File.WriteAllText(Path.Combine(root.Path, "noise", $"File{i:D4}.txt"), string.Empty);
        }

        File.WriteAllText(Path.Combine(root.Path, "PowerShell.sln"), string.Empty);
        Directory.CreateDirectory(Path.Combine(root.Path, "src", "PowerShell"));
        File.WriteAllText(Path.Combine(root.Path, "src", "PowerShell", "PowerShell.csproj"), string.Empty);
        var options = CreateOptions(root.Path);
        var scanner = new GitWorkspaceScanner(options, new WorkspaceRoot(root.Path));

        var result = scanner.TryScan();

        Assert.NotNull(result);
        Assert.False(result.Truncated);
        Assert.True(result.Elapsed < TimeSpan.FromSeconds(30));
        Assert.Equal(["PowerShell.sln"], result.Solutions.Select(x => x.RelativePath).ToArray());
        Assert.Equal(["src/PowerShell/PowerShell.csproj"], result.Projects.Select(x => x.RelativePath).ToArray());
    }

    [Fact]
    public void TryScan_DoesNotMissSolutionWhenProjectLimitIsReachedFirst()
    {
        if (!GitIsAvailable())
        {
            return;
        }

        using var root = TestRoot.Create();
        RunGit(root.Path, "init");
        Directory.CreateDirectory(Path.Combine(root.Path, "a"));
        File.WriteAllText(Path.Combine(root.Path, "a", "One.csproj"), string.Empty);
        File.WriteAllText(Path.Combine(root.Path, "a", "Two.csproj"), string.Empty);
        Directory.CreateDirectory(Path.Combine(root.Path, "z"));
        File.WriteAllText(Path.Combine(root.Path, "z", "App.sln"), string.Empty);
        var options = CreateOptions(root.Path) with { MaxProjectCandidates = 1 };
        var scanner = new GitWorkspaceScanner(options, new WorkspaceRoot(root.Path));

        var result = scanner.TryScan();

        Assert.NotNull(result);
        Assert.True(result.Truncated);
        Assert.Equal("project_candidate_limit", result.TruncationReason);
        Assert.Equal(["z/App.sln"], result.Solutions.Select(x => x.RelativePath).ToArray());
        Assert.Single(result.Projects);
    }

    [Fact]
    public void TryScan_TruncatesWhenSolutionAndProjectLimitsAreBothReached()
    {
        if (!GitIsAvailable())
        {
            return;
        }

        using var root = TestRoot.Create();
        RunGit(root.Path, "init");
        File.WriteAllText(Path.Combine(root.Path, "App.csproj"), string.Empty);
        File.WriteAllText(Path.Combine(root.Path, "App.sln"), string.Empty);
        Directory.CreateDirectory(Path.Combine(root.Path, "src"));
        File.WriteAllText(Path.Combine(root.Path, "src", "Other.csproj"), string.Empty);
        File.WriteAllText(Path.Combine(root.Path, "src", "Other.sln"), string.Empty);
        var options = CreateOptions(root.Path) with
        {
            MaxSolutionCandidates = 1,
            MaxProjectCandidates = 1
        };
        var scanner = new GitWorkspaceScanner(options, new WorkspaceRoot(root.Path));

        var result = scanner.TryScan();

        Assert.NotNull(result);
        Assert.True(result.Truncated);
        Assert.Equal("candidate_limit", result.TruncationReason);
        Assert.Single(result.Solutions);
        Assert.Single(result.Projects);
    }

    [Fact]
    public void WorkspaceScanner_FallsBackToShallowFileSystemScanWhenGitScanFails()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.sln"), string.Empty);
        Directory.CreateDirectory(Path.Combine(root.Path, "src"));
        File.WriteAllText(Path.Combine(root.Path, "src", "App.csproj"), string.Empty);
        Directory.CreateDirectory(Path.Combine(root.Path, "src", "Deep", "A", "TooDeep"));
        File.WriteAllText(Path.Combine(root.Path, "src", "Deep", "A", "TooDeep", "TooDeep.csproj"), string.Empty);
        var options = CreateOptions(root.Path);
        var scanner = new WorkspaceScanner(options, new WorkspaceRoot(root.Path), new FastNullGitScanner());

        var result = scanner.Scan();

        Assert.False(result.Truncated);
        Assert.Equal(["App.sln"], result.Solutions.Select(x => x.RelativePath).ToArray());
        Assert.Equal(["src/App.csproj"], result.Projects.Select(x => x.RelativePath).ToArray());
    }

    [Fact]
    public void WorkspaceScanner_MaxDepthSkipsGitAndLimitsFileSystemDepth()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.sln"), string.Empty);
        Directory.CreateDirectory(Path.Combine(root.Path, "src"));
        File.WriteAllText(Path.Combine(root.Path, "src", "App.csproj"), string.Empty);
        Directory.CreateDirectory(Path.Combine(root.Path, "src", "Deep"));
        File.WriteAllText(Path.Combine(root.Path, "src", "Deep", "Deep.csproj"), string.Empty);
        var options = CreateOptions(root.Path);
        var scanner = new WorkspaceScanner(options, new WorkspaceRoot(root.Path), new ThrowingGitScanner());

        var result = scanner.Scan(maxDepth: 1, cancellationToken: CancellationToken.None);

        Assert.False(result.Truncated);
        Assert.Equal(["App.sln"], result.Solutions.Select(x => x.RelativePath).ToArray());
        Assert.Equal(["src/App.csproj"], result.Projects.Select(x => x.RelativePath).ToArray());
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

    private static bool GitIsAvailable()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("git", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            return process is not null && process.WaitForExit(3000) && process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static void RunGit(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        Assert.True(process.WaitForExit(5000));
        Assert.Equal(0, process.ExitCode);
    }

    private sealed class FastNullGitScanner : IGitWorkspaceScanner
    {
        public WorkspaceScanResult? TryScan(CancellationToken cancellationToken = default) =>
            null;
    }

    private sealed class ThrowingGitScanner : IGitWorkspaceScanner
    {
        public WorkspaceScanResult? TryScan(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Git scanner should not be called.");
    }
}
