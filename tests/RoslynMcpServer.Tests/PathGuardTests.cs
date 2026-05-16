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

    [Fact]
    public void RequireFileInsideRoot_RejectsReparsePointAncestorWhenSupported()
    {
        using var root = TestRoot.Create();
        using var outside = TestRoot.Create();
        File.WriteAllText(Path.Combine(outside.Path, "Outside.sln"), string.Empty);
        var linkPath = Path.Combine(root.Path, "link");

        try
        {
            Directory.CreateSymbolicLink(linkPath, outside.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }

        var guard = new PathGuard(root.Path);

        var userError = Assert.Throws<UserFacingException>(() =>
            guard.RequireFileInsideRoot(Path.Combine("link", "Outside.sln"), ".sln"));

        Assert.Equal("path_reparse_point", userError.Code);
    }
}
