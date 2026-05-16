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

        var scanner = new GitWorkspaceScanner(CreateOptions(root.Path), new PathGuard(root.Path));

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
        var scanner = new GitWorkspaceScanner(CreateOptions(root.Path), new PathGuard(root.Path));

        var result = scanner.TryScan();

        Assert.Null(result);
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
            16);

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
}
