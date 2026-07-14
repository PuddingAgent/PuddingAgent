using System.Text.Json.Serialization;

namespace PuddingCode.Platform;

/// <summary>
/// 聊天命令存储接口 — 聊天执行命令的可靠持久化和租约管理。
/// 
/// 关联 ADR：Docs/07架构/57ADR-056聊天消息受理与可靠事件流架构ADR.md §5 (ADR-056-B)
/// </summary>
public interface IChatCommandStore
{
    /// <summary>持久化一条聊天执行命令，返回分配的命令记录。</summary>
    Task<ChatCommandRecord> SaveAsync(ChatCommandRecord command, CancellationToken ct = default);

    /// <summary>按 commandId 查找命令记录。</summary>
    Task<ChatCommandRecord?> GetAsync(string commandId, CancellationToken ct = default);

    /// <summary>按幂等键查找（clientRequestId + workspaceId）。</summary>
    Task<ChatCommandRecord?> FindByClientRequestIdAsync(string clientRequestId, string workspaceId, CancellationToken ct = default);

    /// <summary>领取下一条待执行的命令（租约语义）。</summary>
    Task<ChatCommandRecord?> LeaseNextAsync(string leaseOwner, long leaseDurationMs, CancellationToken ct = default);

    /// <summary>更新命令状态。</summary>
    Task UpdateStatusAsync(string commandId, string status, string? lastError = null, CancellationToken ct = default);

    /// <summary>完成命令（标记 succeeded/failed/cancelled）。</summary>
    Task CompleteAsync(string commandId, string status, string? lastError = null, CancellationToken ct = default);

    /// <summary>释放租约（将命令重置为 pending）。</summary>
    Task ReleaseLeaseAsync(string commandId, CancellationToken ct = default);
}

/// <summary>聊天命令持久记录。</summary>
public sealed record ChatCommandRecord
{
    public string CommandId { get; init; } = string.Empty;
    public string? ClientRequestId { get; init; }
    public string WorkspaceId { get; init; } = string.Empty;
    public string SessionId { get; init; } = string.Empty;
    public string MessageId { get; init; } = string.Empty;
    public string TurnId { get; init; } = string.Empty;
    public string? AgentInstanceId { get; init; }
    public string? AgentTemplateId { get; init; }
    public string? UserId { get; init; }
    public string PayloadJson { get; init; } = string.Empty;
    public string Status { get; init; } = "pending";
    public int AttemptCount { get; init; }
    public string? LeaseOwner { get; init; }
    public long? LeaseUntil { get; init; }
    public long CreatedAt { get; init; }
    public long? StartedAt { get; init; }
    public long? CompletedAt { get; init; }
    public string? LastError { get; init; }
    public string? EventCursor { get; init; }

    public ChatCommandRecord() { }
}
