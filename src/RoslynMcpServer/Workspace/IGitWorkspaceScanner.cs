namespace RoslynMcpServer.Workspace;

public interface IGitWorkspaceScanner
{
    GitWorkspaceScanAttempt TryScan(CancellationToken cancellationToken = default);

    GitWorkspaceScanAttempt TryScan(TimeSpan budget, CancellationToken cancellationToken = default);
}

public enum GitWorkspaceScanStatus
{
    Succeeded,
    Skipped,
    Failed,
    TimedOut
}

public sealed record GitWorkspaceScanAttempt(
    GitWorkspaceScanStatus Status,
    WorkspaceScanResult? Result,
    TimeSpan Elapsed)
{
    public static GitWorkspaceScanAttempt Succeeded(WorkspaceScanResult result) =>
        new(GitWorkspaceScanStatus.Succeeded, result, result.Elapsed);

    public static GitWorkspaceScanAttempt Skipped(TimeSpan elapsed) =>
        new(GitWorkspaceScanStatus.Skipped, Result: null, elapsed);

    public static GitWorkspaceScanAttempt Failed(TimeSpan elapsed) =>
        new(GitWorkspaceScanStatus.Failed, Result: null, elapsed);

    public static GitWorkspaceScanAttempt TimedOut(TimeSpan elapsed) =>
        new(GitWorkspaceScanStatus.TimedOut, Result: null, elapsed);
}
