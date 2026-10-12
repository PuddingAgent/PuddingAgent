using System.Collections.Concurrent;
using Pudding.CapabilityBroker;
using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using PuddingCode.Abstractions;

namespace PuddingHost.BrowserBridge;

/// <summary>
/// R4 (6.2) Host-side implementation of the delegation lifecycle port: translates the scalar
/// delegation request from Runtime/Platform into the existing control-plane binding issue/revoke
/// calls (same seam as CapabilityChannelBindingGate).
/// Unlike a "gate" (which passes when the channel is simply not enabled), a delegation is
/// EXPLICITLY REQUESTED, so this port is fail-closed: no grant => a denial with a reason.
/// Revoke needs (identity, target) while the port only receives a runId, so issuance is
/// recorded as childRunId -> binding and revoke looks it up (exact and idempotent).
/// </summary>
public sealed class HostBrowserDelegationLifecycle(
    Func<DesktopSession?> sessionResolver,
    DesktopCapabilityDescriptor bindingApplyCapability,
    DesktopCapabilityDescriptor bindingRevokeCapability,
    BrowserPageGrantScope grantedScope,
    TimeSpan validity,
    string source,
    TimeProvider? clock = null) : IBrowserDelegationLifecycle
{
    private readonly Func<DesktopSession?> _sessionResolver =
        sessionResolver ?? throw new ArgumentNullException(nameof(sessionResolver));
    private readonly DesktopCapabilityDescriptor _applyCapability =
        bindingApplyCapability ?? throw new ArgumentNullException(nameof(bindingApplyCapability));
    private readonly DesktopCapabilityDescriptor _revokeCapability =
        bindingRevokeCapability ?? throw new ArgumentNullException(nameof(bindingRevokeCapability));
    private readonly TimeSpan _validity = validity;
    private readonly string _source = source ?? throw new ArgumentNullException(nameof(source));
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    private readonly ConcurrentDictionary<string, BrowserTaskBinding> _issued = new(StringComparer.Ordinal);

    public async ValueTask<BrowserDelegationGrantResult> RequestChildGrantAsync(
        BrowserDelegationGrantRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Contract checks come BEFORE resolving the session: declared but incomplete => explicit denial.
        if (!TryParseScope(request.ScopeName, out var requestedScope)
            || (requestedScope & grantedScope) == BrowserPageGrantScope.None)
        {
            return Denied($"the requested browser scope '{request.ScopeName}' is not grantable by this host");
        }

        if (string.IsNullOrWhiteSpace(request.ContextId) || string.IsNullOrWhiteSpace(request.PageId))
        {
            return Denied("browser delegation requires both a context id and a page id");
        }

        if (string.IsNullOrWhiteSpace(request.ParentRunId) || string.IsNullOrWhiteSpace(request.ChildRunId))
        {
            return Denied("browser delegation requires the trusted parent run and the real child run");
        }

        // No session => deny (NOT pass): an explicitly requested delegation must fail closed.
        var session = _sessionResolver();
        if (session is null)
        {
            return Denied("no desktop session is connected, so the browser delegation cannot be granted");
        }

        BrowserCallerIdentity caller;
        try
        {
            // A sub-agent caller must carry BOTH its own task and the parent task (3.2 subset check).
            caller = new BrowserCallerIdentity(
                BrowserCallerKind.SubAgent,
                request.CallerId,
                request.ChildRunId,
                request.ParentTaskId);
        }
        catch (ArgumentException ex)
        {
            return Denied($"the delegated caller identity is not acceptable: {ex.Message}");
        }

        var binding = new BrowserTaskBinding(
            caller,
            new DesktopPageTarget(request.ContextId, request.PageId),
            requestedScope,
            _validity,
            _source);
        var operationId = new OperationId(Guid.NewGuid().ToString("N"));
        var producer = new DesktopBrowserBindingProducer(
            (capability, innerRequest, call, innerToken) => session.SendAsync(capability, innerRequest, call, innerToken),
            session.DesktopId,
            _applyCapability,
            _clock);

        var outcome = await producer
            .IssueAsync(binding, operationId, deadline: null, cancellationToken)
            .ConfigureAwait(false);

        if (!outcome.Issued)
        {
            return Denied(outcome.Reason ?? "the desktop refused the browser binding");
        }

        _issued[request.ChildRunId] = binding;

        return new BrowserDelegationGrantResult(
            operationId.Value,
            request.ScopeName,
            _clock.GetUtcNow() + _validity);
    }

    public async ValueTask RevokeForRunAsync(
        string runId,
        string reason,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(runId))
        {
            return;
        }

        // Nothing recorded => nothing to revoke (never issued or already revoked) => idempotent.
        if (!_issued.TryRemove(runId, out var binding))
        {
            return;
        }

        var session = _sessionResolver();
        if (session is null)
        {
            return;
        }

        var call = new DesktopCallContext(
            session.DesktopId,
            new OperationId(Guid.NewGuid().ToString("N")),
            _clock.GetUtcNow() + TimeSpan.FromSeconds(30),
            correlationId: null,
            browserCaller: binding.Task);

        // Best effort: the caller logs failures; this must not mask the original failure reason.
        await session
            .SendAsync(
                _revokeCapability.Capability,
                DesktopCapabilityRequest.ForBindingRevoke(binding.Task, binding.Target),
                call,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public ValueTask RevokeForTaskAsync(
        string callerKind,
        string callerId,
        string runId,
        string? parentRunId,
        string reason,
        CancellationToken cancellationToken) => RevokeForRunAsync(runId, reason, cancellationToken);

    public ValueTask OnConnectionLostAsync(string reason, CancellationToken cancellationToken)
    {
        _issued.Clear();
        return ValueTask.CompletedTask;
    }

    private static BrowserDelegationGrantResult Denied(string reason) => new(null, null, null, reason);

    private static bool TryParseScope(string? scopeName, out BrowserPageGrantScope scope)
    {
        scope = BrowserPageGrantScope.None;
        if (string.IsNullOrWhiteSpace(scopeName))
        {
            return false;
        }

        var granted = BrowserPageGrantScope.None;
        foreach (var part in scopeName.Split(
            [',', '+', '|'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parsed = part.ToLowerInvariant() switch
            {
                "read" => BrowserPageGrantScope.Read,
                "write" => BrowserPageGrantScope.Write,
                _ => BrowserPageGrantScope.None,
            };
            if (parsed == BrowserPageGrantScope.None)
            {
                return false;
            }

            granted |= parsed;
        }

        if (granted == BrowserPageGrantScope.None)
        {
            return false;
        }

        scope = granted;
        return true;
    }
}