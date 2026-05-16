using RoslynMcpServer.Workspace;

namespace RoslynMcpServer.Tests;

public sealed class DocumentPathMapperTests
{
    [Fact]
    public void ResolveFileInputAndToFileUri_MapRootRelativePathToFileUri()
    {
        using var root = TestRoot.Create();
        Directory.CreateDirectory(Path.Combine(root.Path, "src"));
        var file = Path.Combine(root.Path, "src", "Program.cs");
        File.WriteAllText(file, "class C { }");
        var mapper = new DocumentPathMapper(new PathGuard(root.Path));

        var fullPath = mapper.ResolveFileInput("src/Program.cs");
        var uri = mapper.ToFileUri(fullPath);

        Assert.Equal(Path.GetFullPath(file), fullPath);
        Assert.True(Uri.TryCreate(uri, UriKind.Absolute, out var parsed));
        Assert.True(parsed!.IsFile);
    }

    [Fact]
    public void UriToRelativePath_MapsLspFileUriToRootRelativePath()
    {
        using var root = TestRoot.Create();
        Directory.CreateDirectory(Path.Combine(root.Path, "src"));
        var file = Path.Combine(root.Path, "src", "Program.cs");
        File.WriteAllText(file, "class C { }");
        var mapper = new DocumentPathMapper(new PathGuard(root.Path));

        var relativePath = mapper.UriToRelativePath(new Uri(file).AbsoluteUri);

        Assert.Equal("src/Program.cs", relativePath);
    }
}
