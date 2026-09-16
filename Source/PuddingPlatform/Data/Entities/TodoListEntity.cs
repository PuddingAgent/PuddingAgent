using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PuddingPlatform.Data.Entities;

/// <summary>
/// 设计 2026-09-16 §3（TD-1；TD-1b 对齐用户裁决）：todo_lists — Goal/Task/Session 作用域的「拆解 TODO」列表。
/// <para>
/// TODO 是 Agent 的拆解与自述层（可临时、可勾选），与 task_nodes 调度验收层语义分离；
/// <c>(agent_id, scope_kind, scope_id)</c> 唯一定位一个列表（TD-1b：跨 Agent 隔离，读写均按 agent_id 过滤）。
/// 列名与 TodoSchemaBootstrapper 的 DDL 严格一致。
/// </para>
/// </summary>
[Table("todo_lists")]
public class TodoListEntity
{
    [Key, Required, MaxLength(64), Column("list_id")]
    public string ListId { get; set; } = string.Empty;

    /// <summary>
    /// 归属 Agent 身份（TD-1b）：工具侧取 ToolExecutionContext.AgentInstanceId（平台注入）；
    /// 读写均按 agent_id 过滤，跨 Agent 即使 scope_id 完全相同也互不可见。
    /// </summary>
    [Required, MaxLength(128), Column("agent_id")]
    public string AgentId { get; set; } = string.Empty;

    /// <summary>goal | task | session。</summary>
    [Required, MaxLength(16), Column("scope_kind")]
    public string ScopeKind { get; set; } = string.Empty;

    /// <summary>goalRunId | taskId | sessionId。</summary>
    [Required, MaxLength(128), Column("scope_id")]
    public string ScopeId { get; set; } = string.Empty;

    /// <summary>可选标题（如「看板梳理拆解」）。</summary>
    [Column("title")]
    public string? Title { get; set; }

    /// <summary>CAS 版本，每次全量写入/勾选 +1。</summary>
    [Required, Column("revision")]
    public int Revision { get; set; }

    [Required, Column("created_at_utc")]
    public DateTimeOffset CreatedAtUtc { get; set; }

    [Required, Column("updated_at_utc")]
    public DateTimeOffset UpdatedAtUtc { get; set; }

    /// <summary>TD-1b（2026-09-16 裁决）：归档语义移除——列保留备用，当前不写入（始终 null）。</summary>
    [Column("archived_at_utc")]
    public DateTimeOffset? ArchivedAtUtc { get; set; }
}
