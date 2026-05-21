using System.Buffers;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using RoslynMcpServer.Cli;

namespace RoslynMcpServer.Workspace;

// Uses git's tracked/untracked file view as the primary workspace discovery
// path. Returning null is intentional: WorkspaceScanner will fall back to a
// bounded filesystem scan when git is unavailable, outside a worktree, or
// fails before exhausting the scan budget.
public sealed class GitWorkspaceScanner(
    CliOptions options,
    PathGuard pathGuard,
    ILogger<GitWorkspaceScanner>? logger = null) : IGitWorkspaceScanner
{
    public GitWorkspaceScanAttempt TryScan(CancellationToken cancellationToken = default)
    {
        return TryScan(options.ScanTimeout, cancellationToken);
    }

    public GitWorkspaceScanAttempt TryScan(TimeSpan budget, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var workTreeStatus = GetWorkTreeStatus(budget, cancellationToken);
        if (workTreeStatus is not GitWorkspaceScanStatus.Succeeded)
        {
            return new GitWorkspaceScanAttempt(workTreeStatus, Result: null, sw.Elapsed);
        }

        var remaining = budget - sw.Elapsed;
        if (remaining <= TimeSpan.Zero)
        {
            return GitWorkspaceScanAttempt.TimedOut(sw.Elapsed);
        }

        using var timeoutCts = new CancellationTokenSource(remaining);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            // --exclude-standard lets git apply repository, local, and global
            // ignore rules. -z keeps paths unambiguous for spaces and newlines.
            var startInfo = CreateGitStartInfo(
                "ls-files",
                "-co",
                "--exclude-standard",
                "-z",
                "--",
                "*.sln",
                "*.slnx",
                "*.csproj");
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                logger?.LogDebug("Git workspace scan could not start git ls-files.");
                return GitWorkspaceScanAttempt.Failed(sw.Elapsed);
            }

            CloseStandardInput(process);

            var builder = new GitScanResultBuilder(options, pathGuard);
            var outputTask = ReadWorkspacePathsAsync(
                process.StandardOutput.BaseStream,
                builder,
                process,
                sw,
                linkedCts.Token);
            var errorTask = process.StandardError.ReadToEndAsync(linkedCts.Token);
            WorkspaceScanResult result;

            try
            {
                result = outputTask.GetAwaiter().GetResult();
                process.WaitForExitAsync(linkedCts.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                KillProcess(process);
                logger?.LogDebug("Git workspace scan timed out after {Elapsed}.", sw.Elapsed);
                return GitWorkspaceScanAttempt.TimedOut(sw.Elapsed);
            }

            if (process.ExitCode != 0 && !builder.StoppedAfterCandidateLimit)
            {
                var error = errorTask.GetAwaiter().GetResult();
                logger?.LogDebug("Git workspace scan failed with exit code {ExitCode}: {Error}", process.ExitCode, error);
                return GitWorkspaceScanAttempt.Failed(sw.Elapsed);
            }

            sw.Stop();
            logger?.LogDebug(
                "Git workspace scan completed in {Elapsed}. Solutions={SolutionCount}, Projects={ProjectCount}, Truncated={Truncated}, Reason={TruncationReason}",
                result.Elapsed,
                result.Solutions.Count,
                result.Projects.Count,
                result.Truncated,
                result.TruncationReason);

            return GitWorkspaceScanAttempt.Succeeded(result);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger?.LogDebug(ex, "Git workspace scan failed before filesystem fallback.");
            return GitWorkspaceScanAttempt.Failed(sw.Elapsed);
        }
    }

    private GitWorkspaceScanStatus GetWorkTreeStatus(TimeSpan budget, CancellationToken cancellationToken)
    {
        try
        {
            using var process = Process.Start(CreateGitStartInfo("rev-parse", "--is-inside-work-tree"));
            if (process is null)
            {
                logger?.LogDebug("Git workspace scan could not start git rev-parse.");
                return GitWorkspaceScanStatus.Failed;
            }

            CloseStandardInput(process);

            var timeoutMs = (int)Math.Clamp(Math.Min(budget.TotalMilliseconds, 3000), 1, int.MaxValue);
            if (!process.WaitForExit(timeoutMs))
            {
                KillProcess(process);
                logger?.LogDebug("Git workspace scan rev-parse timed out after {TimeoutMs}ms.", timeoutMs);
                return GitWorkspaceScanStatus.TimedOut;
            }

            var output = process.StandardOutput.ReadToEnd().Trim();
            var isInsideWorkTree = process.ExitCode == 0 &&
                string.Equals(output, "true", StringComparison.OrdinalIgnoreCase);
            if (!isInsideWorkTree)
            {
                logger?.LogDebug(
                    "Git workspace scan skipped because rev-parse returned exit code {ExitCode} and output '{Output}'.",
                    process.ExitCode,
                    output);
            }

            return isInsideWorkTree
                ? GitWorkspaceScanStatus.Succeeded
                : GitWorkspaceScanStatus.Skipped;
        }
        catch (Exception ex)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            logger?.LogDebug(ex, "Git workspace scan rev-parse failed.");
            return GitWorkspaceScanStatus.Failed;
        }
    }

    private sealed class GitScanResultBuilder
    {
        private readonly CliOptions options;
        private readonly PathGuard pathGuard;
        private readonly List<WorkspaceCandidate> solutions = [];
        private readonly List<WorkspaceCandidate> projects = [];
        private readonly HashSet<string> seen = new(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);

        private string? truncationReason;

        public GitScanResultBuilder(CliOptions options, PathGuard pathGuard)
        {
            this.options = options;
            this.pathGuard = pathGuard;
        }

        public bool StoppedAfterCandidateLimit { get; private set; }

        public void AddRelativePath(ReadOnlySpan<byte> utf8Path)
        {
            var relativePath = Encoding.UTF8.GetString(utf8Path);
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                return;
            }

            var extension = Path.GetExtension(relativePath);
            if (!IsWorkspaceExtension(extension))
            {
                return;
            }

            string fullPath;
            try
            {
                fullPath = this.pathGuard.ResolveInsideRoot(relativePath);
            }
            catch
            {
                return;
            }

            if (!File.Exists(fullPath) || !this.seen.Add(fullPath))
            {
                return;
            }

            if (IsSolutionExtension(extension))
            {
                if (this.solutions.Count >= this.options.MaxSolutionCandidates)
                {
                    this.truncationReason = "solution_candidate_limit";
                    return;
                }

                this.solutions.Add(this.ToCandidate(fullPath, string.Equals(extension, ".slnx", StringComparison.OrdinalIgnoreCase)
                    ? WorkspaceKind.SolutionX
                    : WorkspaceKind.Solution));
            }
            else
            {
                if (this.projects.Count >= this.options.MaxProjectCandidates)
                {
                    this.truncationReason = "project_candidate_limit";
                    return;
                }

                this.projects.Add(this.ToCandidate(fullPath, WorkspaceKind.Project));
            }

            if (this.solutions.Count >= this.options.MaxSolutionCandidates &&
                this.projects.Count >= this.options.MaxProjectCandidates)
            {
                this.truncationReason ??= "candidate_limit";
                this.StoppedAfterCandidateLimit = true;
            }
        }

        public WorkspaceScanResult ToResult(TimeSpan elapsed) =>
            new(
                this.pathGuard.Root,
                WorkspaceScanner.SortCandidates(this.solutions),
                WorkspaceScanner.SortCandidates(this.projects),
                this.truncationReason is not null,
                this.truncationReason,
                elapsed);

        private WorkspaceCandidate ToCandidate(string fullPath, WorkspaceKind kind)
        {
            var normalized = Path.GetFullPath(fullPath);
            return new WorkspaceCandidate(kind, normalized, this.pathGuard.ToRelativePath(normalized));
        }
    }

    private ProcessStartInfo CreateGitStartInfo(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            WorkingDirectory = pathGuard.Root
        };

        startInfo.ArgumentList.Add("-C");
        startInfo.ArgumentList.Add(pathGuard.Root);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static bool IsWorkspaceExtension(string extension) =>
        IsSolutionExtension(extension) || string.Equals(extension, ".csproj", StringComparison.OrdinalIgnoreCase);

    private static bool IsSolutionExtension(string extension) =>
        string.Equals(extension, ".sln", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(extension, ".slnx", StringComparison.OrdinalIgnoreCase);

    private static void KillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }

    private static void CloseStandardInput(Process process)
    {
        try
        {
            process.StandardInput.Close();
        }
        catch
        {
        }
    }

    private static async Task<WorkspaceScanResult> ReadWorkspacePathsAsync(
        Stream stream,
        GitScanResultBuilder builder,
        Process process,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        var path = new ArrayBufferWriter<byte>(256);

        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            for (var i = 0; i < read; i++)
            {
                if (buffer[i] == 0)
                {
                    if (path.WrittenCount > 0)
                    {
                        builder.AddRelativePath(path.WrittenSpan);
                        path.Clear();
                    }

                    if (builder.StoppedAfterCandidateLimit)
                    {
                        KillProcess(process);
                        return builder.ToResult(stopwatch.Elapsed);
                    }

                    continue;
                }

                var destination = path.GetSpan(1);
                destination[0] = buffer[i];
                path.Advance(1);
            }
        }

        if (path.WrittenCount > 0)
        {
            builder.AddRelativePath(path.WrittenSpan);
        }

        return builder.ToResult(stopwatch.Elapsed);
    }
}
