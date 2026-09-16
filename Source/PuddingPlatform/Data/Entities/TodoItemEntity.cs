using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PuddingPlatform.Data.Entities;

/// <summary>
/// 设计 2026-09-16 §3（TD-1）：todo_items — 拆解 TODO 的单项。
/// <para>
/// slug 由写方提供、列表内唯一（全量替换时用于 diff：新增/完成/受阻/移除）；
/// 服务端约束：单列表 ≤20 项、同时最多 1 个 in_progress、blocked 必填 blocked_reason。
/// 列名与 TodoSchemaBootstrapper 的 DDL 严格一致。
/// </para>
/// </summary>
[Table("todo_items")]
public class TodoItemEntity
{
    [Key, Required, MaxLength(64), Column("item_id")]
    public string ItemId { get; set; } = string.Empty;

    [Required, MaxLength(64), Column("list_id")]
    public string ListId { get; set; } = string.Empty;

    /// <summary>稳定标识（写方提供，列表内唯一）。</summary>
    [Required, MaxLength(128), Column("slug")]
    public string Slug { get; set; } = string.Empty;

    /// <summary>≤120 字（TodoWireMaps.MaxTitleLength）。</summary>
    [Required, MaxLength(120), Column("title")]
    public string Title { get; set; } = string.Empty;

    /// <summary>pending | in_progress | completed | blocked。</summary>
    [Required, MaxLength(16), Column("status")]
    public string Status { get; set; } = "pending";

    [Column("note")]
    public string? Note { get; set; }

    /// <summary>可选：commit sha / 文件路径 / 报告 id。</summary>
    [MaxLength(256), Column("evidence_ref")]
    public string? EvidenceRef { get; set; }

    /// <summary>status=blocked 时必填（§5 结构化受阻的 TODO 侧落点）。</summary>
    [Column("blocked_reason")]
    public string? BlockedReason { get; set; }

    [Required, Column("order_index")]
    public int OrderIndex { get; set; }

    [Column("started_at_utc")]
    public DateTimeOffset? StartedAtUtc { get; set; }

    [Column("completed_at_utc")]
    public DateTimeOffset? CompletedAtUtc { get; set; }

    public TodoListEntity? List { get; set; }
}
