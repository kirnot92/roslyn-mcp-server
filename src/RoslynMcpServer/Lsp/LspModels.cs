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
    SymbolKind Kind,
    Range Range,
    Range SelectionRange,
    string? Detail = null,
    IReadOnlyList<DocumentSymbol>? Children = null);

public sealed record Hover(MarkupContent? Contents = null, Range? Range = null);

public sealed record MarkupContent(string Kind, string Value);

public sealed record Location(string Uri, Range Range);

public sealed record LocationLink(string TargetUri, Range TargetRange, Range? TargetSelectionRange = null, Range? OriginSelectionRange = null);

public sealed record ReferenceContext(bool IncludeDeclaration);

public sealed record ReferenceParams(TextDocumentIdentifier TextDocument, Position Position, ReferenceContext Context);

public sealed record WorkspaceSymbolParams(string Query);

// Values are defined by the Language Server Protocol DiagnosticSeverity constants:
// https://microsoft.github.io/language-server-protocol/specifications/lsp/3.17/specification/#diagnosticSeverity
public enum DiagnosticSeverity
{
    Error = 1,
    Warning = 2,
    Information = 3,
    Hint = 4
}

public static class DiagnosticSeverityExtensions
{
    public static string ToMcpName(this DiagnosticSeverity? severity) =>
        severity switch
        {
            DiagnosticSeverity.Error => "error",
            DiagnosticSeverity.Warning => "warning",
            DiagnosticSeverity.Information => "information",
            DiagnosticSeverity.Hint => "hint",
            _ => "unknown"
        };
}

// Values are defined by the Language Server Protocol SymbolKind constants:
// https://microsoft.github.io/language-server-protocol/specifications/lsp/3.17/specification/#symbolKind
public enum SymbolKind
{
    File = 1,
    Module = 2,
    Namespace = 3,
    Package = 4,
    Class = 5,
    Method = 6,
    Property = 7,
    Field = 8,
    Constructor = 9,
    Enum = 10,
    Interface = 11,
    Function = 12,
    Variable = 13,
    Constant = 14,
    String = 15,
    Number = 16,
    Boolean = 17,
    Array = 18,
    Object = 19,
    Key = 20,
    Null = 21,
    EnumMember = 22,
    Struct = 23,
    Event = 24,
    Operator = 25,
    TypeParameter = 26
}

public static class SymbolKindExtensions
{
    public static string ToMcpName(this SymbolKind kind) =>
        kind switch
        {
            _ when !System.Enum.IsDefined(kind) => "unknown",
            // Keep multi-word LSP names in camelCase for stable MCP output.
            SymbolKind.EnumMember => "enumMember",
            SymbolKind.TypeParameter => "typeParameter",
            _ => kind.ToString().ToLowerInvariant()
        };
}
