using RoslynMcpServer.Infrastructure;

namespace RoslynMcpServer.Workspace;

public sealed class DocumentPathMapper(PathGuard pathGuard)
{
    public string ResolveFileInput(string file) => pathGuard.RequireFileInsideRoot(file);

    public string ToFileUri(string fullPath) => new Uri(Path.GetFullPath(fullPath)).AbsoluteUri;

    public string ToRelativePath(string fullPath) => pathGuard.ToRelativePath(Path.GetFullPath(fullPath));

    public string UriToRelativePath(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || !parsed.IsFile)
        {
            throw new UserFacingException("invalid_lsp_uri", $"LSP returned a non-file URI: {uri}");
        }

        var fullPath = pathGuard.ResolveInsideRoot(Path.GetFullPath(parsed.LocalPath));
        return pathGuard.ToRelativePath(fullPath);
    }
}
