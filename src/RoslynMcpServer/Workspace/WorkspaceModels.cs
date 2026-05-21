using RoslynMcpServer.Infrastructure;

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

public sealed record WorkspaceScanResult
{
    public WorkspaceScanResult(
        string root,
        IReadOnlyList<WorkspaceCandidate> solutions,
        IReadOnlyList<WorkspaceCandidate> projects,
        bool truncated,
        string? truncationReason,
        TimeSpan elapsed)
    {
        this.Root = root;
        this.Solutions = SortCandidates(solutions);
        this.Projects = SortCandidates(projects);
        this.Truncated = truncated;
        this.TruncationReason = truncationReason;
        this.Elapsed = elapsed;
    }

    public string Root { get; }

    public IReadOnlyList<WorkspaceCandidate> Solutions { get; }

    public IReadOnlyList<WorkspaceCandidate> Projects { get; }

    public bool Truncated { get; }

    public string? TruncationReason { get; }

    public TimeSpan Elapsed { get; }

    private static IReadOnlyList<WorkspaceCandidate> SortCandidates(IEnumerable<WorkspaceCandidate> candidates) =>
        candidates
            .OrderBy(candidate => candidate.RelativePath.Count(c => c == '/'))
            .ThenBy(candidate => candidate.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
}

public sealed record WorkspaceStatus(
    string Root,
    WorkspaceLoadState State,
    WorkspaceTarget? CurrentTarget,
    bool LanguageServerRunning,
    int PendingLspRequests,
    DateTimeOffset? LastLspResponseAt,
    WorkspaceScanResult Workspaces,
    string? FailureCode,
    string? FailureMessage,
    int OpenDocumentCount,
    int KnownDiagnosticsFileCount,
    DateTimeOffset? LastDiagnosticUpdateAt,
    int DiagnosticNotificationQueueCapacity,
    int PendingDiagnosticNotifications,
    long ProcessedDiagnosticNotifications,
    long DroppedDiagnosticNotifications,
    long StaleDiagnosticNotifications,
    string? DiagnosticNotificationOverflowPolicy,
    IReadOnlyList<WorkspaceWarning> Warnings)
{
    public IReadOnlyList<string> GuidanceResources { get; init; } = ServerResourceUris.GuidanceResources;
}

public sealed record WorkspaceWarning(
    string Code,
    string Message,
    IReadOnlyList<string> RelatedPaths);
