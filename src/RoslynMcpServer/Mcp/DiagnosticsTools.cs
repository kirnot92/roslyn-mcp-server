using System.ComponentModel;
using ModelContextProtocol.Server;
using RoslynMcpServer.Infrastructure;
using RoslynMcpServer.Lsp;
using RoslynMcpServer.Workspace;

namespace RoslynMcpServer.Mcp;

[McpServerToolType]
public sealed class DiagnosticsTools(
    WorkspaceSession session,
    DocumentStateManager documents,
    DocumentPathMapper pathMapper,
    DiagnosticStore diagnostics)
{
    private const int DefaultDiagnosticsMaxResults = 200;
    private const int MaxDiagnosticsMaxResults = 1000;

    [McpServerTool(Name = "diagnostics")]
    [Description("Return currently known C# diagnostics for a file, or for the workspace when scope is workspace.")]
    public async Task<object> Diagnostics(
        string? file = null,
        string? severity = null,
        int? maxResults = null,
        string? scope = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var filter = ParseSeverity(severity);
            var effectiveMaxResults = NormalizeMaxResults(maxResults);
            var context = await session.PrepareReadToolAsync(cancellationToken).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(file))
            {
                if (string.Equals(scope, "workspace", StringComparison.OrdinalIgnoreCase))
                {
                    throw new UserFacingException("invalid_scope", "Specify either file or scope: workspace, not both.");
                }

                return await FileDiagnostics(file, filter, effectiveMaxResults, context, cancellationToken).ConfigureAwait(false);
            }

            if (!string.Equals(scope, "workspace", StringComparison.OrdinalIgnoreCase))
            {
                throw new UserFacingException("invalid_scope", "Specify a file, or set scope to workspace.");
            }

            return WorkspaceDiagnostics(filter, effectiveMaxResults, context);
        }
        catch (UserFacingException ex)
        {
            return ToolError.FromException(ex);
        }
    }

    private async Task<DiagnosticsResult> FileDiagnostics(
        string file,
        DiagnosticSeverity? severity,
        int maxResults,
        ReadToolContext context,
        CancellationToken cancellationToken)
    {
        var document = await documents.EnsureOpenAsync(file, context.Handle.Client, cancellationToken).ConfigureAwait(false);
        var relativePath = pathMapper.ToRelativePath(document.FullPath);
        var snapshot = diagnostics.GetFile(relativePath, severity);
        var metadata = CreateMetadata(context.State, scope: "file", hasKnownPublish: snapshot is not null, truncated: false);
        if (snapshot is null)
        {
            return new DiagnosticsResult(
                [],
                TotalKnown: 0,
                Returned: 0,
                metadata.WorkspaceState,
                metadata.Completeness,
                metadata.Reason,
                metadata.RetryAfterMs,
                Truncated: false,
                LastUpdatedAt: null);
        }

        var items = snapshot.Diagnostics
            .Take(maxResults)
            .Select(diagnostic => MapDiagnostic(snapshot.File, diagnostic))
            .ToArray();
        var truncated = snapshot.Truncated || snapshot.Diagnostics.Count > items.Length;
        metadata = CreateMetadata(context.State, scope: "file", hasKnownPublish: true, truncated);

        return new DiagnosticsResult(
            items,
            snapshot.TotalKnown,
            items.Length,
            metadata.WorkspaceState,
            metadata.Completeness,
            metadata.Reason,
            metadata.RetryAfterMs,
            metadata.Truncated,
            snapshot.LastUpdatedAt);
    }

    private DiagnosticsResult WorkspaceDiagnostics(
        DiagnosticSeverity? severity,
        int maxResults,
        ReadToolContext context)
    {
        var snapshot = diagnostics.GetWorkspace(severity, maxResults);
        var metadata = CreateMetadata(context.State, scope: "workspace", hasKnownPublish: true, snapshot.Truncated);
        var items = snapshot.Items
            .Select(item => MapDiagnostic(item.File, item.Diagnostic))
            .ToArray();

        return new DiagnosticsResult(
            items,
            snapshot.TotalKnown,
            snapshot.Returned,
            metadata.WorkspaceState,
            metadata.Completeness,
            metadata.Reason,
            metadata.RetryAfterMs,
            metadata.Truncated,
            snapshot.LastUpdatedAt);
    }

    private static DiagnosticItem MapDiagnostic(string file, StoredDiagnostic diagnostic)
    {
        var range = PositionMapper.ToMcpRange(diagnostic.Range);
        return new DiagnosticItem(
            file,
            range,
            range.StartLine,
            range.StartColumn,
            diagnostic.Severity.ToMcpName(),
            diagnostic.Code,
            diagnostic.Source,
            diagnostic.Message);
    }

    private static DiagnosticSeverity? ParseSeverity(string? severity)
    {
        if (string.IsNullOrWhiteSpace(severity))
        {
            return null;
        }

        return severity.Trim().ToLowerInvariant() switch
        {
            "error" => DiagnosticSeverity.Error,
            "warning" => DiagnosticSeverity.Warning,
            "information" or "info" => DiagnosticSeverity.Information,
            "hint" => DiagnosticSeverity.Hint,
            _ => throw new UserFacingException(
                "invalid_severity",
                "severity must be one of error, warning, information, info, or hint.")
        };
    }

    private static int NormalizeMaxResults(int? maxResults)
    {
        if (maxResults is null)
        {
            return DefaultDiagnosticsMaxResults;
        }

        if (maxResults.Value < 1)
        {
            throw new UserFacingException("invalid_max_results", "maxResults must be a positive integer.");
        }

        return Math.Min(maxResults.Value, MaxDiagnosticsMaxResults);
    }

    private static ReadToolMetadata CreateMetadata(
        WorkspaceLoadState state,
        string scope,
        bool hasKnownPublish,
        bool truncated)
    {
        if (scope == "file" && !hasKnownPublish)
        {
            return new ReadToolMetadata(
                state.ToString(),
                "unknown",
                "No textDocument/publishDiagnostics notification has been received for this file yet; diagnostics are currently unknown.",
                2000,
                truncated);
        }

        if (scope == "file")
        {
            return new ReadToolMetadata(
                state.ToString(),
                "unknown",
                "Diagnostics reflect the last textDocument/publishDiagnostics notification for this file; Roslyn LS does not report whether diagnostics have fully settled.",
                state is WorkspaceLoadState.Ready ? null : 2000,
                truncated);
        }

        return state switch
        {
            WorkspaceLoadState.Ready => new ReadToolMetadata(
                state.ToString(),
                "unknown",
                "Workspace diagnostics include only currently known textDocument/publishDiagnostics notifications; Roslyn LS does not report a complete workspace diagnostics signal.",
                null,
                truncated),
            WorkspaceLoadState.WorkspaceWarming or WorkspaceLoadState.LspReady => new ReadToolMetadata(
                state.ToString(),
                "partial",
                "Workspace diagnostics include only currently known textDocument/publishDiagnostics notifications while the workspace is still loading.",
                2000,
                truncated),
            _ => new ReadToolMetadata(
                state.ToString(),
                "unknown",
                "Workspace diagnostics include only currently known textDocument/publishDiagnostics notifications.",
                null,
                truncated)
        };
    }
}
