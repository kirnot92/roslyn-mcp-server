using RoslynMcpServer.Workspace;

namespace RoslynMcpServer.Tests;

public sealed class WorkspaceScanResultTests
{
    [Fact]
    public void Constructor_SortsCandidatesByDepthThenRelativePath()
    {
        var result = new WorkspaceScanResult(
            "root",
            [
                new WorkspaceCandidate(WorkspaceKind.Solution, "root/z/App.sln", "z/App.sln"),
                new WorkspaceCandidate(WorkspaceKind.Solution, "root/App.sln", "App.sln"),
                new WorkspaceCandidate(WorkspaceKind.SolutionX, "root/a/App.slnx", "a/App.slnx")
            ],
            [
                new WorkspaceCandidate(WorkspaceKind.Project, "root/src/Z/Z.csproj", "src/Z/Z.csproj"),
                new WorkspaceCandidate(WorkspaceKind.Project, "root/App.csproj", "App.csproj"),
                new WorkspaceCandidate(WorkspaceKind.Project, "root/src/A/A.csproj", "src/A/A.csproj")
            ],
            truncated: false,
            truncationReason: null,
            elapsed: TimeSpan.Zero);

        Assert.Equal(["App.sln", "a/App.slnx", "z/App.sln"], result.Solutions.Select(x => x.RelativePath).ToArray());
        Assert.Equal(["App.csproj", "src/A/A.csproj", "src/Z/Z.csproj"], result.Projects.Select(x => x.RelativePath).ToArray());
    }
}
