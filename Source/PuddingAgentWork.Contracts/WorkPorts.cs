using Pudding.Api.Contracts;

namespace PuddingAgentWork.Contracts;

public sealed record WorkCaller(string ActorId);
public enum WorkAccess { Submit, Read }
public interface IAgentWorkAuthorizer
{
    ValueTask<bool> IsAllowedAsync(WorkCaller caller, AgentRef agent, WorkAccess access, CancellationToken ct);
}

public sealed record WorkKey(WorkCaller Caller, AgentRef Agent, string OperationId, string WorkId);
public sealed record WorkSubmission(WorkKey Key, string Text, string RequestHash);

/// <summary>
/// Adapter to the existing canonical acceptance/journal. Must atomically persist input, receipt and execution intent;
/// serialize same-key submissions; compare request hash on replay; authorize ownership on reads; never create a second loop.
/// A backend failure after entering acceptance must report effectOutcome=unknown unless non-commit is proven.
/// </summary>
public interface IAgentWorkBackend
{
    Task<AgentWorkReceipt> SubmitAsync(WorkSubmission submission, CancellationToken ct);
    Task<AgentWorkSnapshot?> ReadAsync(WorkKey key, CancellationToken ct);
}
