using System.Collections.Immutable;
using PuddingAuthorization;
using PuddingAuthorization.Contracts;

namespace PuddingAuthorizationTests;

public sealed class FakePolicySource : IAuthorizationPolicySource
{
    public List<AuthorizationRule> Rules { get; } = [];
    public Exception? Failure { get; set; }

    public ValueTask<ImmutableArray<AuthorizationRule>> GetRulesAsync(string action, CancellationToken ct = default)
    {
        if (Failure is not null) throw Failure;
        return ValueTask.FromResult<ImmutableArray<AuthorizationRule>>(
            [.. Rules.Where(r => string.Equals(r.Action, action, StringComparison.Ordinal))]);
    }
}

public sealed class FakeApprovalStore : IApprovalStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ApprovalRecord> _records = new(StringComparer.Ordinal);

    public ValueTask<ApprovalRecord> CreateAsync(ApprovalRecord record, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _records[record.ApprovalId] = record;
            return ValueTask.FromResult(record);
        }
    }

    public ValueTask<ApprovalRecord?> GetAsync(string approvalId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _records.TryGetValue(approvalId, out var record);
            return ValueTask.FromResult(record);
        }
    }

    public ValueTask<ApprovalRecord> DecideAsync(string approvalId, ApprovalState decision, string decidedBy,
        long expectedRevision, DateTimeOffset now, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var record = _records[approvalId];
            if (record.Revision != expectedRevision)
                throw new AuthorizationException(AuthorizationErrorCodes.ApprovalConflict, "revision changed");
            var updated = record with
            {
                State = decision,
                DecidedBy = decidedBy,
                Revision = record.Revision + 1,
            };
            _records[approvalId] = updated;
            return ValueTask.FromResult(updated);
        }
    }
}

internal static class AuthorizationFixtures
{
    public static readonly DateTimeOffset Now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
    public static readonly ApprovalBinding HostBinding = new("ws-1", "agent-1", "actor-1", "host-user",
        PolicySourceKind.System);
    public static readonly ApprovalBinding PluginBinding = new("ws-1", "agent-1", "actor-1", "plugin-a",
        PolicySourceKind.Plugin);

    public static AuthorizationRule Rule(AuthorizationOutcome outcome, PolicySourceKind source = PolicySourceKind.System,
        long policyVersion = 7, string action = "files.write", string? reason = null,
        DateTimeOffset? expiresAt = null) => new($"rule-{Guid.NewGuid():N}", action, outcome, policyVersion, source,
        reason, expiresAt);

    public static AuthorizationRequest Request(long policyVersion = 7, string action = "files.write",
        string argumentHash = "sha256-args-abc", string? approvalId = null) => new("ws-1", "agent-1", "actor-1",
        action, "workspace:default/report.md", argumentHash, policyVersion, approvalId);

    public static ApprovalRequest Approval(string operationId = "approval-1", string argumentHash = "sha256-args-abc")
        => new(operationId, "files.write", "workspace:default/report.md", argumentHash, 7, TimeSpan.FromMinutes(10));
}
