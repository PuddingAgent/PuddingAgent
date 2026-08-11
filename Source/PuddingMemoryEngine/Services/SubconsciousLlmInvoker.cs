using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;
using PuddingCode.Platform;

namespace PuddingMemoryEngine.Services;

/// <summary>
/// LLM 调用封装器：提供带超时的 LLM 调用。
/// 从 SubconsciousOrchestrator 提取，消除上帝类（审计 P0 #5）。
/// </summary>
public sealed class SubconsciousLlmInvoker
{
    private readonly IMemoryLlmClient _memoryLlmClient;
    private readonly ILogger _logger;

    public SubconsciousLlmInvoker(IMemoryLlmClient memoryLlmClient, ILogger logger)
    {
        _memoryLlmClient = memoryLlmClient;
        _logger = logger;
    }

    /// <summary>
    /// 带15秒超时的 LLM Chat 调用。
    /// </summary>
    public async Task<string?> ChatWithTimeoutAsync(
        string systemPrompt,
        string userPrompt,
        MemoryLlmConfig memoryLlmConfig,
        string stage,
        int? round,
        CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));

        try
        {
            return await _memoryLlmClient.ChatWithConfigAsync(
                systemPrompt,
                userPrompt,
                memoryLlmConfig,
                tools: null,
                ct: timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(
                "[Subconscious][RecallAugmented] LLM timeout stage={Stage} round={Round}",
                stage,
                round?.ToString() ?? "-");
            return null;
        }
    }
}
