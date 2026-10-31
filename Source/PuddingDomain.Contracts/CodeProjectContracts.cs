namespace PuddingCodeIndex.Contracts;

public enum CodeProjectStatus
{
    Unknown,
    Registering,
    Active,
    Removing,
    Removed,
    Failed,
}

public sealed record CodeProjectRecord(
    string WorkspaceId,
    string ProjectId,
    string ProjectPath,
    CodeProjectStatus Status,
    string? DisplayName = null,
    DateTimeOffset? AddedAtUtc = null,
    DateTimeOffset? UpdatedAtUtc = null,
    ScopeSource? Source = null,
    ScopeState? ScopeState = null,
    /// <summary>
    /// 最近一次状态变更附带的原因（例如索引运行被中断）。为 null 表示没有可说的。
    /// <para>
    /// 放在末尾而不是紧跟 <see cref="Status"/>：位置参数一旦插入中间，所有构造点都得改，
    /// 而这里只需要一个读者。
    /// </para>
    /// </summary>
    string? StatusMessage = null);

public sealed record CodeProjectAddRequest(
    string WorkspaceId,
    string ProjectPath,
    string? ProjectId = null,
    string? DisplayName = null);

public sealed record CodeProjectRemoveRequest(
    string WorkspaceId,
    string ProjectId,
    bool RemoveIndexData = true);
