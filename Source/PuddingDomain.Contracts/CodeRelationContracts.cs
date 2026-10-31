namespace PuddingCodeIndex.Contracts;

// CodeRelationKind 已下沉到 PuddingDomain.Contracts（跨层共享值类型）。
// 命名空间保持不变 ⇒ 引用方零改动。

public sealed record CodeRelationRecord(
    string WorkspaceId,
    string ProjectId,
    string SourceSymbolId,
    string TargetSymbolId,
    CodeRelationKind Kind,
    int? SourceLine = null,
    string? SourceFilePath = null,
    DateTimeOffset? CreatedAtUtc = null);
