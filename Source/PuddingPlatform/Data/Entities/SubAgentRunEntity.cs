using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PuddingPlatform.Data.Entities;

/// <summary>
/// 子代理运行归档的数据库索引实体。
/// 文件系统为主存储（run.json / events.jsonl / tools.jsonl），此表仅做索引查询。
/// 关联 ADR：Docs/07_architecture/21子代理工作空间与运行归档ADR.md
/// </summary>
[Table("sub_agent_runs")]
public class SubAgentRunEntity
{
    [Key]
    public long Id { get; set; }

    [Required, MaxLength(64), Column("run_id")]
    public string RunId { get; set; } = "";

    [Required, MaxLength(64), Column("parent_session_id")]
    public string ParentSessionId { get; set; } = "";

    /// <summary>
    /// 父执行身份 — 父 Turn ID（RuntimeExecutionIdentity.TurnId）。
    /// slice-4 «父 Turn 等待异步子代理» 的归属依据：按父 Turn 统计运行中子代理只认此列；
    /// 本列之前的旧行为 NULL（只能退化到会话粒度）。
    /// </summary>
    [MaxLength(64), Column("parent_turn_id")]
    public string? ParentTurnId { get; set; }

    /// <summary>父执行身份 — 父命令 ID（RuntimeExecutionIdentity.CommandId）；旧行为 NULL。</summary>
    [MaxLength(64), Column("parent_command_id")]
    public string? ParentCommandId { get; set; }

    /// <summary>父执行身份 — 父 Run ID（RuntimeExecutionIdentity.RunId）；旧行为 NULL。</summary>
    [MaxLength(64), Column("parent_run_id")]
    public string? ParentRunId { get; set; }

    [Required, MaxLength(64), Column("sub_session_id")]
    public string SubSessionId { get; set; } = "";

    [Required, MaxLength(64), Column("workspace_id")]
    public string WorkspaceId { get; set; } = "";

    [Required, MaxLength(64), Column("agent_instance_id")]
    public string AgentInstanceId { get; set; } = "";

    [Required, MaxLength(64), Column("template_id")]
    public string TemplateId { get; set; } = "";

    /// <summary>运行状态；取值词表见 <see cref="TerminalStatuses"/> / <see cref="NonTerminalStatuses"/>。</summary>
    [Required, MaxLength(16)]
    public string Status { get; set; } = "running";

    /// <summary>非终态（运行中）状态值；创建运行时写入，终态由 UpdateDbIndexAsync 覆盖。</summary>
    public const string RunningStatus = "running";

    /// <summary>
    /// 终态状态值集合 —— 「这个 run 已经结束」的唯一真源。
    /// <para>
    /// <b>为什么放在实体上：</b>run 的状态取值是这张表的词表，散落在
    /// <c>FileSubAgentRunStore</c>、GC 与池恢复三处的字面量列表必然漂移；
    /// 漂移的后果不对称——池恢复把「还在跑」误判成终态会去重置一条正在被驱动的条目，
    /// 两个执行同时驱动同一个子会话，而反过来只是少救一条。
    /// </para>
    /// <para>新增终态值时必须同时检查 <see cref="NonTerminalStatuses"/>：两者是同一词表的划分。</para>
    /// </summary>
    public static readonly IReadOnlyList<string> TerminalStatuses =
    [
        "completed",
        // 预算耗尽与异常暂停并列：两者都是「执行已停止、上下文仍热」的可续跑终态。
        "budget_exhausted",
        "paused",
        "failed",
        "cancelled",
        "timed_out",
        "interrupted",
    ];

    /// <summary>
    /// 非终态（活动）状态值集合 —— 池恢复判据 <c>AgentPoolEntryStore.ResetStaleBusyAsync</c> 使用。
    /// <para>
    /// 现在只有一个取值（<see cref="RunningStatus"/>）：创建 run 时写它，收尾时由终态覆盖。
    /// 之所以仍写成集合，是因为恢复判据是「有没有非终态 run」，而不是「status 是不是 running」；
    /// 将来若引入 waiting 之类**真的还在被调度**的活动态，只改这里就能让池恢复自动跟上。
    /// </para>
    /// <para>
    /// <b>paused 不在本集合：</b>它在 2026-11-21 被裁定为「终态 + 可续跑」（见 <see cref="ResumableStatuses"/>）——
    /// 检测器叫停后已无轮次在跑，池若把它当活动态就会去重置一条暂停中的条目。
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<string> NonTerminalStatuses = [RunningStatus];

    /// <summary>
    /// 可续跑状态集合 —— 「终态，但可以直接 resume 继续同一个会话」的唯一真源。
    /// <para>
    /// <b>为什么 paused 与 budget_exhausted 并列：</b>两者停止的原因不同（预算用尽 / 重复输出被检测器叫停），
    /// 但对调度而言一样——执行已停、无新轮次；对使用方而言也一样——上下文是热的，恢复靠 resume 而不是继续跑。
    /// 字面量散落在 Manager / 状态投影各处时，新增一个可续态就会漏掉某一处，
    /// 表现正是本词表要防的那类缺陷：界面把「暂停等裁决」显示成「失败」。
    /// </para>
    /// <para>本集合必须是 <see cref="TerminalStatuses"/> 的子集：可续跑的前提是先已停止。</para>
    /// </summary>
    public static readonly IReadOnlyList<string> ResumableStatuses = ["budget_exhausted", "paused"];

    /// <summary>
    /// 终态判定（Ordinal 精确比较）。不用大小写宽容：状态值是代码写入的内部词表，
    /// 宽容只会让一个拼错的状态值静默通过判定，而不是尽早暴露。
    /// </summary>
    public static bool IsTerminalStatus(string? status) =>
        status is not null && TerminalStatuses.Contains(status, StringComparer.Ordinal);

    /// <summary>可续跑判定 —— 终态中能直接 resume 的那些。</summary>
    public static bool IsResumableStatus(string? status) =>
        status is not null && ResumableStatuses.Contains(status, StringComparer.Ordinal);

    [Required, MaxLength(32), Column("started_at")]
    public string StartedAt { get; set; } = "";

    [MaxLength(32), Column("completed_at")]
    public string? CompletedAt { get; set; }

    [Required, MaxLength(512), Column("archive_path")]
    public string ArchivePath { get; set; } = "";

    [MaxLength(64), Column("trace_id")]
    public string? TraceId { get; set; }

    [MaxLength(64), Column("correlation_id")]
    public string? CorrelationId { get; set; }

    [MaxLength(1024), Column("error_message")]
    public string? ErrorMessage { get; set; }

    [Column("task_planning_metadata_json")]
    public string? TaskPlanningMetadataJson { get; set; }

    [Column("total_rounds")]
    public int TotalRounds { get; set; }

    [Column("total_tool_calls")]
    public int TotalToolCalls { get; set; }

    [Column("total_duration_ms")]
    public long TotalDurationMs { get; set; }
}
