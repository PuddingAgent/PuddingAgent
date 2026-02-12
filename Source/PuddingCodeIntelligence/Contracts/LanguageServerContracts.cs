namespace PuddingCodeIntelligence.Contracts;

public enum LanguageServerMethod
{
    Unknown,
    Completion,
    Hover,
    Definition,
    TypeDefinition,
    References,
    CallHierarchy,
    Rename,
    Format,
    OrganizeImports,
    SourceAction,
}

public sealed record LanguageServerRequest(
    string WorkspaceId,
    LanguageServerMethod Method,
    string DocumentPath,
    int Line = 0,
    int Character = 0,
    string? ProjectId = null,
    string? PayloadJson = null,
    string? CorrelationId = null);

public sealed record LanguageServerResponse(
    bool IsSupported,
    LanguageServerMethod Method,
    string? ResultJson = null,
    string? Error = null,
    string? CorrelationId = null)
{
    public static LanguageServerResponse Unsupported(LanguageServerMethod method, string? correlationId = null) =>
        new(false, method, Error: "Language server is not implemented.", CorrelationId: correlationId);

    public static LanguageServerResponse Success(
        LanguageServerMethod method,
        string resultJson,
        string? correlationId = null) =>
        new(true, method, ResultJson: resultJson, CorrelationId: correlationId);
}
