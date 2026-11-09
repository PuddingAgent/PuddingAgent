using System.Collections.Immutable;
using PuddingAgents.Contracts;

namespace PuddingAgents;

/// <summary>
/// PA-02A ownership policy: every request is checked against the host-bound identity. A caller string can never
/// widen the workspace, invent a parent, borrow another actor's work or exceed the parent's child limits.
/// </summary>
public sealed class AgentOwnershipPolicy
{
    public static void EnsureBinding(AgentBinding binding)
    {
        if (binding is null || !binding.IsValid)
            throw Error(AgentErrorCodes.BindingInvalid, "A valid host-bound agent identity is required.");
    }

    public static void EnsureBinding(AgentReadBinding binding)
    {
        if (binding is null || !binding.IsValid)
            throw Error(AgentErrorCodes.BindingInvalid, "A valid host-bound read identity is required.");
    }

    public static void EnsureWorkspace(AgentReadBinding binding, string workspaceId)
    {
        EnsureBinding(binding);
        if (string.IsNullOrWhiteSpace(workspaceId) || workspaceId.Length > 128)
            throw Error(AgentErrorCodes.RequestInvalid, "A bounded workspace id is required.");
        if (!string.Equals(binding.WorkspaceId, workspaceId, StringComparison.Ordinal))
            throw Error(AgentErrorCodes.Forbidden, "The bound identity cannot read another workspace.");
    }

    public static void EnsureWorkspace(AgentBinding binding, string workspaceId)
    {
        EnsureBinding(binding);
        if (string.IsNullOrWhiteSpace(workspaceId) || workspaceId.Length > 128)
            throw Error(AgentErrorCodes.RequestInvalid, "A bounded workspace id is required.");
        if (!string.Equals(binding.WorkspaceId, workspaceId, StringComparison.Ordinal))
            throw Error(AgentErrorCodes.Forbidden, "The bound identity cannot act in another workspace.");
    }

    public static long? CheckChildOutputLimit(AgentBinding binding, long? requested)
    {
        if (requested is null) return null;
        if (requested is < 1 or > 1_000_000)
            throw Error(AgentErrorCodes.RequestInvalid, "The child output limit is out of bounds.");
        if (binding.ParentMaxOutputTokens is { } parent && requested > parent)
            throw Error(AgentErrorCodes.ChildLimitExceedsParent, "A child limit cannot exceed the parent allocation.");
        return requested;
    }

    public static decimal? CheckChildCostLimit(AgentBinding binding, decimal? requested)
    {
        if (requested is null) return null;
        if (requested is < 0 or > 1_000_000m)
            throw Error(AgentErrorCodes.RequestInvalid, "The child cost limit is out of bounds.");
        if (binding.ParentMaxCost is { } parent && requested > parent)
            throw Error(AgentErrorCodes.ChildLimitExceedsParent, "A child limit cannot exceed the parent allocation.");
        return requested;
    }

    public static ImmutableArray<string> CheckCapabilities(AgentBinding binding, ImmutableArray<string> requested)
    {
        if (requested.IsDefaultOrEmpty) return [];
        foreach (var capability in requested)
        {
            if (!AgentBinding.Valid(capability))
                throw Error(AgentErrorCodes.RequestInvalid, "A capability name is invalid.");
            if (!binding.AllowedCapabilities.Contains(capability, StringComparer.Ordinal))
                throw Error(AgentErrorCodes.CapabilityDenied, "The bound identity does not hold this capability.");
        }
        return requested;
    }

    private static AgentBackendException Error(string code, string message) => new(code, message);
}
