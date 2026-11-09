using Pudding.Api.Contracts;
using PuddingAgentConfiguration;
using PuddingAgentConfiguration.Contracts;

namespace PuddingAgentDelegation;

/// <summary>Host-authored grant. No caller-supplied credentials, services or mutable permission sets.</summary>
public sealed record AgentConfigurationGrant(string ActorId, AgentRef Agent, DateTimeOffset Deadline,
    bool Read, bool Update, bool ReadChanges, int MaxCalls = 16);

/// <summary>
/// A revocable, bounded configuration handle. Revoke stops admission immediately and completes only after
/// admitted operations drain. A write racing cancellation may already have committed: it is never labelled undone.
/// </summary>
public sealed class DelegatedAgentConfiguration : IAgentConfigurationApi, IAsyncDisposable
{
    private readonly AgentConfigurationGrant _grant;
    private readonly TimeProvider _clock;
    private readonly IAgentConfigurationApi _api;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stop;
    private int _revoked, _disposed, _calls;

    public DelegatedAgentConfiguration(IAgentConfigurationStore store, AgentConfigurationGrant grant, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(store); ArgumentNullException.ThrowIfNull(grant);
        AgentConfigurationService.ValidateReference(grant.Agent);
        _clock = clock ?? TimeProvider.System;
        var duration = grant.Deadline - _clock.GetUtcNow();
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromMinutes(5) || grant.MaxCalls is < 1 or > 32)
            throw new ArgumentException("A live grant of at most five minutes and 1..32 calls is required.", nameof(grant));
        _grant = grant;
        var caller = new ConfigurationCaller(grant.ActorId);
        _api = new AgentConfigurationService(store, new GrantAuthorizer(grant), _clock).ForCaller(caller);
        _stop = new CancellationTokenSource(duration, _clock);
    }

    public Task<AgentConfigurationSnapshot> GetAsync(AgentRef agent, CancellationToken ct = default)
        => Run(agent, ConfigurationAccess.Read, token => _api.GetAsync(agent, token), ct);
    public Task<ConfigurationChangeReceipt> UpdateAsync(AgentRef agent, AgentConfigurationPatch patch,
        ConfigurationUpdateOptions options, CancellationToken ct = default)
        => Run(agent, ConfigurationAccess.Update, token => _api.UpdateAsync(agent, patch, options, token), ct);
    public Task<ConfigurationChangePage> ReadChangesAsync(AgentRef agent, long afterSequence = 0, int limit = 100, CancellationToken ct = default)
        => Run(agent, ConfigurationAccess.ReadChanges, token => _api.ReadChangesAsync(agent, afterSequence, limit, token), ct);

    private async Task<T> Run<T>(AgentRef agent, ConfigurationAccess access, Func<CancellationToken, Task<T>> action, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await _gate.WaitAsync(ct);
        try
        {
            if (Volatile.Read(ref _revoked) != 0 || _clock.GetUtcNow() >= _grant.Deadline)
                throw new ConfigurationApiException("delegation.revoked", "This delegated invocation has ended or expired.");
            if (agent != _grant.Agent || !Allowed(_grant, access))
                throw new ConfigurationApiException("permission.denied", "The grant does not allow this operation on this Agent.");
            if (_calls >= _grant.MaxCalls)
                throw new ConfigurationApiException("delegation.limit", "The delegated invocation call budget is exhausted.");
            _calls++;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
            try { return await action(linked.Token); }
            catch (Exception ex) when (access == ConfigurationAccess.Update && ex is OperationCanceledException or IOException)
            {
                throw new ConfigurationApiException("operation.unknown", "The delegated write could not be confirmed. Reuse the same operation ID and request.", "unknown");
            }
        }
        finally { _gate.Release(); }
    }

    public async Task RevokeAsync()
    {
        if (Interlocked.Exchange(ref _revoked, 1) == 0)
        {
            try { _stop.Cancel(); } catch (AggregateException) { /* Drain even if a cancellation observer throws. */ }
        }
        await _gate.WaitAsync();
        _gate.Release();
    }
    public async ValueTask DisposeAsync()
    {
        await RevokeAsync();
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _stop.Dispose();
    }
    private static bool Allowed(AgentConfigurationGrant grant, ConfigurationAccess access) => access switch
    { ConfigurationAccess.Read => grant.Read, ConfigurationAccess.Update => grant.Update, ConfigurationAccess.ReadChanges => grant.ReadChanges, _ => false };
    private sealed class GrantAuthorizer(AgentConfigurationGrant grant) : IAgentConfigurationAuthorizer
    {
        public ValueTask<bool> IsAllowedAsync(ConfigurationCaller caller, AgentRef agent, ConfigurationAccess access, CancellationToken ct)
            => ValueTask.FromResult(caller.ActorId == grant.ActorId && agent == grant.Agent && Allowed(grant, access));
    }
}
