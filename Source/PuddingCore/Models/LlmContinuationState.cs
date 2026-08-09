namespace PuddingCode.Models;

/// <summary>
/// Opaque provider continuation data that must be replayed on a later LLM turn.
/// The runtime transports these items without interpreting provider-specific JSON.
/// </summary>
public sealed record LlmContinuationState(
    string Protocol,
    IReadOnlyList<string> OutputItemsJson);
