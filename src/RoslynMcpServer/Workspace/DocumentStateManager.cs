using RoslynMcpServer.Cli;
using RoslynMcpServer.Infrastructure;
using RoslynMcpServer.Lsp;

namespace RoslynMcpServer.Workspace;

public sealed class DocumentStateManager(CliOptions options, DocumentPathMapper pathMapper)
{
    private readonly Dictionary<string, OpenDocumentState> documents = new(PathComparer);
    private readonly SemaphoreSlim syncLock = new(1, 1);
    private ILspClient? lspClient;

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    public int OpenDocumentCount => this.documents.Count;
    public long MaxDocumentBytes => options.MaxDocumentBytes;

    public async Task<OpenDocumentState> EnsureOpenAsync(
        string file,
        ILspClient client,
        CancellationToken cancellationToken = default)
    {
        var fullPath = pathMapper.ResolveFileInput(file);
        await this.syncLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(this.lspClient, client))
            {
                this.documents.Clear();
                this.lspClient = client;
            }

            var info = new FileInfo(fullPath);
            if (!info.Exists)
            {
                throw new UserFacingException("file_not_found", $"File was not found: {file}");
            }

            if (info.Length > options.MaxDocumentBytes)
            {
                throw new UserFacingException(
                    "document_too_large",
                    $"File exceeds the configured MaxDocumentBytes limit ({options.MaxDocumentBytes} bytes): {file}");
            }

            var lastWriteTime = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
            var now = DateTimeOffset.UtcNow;
            var key = Path.GetFullPath(fullPath);
            var uri = pathMapper.ToFileUri(fullPath);

            if (!this.documents.TryGetValue(key, out var state))
            {
                var text = await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false);
                state = new OpenDocumentState(
                    uri,
                    key,
                    Version: 1,
                    lastWriteTime,
                    info.Length,
                    now,
                    DocumentLineMap.FromText(text));
                await client.NotifyAsync(
                    "textDocument/didOpen",
                    new DidOpenTextDocumentParams(new TextDocumentItem(uri, "csharp", state.Version, text)),
                    cancellationToken).ConfigureAwait(false);

                this.documents[key] = state;
                await EvictIfNeededAsync(key, client, cancellationToken).ConfigureAwait(false);
                return state;
            }

            if (state.LastWriteTime != lastWriteTime || state.Length != info.Length)
            {
                var text = await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false);
                state = state with
                {
                    Version = state.Version + 1,
                    LastWriteTime = lastWriteTime,
                    Length = info.Length,
                    LastAccessedAt = now,
                    LineMap = DocumentLineMap.FromText(text)
                };

                await client.NotifyAsync(
                    "textDocument/didChange",
                    new DidChangeTextDocumentParams(
                        new VersionedTextDocumentIdentifier(uri, state.Version),
                        [new TextDocumentContentChangeEvent(text)]),
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                state = state with { LastAccessedAt = now };
            }

            this.documents[key] = state;
            await EvictIfNeededAsync(key, client, cancellationToken).ConfigureAwait(false);
            return state;
        }
        finally
        {
            this.syncLock.Release();
        }
    }

    public void ValidatePosition(OpenDocumentState document, string file, int line, int column)
    {
        if (line < 1 || column < 1)
        {
            throw new UserFacingException(
                "invalid_position",
                "line and column must be 1-based positive integers.");
        }

        if (line > document.LineCount)
        {
            throw new UserFacingException(
                "position_out_of_range",
                $"Position line {line}, column {column} is outside {file}. Allowed line range is 1..{document.LineCount}.");
        }

        var lineLength = document.GetLineLength(line);
        var maxColumn = lineLength + 1;
        if (column > maxColumn)
        {
            throw new UserFacingException(
                "position_out_of_range",
                $"Position line {line}, column {column} is outside {file}. Allowed column range for line {line} is 1..{maxColumn} (line length {lineLength}).");
        }
    }

    private async Task EvictIfNeededAsync(string currentKey, ILspClient client, CancellationToken cancellationToken)
    {
        while (this.documents.Count > options.MaxOpenDocuments)
        {
            var lru = this.documents
                .Where(pair => !PathComparer.Equals(pair.Key, currentKey))
                .OrderBy(pair => pair.Value.LastAccessedAt)
                .FirstOrDefault();

            if (string.IsNullOrEmpty(lru.Key))
            {
                break;
            }

            this.documents.Remove(lru.Key);
            await client.NotifyAsync(
                "textDocument/didClose",
                new DidCloseTextDocumentParams(new TextDocumentIdentifier(lru.Value.Uri)),
                cancellationToken).ConfigureAwait(false);
        }
    }
}

public sealed record OpenDocumentState(
    string Uri,
    string FullPath,
    int Version,
    DateTimeOffset LastWriteTime,
    long Length,
    DateTimeOffset LastAccessedAt,
    DocumentLineMap LineMap)
{
    public int LineCount => this.LineMap.LineCount;

    public int GetLineLength(int line) => this.LineMap.GetLineLength(line);
}

public sealed record DocumentLineMap(IReadOnlyList<int> LineLengths)
{
    public int LineCount => this.LineLengths.Count;

    public static DocumentLineMap FromText(string text)
    {
        var lineLengths = new List<int>();
        var currentLineLength = 0;

        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (character == '\r')
            {
                lineLengths.Add(currentLineLength);
                currentLineLength = 0;
                if (index + 1 < text.Length && text[index + 1] == '\n')
                {
                    index++;
                }

                continue;
            }

            if (character == '\n')
            {
                lineLengths.Add(currentLineLength);
                currentLineLength = 0;
                continue;
            }

            currentLineLength++;
        }

        lineLengths.Add(currentLineLength);
        return new DocumentLineMap(lineLengths.ToArray());
    }

    public int GetLineLength(int line) => this.LineLengths[line - 1];
}
