using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Pudding.Api.Contracts;

// PA-02A 委派与池租约。父身份、根预算由 bound context 注入；子级限额只能收紧；
// 租约是原子占用的持久回执，不是“List 后再 Execute”。

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DelegateWorkRequest(string OperationId, string ParentOperationId, string Text,
    ImmutableArray<string> Capabilities, long? MaxOutputTokens = null, decimal? MaxCost = null,
    string DeliverMode = "result-ref");

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DelegationReceipt(string OperationId, string ChildOperationId, string ParentOperationId,
    string State, DateTimeOffset AcceptedAt);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AcquireSubAgentRequest(string OperationId, string WorkspaceId, string AgentId,
    ImmutableArray<string> RequiredCapabilities, int MaxWaitMilliseconds = 5000);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SubAgentLease(string LeaseId, string WorkspaceId, string AgentId, long Generation,
    string OwnerWorkId, DateTimeOffset ExpiresAt, string Status);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReleaseSubAgentRequest(string LeaseId, long Generation, string OwnerWorkId);

public interface IAgentDelegationApi
{
    Task<DelegationReceipt> SubmitChildAsync(DelegateWorkRequest request, CancellationToken ct = default);
    Task<SubAgentLease> AcquireAsync(AcquireSubAgentRequest request, CancellationToken ct = default);
    Task ReleaseAsync(ReleaseSubAgentRequest request, CancellationToken ct = default);
}

public sealed class AgentDelegationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
