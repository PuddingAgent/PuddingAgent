using System.Collections.Immutable;

namespace PuddingEventing.Contracts;

// PA-07A：插件后台任务最小协议。Submit 只做持久受理并立即返回票据，不等待任务完成；
// Owner、RootBudget、Generation 与 deadline 由宿主绑定，请求者不能自报或扩大。

public sealed record PluginJobRequest(string Kind, string OperationId, string InputRef, string InputHash,
    int TimeoutSeconds, ImmutableArray<string> RequiredGrants)
{
    public bool IsValid => EventEnvelope.Valid(Kind) && EventEnvelope.Valid(OperationId)
        && EventEnvelope.Valid(InputRef) && EventEnvelope.Valid(InputHash) && TimeoutSeconds is >= 1 and <= 3600
        && !RequiredGrants.IsDefault && RequiredGrants.All(EventEnvelope.Valid);
}

public sealed record PluginJobBinding(string PluginId, long PluginGeneration, string WorkspaceId, string OwnerWorkId,
    string RootBudgetId, DateTimeOffset Deadline)
{
    public bool IsValid => EventEnvelope.Valid(PluginId) && PluginGeneration >= 1
        && EventEnvelope.Valid(WorkspaceId) && EventEnvelope.Valid(OwnerWorkId) && EventEnvelope.Valid(RootBudgetId)
        && Deadline > DateTimeOffset.UtcNow;
}

public sealed record PluginJobRecord(string JobId, PluginJobRequest Request, PluginJobBinding Binding,
    DateTimeOffset AcceptedAt, long AcceptedSequence)
{
    public bool IsValid => EventEnvelope.Valid(JobId) && Request is { IsValid: true } && Binding is { IsValid: true }
        && AcceptedAt != default && AcceptedSequence >= 0;
}

public sealed record PluginJobTicket(string JobId, long AcceptedSequence);

public enum PluginJobState { Accepted, Running, Succeeded, Failed, Cancelled, Unknown }

/// <summary>Job state is owned by the job protocol, never by the Review/Goal domain.</summary>
public sealed record PluginJobSnapshot(string JobId, PluginJobState State, string PluginId, string WorkspaceId,
    long PluginGeneration, string? ReasonCode, string? ResultRef, string? ResultHash, DateTimeOffset UpdatedAt, int Attempt);

public interface IPluginJobStore
{
    /// <summary>Idempotent by (WorkspaceId, OperationId): replay returns the same ticket, different content conflicts.</summary>
    ValueTask<PluginJobTicket> SubmitAsync(PluginJobRecord record, CancellationToken ct = default);
    ValueTask<PluginJobSnapshot> GetAsync(string jobId, CancellationToken ct = default);
    /// <summary>Cancellation is a separate non-blocking path; a stale generation cannot cancel the current owner's job.</summary>
    ValueTask<PluginJobSnapshot> CancelAsync(string jobId, long pluginGeneration, string reasonCode, CancellationToken ct = default);
    ValueTask<ImmutableArray<PluginJobSnapshot>> ListAsync(string workspaceId, CancellationToken ct = default);
    /// <summary>Owner-scoped transition. Old-generation callbacks must be rejected by the store's generation fence.</summary>
    ValueTask<PluginJobSnapshot> TransitionAsync(string jobId, long pluginGeneration, PluginJobState state,
        string? reasonCode, string? resultRef, string? resultHash, CancellationToken ct = default);
}

public static class JobErrorCodes
{
    public const string RequestInvalid = "job.request_invalid";
    public const string BindingInvalid = "job.binding_invalid";
    public const string JobMissing = "job.missing";
    public const string Conflict = "job.conflict";
    public const string GenerationStale = "job.generation_stale";
    public const string StateInvalid = "job.state_invalid";
    public const string DeadlineExpired = "job.deadline_expired";
    public const string GrantMissing = "job.grant_missing";
}

public sealed class JobException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
