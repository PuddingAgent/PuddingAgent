namespace PuddingCode.Platform;

/// <summary>
/// ADR-059: 执行控制服务 — 统一 Cancel/Steering/Approval 入口。
/// 同事务写 Control Inbox + Conversation Event + 状态更新。
/// Handler 只依赖此服务和只读的 IExecutionCommandReader，
/// 不直接依赖租约、Journal、SessionSteeringService 或 Event Store 写接口。
/// </summary>
public interface IExecutionControlService
{
    /// <summary>
    /// 提交一条执行控制命令。同事务：
    ///   1. 写 execution_control_messages
    ///   2. 写对应 Conversation Event
    ///   3. 更新相关状态（如 Cancel → CommandStatus.CancelRequested）
    ///   4. 推进 Conversation Head
    ///   5. 发轻量 Signal 唤醒当前 Run
    /// </summary>
    Task<ControlReceipt> SubmitAsync(
        ExecutionControlCommand command,
        CancellationToken ct);
}

/// <summary>
/// 控制命令 — 统一 Cancel/Steering/Approval 载荷。
/// </summary>
public sealed record ExecutionControlCommand(
    string ConversationId,
    string? TurnId,
    ControlMessageKind Kind,
    string Payload,
    string? SourceUserId,
    int Priority);

/// <summary>
/// 控制命令回执。
/// </summary>
public sealed record ControlReceipt(
    string ControlId,
    long ControlSequence,
    long EventSequence);
