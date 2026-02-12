namespace PuddingCodeIntelligence.Contracts;

public enum CodeSymbolKind
{
    Unknown,
    Namespace,
    Type,
    Struct,
    Class,
    Interface,
    Enum,
    Delegate,
    Method,
    Constructor,
    Property,
    Field,
    Event,
    Parameter,
    Local,
    Variable,
    Constant,
    Operator,
}

public sealed record CodeSymbolRecord(
    string WorkspaceId,
    string ProjectId,
    string FilePath,
    string SymbolId,
    string Name,
    CodeSymbolKind Kind,
    int StartLine,
    int EndLine,
    string? Signature = null,
    string? Container = null);

public sealed record CodeSymbolSearchRequest(
    string WorkspaceId,
    string Query,
    string? ProjectId = null,
    CodeSymbolKind? Kind = null,
    int Limit = 50,
    int Skip = 0);

public sealed record CodeSymbolDetail(
    CodeSymbolRecord Symbol,
    CodeFileRecord? File = null,
    string? DisplayName = null);
