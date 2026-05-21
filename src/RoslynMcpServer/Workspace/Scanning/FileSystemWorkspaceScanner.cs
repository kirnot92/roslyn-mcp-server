using System.Diagnostics;
using RoslynMcpServer.Cli;

namespace RoslynMcpServer.Workspace;

public sealed class FileSystemWorkspaceScanner(CliOptions options, WorkspaceRoot workspaceRoot)
{
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        ".vs",
        "bin",
        "obj",
        "node_modules",
        "packages",
        ".idea",
        ".vscode",
        ".cache",
        ".nuke",
        "artifacts",
        "dist",
        "out",
        "target"
    };

    public WorkspaceScanResult Scan(
        TimeSpan scanTimeout,
        int maxDepth,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var solutions = new List<WorkspaceCandidate>();
        var projects = new List<WorkspaceCandidate>();
        string? truncationReason = null;

        var queue = new Queue<(string Directory, int Depth)>();
        queue.Enqueue((workspaceRoot.Root, 0));
        var stopScanning = false;

        while (queue.Count > 0 && !stopScanning)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (sw.Elapsed > scanTimeout)
            {
                truncationReason = "scan_timeout";
                break;
            }

            var (directory, depth) = queue.Dequeue();
            if (depth > maxDepth)
            {
                continue;
            }

            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(directory);
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (IOException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (sw.Elapsed > scanTimeout)
                {
                    truncationReason = "scan_timeout";
                    break;
                }

                var attributes = SafeGetAttributes(entry);
                if (attributes is null)
                {
                    continue;
                }

                if ((attributes.Value & FileAttributes.Directory) != 0)
                {
                    var name = Path.GetFileName(entry);
                    if (depth < maxDepth &&
                        !ExcludedDirectories.Contains(name) &&
                        (attributes.Value & FileAttributes.ReparsePoint) == 0)
                    {
                        queue.Enqueue((entry, depth + 1));
                    }

                    continue;
                }

                var extension = Path.GetExtension(entry);
                if (string.Equals(extension, ".sln", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(extension, ".slnx", StringComparison.OrdinalIgnoreCase))
                {
                    if (solutions.Count >= options.MaxSolutionCandidates)
                    {
                        truncationReason = "solution_candidate_limit";
                        stopScanning = CandidateLimitsReached(solutions, projects);
                        continue;
                    }

                    solutions.Add(ToCandidate(entry, string.Equals(extension, ".slnx", StringComparison.OrdinalIgnoreCase)
                        ? WorkspaceKind.SolutionX
                        : WorkspaceKind.Solution));
                    stopScanning = CandidateLimitsReached(solutions, projects);
                }
                else if (string.Equals(extension, ".csproj", StringComparison.OrdinalIgnoreCase))
                {
                    if (projects.Count >= options.MaxProjectCandidates)
                    {
                        truncationReason = "project_candidate_limit";
                        stopScanning = CandidateLimitsReached(solutions, projects);
                        continue;
                    }

                    projects.Add(ToCandidate(entry, WorkspaceKind.Project));
                    stopScanning = CandidateLimitsReached(solutions, projects);
                }

                if (stopScanning)
                {
                    truncationReason ??= "candidate_limit";
                    break;
                }
            }
        }

        sw.Stop();

        return new WorkspaceScanResult(
            workspaceRoot.Root,
            solutions,
            projects,
            truncationReason is not null,
            truncationReason,
            sw.Elapsed);
    }

    private WorkspaceCandidate ToCandidate(string fullPath, WorkspaceKind kind)
    {
        var normalized = Path.GetFullPath(fullPath);
        return new WorkspaceCandidate(kind, normalized, workspaceRoot.ToRelativePath(normalized));
    }

    private static FileAttributes? SafeGetAttributes(string path)
    {
        try
        {
            return File.GetAttributes(path);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private bool CandidateLimitsReached(
        IReadOnlyCollection<WorkspaceCandidate> solutions,
        IReadOnlyCollection<WorkspaceCandidate> projects) =>
        solutions.Count >= options.MaxSolutionCandidates &&
        projects.Count >= options.MaxProjectCandidates;
}
