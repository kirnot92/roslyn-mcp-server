using RoslynMcpServer.Infrastructure;

namespace RoslynMcpServer.Workspace;

public sealed class DocumentPathMapper(PathGuard pathGuard)
{
    public string ResolveFileInput(string file) => pathGuard.RequireFileInsideRoot(file);

    public string ToFileUri(string fullPath) => new Uri(Path.GetFullPath(fullPath)).AbsoluteUri;

    public string ToRelativePath(string fullPath) => pathGuard.ToRelativePath(Path.GetFullPath(fullPath));

    public string NormalizePathPrefix(string pathPrefix)
    {
        try
        {
            var normalizedInput = pathPrefix
                .Replace('\\', Path.DirectorySeparatorChar)
                .Replace('/', Path.DirectorySeparatorChar);
            var fullPath = pathGuard.ResolveInsideRoot(normalizedInput);
            return TrimTrailingSlashes(pathGuard.ToRelativePath(fullPath));
        }
        catch (UserFacingException ex) when (ex.Code is "invalid_path" or "path_outside_root" or "path_reparse_point")
        {
            throw new UserFacingException("invalid_path_prefix", $"Invalid path prefix: {pathPrefix}");
        }
        catch (ArgumentException)
        {
            throw new UserFacingException("invalid_path_prefix", $"Invalid path prefix: {pathPrefix}");
        }
        catch (NotSupportedException)
        {
            throw new UserFacingException("invalid_path_prefix", $"Invalid path prefix: {pathPrefix}");
        }
    }

    public string UriToRelativePath(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || !parsed.IsFile)
        {
            throw new UserFacingException("invalid_lsp_uri", $"LSP returned a non-file URI: {uri}");
        }

        var fullPath = pathGuard.ResolveInsideRoot(Path.GetFullPath(parsed.LocalPath));
        return pathGuard.ToRelativePath(fullPath);
    }

    private static string TrimTrailingSlashes(string path) =>
        path.TrimEnd('/', '\\');
}
