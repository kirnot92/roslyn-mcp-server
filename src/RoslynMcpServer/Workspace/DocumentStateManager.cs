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

    public int OpenDocumentCount => documents.Count;

    public async Task<OpenDocumentState> EnsureOpenAsync(
        string file,
        ILspClient client,
        CancellationToken cancellationToken = default)
    {
        var fullPath = pathMapper.ResolveFileInput(file);
        await syncLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(lspClient, client))
            {
                documents.Clear();
                lspClient = client;
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

            if (!documents.TryGetValue(key, out var state))
            {
                var text = await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false);
                state = new OpenDocumentState(uri, key, Version: 1, lastWriteTime, info.Length, now);
                await client.NotifyAsync(
                    "textDocument/didOpen",
                    new DidOpenTextDocumentParams(new TextDocumentItem(uri, "csharp", state.Version, text)),
                    cancellationToken).ConfigureAwait(false);

                documents[key] = state;
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
                    LastAccessedAt = now
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

            documents[key] = state;
            await EvictIfNeededAsync(key, client, cancellationToken).ConfigureAwait(false);
            return state;
        }
        finally
        {
            syncLock.Release();
        }
    }

    private async Task EvictIfNeededAsync(string currentKey, ILspClient client, CancellationToken cancellationToken)
    {
        while (documents.Count > options.MaxOpenDocuments)
        {
            var lru = documents
                .Where(pair => !PathComparer.Equals(pair.Key, currentKey))
                .OrderBy(pair => pair.Value.LastAccessedAt)
                .FirstOrDefault();

            if (string.IsNullOrEmpty(lru.Key))
            {
                break;
            }

            documents.Remove(lru.Key);
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
    DateTimeOffset LastAccessedAt);
