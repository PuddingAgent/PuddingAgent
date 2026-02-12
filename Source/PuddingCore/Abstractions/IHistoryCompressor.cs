using PuddingCode.Models;

namespace PuddingCode.Abstractions;

/// <summary>
/// Compresses a window of conversation history messages into a concise summary string.
/// Used by <see cref="PuddingCode.Core.ContextBudgetManager"/> to perform layered
/// compression instead of blindly dropping old messages when the context budget is exceeded.
/// </summary>
public interface IHistoryCompressor
{
    /// <summary>
    /// Summarise the given messages.
    /// Returns <c>null</c> when the compressor is unavailable or fails — the caller
    /// must fall back to simple trimming in that case.
    /// </summary>
    Task<string?> CompressAsync(
        IReadOnlyList<ChatMessage> messages,
        CancellationToken ct = default);
}
