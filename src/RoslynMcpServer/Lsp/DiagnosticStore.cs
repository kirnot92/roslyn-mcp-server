using System.Text.Json;
using RoslynMcpServer.Infrastructure;
using RoslynMcpServer.Workspace;

namespace RoslynMcpServer.Lsp;

public sealed class DiagnosticStore(DocumentPathMapper pathMapper, IClock clock)
{
    public const int DefaultMaxDiagnosticFiles = 1000;
    public const int DefaultMaxDiagnosticsPerFile = 500;

    private readonly object syncLock = new();
    private readonly Dictionary<string, DiagnosticEntry> entries = new(PathComparer);
    private DateTimeOffset? lastUpdatedAt;

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    public int KnownFileCount
    {
        get
        {
            lock (this.syncLock)
            {
                return this.entries.Count;
            }
        }
    }

    public DateTimeOffset? LastUpdatedAt
    {
        get
        {
            lock (this.syncLock)
            {
                return this.lastUpdatedAt;
            }
        }
    }

    public void Clear()
    {
        lock (this.syncLock)
        {
            this.entries.Clear();
            this.lastUpdatedAt = null;
        }
    }

    public bool TryUpdateFromPublishDiagnostics(JsonElement? parameters)
    {
        try
        {
            if (parameters is null ||
                parameters.Value.ValueKind != JsonValueKind.Object ||
                !parameters.Value.TryGetProperty("uri", out var uriElement) ||
                uriElement.ValueKind != JsonValueKind.String ||
                !parameters.Value.TryGetProperty("diagnostics", out var diagnosticsElement) ||
                diagnosticsElement.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var uri = uriElement.GetString();
            if (string.IsNullOrWhiteSpace(uri))
            {
                return false;
            }

            string relativePath;
            try
            {
                relativePath = pathMapper.UriToRelativePath(uri);
            }
            catch (UserFacingException ex) when (ex.Code is "path_outside_root" or "invalid_lsp_uri")
            {
                return false;
            }

            var diagnostics = new List<StoredDiagnostic>();
            foreach (var diagnosticElement in diagnosticsElement.EnumerateArray())
            {
                if (diagnostics.Count >= DefaultMaxDiagnosticsPerFile)
                {
                    break;
                }

                var diagnostic = TryParseDiagnostic(diagnosticElement);
                if (diagnostic is not null)
                {
                    diagnostics.Add(diagnostic);
                }
            }

            var now = clock.UtcNow;
            lock (this.syncLock)
            {
                this.entries[relativePath] = new DiagnosticEntry(relativePath, diagnostics, now, now);
                this.lastUpdatedAt = now;
                EvictOldEntries();
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or UriFormatException)
        {
            return false;
        }
    }

    public DiagnosticFileSnapshot? GetFile(string relativePath, DiagnosticSeverity? severity)
    {
        lock (this.syncLock)
        {
            if (!this.entries.TryGetValue(relativePath, out var entry))
            {
                return null;
            }

            var now = clock.UtcNow;
            entry = entry with { LastAccessedAt = now };
            this.entries[relativePath] = entry;
            return new DiagnosticFileSnapshot(
                entry.File,
                FilterDiagnostics(entry.Diagnostics, severity).ToArray(),
                entry.LastUpdatedAt);
        }
    }

    public DiagnosticQuerySnapshot GetWorkspace(DiagnosticSeverity? severity, int maxResults)
    {
        lock (this.syncLock)
        {
            var items = new List<DiagnosticWithFile>();
            var totalKnown = 0;
            foreach (var entry in this.entries.Values.OrderBy(entry => entry.LastUpdatedAt).ThenBy(entry => entry.File, StringComparer.Ordinal))
            {
                foreach (var diagnostic in FilterDiagnostics(entry.Diagnostics, severity))
                {
                    totalKnown++;
                    if (items.Count < maxResults)
                    {
                        items.Add(new DiagnosticWithFile(entry.File, diagnostic));
                    }
                }
            }

            return new DiagnosticQuerySnapshot(
                items,
                totalKnown,
                items.Count,
                totalKnown > items.Count,
                this.lastUpdatedAt);
        }
    }

    public SeverityCounts GetSeverityCounts()
    {
        lock (this.syncLock)
        {
            var error = 0;
            var warning = 0;
            var information = 0;
            var hint = 0;
            var unknown = 0;

            foreach (var diagnostic in this.entries.Values.SelectMany(entry => entry.Diagnostics))
            {
                switch (diagnostic.Severity)
                {
                    case DiagnosticSeverity.Error:
                        error++;
                        break;
                    case DiagnosticSeverity.Warning:
                        warning++;
                        break;
                    case DiagnosticSeverity.Information:
                        information++;
                        break;
                    case DiagnosticSeverity.Hint:
                        hint++;
                        break;
                    default:
                        unknown++;
                        break;
                }
            }

            return new SeverityCounts(error, warning, information, hint, unknown);
        }
    }

    private void EvictOldEntries()
    {
        while (this.entries.Count > DefaultMaxDiagnosticFiles)
        {
            var oldest = this.entries.Values
                .OrderBy(entry => entry.LastUpdatedAt)
                .ThenBy(entry => entry.LastAccessedAt)
                .First();
            this.entries.Remove(oldest.File);
        }
    }

    private static IEnumerable<StoredDiagnostic> FilterDiagnostics(
        IReadOnlyList<StoredDiagnostic> diagnostics,
        DiagnosticSeverity? severity)
    {
        foreach (var diagnostic in diagnostics)
        {
            if (severity is null || diagnostic.Severity == severity.Value)
            {
                yield return diagnostic;
            }
        }
    }

    private static StoredDiagnostic? TryParseDiagnostic(JsonElement diagnosticElement)
    {
        if (diagnosticElement.ValueKind != JsonValueKind.Object ||
            !diagnosticElement.TryGetProperty("range", out var rangeElement) ||
            rangeElement.ValueKind != JsonValueKind.Object ||
            !diagnosticElement.TryGetProperty("message", out var messageElement) ||
            messageElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var range = TryReadRange(rangeElement);
        var message = messageElement.GetString();
        if (range is null || string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        return new StoredDiagnostic(
            range,
            TryReadSeverity(diagnosticElement),
            TryReadCode(diagnosticElement),
            TryReadOptionalString(diagnosticElement, "source"),
            message);
    }

    private static Range? TryReadRange(JsonElement rangeElement)
    {
        try
        {
            var range = rangeElement.Deserialize<Range>(JsonOptions.Default);
            return range?.Start is null || range.End is null ? null : range;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static DiagnosticSeverity? TryReadSeverity(JsonElement diagnosticElement)
    {
        if (!diagnosticElement.TryGetProperty("severity", out var severityElement) ||
            severityElement.ValueKind != JsonValueKind.Number ||
            !severityElement.TryGetInt32(out var value))
        {
            return null;
        }

        var severity = (DiagnosticSeverity)value;
        return Enum.IsDefined(severity) ? severity : null;
    }

    private static string? TryReadCode(JsonElement diagnosticElement)
    {
        if (!diagnosticElement.TryGetProperty("code", out var codeElement))
        {
            return null;
        }

        return codeElement.ValueKind switch
        {
            JsonValueKind.String => codeElement.GetString(),
            JsonValueKind.Number => codeElement.ToString(),
            _ => null
        };
    }

    private static string? TryReadOptionalString(JsonElement diagnosticElement, string propertyName) =>
        diagnosticElement.TryGetProperty(propertyName, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private sealed record DiagnosticEntry(
        string File,
        IReadOnlyList<StoredDiagnostic> Diagnostics,
        DateTimeOffset LastUpdatedAt,
        DateTimeOffset LastAccessedAt);
}

public sealed record StoredDiagnostic(
    Range Range,
    DiagnosticSeverity? Severity,
    string? Code,
    string? Source,
    string Message);

public sealed record DiagnosticFileSnapshot(
    string File,
    IReadOnlyList<StoredDiagnostic> Diagnostics,
    DateTimeOffset LastUpdatedAt);

public sealed record DiagnosticWithFile(string File, StoredDiagnostic Diagnostic);

public sealed record DiagnosticQuerySnapshot(
    IReadOnlyList<DiagnosticWithFile> Items,
    int TotalKnown,
    int Returned,
    bool Truncated,
    DateTimeOffset? LastUpdatedAt);

public sealed record SeverityCounts(
    int Error,
    int Warning,
    int Information,
    int Hint,
    int Unknown);
