namespace PuddingRuntime.Services.Improvement.Rsi;

/// <summary>RSI 轨迹中的单个工具调用步（规格 §2.3：结局是一等字段，失败步不得被整体丢弃）。</summary>
public sealed record RsiToolStep
{
    /// <summary>所属回合 Id。</summary>
    public required string TurnId { get; init; }

    /// <summary>工具名（payload 的 name）。</summary>
    public required string ToolName { get; init; }

    /// <summary>稳定排序键（不得依赖 DB 返回顺序）。</summary>
    public required long Sequence { get; init; }

    /// <summary>三态结局：Unknown / Completed / Failed（缺失不是成功）。</summary>
    public required RsiToolOutcome Outcome { get; init; }

    /// <summary>退出码；缺失就是 null，不得填 0 冒充。</summary>
    public int? ExitCode { get; init; }

    /// <summary>
    /// 错误预览（B4 冻结，任务书 §4.1 / 规格 §2.13 切片合同）：原文缺失 ⇒ <c>null</c>；原文非缺失 ⇒ 总长 ≤ 512 个 UTF-16 字符
    /// —— 原文 > 512 ⇒ 取前 511 字符 + <c>…</c>（U+2026，1 字符）；≤ 512 ⇒ 原样（不加省略号）。
    /// ⛔ 缺失不得压平成空串：「没有错误」与「错误为空」必须可区分。
    /// </summary>
    public string? ErrorPreview { get; init; }

    /// <summary>截断前错误文本的 UTF-16 字符数（原文缺失 ⇒ 0；与缺失的区分看 <see cref="ErrorPreview"/> 是否为 null）。</summary>
    public int ErrorChars { get; init; }

    /// <summary>
    /// 工具输出预览（规则同 <see cref="ErrorPreview"/>）。全文不进轨迹：设计 §4.6「工具输出全文 ⛔」的替代 =
    /// args_hash + exit_code + 截断摘要（≤512 字符）+ 字节数（本类型已全部承载）。
    /// </summary>
    public string? OutputPreview { get; init; }

    /// <summary>截断前输出的 UTF-16 字符数（原文缺失 ⇒ 0；一律按截断前原文计数）。</summary>
    public int OutputChars { get; init; }

    /// <summary>截断前原文的 UTF-8 字节数（原文缺失 ⇒ 0；按截断前原文的 UTF-8 编码计数，不是 UTF-16 长度）。</summary>
    public int OutputBytes { get; init; }

    /// <summary>
    /// 参数指纹（B4 冻结，任务书 §4.2）：SHA-256（配对 tool.call.requested 的 arguments 原文，UTF-8）小写 hex，固定 64 位。
    /// ⛔ 不做任何归一化（不 trim / 不解析 JSON / 不排序键）；不得照抄「缺失参数兜底为空对象字面量」的写法（即 ?? "{}"）——
    /// （ConversationSkillEvolutionTrajectorySource.cs:115 反面教材：把「参数缺失」捏造成真实值，
    /// 两次缺失调用会得到同一哈希 ⇒ 虚假「参数相同」信号且无断言会失败）。
    /// arguments 缺失或纯空白 ⇒ <c>null</c>；无未配对 requested ⇒ <c>null</c>（不得猜，不得用最近一条顶替）。
    /// </summary>
    public string? ArgsHash { get; init; }

    /// <summary>事件发生时间（UTC）。</summary>
    public required DateTimeOffset OccurredAtUtc { get; init; }
}

/// <summary>带结局标注的 RSI 工具轨迹（规格 §2.3）。</summary>
public sealed record RsiTrajectory
{
    public required string WorkspaceId { get; init; }

    public required string AgentInstanceId { get; init; }

    public required string SessionId { get; init; }

    public required string TurnId { get; init; }

    public required IReadOnlyList<RsiToolStep> Steps { get; init; }

    /// <summary>含 Unknown / Failed 步时为 true（供上层显式判断，不得静默丢弃失败步）。</summary>
    public required bool HasOutcomeAnomaly { get; init; }
}

/// <summary>RSI 轨迹装配的事件行输入形状（规格 §2.4：按 (TurnId, Type) 索引查询返回的原始事件投影）。</summary>
public sealed record RsiEventRow
{
    /// <summary>事件类型（tool.call.completed / tool.call.failed / tool.call.requested 等）。</summary>
    public required string Type { get; init; }

    /// <summary>稳定排序键（不得依赖 DB 返回顺序）。</summary>
    public required long Sequence { get; init; }

    /// <summary>payload JSON（原样透传给结局推导器解析）。</summary>
    public string? Payload { get; init; }

    /// <summary>事件发生时间（UTC）。</summary>
    public required DateTimeOffset OccurredAtUtc { get; init; }
}

/// <summary>单个回合的事件切片输入形状（规格 §2.5 推荐路径：按 turn 取事件）。</summary>
public sealed record RsiTurnSlice
{
    public required string WorkspaceId { get; init; }

    public required string AgentInstanceId { get; init; }

    public required string SessionId { get; init; }

    public required string TurnId { get; init; }

    public required IReadOnlyList<RsiEventRow> Events { get; init; }
}
