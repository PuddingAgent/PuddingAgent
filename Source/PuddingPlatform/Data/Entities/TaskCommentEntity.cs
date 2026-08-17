using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PuddingPlatform.Data.Entities;

/// <summary>
/// TB-11: task_comments.author_kind 枚举（枚举存 int）。
/// wire: user / agent / system。
/// </summary>
public enum TaskCommentAuthorKind
{
    /// <summary>用户。wire: user</summary>
    User = 0,

    /// <summary>Agent。wire: agent</summary>
    Agent = 1,

    /// <summary>系统。wire: system</summary>
    System = 2
}

/// <summary>
/// TB-11: task_comments 表实体 — 任务评论/备注。
/// 枚举存 int、时间存 DateTimeOffset（TEXT）、列名 snake_case（遵循 TB-02 约定）。
/// </summary>
[Table("task_comments")]
public class TaskCommentEntity
{
    [Key, Column("id"), DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    [Required, MaxLength(64), Column("comment_id")]
    public string CommentId { get; set; } = string.Empty;

    [Required, MaxLength(64), Column("task_id")]
    public string TaskId { get; set; } = string.Empty;

    [Required, MaxLength(64), Column("workspace_id")]
    public string WorkspaceId { get; set; } = string.Empty;

    [Required, Column("author_kind")]
    public TaskCommentAuthorKind AuthorKind { get; set; }

    [MaxLength(64), Column("author_id")]
    public string? AuthorId { get; set; }

    [Required, Column("content")]
    public string Content { get; set; } = string.Empty;

    [Required, Column("created_at_utc")]
    public DateTimeOffset CreatedAtUtc { get; set; }
}
