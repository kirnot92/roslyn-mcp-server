namespace RoslynMcpServer.Workspace;

public enum WorkspaceKind
{
    Solution,
    SolutionX,
    Project
}

public enum WorkspaceLoadState
{
    NotLoaded,
    StartingLanguageServer,
    LspReady,
    WorkspaceWarming,
    LoadedWithErrors,
    Ready,
    Failed
}

public sealed record WorkspaceTarget(
    WorkspaceKind Kind,
    string FullPath,
    string RelativePath,
    string RepositoryRoot,
    string WorkspaceDirectory);

public sealed record WorkspaceCandidate(
    WorkspaceKind Kind,
    string FullPath,
    string RelativePath);

public sealed record WorkspaceScanResult(
    string Root,
    IReadOnlyList<WorkspaceCandidate> Solutions,
    IReadOnlyList<WorkspaceCandidate> Projects,
    bool Truncated,
    string? TruncationReason,
    TimeSpan Elapsed);

public sealed record WorkspaceStatus(
    string Root,
    WorkspaceLoadState State,
    WorkspaceTarget? CurrentTarget,
    bool LanguageServerRunning,
    int PendingLspRequests,
    WorkspaceScanResult Workspaces,
    string? FailureCode,
    string? FailureMessage,
    int OpenDocumentCount,
    int KnownDiagnosticsFileCount,
    DateTimeOffset? LastDiagnosticUpdateAt,
    IReadOnlyList<WorkspaceWarning> Warnings);

public sealed record WorkspaceWarning(
    string Code,
    string Message,
    IReadOnlyList<string> RelatedPaths);
