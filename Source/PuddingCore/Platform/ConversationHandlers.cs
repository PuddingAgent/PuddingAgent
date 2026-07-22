using PuddingCode.Runtime;

namespace PuddingCode.Platform;

// ════════════════════════════════════════════════════════════════
// ADR-059: 拆分的 Conversation 应用 Handler 接口。
// 每个接口拥有独立的输入、权限、事务和测试。
// ════════════════════════════════════════════════════════════════

/// <summary>提交用户 Turn 的应用入口。</summary>
public interface ISubmitTurnHandler
{
    Task<AcceptanceResult> HandleAsync(SubmitTurnCommand command, CancellationToken ct);
}

/// <summary>取消正在运行的 Turn。</summary>
public interface IRequestTurnCancellationHandler
{
    Task<CancelTurnResult> HandleAsync(RequestTurnCancellationCommand command, CancellationToken ct);
}

/// <summary>创建 Steering 注入消息。</summary>
public interface ICreateSteeringHandler
{
    Task<CreateSteeringResult> HandleAsync(CreateSteeringCommand command, CancellationToken ct);
}

/// <summary>请求 Conversation 压缩。</summary>
public interface IRequestCompactionHandler
{
    Task<CompactionResult> HandleAsync(RequestCompactionCommand command, CancellationToken ct);
}

// ════════════════════════════════════════════════════════════════
// 应用命令 DTO（不含 HTTP 类型、不含 LLM 配置）
// ════════════════════════════════════════════════════════════════

public sealed record SubmitTurnCommand(
    string ConversationId,
    string WorkspaceId,
    string UserId,
    string ClientRequestId,
    string ClientMessageId,
    RecipientRequest Recipients,
    IReadOnlyList<ContentPart> Content,
    IReadOnlyDictionary<string, string>? Metadata);

public sealed record RequestTurnCancellationCommand(
    string ConversationId,
    string TurnId,
    string UserId);

public sealed record CreateSteeringCommand(
    string ConversationId,
    string TurnId,
    string Text,
    int Priority,
    string UserId);

public sealed record RequestCompactionCommand(
    string ConversationId,
    string WorkspaceId,
    string AgentId,
    ContextCompactionLevel Level,
    string Reason,
    string CompactionId,
    string? UserId);

public sealed record CancelTurnResult(
    string ConversationId,
    string TurnId,
    string Status);

public sealed record CreateSteeringResult(
    string SteeringId);

public sealed record CompactionResult(
    string CompactionId,
    ContextCompactionResult Compaction,
    string NewConversationId,
    string? NewConversationTitle);

/// <summary>
/// 创建压缩后的后继 Conversation，并原子收敛 Agent 主会话身份。
/// 该接口隔离 Session 存储、Agent manifest 与重定向存储，压缩 Handler
/// 不得直接读取数据库或配置文件。
/// </summary>
public interface ICompactionSessionSuccessor
{
    Task<CompactionSuccessor> CreateAsync(
        CreateCompactionSuccessorCommand command,
        CancellationToken ct);
}

public sealed record CreateCompactionSuccessorCommand(
    string PreviousConversationId,
    string WorkspaceId,
    string AgentId,
    string? SourceTemplateId);

public sealed record CompactionSuccessor(
    string ConversationId,
    string? Title);
