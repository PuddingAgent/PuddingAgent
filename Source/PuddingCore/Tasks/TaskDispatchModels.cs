using System.Security.Cryptography;
using System.Text;
using PuddingCode.Models;

namespace PuddingCode.Tasks;

/// <summary>
/// TB-05: 手工派发闭环的 Message Envelope 模型与幂等键派生。
/// <para>
/// 权威来源：ADR-072 §8.1（手工派发链）、§9.1（Message Envelope）。idempotency_key
/// 稳定格式 <c>task:{taskId}:assign:{assignmentId}</c>；MessageId 由 idempotency_key
/// 确定性派生（SHA-256 前 32 位 hex），保证「发送成功但未绑定」崩溃后按同一 idempotency
/// key 找回同一 Message Fabric Delivery（不变量 #8）。
/// </para>
/// </summary>
public static class TaskDispatchIds
{
    /// <summary>幂等键稳定格式（不变量 #4：task_dispatch_outbox(idempotency_key) 唯一）。</summary>
    public static string BuildIdempotencyKey(string taskId, string assignmentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(assignmentId);
        return $"task:{taskId}:assign:{assignmentId}";
    }

    /// <summary>
    /// 由幂等键确定性派生 MessageId（≤64 字符，满足 message_deliveries.message_id 上限）。
    /// 同一 assignment 的每次派发都得到同一 MessageId，使 Message Fabric 的 message_id 去重
    /// 与 DeliveryId（由 messageId+target 派生）保持一致。
    /// </summary>
    public static string BuildMessageId(string idempotencyKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(idempotencyKey)))
            .ToLowerInvariant();
        return $"task-{hash[..32]}";
    }
}

/// <summary>
/// TB-05: task_instruction 消息封套的权威输入（ADR-072 §9.1）。
/// <para>
/// From.Kind = system，From.Id = task-orchestrator；ContentType = task_instruction；
/// body 以自然语言呈现任务指令，Metadata 承载 origin/task_id/assignment_id/priority/
/// execution_window/dispatch_idempotency_key，Prompt 不得改写。
/// </para>
/// </summary>
public sealed record TaskInstructionEnvelope
{
    public const string ContentTypeTaskInstruction = "task_instruction";
    public const string OriginTaskManual = "task.manual";
    public const string FromSystemId = "task-orchestrator";

    /// <summary>幂等键（不变量 #4）。</summary>
    public required string IdempotencyKey { get; init; }

    /// <summary>所属工作区 ID。</summary>
    public required string WorkspaceId { get; init; }

    /// <summary>任务 ID。</summary>
    public required string TaskId { get; init; }

    /// <summary>Assignment ID。</summary>
    public required string AssignmentId { get; init; }

    /// <summary>被分配 Agent ID。</summary>
    public required string AgentId { get; init; }

    /// <summary>来源 wire（本阶段仅 task.manual）。</summary>
    public required string Origin { get; init; }

    /// <summary>优先级 wire（p0/p1/p2/p3）。</summary>
    public required string Priority { get; init; }

    /// <summary>执行窗口 wire（inherit/anytime/off_peak_only）。</summary>
    public required string ExecutionWindow { get; init; }

    /// <summary>任务标题。</summary>
    public required string Title { get; init; }

    /// <summary>任务描述。</summary>
    public string? Description { get; init; }

    /// <summary>验收标准。</summary>
    public string? AcceptanceCriteria { get; init; }

    /// <summary>
    /// 派发时刻的 Task.Version（TB-06 增补，评审 R2 方案 A）。随 Outbox 序列化、
    /// 写入 metadata <c>expected_version</c>，由派发时取 task.Version 注入，供 Agent
    /// 侧 claim/update 的 version_conflict 校验（§4.3）。</summary>
    public int? ExpectedVersion { get; init; }

    /// <summary>由幂等键确定性派生的 MessageId。</summary>
    public string MessageId => TaskDispatchIds.BuildMessageId(IdempotencyKey);

    /// <summary>转换为 Message Fabric 权威 <see cref="MessageEnvelope"/>（ADR-072 §9.1）。</summary>
    public MessageEnvelope ToMessageEnvelope()
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["origin"] = Origin,
            ["task_id"] = TaskId,
            ["assignment_id"] = AssignmentId,
            ["priority"] = Priority,
            ["execution_window"] = ExecutionWindow,
            ["dispatch_idempotency_key"] = IdempotencyKey,
        };
        if (ExpectedVersion.HasValue)
        {
            metadata["expected_version"] = ExpectedVersion.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return new MessageEnvelope
        {
            MessageId = MessageId,
            From = new MessageAddress
            {
                Kind = MessageEndpointKinds.System,
                Id = FromSystemId,
                WorkspaceId = WorkspaceId,
                DisplayName = "Task Orchestrator",
            },
            To = new[]
            {
                new MessageAddress
                {
                    Kind = MessageEndpointKinds.Agent,
                    Id = AgentId,
                    WorkspaceId = WorkspaceId,
                },
            },
            Audience = MessageAudiences.Direct,
            Visibility = MessageVisibilities.System,
            ContentType = ContentTypeTaskInstruction,
            Content = BuildBody(),
            Priority = MapPriority(Priority),
            CorrelationId = AssignmentId,
            Metadata = metadata,
        };
    }

    private string BuildBody()
    {
        var sb = new StringBuilder();
        sb.Append("请完成以下任务：\n\n");
        sb.Append("标题：").Append(Title).Append('\n');
        if (!string.IsNullOrWhiteSpace(Description))
        {
            sb.Append("\n描述：\n").Append(Description).Append('\n');
        }

        if (!string.IsNullOrWhiteSpace(AcceptanceCriteria))
        {
            sb.Append("\n验收标准：\n").Append(AcceptanceCriteria).Append('\n');
        }

        return sb.ToString();
    }

    private static int MapPriority(string priority) => priority switch
    {
        "p0" => 3,
        "p1" => 2,
        "p2" => 1,
        _ => 0,
    };
}
