using System.Text.Json.Serialization;

namespace PuddingCode.Platform;

/// <summary>
/// 聊天命令存储接口 — 聊天执行命令的可靠持久化和租约管理。
/// ADR-057-E: Status 使用 CommandStatus 枚举；CompleteAsync 拆分为终态方法。
/// </summary>
public interface IChatCommandStore
{
    /// <summary>持久化一条聊天执行命令，返回分配的命令记录。</summary>
    Task<ChatCommandRecord> SaveAsync(ChatCommandRecord command, CancellationToken ct = default);

    /// <summary>原子写入命令 + turn.accepted 事件到同一事务。</summary>
    Task<(ChatCommandRecord Command, long EventCursor)> SaveCommandWithTurnAcceptedAsync(
        ChatCommandRecord command,
        string turnAcceptedPayloadJson,
        string turnAcceptedEventType,
        string? agentId,
        int schemaVersion,
        CancellationToken ct = default);

    /// <summary>按 commandId 查找命令记录。</summary>
    Task<ChatCommandRecord?> GetAsync(string commandId, CancellationToken ct = default);

    /// <summary>按 turnId + conversationId 查找命令记录（用于 Cancel/Steering API）。</summary>
    Task<ChatCommandRecord?> FindByTurnIdAsync(string conversationId, string turnId, CancellationToken ct = default);

    /// <summary>
    /// CAS: Running → CancelRequested。只有当前持有 FenceToken 的 Worker 或 Cancel API 可以操作。
    /// 返回 true 表示状态已变更为 CancelRequested（Worker 后续检测到后取消 runCts）。
    /// </summary>
    Task<bool> RequestCancellationAsync(string commandId, string fenceToken, CancellationToken ct = default);

    /// <summary>按幂等键查找（clientRequestId + workspaceId）。</summary>
    Task<ChatCommandRecord?> FindByClientRequestIdAsync(string clientRequestId, string workspaceId, CancellationToken ct = default);

    /// <summary>领取下一条待执行的命令（租约语义）。</summary>
    Task<ChatCommandRecord?> LeaseNextAsync(string leaseOwner, long leaseDurationMs, CancellationToken ct = default);

    /// <summary>更新命令状态为运行中。</summary>
    Task MarkRunningAsync(string commandId, string runId, CancellationToken ct = default);

    /// <summary>续租。须传入正确的 FenceToken。返回 false 表示 fencing 冲突。</summary>
    Task<bool> RenewLeaseAsync(string commandId, string fenceToken, long leaseDurationMs, CancellationToken ct = default);

    /// <summary>释放租约（重置为 pending）。须传入正确的 FenceToken。</summary>
    Task ReleaseLeaseAsync(string commandId, string fenceToken, CancellationToken ct = default);

    /// <summary>提交命令成功终态。必须传入 terminalSequence 以验证终态事件已持久化。</summary>
    Task CommitSucceededAsync(string commandId, string fenceToken, string runId, long terminalSequence, CancellationToken ct = default);

    /// <summary>提交命令失败终态。</summary>
    Task CommitFailedAsync(string commandId, string fenceToken, string runId, string errorCode, string errorMessage, long? terminalSequence, CancellationToken ct = default);

    /// <summary>提交命令取消终态。</summary>
    Task CommitCancelledAsync(string commandId, string fenceToken, string runId, CancellationToken ct = default);

    /// <summary>标记租约丢失。</summary>
    Task MarkLeaseLostAsync(string commandId, string fenceToken, string runId, CancellationToken ct = default);
}

/// <summary>
/// 聊天执行命令持久记录 — 只保存执行引用，不包含运行时配置。
/// ADR-058: PayloadJson/AgentTemplateId 已删除。
/// 所有 LLM/Tool/Skill 配置由 Worker 执行时通过 IAgentRuntimeProfileResolver 动态装配。
/// </summary>
public sealed record ChatCommandRecord
{
    public string CommandId { get; init; } = string.Empty;
    public string? ClientRequestId { get; init; }
    public string WorkspaceId { get; init; } = string.Empty;
    public string SessionId { get; init; } = string.Empty;
    public string MessageId { get; init; } = string.Empty;         // Assistant message ID
    public required string UserMessageId { get; init; }             // User message ID (stable business key)
    public string TurnId { get; init; } = string.Empty;
    public required string AgentInstanceId { get; init; }           // Agent to execute
    public string? UserId { get; init; }
    public string? ChannelId { get; init; }
    public CommandStatus Status { get; init; } = CommandStatus.Pending;
    public int AttemptCount { get; init; }
    public string? LeaseOwner { get; init; }
    public long? LeaseUntil { get; init; }
    public long CreatedAt { get; init; }
    public long? StartedAt { get; init; }
    public long? CompletedAt { get; init; }
    public string? LastError { get; init; }
    public string? EventCursor { get; init; }
    public string? FenceToken { get; init; }
    public string? AssistantMessageId { get; init; }
    public string? RunId { get; init; }
    public long? TerminalSequence { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }

    public ChatCommandRecord() { }
}
