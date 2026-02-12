namespace PuddingCodeIntelligence.Contracts;

public enum CodeRelationKind
{
    Unknown,
    Contains,
    Calls,
    References,
    Inherits,
    Implements,
    Overrides,
    Overloads,
    Uses,
}

public sealed record CodeRelationRecord(
    string WorkspaceId,
    string ProjectId,
    string SourceSymbolId,
    string TargetSymbolId,
    CodeRelationKind Kind,
    int? SourceLine = null,
    string? SourceFilePath = null,
    DateTimeOffset? CreatedAtUtc = null);
