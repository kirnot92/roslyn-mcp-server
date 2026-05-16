using System.Diagnostics;
using RoslynMcpServer.Cli;

namespace RoslynMcpServer.Workspace;

// Git-aware scanning is preferred when available because git already applies
// repository ignore rules. This scanner owns the bounded filesystem fallback
// for non-git roots or environments where git probing fails before consuming
// the scan budget.
public sealed class WorkspaceScanner(CliOptions options, PathGuard pathGuard, IGitWorkspaceScanner? gitScanner)
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

    public WorkspaceScanResult Scan(CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        // Try git first so .gitignore, .git/info/exclude, and global excludes
        // shape workspace discovery without reimplementing those rules here.
        var gitResult = gitScanner?.TryScan(options.ScanTimeout, cancellationToken);
        if (gitResult is not null)
        {
            return gitResult;
        }

        // If git used the whole budget, return a timeout instead of starting a
        // second full tree walk that is likely to be just as expensive.
        var remaining = options.ScanTimeout - sw.Elapsed;
        if (remaining <= TimeSpan.Zero)
        {
            return new WorkspaceScanResult(
                pathGuard.Root,
                [],
                [],
                Truncated: true,
                TruncationReason: "scan_timeout",
                sw.Elapsed);
        }

        return ScanFileSystem(remaining, cancellationToken);
    }

    private WorkspaceScanResult ScanFileSystem(TimeSpan scanTimeout, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var solutions = new List<WorkspaceCandidate>();
        var projects = new List<WorkspaceCandidate>();
        string? truncationReason = null;

        var queue = new Queue<(string Directory, int Depth)>();
        queue.Enqueue((pathGuard.Root, 0));

        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (sw.Elapsed > scanTimeout)
            {
                truncationReason = "scan_timeout";
                break;
            }

            var (directory, depth) = queue.Dequeue();
            if (depth > options.ScanMaxDepth)
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
                    if (depth < options.ScanMaxDepth &&
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
                        continue;
                    }

                    solutions.Add(ToCandidate(entry, string.Equals(extension, ".slnx", StringComparison.OrdinalIgnoreCase)
                        ? WorkspaceKind.SolutionX
                        : WorkspaceKind.Solution));
                }
                else if (string.Equals(extension, ".csproj", StringComparison.OrdinalIgnoreCase))
                {
                    if (projects.Count >= options.MaxProjectCandidates)
                    {
                        truncationReason = "project_candidate_limit";
                        continue;
                    }

                    projects.Add(ToCandidate(entry, WorkspaceKind.Project));
                }
            }

        }

        sw.Stop();

        return new WorkspaceScanResult(
            pathGuard.Root,
            SortCandidates(solutions),
            SortCandidates(projects),
            truncationReason is not null,
            truncationReason,
            sw.Elapsed);
    }

    private WorkspaceCandidate ToCandidate(string fullPath, WorkspaceKind kind)
    {
        var normalized = Path.GetFullPath(fullPath);
        return new WorkspaceCandidate(kind, normalized, pathGuard.ToRelativePath(normalized));
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

    public static IReadOnlyList<WorkspaceCandidate> SortCandidates(IEnumerable<WorkspaceCandidate> candidates) =>
        candidates
            .OrderBy(candidate => candidate.RelativePath.Count(c => c == '/'))
            .ThenBy(candidate => candidate.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
}
