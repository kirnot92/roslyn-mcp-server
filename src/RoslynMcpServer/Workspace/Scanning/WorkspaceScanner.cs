using RoslynMcpServer.Cli;
using RoslynMcpServer.Infrastructure;

namespace RoslynMcpServer.Workspace;

// Coordinates git-aware discovery with bounded filesystem fallback. An explicit
// maxDepth skips git and uses filesystem BFS.
public sealed class WorkspaceScanner(CliOptions options, PathGuard pathGuard, IGitWorkspaceScanner? gitScanner = null)
{
    private const int FallbackFileSystemMaxDepth = 3;
    private static readonly TimeSpan DefaultScanTimeout = TimeSpan.FromSeconds(30);

    private readonly FileSystemWorkspaceScanner fileSystemScanner = new(options, pathGuard);
    private readonly IGitWorkspaceScanner gitScanner = gitScanner ?? new GitWorkspaceScanner(options, pathGuard);

    public WorkspaceScanResult Scan(int? maxDepth = null, CancellationToken cancellationToken = default)
    {
        if (maxDepth is not null)
        {
            if (maxDepth.Value < 0)
            {
                throw new UserFacingException("invalid_max_depth", "maxDepth must be a non-negative integer.");
            }

            return this.fileSystemScanner.Scan(DefaultScanTimeout, maxDepth.Value, cancellationToken);
        }

        // Try git first so .gitignore, .git/info/exclude, and global excludes
        // shape workspace discovery without reimplementing those rules here.
        var gitResult = this.gitScanner.TryScan(cancellationToken);
        if (gitResult is not null)
        {
            return gitResult;
        }

        return this.fileSystemScanner.Scan(
            DefaultScanTimeout,
            FallbackFileSystemMaxDepth,
            cancellationToken);
    }
}
