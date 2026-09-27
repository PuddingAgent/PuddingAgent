namespace PuddingChat;

public enum ApprovalChoice { AllowOnce, Deny }
public enum ChatApprovalStatus { Pending, Approved, Denied, Expired, Consumed, DispatchUnknown, DeferredDependency }
public sealed record ChatApproval(RoleKey Role, string SessionId, string ApprovalId, long Version,
    string ToolName, string Arguments, string Description, DateTimeOffset ExpiresAt,
    ChatApprovalStatus Status, IReadOnlyList<ApprovalChoice> AllowedChoices, string? RiskDescription = null);
public sealed record ApprovalSubmission(RoleKey Role, string SessionId, string ApprovalId, long ExpectedVersion,
    string DecisionId, ApprovalChoice Choice, string? Reason);
/// <summary>Direct application call. The implementation checks the stored operation identity and returns authoritative state.</summary>
public interface IChatApprovals
{
    Task<ChatApproval> DecideAsync(ApprovalSubmission submission, CancellationToken ct);
}
