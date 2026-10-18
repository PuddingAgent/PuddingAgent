namespace PuddingConversationReadModel;

/// <summary>Turn 终态；未知表示尚未观察到终态事件。终态一旦写入，迟到事件不能把它改回运行态（§4.1.2）。</summary>
public enum TurnTerminalState
{
    Unknown = 0,
    Completed = 1,
    Failed = 2,
    Cancelled = 3,
}

/// <summary>
/// 摘要完整性（§3.2）：<c>unavailable</c> 表示证据缺失/未初始化，<c>partial</c> 表示只覆盖有界窗口，
/// <c>complete</c> 表示当前摘要覆盖了该 Turn 的已知事件范围。未知计数不得填零冒充满分。
/// </summary>
public enum SummaryCompleteness
{
    Unavailable = 0,
    Partial = 1,
    Complete = 2,
}

/// <summary>读模型整体初始化状态；旧数据未建立基线时不得伪装为 ready（§8.2）。</summary>
public enum ReadModelInitializationState
{
    Uninitialized = 0,
    BaselineBuilding = 1,
    Partial = 2,
    Ready = 3,
}

/// <summary>活动过程的有界摘要与完整性；计数是饱和累计值，不随预览截断而减少。</summary>
public sealed record ProcessSummary
{
    public int ContentAppendCount { get; init; }
    public int ThinkingAppendCount { get; init; }
    public int ToolCallRequestedCount { get; init; }
    public int ToolCallCompletedCount { get; init; }
    public int ToolCallFailedCount { get; init; }

    /// <summary>
    /// 进入有界子代理摘要（<see cref="ConversationReadModelState.SubAgentRuns"/>）的 run 行数，
    /// **不是**全量 distinct run 计数。该 Turn 有 run 被淘汰/拒收（
    /// <see cref="ConversationReadModelState.SubAgentRunsOmitted"/> 增长）时，计数不完整，
    /// <see cref="Completeness"/> 不得为 <see cref="SummaryCompleteness.Complete"/>（Plan §3.2）。
    /// </summary>
    public int SubAgentRunCount { get; init; }

    /// <summary>
    /// 摘要完整性。判定优先级（冻结，从高到低）：
    /// <c>baseline_incomplete</c> &gt; <c>turn_header_missing</c> &gt;
    /// <c>subagent_summary_truncated</c> &gt; <c>turn_active</c> &gt; <see cref="SummaryCompleteness.Complete"/>。
    /// </summary>
    public SummaryCompleteness Completeness { get; init; } = SummaryCompleteness.Partial;

    /// <summary>
    /// 摘要不完整的原因；<see cref="SummaryCompleteness.Complete"/> 时必须为 null。取值（冻结）：
    /// <list type="bullet">
    /// <item><c>turn_active</c>：Turn 仍在进行，过程计数只覆盖已投影部分；</item>
    /// <item><c>turn_header_missing</c>：未观察到 TurnStarted 头（内容/终态首见建的 partial Turn），计数覆盖不可证；</item>
    /// <item><c>baseline_incomplete</c>：读模型初始化未 Ready（旧库基线未建立），计数只覆盖增量部分；</item>
    /// <item><c>subagent_summary_truncated</c>：该 Turn 的子代理 run 有被淘汰/拒收（
    /// <see cref="ConversationReadModelState.SubAgentRunsOmitted"/> &gt; 0），
    /// <see cref="SubAgentRunCount"/> 不是完整 distinct 计数，终态也不得宣称覆盖完整。</item>
    /// </list>
    /// <para>null 仅可能出现在新读模型写入的行（活动且无更高优先级原因时不会为 null）；
    /// 旧持久化数据应由 Platform 显式写入原因，组件对 Ready 状态下的 null 按「无已知缺失」处理。</para>
    /// </summary>
    public string? IncompleteReason { get; init; }
    public int Revision { get; init; }
}

/// <summary>单个 Turn 的轻量摘要；只保留首屏需要的状态与有界预览，完整明细仍在事件日志。</summary>
public sealed record TurnReadSummary
{
    public required string TurnId { get; init; }
    public string? RootRunId { get; init; }
    public string? UserMessageId { get; init; }
    public string? AssistantMessageId { get; init; }
    public TurnTerminalState TerminalState { get; init; } = TurnTerminalState.Unknown;

    /// <summary>只有收到 MessageCompleted 才为 true；TurnFailed/TurnCancelled 不得伪造助手正文（§4.1.2）。</summary>
    public bool HasAssistantBody { get; init; }

    public ProcessSummary Process { get; init; } = new();

    /// <summary>活动正文的有界尾部（UTF-8 &lt;= Limits.ActivePreviewMaxBytes），不保存完整历史正文。</summary>
    public string ActivePreview { get; init; } = string.Empty;
    public bool ActivePreviewTruncated { get; init; }

    public long FirstSequence { get; init; }
    public long LastSequence { get; init; }
    public int Revision { get; init; }
}

/// <summary>会话级压缩状态；只表达当前运行态与最近一次失败，不保存压缩摘要全文。</summary>
public sealed record CompactionReadState
{
    public bool IsRunning { get; init; }
    public string? RunId { get; init; }
    public long StartedSequence { get; init; }
    public long? CompletedSequence { get; init; }
    public bool LastAttemptFailed { get; init; }
    public int Revision { get; init; }
}

/// <summary>子代理生命周期状态（与 canonical 子代理 run 事件对应）。</summary>
public enum SubAgentRunLifecycleState
{
    Created = 0,
    Running = 1,
    Completed = 2,
    Failed = 3,
    Cancelled = 4,
    TimedOut = 5,
    Interrupted = 6,
    BudgetExhausted = 7,
}

/// <summary>有界子代理 run 摘要；活动项优先，历史结果另页读取（§5.4）。</summary>
public sealed record SubAgentRunSummary
{
    public required string RunId { get; init; }
    public string? SubAgentId { get; init; }
    public string? ParentRunId { get; init; }
    public string? TurnId { get; init; }
    public SubAgentRunLifecycleState State { get; init; } = SubAgentRunLifecycleState.Created;
    public long FirstSequence { get; init; }
    public long LastSequence { get; init; }

    public bool IsActive => State is SubAgentRunLifecycleState.Created or SubAgentRunLifecycleState.Running;
}

/// <summary>
/// 归约后的会话读模型状态。
/// <para><see cref="ProjectedThroughSequence"/> 是已投影位置 C；事件 head H 由 Platform 另行读取（§4.2），
/// 两者不得混用。</para>
/// <para><see cref="Turns"/> 只保存轻量摘要；组件允许在内存中按批更新，持久化由 Platform 负责。</para>
/// </summary>
public sealed record ConversationReadModelState
{
    public required string ConversationId { get; init; }
    public long ProjectedThroughSequence { get; init; }
    public string? ProjectedThroughEventId { get; init; }

    public ReadModelInitializationState Initialization { get; init; } =
        ReadModelInitializationState.Uninitialized;

    public IReadOnlyDictionary<string, TurnReadSummary> Turns { get; init; } =
        new Dictionary<string, TurnReadSummary>();

    public string? CurrentTurnId { get; init; }
    public string? CurrentRootRunId { get; init; }
    public TurnTerminalState CurrentRootTerminalState { get; init; } = TurnTerminalState.Unknown;

    public CompactionReadState? Compaction { get; init; }

    public IReadOnlyList<SubAgentRunSummary> SubAgentRuns { get; init; } =
        Array.Empty<SubAgentRunSummary>();

    /// <summary>
    /// 因 <see cref="ConversationReadModelLimits.SubAgentSummaryLimit"/> 上限而未保留进
    /// <see cref="SubAgentRuns"/> 的子代理 run 数量：既包括为腾出槽位被淘汰的最旧终态 run，
    /// 也包括列表全为活动 run 时未能加入的新 run。完整子代理结果另页读取（Plan §3.2），
    /// 本计数值用于说明首屏摘要的缺失量。
    /// <para>该值 &gt; 0 表示 <see cref="ProcessSummary.SubAgentRunCount"/> 不是完整 distinct 计数：
    /// 受影响 Turn 的 <see cref="ProcessSummary.Completeness"/> 必须为
    /// <see cref="SummaryCompleteness.Partial"/>，原因为 <c>subagent_summary_truncated</c>
    /// （优先级低于 baseline/header 原因）。</para>
    /// </summary>
    public int SubAgentRunsOmitted { get; init; }

    /// <summary>读模型合同版本；ETag 与缓存失效需要覆盖该版本（§4.4）。</summary>
    public int ReadModelVersion { get; init; } = ConversationReadModelLimits.CurrentReadModelVersion;

    /// <summary>摘要修订号；每次写入归约批次递增，用于表示层条件请求。</summary>
    public int Revision { get; init; }
}

/// <summary>
/// 一次批量归约的结果。
/// <para>重复批次（同一 batch 再次执行）不得重复累计；<see cref="DuplicateEventCount"/> 为被忽略的重复事件数。</para>
/// <para>倒序/跳号、跨会话、终态之后迟到的事件分别计数，不得静默混入正常计数。</para>
/// </summary>
public sealed record ReduceBatchResult
{
    public required ConversationReadModelState State { get; init; }

    public int AppliedEventCount { get; init; }
    public int DuplicateEventCount { get; init; }
    public int OutOfOrderEventCount { get; init; }
    public int ForeignConversationCount { get; init; }
    public int LateAfterTerminalCount { get; init; }
    public int IgnoredEventCount { get; init; }

    public bool ReplayDetected => DuplicateEventCount > 0;
}

/// <summary>
/// canonical 事件的纯归约规则（Plan §4.1）。实现必须：
/// <list type="bullet">
/// <item>按 <see cref="ConversationEventInput.Sequence"/> 顺序应用，重复/终态迟到事件不重复累计；</item>
/// <item>子代理事件（ParentRunId 非空）不能抢占根 run；失败/取消 Turn 不伪造助手正文；</item>
/// <item>活动预览只保留 UTF-8 有界尾部，超过预算置 <see cref="TurnReadSummary.ActivePreviewTruncated"/>；</item>
/// <item>不执行 I/O、不读取时间、不访问数据目录或环境变量。</item>
/// </list>
/// </summary>
public static class ConversationReadModelReducer
{
    /// <summary>创建空状态；<paramref name="initialization"/> 明确区分旧库未初始化与正常就绪。</summary>
    public static ConversationReadModelState CreateEmpty(
        string conversationId,
        ReadModelInitializationState initialization = ReadModelInitializationState.Ready)
        => ReducerEngine.CreateEmpty(conversationId, initialization);

    /// <summary>应用一个有界事件批次，返回新状态与分类计数；相同批次重复执行必须幂等。</summary>
    public static ReduceBatchResult Apply(
        ConversationReadModelState state,
        IReadOnlyList<ConversationEventInput> batch,
        ConversationReadModelLimits? limits = null)
        => ReducerEngine.Apply(state, batch, limits ?? ConversationReadModelLimits.Default);
}
