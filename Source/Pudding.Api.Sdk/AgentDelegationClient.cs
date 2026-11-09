using Pudding.Api.Contracts;

namespace Pudding.Api.Sdk;

/// <summary>PA-02 delegation facade: parent identity and budgets stay host-bound; the client only forwards.</summary>
public sealed class AgentDelegationClient(IAgentDelegationApi api)
{
    public Task<DelegationReceipt> SubmitChildAsync(DelegateWorkRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return api.SubmitChildAsync(request, ct);
    }

    public Task<SubAgentLease> AcquireAsync(AcquireSubAgentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return api.AcquireAsync(request, ct);
    }

    public Task ReleaseAsync(ReleaseSubAgentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return api.ReleaseAsync(request, ct);
    }
}
