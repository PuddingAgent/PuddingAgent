using System.Text.Json;

namespace PuddingCode.Platform;

// ═══════════════════════════════════════════════════════════════
// Frozen Contracts — ADR-059 Phase 1
// 这些类型定义了 Harness Execution Kernel 的不可变契约。
// 只有 API 契约隐含向后兼容时才允许修改。
// ═══════════════════════════════════════════════════════════════

/// <summary>
/// 执行租约 — Worker 从 LeaseStore 获取的唯一执行凭证。
/// 整个执行链路（Worker → Coordinator → Journal → Inbox）透传同一个实例。
/// 禁止下游组件重新创建或修改 Lease。
/// </summary>
public sealed record ExecutionLease(
    string CommandId,
    string WorkerId,
    string WorkspaceId,
    string ConversationId,
    string TurnId,
    string RunId,
    long FencingToken,
    DateTimeOffset ExpiresAt)
{
    public bool HasExpired(DateTimeOffset now) => now >= ExpiresAt;

    public bool IsOwnedBy(string workerId) =>
        string.Equals(WorkerId, workerId, StringComparison.Ordinal);

    public void Deconstruct(
        out string commandId, out string workerId, out string workspaceId,
        out string conversationId, out string turnId, out string runId,
        out long fencingToken, out DateTimeOffset expiresAt)
    {
        commandId = CommandId;
        workerId = WorkerId;
        workspaceId = WorkspaceId;
        conversationId = ConversationId;
        turnId = TurnId;
        runId = RunId;
        fencingToken = FencingToken;
        expiresAt = ExpiresAt;
    }
}

/// <summary>
/// Turn 终态 — Coordinator 写入 Journal 的终端事件。
/// Kind 决定 Command/Turn/Run 的最终状态，禁止从字符串推测。
/// </summary>
public sealed record TurnTerminal(
    TurnTerminalKind Kind,
    string? ErrorCode,
    string? ErrorMessage,
    string? Reply,
    JsonElement? Usage)
{
    public static TurnTerminal Success(string? reply, JsonElement? usage) =>
        new(TurnTerminalKind.Completed, null, null, reply, usage);

    public static TurnTerminal Failure(string errorCode, string errorMessage) =>
        new(TurnTerminalKind.Failed, errorCode, errorMessage, null, null);

    public static TurnTerminal Cancelled =>
        new(TurnTerminalKind.Cancelled,
            TerminalErrorCodes.ExecutionCancelled,
            "Turn was cancelled.", null, null);

    public static TurnTerminal LeaseLost =>
        new(TurnTerminalKind.Failed,
            TerminalErrorCodes.LeaseLost,
            "Execution lease lost.", null, null);

    public static TurnTerminal ProtocolError(string message) =>
        new(TurnTerminalKind.Failed,
            TerminalErrorCodes.ExecutionProtocolError,
            message, null, null);

    public string TerminalEventType => Kind switch
    {
        TurnTerminalKind.Completed => ConversationEventTypes.TurnCompleted,
        TurnTerminalKind.Failed => ConversationEventTypes.TurnFailed,
        TurnTerminalKind.Cancelled => ConversationEventTypes.TurnCancelled,
        _ => ConversationEventTypes.TurnFailed,
    };

    public CommandStatus CommandStatus => Kind switch
    {
        TurnTerminalKind.Completed => CommandStatus.Succeeded,
        TurnTerminalKind.Failed => CommandStatus.Failed,
        TurnTerminalKind.Cancelled => CommandStatus.Cancelled,
        _ => CommandStatus.Failed,
    };

    public RunStatus RunStatus => Kind switch
    {
        TurnTerminalKind.Completed => RunStatus.Succeeded,
        TurnTerminalKind.Failed => RunStatus.Failed,
        TurnTerminalKind.Cancelled => RunStatus.Cancelled,
        _ => RunStatus.Failed,
    };
}

/// <summary>
/// Agent 执行快照 — Run 启动时由 SnapshotFactory 一次性生产。
/// 快照不可变；同一 Turn 的重试复用第一次生成的快照。
/// 快照不保存 API Key 等秘密；LlmConfig 内密钥在快照化前剥离。
/// </summary>
public sealed record AgentExecutionSnapshot(
    string SnapshotId,
    string WorkspaceId,
    string AgentId,
    int Revision,
    string SnapshotHash,
    string? DisplayName,
    string? AvatarUrl,
    string? SystemPrompt,
    string? PersonaJson,
    string? ProviderId,
    string? ModelId,
    CapabilityPolicy? CapabilityPolicy,
    IReadOnlyList<SnapshotToolRef>? ToolDefinitions,
    IReadOnlyList<SnapshotSkillRef>? SkillReferences,
    string? MemoryPolicyJson,
    int? BudgetTotalTokens,
    int? BudgetMaxRounds,
    TimeSpan? Timeout,
    DateTimeOffset CreatedAt);

/// <summary>
/// 快照内工具引用 — 只保存工具标识和版本，不保存完整实现。
/// </summary>
public sealed record SnapshotToolRef(
    string Name,
    string? Version,
    string? Source);

/// <summary>
/// 快照内 Skill 引用 — 只保存 Skill ID 和修订号。
/// </summary>
public sealed record SnapshotSkillRef(
    string SkillId,
    int Revision);

/// <summary>
/// 执行结果 — Coordinator 向 Worker 上报的完整运行结果。
/// </summary>
public sealed record ExecutionRunOutcome(
    string CommandId,
    string TurnId,
    string RunId,
    TurnTerminal Terminal,
    long TerminalSequence,
    long FirstEventSequence,
    long LastEventSequence,
    int TotalEventCount);

/// <summary>
/// Output Chunker 输出契约。Terminal 事件不得与普通 Output 混合；
/// 调用方必须先 flush pending output，然后单独提交 terminal。
/// </summary>
public sealed record ChunkerFlushResult(
    IReadOnlyList<NewConversationEvent> OutputEvents,
    TurnTerminal? Terminal);

/// <summary>
/// 控制消息类型 — 统一 Cancel、Steering、Approval、Budget 变更等入口。
/// </summary>
public enum ControlMessageKind
{
    CancelRequested,
    Steering,
    ToolApprovalGranted,
    ToolApprovalDenied,
    Pause,
    Resume,
    BudgetChanged,
}
