namespace RoslynMcpServer.Lsp;

public sealed record TextDocumentIdentifier(string Uri);

public sealed record VersionedTextDocumentIdentifier(string Uri, int Version);

public sealed record TextDocumentItem(string Uri, string LanguageId, int Version, string Text);

public sealed record DidOpenTextDocumentParams(TextDocumentItem TextDocument);

public sealed record TextDocumentContentChangeEvent(string Text);

public sealed record DidChangeTextDocumentParams(
    VersionedTextDocumentIdentifier TextDocument,
    IReadOnlyList<TextDocumentContentChangeEvent> ContentChanges);

public sealed record DidCloseTextDocumentParams(TextDocumentIdentifier TextDocument);

public sealed record Position(int Line, int Character);

public sealed record Range(Position Start, Position End);

public sealed record DocumentSymbol(
    string Name,
    int Kind,
    Range Range,
    Range SelectionRange,
    string? Detail = null,
    IReadOnlyList<DocumentSymbol>? Children = null);

public sealed record Hover(MarkupContent? Contents = null, Range? Range = null);

public sealed record MarkupContent(string Kind, string Value);
