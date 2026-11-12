using System.Collections.Immutable;

namespace PuddingAuthorization.Contracts;

// PA-05A 授权端口（BCL-only）：纯判定与授权申请生命周期；UI、用户身份认证与存储留适配侧。
// 系统 Deny 不可被插件 Allow 覆盖；插件只能收紧；批准绑定动作/资源/参数 hash/策略版本。

public enum AuthorizationOutcome { Allow, Deny, NeedsApproval, Unavailable }

public enum PolicySourceKind { System, Plugin, Workspace }

public sealed record AuthorizationRule(string RuleId, string Action, AuthorizationOutcome Outcome, long PolicyVersion,
    PolicySourceKind Source, string? ReasonCode, DateTimeOffset? ExpiresAt)
{
    public bool IsActiveAt(DateTimeOffset now) => ExpiresAt is null || now < ExpiresAt;
}

public sealed record AuthorizationRequest(string WorkspaceId, string AgentId, string ActorId, string Action,
    string Resource, string ArgumentHash, long PolicyVersion, string? ApprovalId = null)
{
    public bool IsValid => Valid(WorkspaceId) && Valid(AgentId) && Valid(ActorId) && Valid(Action) && Valid(Resource)
        && Valid(ArgumentHash) && PolicyVersion >= 0;

    public static bool Valid(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 256;
}

public sealed record AuthorizationDecision(AuthorizationOutcome Outcome, long PolicyVersion, string ReasonCode,
    string? ApprovalId, DateTimeOffset DecidedAt)
{
    public bool AllowsExecution => Outcome == AuthorizationOutcome.Allow;
}

public interface IAuthorizationPolicySource
{
    ValueTask<ImmutableArray<AuthorizationRule>> GetRulesAsync(string action, CancellationToken ct = default);
}

public enum ApprovalState { Pending, Approved, Denied, Expired }

public sealed record ApprovalRecord(string ApprovalId, string WorkspaceId, string AgentId, string Action,
    string Resource, string ArgumentHash, long PolicyVersion, string RequestedBy, ApprovalState State,
    DateTimeOffset RequestedAt, DateTimeOffset ExpiresAt, string? DecidedBy, long Revision);

public interface IApprovalStore
{
    ValueTask<ApprovalRecord> CreateAsync(ApprovalRecord record, CancellationToken ct = default);
    ValueTask<ApprovalRecord?> GetAsync(string approvalId, CancellationToken ct = default);
    ValueTask<ApprovalRecord> DecideAsync(string approvalId, ApprovalState decision, string decidedBy,
        long expectedRevision, DateTimeOffset now, CancellationToken ct = default);
}

/// <summary>Host-authored identity: a plugin cannot decide its own approval request.</summary>
public sealed record ApprovalBinding(string WorkspaceId, string AgentId, string ActorId, string RequestedBy,
    PolicySourceKind DeciderKind);

public static class AuthorizationErrorCodes
{
    public const string RequestInvalid = "authorization.request_invalid";
    public const string Unavailable = "authorization.unavailable";
    public const string Denied = "authorization.denied";
    public const string ApprovalRequired = "authorization.approval_required";
    public const string ApprovalMissing = "authorization.approval_missing";
    public const string ApprovalConflict = "authorization.approval_conflict";
    public const string ApprovalExpired = "authorization.approval_expired";
    public const string ApprovalMismatch = "authorization.approval_mismatch";
    public const string ApprovalNotPending = "authorization.approval_not_pending";
    public const string ApprovalForbidden = "authorization.approval_forbidden";
}

public sealed class AuthorizationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
