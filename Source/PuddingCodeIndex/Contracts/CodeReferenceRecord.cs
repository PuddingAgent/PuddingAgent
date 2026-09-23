namespace PuddingCodeIntelligence.Contracts;

public sealed record CodeReferenceRecord(
    string WorkspaceId,
    string ProjectId,
    string SourceSymbolId,
    string TargetSymbolId,
    string SourceFilePath,
    int SourceLine,
    string? SourceText = null,
    DateTimeOffset? ObservedAtUtc = null);
