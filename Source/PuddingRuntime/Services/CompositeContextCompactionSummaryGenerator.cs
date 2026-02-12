using Microsoft.Extensions.Logging;
using PuddingCode.Runtime;

namespace PuddingRuntime.Services;

/// <summary>
/// 组合式上下文压缩摘要生成器。
/// 默认通过当前 Agent 生成语义摘要；也支持显式切换到 Flash 或 Extractive。
/// Extractive 只作为显式模式或显式 fallback 使用，避免把降级摘录伪装成正常语义摘要。
/// </summary>
public sealed class CompositeContextCompactionSummaryGenerator : IContextCompactionSummaryGenerator
{
    private readonly IContextCompactionSummaryGenerator _agent;
    private readonly IContextCompactionSummaryGenerator _flash;
    private readonly IContextCompactionSummaryGenerator _extractive;
    private readonly ContextCompactionOptions _options;
    private readonly ILogger<CompositeContextCompactionSummaryGenerator> _logger;

    public CompositeContextCompactionSummaryGenerator(
        AgentContextCompactionSummaryGenerator agent,
        FlashContextCompactionSummaryGenerator flash,
        ExtractiveContextCompactionSummaryGenerator extractive,
        ContextCompactionOptions options,
        ILogger<CompositeContextCompactionSummaryGenerator> logger)
    {
        _agent = agent;
        _flash = flash;
        _extractive = extractive;
        _options = options;
        _logger = logger;
    }

    public async Task<string> GenerateSummaryAsync(
        ContextCompactionSummaryRequest request,
        CancellationToken ct = default)
    {
        // P1-1: 自动降级链 Agent → Flash → Extractive
        // 不再依赖单一 config 选择，而是按优先级尝试，失败时自动回退

        // 第 1 优先级：Agent 自生成摘要（语义最精准，Agent 最了解上下文重点）
        try
        {
            var agentSummary = await _agent.GenerateSummaryAsync(request, ct);
            if (!string.IsNullOrWhiteSpace(agentSummary))
            {
                _logger.LogInformation(
                    "[CompactionSummary] generator=agent session={SessionId} len={Len}",
                    request.SessionId, agentSummary.Length);
                return agentSummary;
            }
            _logger.LogWarning(
                "[CompactionSummary] agent returned empty summary, falling back to flash session={SessionId}",
                request.SessionId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[CompactionSummary] agent generator failed, falling back to flash session={SessionId}",
                request.SessionId);
        }

        // 第 2 优先级：Flash LLM 生成摘要（成本低，速度快）
        try
        {
            var flashSummary = await _flash.GenerateSummaryAsync(request, ct);
            if (!string.IsNullOrWhiteSpace(flashSummary))
            {
                _logger.LogInformation(
                    "[CompactionSummary] generator=flash session={SessionId} len={Len}",
                    request.SessionId, flashSummary.Length);
                return flashSummary;
            }
            _logger.LogWarning(
                "[CompactionSummary] flash returned empty summary, falling back to extractive session={SessionId}",
                request.SessionId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[CompactionSummary] flash generator failed, falling back to extractive session={SessionId}",
                request.SessionId);
        }

        // 第 3 优先级：纯摘录（最终兜底，无外部依赖）
        _logger.LogInformation(
            "[CompactionSummary] falling back to extractive generator session={SessionId}",
            request.SessionId);
        return await GenerateExtractiveAsync(request, ct);
    }

    private async Task<string> GenerateExtractiveAsync(
        ContextCompactionSummaryRequest request,
        CancellationToken ct)
    {
        var extractiveSummary = await _extractive.GenerateSummaryAsync(request, ct);
        _logger.LogInformation(
            "[CompactionSummary] summaryGenerator=extractive_fallback session={SessionId} len={Len}",
            request.SessionId, extractiveSummary.Length);
        return extractiveSummary;
    }
}
