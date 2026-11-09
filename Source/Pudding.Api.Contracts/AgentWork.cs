using System.Text.Json.Serialization;

namespace Pudding.Api.Contracts;

/// <summary>One conversational turn, not a Goal or a claim that the user's task has been verified.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AgentWorkRequest(string OperationId, string Text);

[JsonConverter(typeof(JsonStringEnumConverter<AgentWorkStatus>))]
public enum AgentWorkStatus { Accepted, Running, Waiting, Completed, Failed, Cancelled, Unknown }

public sealed record AgentWorkReceipt(
    AgentRef Agent, string OperationId, string WorkId, string ConversationId,
    string InputMessageId, string TurnId, string CommandId, long AcceptedSequence,
    DateTimeOffset AcceptedAt);

/// <summary>Exact committed terminal event. Reply may be absent, including on successful execution.</summary>
public sealed record AgentWorkTerminal(
    string EventId, string RunId, long Sequence, string? Reply, string? ErrorCode, string? ErrorMessage);

public sealed record AgentWorkSnapshot(
    AgentWorkReceipt Receipt, AgentWorkStatus Status, AgentWorkTerminal? Terminal, string? DiagnosticCode)
{
    public string Verification => "not_requested";
    public bool IsTerminal => Status is AgentWorkStatus.Completed or AgentWorkStatus.Failed or AgentWorkStatus.Cancelled;
}

public sealed record AgentWorkError(string Code, string Message, string EffectOutcome);

public sealed class AgentWorkException(string code, string message, string effectOutcome = "not_applied") : Exception(message)
{
    public string Code { get; } = code;
    public string EffectOutcome { get; } = effectOutcome;
}

/// <summary>Bound to a trusted caller by the host. Callers cannot supply an actor or runtime internals.</summary>
public interface IAgentWorkApi
{
    /// <summary>Reuse the exact OperationId AND Text after an unknown outcome. Never auto-generate a replacement ID.</summary>
    Task<AgentWorkReceipt> SubmitAsync(AgentRef agent, AgentWorkRequest request, CancellationToken ct = default);
    /// <summary>Null means no committed acceptance was found now; it does not cancel an in-flight submission.</summary>
    Task<AgentWorkSnapshot?> GetAsync(AgentRef agent, string operationId, CancellationToken ct = default);
}
