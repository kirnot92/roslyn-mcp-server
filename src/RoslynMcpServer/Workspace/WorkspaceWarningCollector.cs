using System.Text.Json;

namespace RoslynMcpServer.Workspace;

internal sealed class WorkspaceWarningCollector
{
    private const string ProjectLoaderErrorToken = "[LanguageServerProjectLoader] Error while loading ";
    private const int MaxWorkspaceWarnings = 50;
    private readonly PathGuard pathGuard;
    private readonly object warningsLock = new();
    private readonly List<WorkspaceWarning> warnings = [];

    public WorkspaceWarningCollector(PathGuard pathGuard)
    {
        this.pathGuard = pathGuard;
    }

    public bool TryRecordWorkspaceLoadWarning(JsonElement? parameters)
    {
        if (parameters is null ||
            parameters.Value.ValueKind != JsonValueKind.Object ||
            !parameters.Value.TryGetProperty("message", out var messageElement) ||
            messageElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var message = messageElement.GetString();
        if (string.IsNullOrWhiteSpace(message) ||
            !message.Contains(ProjectLoaderErrorToken, StringComparison.Ordinal))
        {
            return false;
        }

        var relatedPaths = this.TryGetLoadErrorRelativePath(message) is { } path
            ? new[] { path }
            : [];
        var warning = new WorkspaceWarning(
            "workspace_project_load_failed",
            BuildWorkspaceLoadWarningMessage(message),
            relatedPaths);

        this.Add(warning);
        return true;
    }

    public bool HasWarnings()
    {
        lock (this.warningsLock)
        {
            return this.warnings.Count > 0;
        }
    }

    public IReadOnlyList<WorkspaceWarning> GetWarnings()
    {
        lock (this.warningsLock)
        {
            return this.warnings.ToArray();
        }
    }

    public void Clear()
    {
        lock (this.warningsLock)
        {
            this.warnings.Clear();
        }
    }

    private void Add(WorkspaceWarning warning)
    {
        lock (this.warningsLock)
        {
            if (this.warnings.Any(existing =>
                string.Equals(existing.Code, warning.Code, StringComparison.Ordinal) &&
                existing.RelatedPaths.SequenceEqual(warning.RelatedPaths, StringComparer.OrdinalIgnoreCase)))
            {
                return;
            }

            if (this.warnings.Count >= MaxWorkspaceWarnings)
            {
                return;
            }

            this.warnings.Add(warning);
        }
    }

    private string? TryGetLoadErrorRelativePath(string message)
    {
        var tokenIndex = message.IndexOf(ProjectLoaderErrorToken, StringComparison.Ordinal);
        if (tokenIndex < 0)
        {
            return null;
        }

        var start = tokenIndex + ProjectLoaderErrorToken.Length;
        var suffix = message[start..];
        string[] extensions = [".csproj", ".slnx", ".sln"];
        foreach (var extension in extensions)
        {
            var extensionIndex = suffix.IndexOf(extension, StringComparison.OrdinalIgnoreCase);
            if (extensionIndex < 0)
            {
                continue;
            }

            var path = suffix[..(extensionIndex + extension.Length)];
            try
            {
                var fullPath = Path.GetFullPath(path);
                return this.pathGuard.IsInsideRoot(fullPath)
                    ? this.pathGuard.ToRelativePath(fullPath)
                    : path;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return path;
            }
        }

        return null;
    }

    private static string BuildWorkspaceLoadWarningMessage(string message)
    {
        const string sdkMissing = "A compatible .NET SDK was not found.";
        if (message.Contains(sdkMissing, StringComparison.OrdinalIgnoreCase))
        {
            var requestedSdk = TryExtractLineValue(message, "Requested SDK version:");
            var globalJson = TryExtractLineValue(message, "global.json file:");
            var details = new List<string>
            {
                "Roslyn LS failed to load a project because a compatible .NET SDK was not found."
            };

            if (!string.IsNullOrWhiteSpace(requestedSdk))
            {
                details.Add($"Requested SDK version: {requestedSdk}.");
            }

            if (!string.IsNullOrWhiteSpace(globalJson))
            {
                details.Add($"global.json: {globalJson}.");
            }

            return string.Join(' ', details);
        }

        return "Roslyn LS reported a project load error. Read-tool results may be incomplete; inspect server logs for the full load error.";
    }

    private static string? TryExtractLineValue(string message, string prefix)
    {
        using var reader = new StringReader(message);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            line = line.Trim();
            if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return line[prefix.Length..].Trim();
            }
        }

        return null;
    }
}
