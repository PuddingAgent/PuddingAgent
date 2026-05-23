using System.Text.Json.Serialization;

namespace PuddingCode.Runtime;

[JsonConverter(typeof(JsonStringEnumConverter<ContextHealthState>))]
public enum ContextHealthState
{
    Healthy,
    Warning,
    Unhealthy,
    Critical,
    Blocking,
}

[JsonConverter(typeof(JsonStringEnumConverter<ContextCompactionMode>))]
public enum ContextCompactionMode
{
    Manual,
    Auto,
}

[JsonConverter(typeof(JsonStringEnumConverter<ContextCompactionLevel>))]
public enum ContextCompactionLevel
{
    Micro,
    SessionMemory,
    Full,
}

public sealed record ContextHealthSnapshot(
    string SessionId,
    int UsedTokens,
    int ContextWindowTokens,
    int EffectiveWindowTokens,
    int RemainingTokens,
    double UsageRatio,
    ContextHealthState State,
    bool ShouldSuggestCompact,
    bool ShouldAutoCompact,
    bool ShouldBlockSend);

public sealed record ContextCompactionRequest(
    string WorkspaceId,
    string SessionId,
    string? AgentId,
    ContextCompactionMode Mode,
    ContextCompactionLevel Level,
    string Reason);

public sealed record ContextCompactionResult(
    string SessionId,
    string SummaryMessageId,
    ContextCompactionMode Mode,
    ContextCompactionLevel Level,
    int BeforeTokens,
    int AfterTokens,
    int CompactedMessageCount,
    string SummaryPreview);

public sealed record ContextCompactionSummaryRequest(
    string WorkspaceId,
    string SessionId,
    string? AgentId,
    IReadOnlyList<ContextCompactionMessage> Messages,
    string Reason);

public sealed record ContextCompactionMessage(
    string MessageId,
    long Sequence,
    string Role,
    string Content);

public interface IContextCompactionService
{
    Task<ContextHealthSnapshot> GetHealthAsync(string sessionId, CancellationToken ct = default);

    Task<ContextCompactionResult> CompactAsync(
        ContextCompactionRequest request,
        CancellationToken ct = default);
}

public interface IContextCompactionSummaryGenerator
{
    Task<string> GenerateSummaryAsync(
        ContextCompactionSummaryRequest request,
        CancellationToken ct = default);
}
