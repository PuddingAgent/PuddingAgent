using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PuddingPlatform.Data.Entities;

/// <summary>
/// 持久子代理池条目（S1：仅 schema，不含任何读写代码）。
/// <para>
/// 池条目原先只活在内存里：进程一重启，哪些子代理可以复用就全丢了。本表要把它落成事实，
/// 因此列的定义必须回答三个问题：<b>谁</b>可被复用（<see cref="SubSessionId"/> 指向
/// session_sub_agents，是复用键）、<b>由谁</b>驱动（owner_session_id / owner_agent_id，
/// 防跨会话误驱动）、当前<b>在用否</b>（status + active_execution_id，后者是执行令牌，
/// 用来挡住迟到的收尾把别的执行状态写回本条）。
/// </para>
/// <para>
/// 改名不能换主键：外部（驱动方、日志、诊断）引用的是 <see cref="PoolId"/>，
/// <see cref="Name"/> 只是池内展示名，所以 name 上是「未软删时唯一」的部分唯一索引，
/// 而 pool_id 由调用方生成后不再变化。
/// </para>
/// <para>
/// 本阶段只建表：库里有表但没有任何代码读写它，因此运行时行为与建表前完全一致。
/// </para>
/// </summary>
[Table("agent_pool")]
public class AgentPoolEntity
{
    /// <summary>空闲：可被复用派活。</summary>
    public const string StatusIdle = "idle";

    /// <summary>忙：正持有一次执行（active_execution_id 非空）。</summary>
    public const string StatusBusy = "busy";

    /// <summary>沉睡：保留条目但不主动派活（例如等待唤醒或已超过空闲阈值）。</summary>
    public const string StatusSleeping = "sleeping";

    /// <summary>死：复用键已失效，只保留墓碑供诊断与去重。</summary>
    public const string StatusDead = "dead";

    /// <summary>全部合法状态值，供校验与测试引用，禁止在别处重复写字符串字面量。</summary>
    public static readonly IReadOnlyList<string> AllStatuses =
        [StatusIdle, StatusBusy, StatusSleeping, StatusDead];

    /// <summary>
    /// 复位原因：这条曾被「重启前残留的 busy」恢复路径救回（见
    /// <c>AgentPoolEntryStore.ResetStaleBusyAsync</c>）。
    /// <para>
    /// 状态与原因写在一起：诊断时只看 <c>status='sleeping'</c> 无法区分「正常休眠」与
    /// 「从僵尸 busy 救回来」，而这两者的后续行为完全一样、来源却不同。
    /// </para>
    /// </summary>
    public const string StatusReasonRecoveredBusy = "recovered_busy";

    /// <summary>
    /// 索引名由实体声明，而不是由 bootstrapper 或 DbContext 各自写一遍：
    /// 新库走 EF 模型建索引、存量库走 raw DDL 建索引，同一张表上出现两个不同的名字就是重复索引。
    /// </summary>
    public const string NameIndexName = "IX_agent_pool_name";

    /// <summary>复用键唯一索引名（同上，两路共用）。</summary>
    public const string SubSessionIndexName = "IX_agent_pool_sub_session";

    /// <summary>「状态 + 最近使用」选池索引名（同上，两路共用）。</summary>
    public const string StatusLastUsedIndexName = "IX_agent_pool_status_last_used";

    [Key]
    public long Id { get; set; }

    /// <summary>稳定 id：创建后不再变化，改名只动 name，不动物理主键与外部引用。</summary>
    [Required, MaxLength(64), Column("pool_id")]
    public string PoolId { get; set; } = string.Empty;

    /// <summary>创建者工作区：池是工作区私有的，跨工作区不可见。</summary>
    [Required, MaxLength(64), Column("owner_workspace_id")]
    public string OwnerWorkspaceId { get; set; } = string.Empty;

    /// <summary>池内名：仅用于人读与按名复用；软删后同名可被新条目占用，故唯一索引带 deleted_at 过滤。</summary>
    [Required, MaxLength(128), Column("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>复用键：指向 session_sub_agents.sub_session_id，恢复会话靠它而非靠名字。</summary>
    [Required, MaxLength(64), Column("sub_session_id")]
    public string SubSessionId { get; set; } = string.Empty;

    /// <summary>创建该条目所用的 agent 模板，重启后据此重建运行时画像。</summary>
    [Required, MaxLength(64), Column("template_id")]
    public string TemplateId { get; set; } = string.Empty;

    [MaxLength(128), Column("role")]
    public string? Role { get; set; }

    /// <summary>创建者父会话：驱动方必须与会话匹配，防止 A 会话把 B 会话的条目当自己的用。</summary>
    [MaxLength(64), Column("owner_session_id")]
    public string? OwnerSessionId { get; set; }

    [MaxLength(64), Column("owner_agent_id")]
    public string? OwnerAgentId { get; set; }

    /// <summary>
    /// 当前执行的令牌。一次执行拿一个 token，收尾时只允许 token 相同者写回，
    /// 迟到的旧执行因此改不动新执行的状态。
    /// </summary>
    [MaxLength(64), Column("active_execution_id")]
    public string? ActiveExecutionId { get; set; }

    /// <summary>状态：取值必须是 <see cref="AllStatuses"/> 之一（idle / busy / sleeping / dead）。</summary>
    [Required, MaxLength(16), Column("status")]
    public string Status { get; set; } = StatusIdle;

    /// <summary>状态原因（如 recovered_busy / execution_failed），用于解释为什么不是 idle。</summary>
    [MaxLength(64), Column("status_reason")]
    public string? StatusReason { get; set; }

    /// <summary>累计承接的任务数：复用价值的主要指标。</summary>
    [Column("task_count")]
    public int TaskCount { get; set; }

    /// <summary>三态：NULL = 从未执行过；0 = 上次失败；1 = 上次成功（对应 bool?）。</summary>
    [Column("last_success")]
    public bool? LastSuccess { get; set; }

    /// <summary>创建时间（ISO-8601 文本，与仓内其它表的时间列口径一致）。</summary>
    [Required, MaxLength(32), Column("created_at")]
    public string CreatedAt { get; set; } = string.Empty;

    /// <summary>最近使用时间：LRU 淘汰的排序键，必须由使用方在每次派活后推进。</summary>
    [Required, MaxLength(32), Column("last_used_at")]
    public string LastUsedAt { get; set; } = string.Empty;

    /// <summary>乐观并发版本：写入方带 version 条件更新，冲突即重读，不覆盖他人更新。</summary>
    [Column("version")]
    public int Version { get; set; }

    /// <summary>墓碑时间：软删后保留行供诊断与去重；非空即视为不可复用。</summary>
    [MaxLength(32), Column("deleted_at")]
    public string? DeletedAt { get; set; }
}
