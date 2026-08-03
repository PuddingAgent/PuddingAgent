using System.Text.RegularExpressions;
using PuddingCode.Models;
using PuddingCode.Platform;

namespace PuddingRuntime.Services;

public sealed record LlmRequestBudgetResult(
    IReadOnlyList<ChatMessage> Messages,
    ContextUsageSnapshot Snapshot,
    int EffectiveInputLimit,
    int RemovedMessageCount);

public sealed class LlmInputBudgetExceededException : InvalidOperationException
{
    public LlmInputBudgetExceededException(int estimatedTokens, int effectiveInputLimit)
        : base($"LLM request input is too large after history trimming: estimated={estimatedTokens}, limit={effectiveInputLimit}.")
    {
        EstimatedTokens = estimatedTokens;
        EffectiveInputLimit = effectiveInputLimit;
    }

    public int EstimatedTokens { get; }
    public int EffectiveInputLimit { get; }
}

/// <summary>
/// Applies the provider/model input budget to the final message and tool payload
/// immediately before an LLM request is sent.
/// </summary>
public static partial class LlmRequestBudgetGuard
{
    public const int DefaultSafetyBufferTokens = 1_024;
    private const int ProtectedTailMessages = 8;

    [GeneratedRegex(@"Range\s+of\s+input\s+length\s+should\s+be\s+\[\s*1\s*,\s*(?<max>[0-9_]+)\s*\]", RegexOptions.IgnoreCase)]
    private static partial Regex InputLengthRangeRegex();

    public static int ResolveEffectiveInputLimit(
        LlmConfig? config,
        int safetyBufferTokens = DefaultSafetyBufferTokens)
    {
        var providerLimit = config.MaxInputTokens is > 0
            ? config.MaxInputTokens.Value
            : int.MaxValue;
        if (config?.MaxContextTokens is not > 0)
            return providerLimit;

        var contextWindow = config.MaxContextTokens.Value;
        var requestedOutput = Math.Max(0, config.MaxOutputTokens ?? 0);
        var safetyBuffer = Math.Max(0, safetyBufferTokens);
        var contextDerivedLimit = Math.Max(1, contextWindow - requestedOutput - safetyBuffer);
        return Math.Max(1, Math.Min(contextDerivedLimit, providerLimit));
    }

    public static LlmRequestBudgetResult Prepare(
        ContextUsageSnapshotStore usageStore,
        string sessionId,
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<LlmToolDefinition>? tools,
        LlmConfig? config,
        int safetyBufferTokens = DefaultSafetyBufferTokens)
    {
        ArgumentNullException.ThrowIfNull(usageStore);

        var working = messages.ToList();
        var initialCount = working.Count;
        var effectiveInputLimit = ResolveEffectiveInputLimit(config, safetyBufferTokens);
        var snapshot = usageStore.CaptureLlmRequest(
            sessionId,
            working,
            tools,
            config?.ModelId);

        while (snapshot.UsedTokens > effectiveInputLimit && RemoveOldestConversationUnit(working))
        {
            working = LlmMessageSequenceNormalizer.Normalize(working).Messages.ToList();
            snapshot = usageStore.CaptureLlmRequest(
                sessionId,
                working,
                tools,
                config?.ModelId);
        }

        if (snapshot.UsedTokens > effectiveInputLimit)
            throw new LlmInputBudgetExceededException(snapshot.UsedTokens, effectiveInputLimit);

        return new LlmRequestBudgetResult(
            working,
            snapshot,
            effectiveInputLimit,
            Math.Max(0, initialCount - working.Count));
    }

    public static bool TryGetProviderMaxInputTokens(Exception exception, out int maxInputTokens)
        => TryGetProviderMaxInputTokens(exception.Message, out maxInputTokens);

    public static bool TryGetProviderMaxInputTokens(string? error, out int maxInputTokens)
    {
        maxInputTokens = 0;
        if (string.IsNullOrWhiteSpace(error))
            return false;

        var match = InputLengthRangeRegex().Match(error);
        return match.Success
            && int.TryParse(
                match.Groups["max"].Value.Replace("_", string.Empty, StringComparison.Ordinal),
                out maxInputTokens)
            && maxInputTokens > 0;
    }

    private static bool RemoveOldestConversationUnit(List<ChatMessage> messages)
    {
        var firstRemovable = messages.FindIndex(message => message.Role != ChatRole.System);
        if (firstRemovable < 0)
            return false;

        var protectedTailStart = Math.Max(firstRemovable + 1, messages.Count - ProtectedTailMessages);
        if (firstRemovable >= protectedTailStart)
            return false;

        var removeEnd = firstRemovable + 1;
        if (messages[firstRemovable].Role == ChatRole.User)
        {
            while (removeEnd < protectedTailStart && messages[removeEnd].Role != ChatRole.User)
                removeEnd++;
        }

        messages.RemoveRange(firstRemovable, Math.Max(1, removeEnd - firstRemovable));
        return true;
    }
}
