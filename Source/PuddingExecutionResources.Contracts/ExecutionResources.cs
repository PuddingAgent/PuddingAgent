namespace PuddingExecutionResources.Contracts;

public sealed record ExecutionResourceIdentity(string WorkspaceId, string AgentId, string SessionId, string RunId);
// Null means unbounded; zero means exhausted. Input is a per-request capacity, not cumulative usage.
public sealed record ExecutionResourceLimits(long? MaxOutputTokens = null, decimal? MaxCost = null,
    int? MaxCalls = null, int MaxConcurrentCalls = 2, long? MaxInputTokensPerRequest = null);
public sealed record ExecutionResourceSnapshot(long InputTokens, long OutputTokens, long CacheHitTokens, decimal Cost,
    long ReservedOutputTokens, decimal ReservedCost, long UnknownOutputTokens, decimal UnknownCost,
    int Calls, int ActiveLeases, string? FailureCode, bool Revoked);
public sealed record ModelPrice(decimal InputPerMillion, decimal OutputPerMillion, decimal CacheHitPerMillion,
    string Unit = "configured-price-unit");
public sealed record ModelResourceDescriptor(string Key, string Revision, string ModelId, int MaxInputTokens,
    int MaxOutputTokens, int MaxConcurrentCalls, ModelPrice Price);
public sealed record ModelUsage(long InputTokens, long OutputTokens, long CacheHitTokens = 0);
public sealed record ModelCompletion(string Text, ModelUsage? Usage, string? FinishReason);
public sealed record ModelResourceRequest(string CallId, string Instruction, string Input, int MaxOutputTokens, int TimeoutMilliseconds = 15000);
public sealed record ModelResourceReceipt(string LeaseId, string CallId, ExecutionResourceIdentity Parent,
    string ModelId, string ModelRevision, ModelUsage Usage, decimal Cost, string CostUnit, string Text, string? FinishReason);

/// <summary>Host-only model binding. Call dispatch exactly once immediately before sending; never retry internally.</summary>
public interface ILeasedModel
{
    ModelResourceDescriptor Descriptor { get; }
    Task<ModelCompletion> InvokeAsync(ModelResourceRequest request, Action dispatch, CancellationToken ct);
}
public interface IExecutionResourceScope : IDisposable
{
    ExecutionResourceIdentity Identity { get; }
    DateTimeOffset Deadline { get; }
    CancellationToken Cancellation { get; }
    ExecutionResourceSnapshot Snapshot();
    IExecutionResourceScope CreateChild(ExecutionResourceLimits limits, DateTimeOffset deadline);
    IModelBudgetReservation Reserve(ModelResourceDescriptor model, int maxOutputTokens);
}
public interface IModelBudgetReservation : IDisposable
{
    string Id { get; }
    bool Dispatched { get; }
    void MarkDispatched();
    void Settle(ModelUsage usage);
    void MarkUncertain();
}
public interface IModelResourcePool
{
    Task<ModelResourceReceipt> InvokeAsync(IExecutionResourceScope parent, ILeasedModel model, ModelResourceRequest request, CancellationToken ct = default);
}
public sealed class ExecutionResourceException(string code, string message) : Exception(message)
{ public string Code { get; } = code; }
