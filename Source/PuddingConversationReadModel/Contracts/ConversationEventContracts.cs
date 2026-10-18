namespace PuddingConversationReadModel;

/// <summary>
/// Platform 将 canonical 事件映射后的中性类别；组件不引用事件存储、EF 或任何宿主类型。
/// 迁移/归并规则见 Docs/12_features/会话首屏与渐进加载架构优化方案-2026-10-18.md §4.1。
/// </summary>
public enum ConversationEventKind
{
    /// <summary>未知/暂不参与归约的事件；计入 ignored 而非推断状态。</summary>
    Other = 0,

    TurnStarted = 1,
    TurnCompleted = 2,
    TurnFailed = 3,
    TurnCancelled = 4,

    MessageContentAppended = 10,
    MessageThinkingSummaryAppended = 11,
    MessageCompleted = 12,
    MessageFailed = 13,

    ToolCallRequested = 20,
    ToolCallCompleted = 21,
    ToolCallFailed = 22,

    SubAgentRunCreated = 30,
    SubAgentRunStarted = 31,
    SubAgentRunCompleted = 32,
    SubAgentRunFailed = 33,
    SubAgentRunCancelled = 34,
    SubAgentRunTimedOut = 35,
    SubAgentRunInterrupted = 36,
    SubAgentRunBudgetExhausted = 37,

    ContextCompactionStarted = 40,
    ContextCompactionCompleted = 41,
    ContextCompactionFailed = 42,
}

/// <summary>
/// 归约输入的一条 canonical 事件。字段是读模型需要的最小投影；完整 payload 不进入纯组件。
/// <para><see cref="Sequence"/> 是会话内单调递增位置；<see cref="EventId"/> 是全局稳定身份。</para>
/// <para><see cref="ParentRunId"/> 非空表示子代理 run 事件，不能抢占根 run（§4.1.2）；
/// 子 run 的正文/思考/工具事件归子代理明细，不并入父 Turn 摘要。</para>
/// </summary>
public sealed record ConversationEventInput
{
    public required string ConversationId { get; init; }
    public required long Sequence { get; init; }
    public required string EventId { get; init; }
    public required ConversationEventKind Kind { get; init; }

    public string? TurnId { get; init; }
    public string? RunId { get; init; }
    public string? ParentRunId { get; init; }
    public string? MessageId { get; init; }
    public string? SubAgentId { get; init; }

    /// <summary>
    /// 内容/思考增量文本；组件按字节预算只保留有界尾部，不保存完整历史正文。
    /// TurnCompleted 时可携带终态 reply（仅用于判断助手正文是否存在与身份，不保存全文）。
    /// </summary>
    public string? TextDelta { get; init; }

    public int PayloadBytes { get; init; }
    public DateTimeOffset? OccurredAtUtc { get; init; }
}
