using System.Diagnostics;
using RoslynMcpServer.Cli;

namespace RoslynMcpServer.Workspace;

// Uses git's tracked/untracked file view as the primary workspace discovery
// path. Returning null is intentional: WorkspaceScanner will fall back to a
// bounded filesystem scan when git is unavailable, outside a worktree, or
// fails before exhausting the scan budget.
public sealed class GitWorkspaceScanner(CliOptions options, PathGuard pathGuard) : IGitWorkspaceScanner
{
    public WorkspaceScanResult? TryScan(CancellationToken cancellationToken = default)
    {
        return TryScan(options.ScanTimeout, cancellationToken);
    }

    public WorkspaceScanResult? TryScan(TimeSpan budget, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        if (!IsInsideGitWorkTree(budget, cancellationToken))
        {
            return null;
        }

        var remaining = budget - sw.Elapsed;
        if (remaining <= TimeSpan.Zero)
        {
            return null;
        }

        using var timeoutCts = new CancellationTokenSource(remaining);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            // --exclude-standard lets git apply repository, local, and global
            // ignore rules. -z keeps paths unambiguous for spaces and newlines.
            var startInfo = CreateGitStartInfo("ls-files", "-co", "--exclude-standard", "-z");
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            var outputTask = ReadAllBytesAsync(process.StandardOutput.BaseStream, linkedCts.Token);
            var errorTask = process.StandardError.ReadToEndAsync(linkedCts.Token);

            try
            {
                process.WaitForExitAsync(linkedCts.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                KillProcess(process);
                return null;
            }

            if (process.ExitCode != 0)
            {
                _ = errorTask.GetAwaiter().GetResult();
                return null;
            }

            var output = outputTask.GetAwaiter().GetResult();
            sw.Stop();

            return BuildScanResult(output, sw.Elapsed);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private bool IsInsideGitWorkTree(TimeSpan budget, CancellationToken cancellationToken)
    {
        try
        {
            using var process = Process.Start(CreateGitStartInfo("rev-parse", "--is-inside-work-tree"));
            if (process is null)
            {
                return false;
            }

            var timeoutMs = (int)Math.Clamp(Math.Min(budget.TotalMilliseconds, 3000), 1, int.MaxValue);
            if (!process.WaitForExit(timeoutMs))
            {
                KillProcess(process);
                return false;
            }

            var output = process.StandardOutput.ReadToEnd().Trim();
            return process.ExitCode == 0 && string.Equals(output, "true", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return false;
        }
    }

    private WorkspaceScanResult BuildScanResult(byte[] output, TimeSpan elapsed)
    {
        var solutions = new List<WorkspaceCandidate>();
        var projects = new List<WorkspaceCandidate>();
        var seen = new HashSet<string>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);
        string? truncationReason = null;

        foreach (var relativePath in SplitNullTerminatedUtf8(output))
        {
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                continue;
            }

            var extension = Path.GetExtension(relativePath);
            if (!IsWorkspaceExtension(extension))
            {
                continue;
            }

            string fullPath;
            try
            {
                fullPath = pathGuard.ResolveInsideRoot(relativePath);
            }
            catch
            {
                continue;
            }

            if (!File.Exists(fullPath) || !seen.Add(fullPath))
            {
                continue;
            }

            if (IsSolutionExtension(extension))
            {
                if (solutions.Count >= options.MaxSolutionCandidates)
                {
                    truncationReason = "solution_candidate_limit";
                    continue;
                }

                solutions.Add(ToCandidate(fullPath, string.Equals(extension, ".slnx", StringComparison.OrdinalIgnoreCase)
                    ? WorkspaceKind.SolutionX
                    : WorkspaceKind.Solution));
            }
            else
            {
                if (projects.Count >= options.MaxProjectCandidates)
                {
                    truncationReason = "project_candidate_limit";
                    continue;
                }

                projects.Add(ToCandidate(fullPath, WorkspaceKind.Project));
            }
        }

        return new WorkspaceScanResult(
            pathGuard.Root,
            WorkspaceScanner.SortCandidates(solutions),
            WorkspaceScanner.SortCandidates(projects),
            truncationReason is not null,
            truncationReason,
            elapsed);
    }

    private ProcessStartInfo CreateGitStartInfo(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
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

    private WorkspaceCandidate ToCandidate(string fullPath, WorkspaceKind kind)
    {
        var normalized = Path.GetFullPath(fullPath);
        return new WorkspaceCandidate(kind, normalized, pathGuard.ToRelativePath(normalized));
    }

    private static IEnumerable<string> SplitNullTerminatedUtf8(byte[] bytes)
    {
        var start = 0;
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] != 0)
            {
                continue;
            }

            if (i > start)
            {
                yield return System.Text.Encoding.UTF8.GetString(bytes, start, i - start);
            }

            start = i + 1;
        }
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

    private static async Task<byte[]> ReadAllBytesAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var memoryStream = new MemoryStream();
        await stream.CopyToAsync(memoryStream, cancellationToken).ConfigureAwait(false);
        return memoryStream.ToArray();
    }
}
