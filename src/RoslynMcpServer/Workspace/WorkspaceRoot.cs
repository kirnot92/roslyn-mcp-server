using RoslynMcpServer.Infrastructure;

namespace RoslynMcpServer.Workspace;

public sealed class WorkspaceRoot
{
    private readonly string rootWithSeparator;
    private readonly StringComparison comparison;

    public WorkspaceRoot(string root)
    {
        Root = Path.GetFullPath(root);
        if (!Directory.Exists(Root))
        {
            throw new UserFacingException("root_not_found", $"Workspace root does not exist: {Root}");
        }

        if ((File.GetAttributes(Root) & FileAttributes.Directory) == 0)
        {
            throw new UserFacingException("root_not_directory", $"Workspace root is not a directory: {Root}");
        }

        this.comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        this.rootWithSeparator = EnsureTrailingSeparator(Root);
    }

    public string Root { get; }

    public string ResolveFileInput(string file) => RequireFileInsideRoot(file);

    public string ResolveInsideRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new UserFacingException("invalid_path", "Path must not be empty.");
        }

        var candidate = Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(Root, path));

        if (!IsInsideRoot(candidate))
        {
            throw new UserFacingException("path_outside_root", $"Path is outside the workspace root: {path}");
        }

        RejectReparsePointInPath(candidate, path);

        return candidate;
    }

    public string RequireFileInsideRoot(string path, params string[] allowedExtensions)
    {
        var fullPath = ResolveInsideRoot(path);
        if (!File.Exists(fullPath))
        {
            throw new UserFacingException("file_not_found", $"File was not found: {path}");
        }

        var extension = Path.GetExtension(fullPath);
        if (allowedExtensions.Length > 0 &&
            !allowedExtensions.Any(e => string.Equals(e, extension, StringComparison.OrdinalIgnoreCase)))
        {
            throw new UserFacingException(
                "invalid_workspace_file",
                $"Expected one of {string.Join(", ", allowedExtensions)}, but got: {path}");
        }

        return fullPath;
    }

    public string ToRelativePath(string fullPath) =>
        Path.GetRelativePath(Root, Path.GetFullPath(fullPath)).Replace(Path.DirectorySeparatorChar, '/');

    public string ToFileUri(string fullPath) => new Uri(Path.GetFullPath(fullPath)).AbsoluteUri;

    public string NormalizePathPrefix(string pathPrefix)
    {
        try
        {
            var normalizedInput = pathPrefix
                .Replace('\\', Path.DirectorySeparatorChar)
                .Replace('/', Path.DirectorySeparatorChar);
            var fullPath = ResolveInsideRoot(normalizedInput);
            return TrimTrailingSlashes(ToRelativePath(fullPath));
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

        var fullPath = ResolveInsideRoot(Path.GetFullPath(parsed.LocalPath));
        return ToRelativePath(fullPath);
    }

    public bool IsInsideRoot(string fullPath)
    {
        var normalized = Path.GetFullPath(fullPath);
        return string.Equals(normalized, Root, this.comparison) ||
            normalized.StartsWith(this.rootWithSeparator, this.comparison);
    }

    private void RejectReparsePointInPath(string fullPath, string originalPath)
    {
        var relative = Path.GetRelativePath(Root, fullPath);
        if (relative == ".")
        {
            return;
        }

        var current = Root;
        foreach (var part in relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (IsReparsePoint(current))
            {
                throw new UserFacingException(
                    "path_reparse_point",
                    $"Reparse point paths are not supported in M1: {originalPath}");
            }
        }
    }

    private static bool IsReparsePoint(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return false;
        }

        return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    }

    private static string EnsureTrailingSeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;

    private static string TrimTrailingSlashes(string path) =>
        path.TrimEnd('/', '\\');
}
