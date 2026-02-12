using PuddingCode.Platform;

namespace PuddingCode.Abstractions;

/// <summary>
/// Unified text-processing entrypoint backed by the dedicated subconscious/memory LLM.
/// </summary>
public interface ISubconsciousTextProcessingService
{
    Task<string> SummarizeDailyLogAsync(DailyLogSummaryRequest request, CancellationToken ct = default);

    Task<string> SummarizeCurrentSessionAsync(CurrentSessionSummaryRequest request, CancellationToken ct = default);

    Task<string> CompressConversationAsync(ConversationCompressionRequest request, CancellationToken ct = default);
}

public sealed record DailyLogSummaryRequest(
    string WorkspaceId,
    string AgentInstanceId,
    string? AgentTemplateId,
    string Day,
    string OrdinaryLogMarkdown,
    MemoryLlmConfig? MemoryLlmConfig);

public sealed record CurrentSessionSummaryRequest(
    string WorkspaceId,
    string AgentInstanceId,
    string? AgentTemplateId,
    string SessionId,
    string ConversationText,
    string Reason,
    MemoryLlmConfig? MemoryLlmConfig);

public sealed record ConversationCompressionRequest(
    string WorkspaceId,
    string AgentInstanceId,
    string? AgentTemplateId,
    string SessionId,
    IReadOnlyList<ConversationCompressionMessage> Messages,
    string Reason,
    MemoryLlmConfig? MemoryLlmConfig);

public sealed record ConversationCompressionMessage(
    string Role,
    long Sequence,
    string Content);
