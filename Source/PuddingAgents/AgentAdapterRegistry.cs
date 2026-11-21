using PuddingAgents.Contracts;

namespace PuddingAgents;

/// <summary>
/// Tracks which authenticated adapter connection owns each logical Agent on this Core.
/// Grants and durable work live outside this registry.
/// </summary>
public sealed class AgentAdapterRegistry
{
    private readonly TimeProvider _clock;
    private readonly TimeSpan _leaseDuration;
    private readonly object _gate = new();
    private readonly Dictionary<(string WorkspaceId, string AgentId), AgentAdapterLease> _active = [];

    public AgentAdapterRegistry(TimeProvider clock, TimeSpan leaseDuration)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(leaseDuration, TimeSpan.Zero);
        _clock = clock;
        _leaseDuration = leaseDuration;
    }

    /// <summary>Opens a new lease. Replacing an active connection requires its lease ID and the same host-bound installation.</summary>
    public AgentAdapterLease Register(AgentAdapterBinding binding, Guid? replacesLeaseId = null)
    {
        RequireBinding(binding);
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            var key = (binding.WorkspaceId, binding.AgentId);
            var current = FindActive(key, now);
            if (replacesLeaseId is { } expected)
            {
                if (!Matches(current, binding, expected))
                    throw new AgentBackendException(AgentAdapterErrorCodes.LeaseRejected, "The replaced adapter lease is no longer owned by this installation.");
            }
            else if (current is not null)
            {
                throw new AgentBackendException(AgentAdapterErrorCodes.Busy, "An adapter connection already owns this Agent.");
            }

            var lease = new AgentAdapterLease(binding, Guid.NewGuid(), now + _leaseDuration);
            _active[key] = lease;
            return lease;
        }
    }

    /// <summary>Looks up a route after the host has authorized the workspace and logical Agent.</summary>
    public AgentAdapterLease? Resolve(string workspaceId, string agentId)
    {
        if (!AgentBinding.Valid(workspaceId) || !AgentBinding.Valid(agentId))
            return null;
        lock (_gate)
            return FindActive((workspaceId, agentId), _clock.GetUtcNow());
    }

    public AgentAdapterLease? Renew(AgentAdapterBinding binding, Guid leaseId)
    {
        RequireBinding(binding);
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            var key = (binding.WorkspaceId, binding.AgentId);
            var current = FindActive(key, now);
            if (current is null || !Matches(current, binding, leaseId))
                return null;

            var renewed = current with { ExpiresAtUtc = now + _leaseDuration };
            _active[key] = renewed;
            return renewed;
        }
    }

    public bool Release(AgentAdapterBinding binding, Guid leaseId)
    {
        RequireBinding(binding);
        lock (_gate)
        {
            var key = (binding.WorkspaceId, binding.AgentId);
            return Matches(FindActive(key, _clock.GetUtcNow()), binding, leaseId) && _active.Remove(key);
        }
    }

    private AgentAdapterLease? FindActive((string WorkspaceId, string AgentId) key, DateTimeOffset now)
    {
        if (!_active.TryGetValue(key, out var current))
            return null;
        if (now < current.ExpiresAtUtc)
            return current;

        _active.Remove(key);
        return null;
    }

    private static bool Matches(AgentAdapterLease? lease, AgentAdapterBinding binding, Guid leaseId) =>
        lease is not null && lease.LeaseId == leaseId && lease.Binding == binding;

    private static void RequireBinding(AgentAdapterBinding binding)
    {
        if (binding is null || !binding.IsValid)
            throw new AgentBackendException(AgentErrorCodes.BindingInvalid, "An approved adapter binding is required.");
    }
}
