using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services.Tasks;

/// <summary>
/// TB-11: 任务评论/备注领域记录（Store 返回，不直接暴露 EF 实体，参照 <see cref="PuddingCode.Tasks.WorkspaceTask"/> 风格）。
/// </summary>
public sealed record TaskComment
{
    /// <summary>评论业务 GUID。</summary>
    public required string CommentId { get; init; }

    /// <summary>归属任务 ID。</summary>
    public required string TaskId { get; init; }

    /// <summary>所属工作区 ID。</summary>
    public required string WorkspaceId { get; init; }

    /// <summary>作者类型（user/agent/system）。</summary>
    public TaskCommentAuthorKind AuthorKind { get; init; } = TaskCommentAuthorKind.User;

    /// <summary>作者 ID（可从 HttpContext.User 的 nameidentifier/sub claim 取，可为空）。</summary>
    public string? AuthorId { get; init; }

    /// <summary>评论/备注内容。</summary>
    public required string Content { get; init; }

    /// <summary>创建时间（UTC）。</summary>
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}
