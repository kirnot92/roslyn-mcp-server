using RoslynMcpServer.Infrastructure;
using RoslynMcpServer.Workspace;

namespace RoslynMcpServer.Tests;

public sealed class PathGuardTests
{
    [Fact]
    public void ResolveInsideRoot_AllowsRelativePathUnderRoot()
    {
        using var root = TestRoot.Create();
        File.WriteAllText(Path.Combine(root.Path, "App.sln"), string.Empty);
        var guard = new PathGuard(root.Path);

        var fullPath = guard.ResolveInsideRoot("App.sln");

        Assert.Equal(Path.Combine(root.Path, "App.sln"), fullPath);
        Assert.Equal("App.sln", guard.ToRelativePath(fullPath));
    }

    [Fact]
    public void ResolveInsideRoot_RejectsParentEscape()
    {
        using var root = TestRoot.Create();
        var guard = new PathGuard(root.Path);

        var ex = Assert.Throws<UserFacingException>(() => guard.ResolveInsideRoot("..\\outside.sln"));

        Assert.Equal("path_outside_root", ex.Code);
    }
}
