namespace PuddingAgents.Contracts;

/// <summary>Identity resolved by the host from an approved adapter installation.</summary>
public sealed record AgentAdapterBinding(string WorkspaceId, string AgentId, string InstallationId)
{
    public bool IsValid => AgentBinding.Valid(WorkspaceId)
        && AgentBinding.Valid(AgentId)
        && AgentBinding.Valid(InstallationId);
}

/// <summary>Server-side routing information. The lease ID is a fence, not an authentication credential.</summary>
public sealed record AgentAdapterLease(AgentAdapterBinding Binding, Guid LeaseId, DateTimeOffset ExpiresAtUtc);

public static class AgentAdapterErrorCodes
{
    public const string Busy = "agent.adapter_busy";
    public const string LeaseRejected = "agent.adapter_lease_rejected";
}
