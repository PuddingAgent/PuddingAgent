using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using PuddingCode.Goals;

namespace PuddingPlatform.Data.Entities;

/// <summary>
/// ADR-074 §12.1: goal_runs — Goal 聚合当前状态与 CAS owner。
/// 每次状态转换与对应 Conversation Event 在同一 SQLite 事务提交。
/// 枚举存 int（GoalPhase）、时间存 DateTimeOffset、列名 snake_case。
/// </summary>
[Table("goal_runs")]
public class GoalRunEntity
{
    [Key, Required, MaxLength(64), Column("goal_run_id")]
    public string GoalRunId { get; set; } = string.Empty;

    [Required, MaxLength(64), Column("workspace_id")]
    public string WorkspaceId { get; set; } = string.Empty;

    [Required, MaxLength(64), Column("current_conversation_id")]
    public string CurrentConversationId { get; set; } = string.Empty;

    [Required, MaxLength(64), Column("agent_instance_id")]
    public string AgentInstanceId { get; set; } = string.Empty;

    [Required, Column("objective")]
    public string Objective { get; set; } = string.Empty;

    [Required, Column("objective_version")]
    public int ObjectiveVersion { get; set; } = 1;

    [Required, Column("status")]
    public GoalPhase Status { get; set; } = GoalPhase.Active;

    [MaxLength(64), Column("blocked_code")]
    public string? BlockedCode { get; set; }

    [Column("blocked_message")]
    public string? BlockedMessage { get; set; }

    /// <summary>pause / cancel / disarm 等状态原因的自由文本（审计与 UI 展示）。</summary>
    [Column("status_reason")]
    public string? StatusReason { get; set; }

    [Required, Column("max_iterations")]
    public int MaxIterations { get; set; } = GoalLimits.DefaultMaxIterations;

    [Required, Column("iterations_started")]
    public int IterationsStarted { get; set; }

    [Required, Column("iterations_settled")]
    public int IterationsSettled { get; set; }

    [Required, Column("activation_epoch")]
    public int ActivationEpoch { get; set; } = 1;

    /// <summary>boot fence：创建/最后一次激活时的 boot 标识，重启 disarm 用。</summary>
    [MaxLength(64), Column("activation_boot_id")]
    public string? ActivationBootId { get; set; }

    [Required, Column("aggregate_version")]
    public int AggregateVersion { get; set; } = 1;

    [MaxLength(64), Column("created_by_user_id")]
    public string? CreatedByUserId { get; set; }

    [MaxLength(32), Column("source_channel")]
    public string? SourceChannel { get; set; }

    /// <summary>创建命令的幂等锚点（唯一索引）；重放返回首次结果。</summary>
    [MaxLength(128), Column("source_command_id")]
    public string? SourceCommandId { get; set; }

    [MaxLength(128), Column("permission_snapshot_hash")]
    public string? PermissionSnapshotHash { get; set; }

    [MaxLength(128), Column("policy_snapshot_hash")]
    public string? PolicySnapshotHash { get; set; }

    [Column("route_snapshot_json")]
    public string? RouteSnapshotJson { get; set; }

    [Required, Column("active_elapsed_ms")]
    public long ActiveElapsedMs { get; set; }

    [Required, Column("total_tool_calls")]
    public int TotalToolCalls { get; set; }

    [Required, Column("input_tokens")]
    public long InputTokens { get; set; }

    [Required, Column("output_tokens")]
    public long OutputTokens { get; set; }

    [Required, Column("cost")]
    public decimal Cost { get; set; }

    [Required, Column("consecutive_no_progress")]
    public int ConsecutiveNoProgress { get; set; }

    [Required, Column("consecutive_same_blocker")]
    public int ConsecutiveSameBlocker { get; set; }

    [Required, Column("consecutive_infra_failures")]
    public int ConsecutiveInfraFailures { get; set; }

    [MaxLength(128), Column("last_progress_fingerprint")]
    public string? LastProgressFingerprint { get; set; }

    [MaxLength(64), Column("last_verification_id")]
    public string? LastVerificationId { get; set; }

    [Column("last_next_action")]
    public string? LastNextAction { get; set; }

    /// <summary>clear 只清展示指针：记录 cleared_at，不删除任何事实。</summary>
    [Column("cleared_at_utc")]
    public DateTimeOffset? ClearedAtUtc { get; set; }

    [Required, Column("created_at_utc")]
    public DateTimeOffset CreatedAtUtc { get; set; }

    [Required, Column("updated_at_utc")]
    public DateTimeOffset UpdatedAtUtc { get; set; }

    [Column("terminal_at_utc")]
    public DateTimeOffset? TerminalAtUtc { get; set; }

    public GoalSnapshot ToSnapshot() => new()
    {
        GoalRunId = GoalRunId,
        WorkspaceId = WorkspaceId,
        ConversationId = CurrentConversationId,
        AgentInstanceId = AgentInstanceId,
        Objective = Objective,
        ObjectiveVersion = ObjectiveVersion,
        Phase = Status,
        BlockedCode = BlockedCode,
        BlockedMessage = BlockedMessage,
        StatusReason = StatusReason,
        MaxIterations = MaxIterations,
        IterationsStarted = IterationsStarted,
        IterationsSettled = IterationsSettled,
        ActivationEpoch = ActivationEpoch,
        AggregateVersion = AggregateVersion,
        CreatedByUserId = CreatedByUserId,
        SourceChannel = SourceChannel,
        SourceCommandId = SourceCommandId,
        LastNextAction = LastNextAction,
        CreatedAtUtc = CreatedAtUtc,
        UpdatedAtUtc = UpdatedAtUtc,
        TerminalAtUtc = TerminalAtUtc,
    };
}
