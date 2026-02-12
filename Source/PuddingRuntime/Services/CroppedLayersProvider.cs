using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingRuntime.Models;

namespace PuddingRuntime.Services;

/// <summary>
/// 记忆裁剪管线 — 用便宜的 Flash 模型裁剪 L2/L5/L6 原始内容，
/// 只保留与当前问题高度相关的「记忆片段」，减少注入 Pro 的 Token 量。
///
/// 设计原则：
///   1. 单次 Flash 调用处理全部原始层（避免多次调用）
///   2. 原始总量 &lt; 5K tokens 时跳过（不值得启动一次 Flash）
///   3. Flash 失败时降级为原始内容简版，不阻断主流程
/// </summary>
public sealed class CroppedLayersProvider
{
    private readonly ILlmInvocationService _llmInvocationService;
    private readonly TimeClusterAnalyzer? _timeClusterAnalyzer;
    private readonly MemorySnippetRelevanceCalculator? _relevanceCalculator;
    private readonly SessionSummaryStore? _sessionSummaryStore;
    private readonly ILogger<CroppedLayersProvider> _logger;

    // 跳过裁剪的阈值：原始总量低于此则不调用 Flash
    private const int SkipCropTokenThreshold = 5000;

    // Flash 输出最大 Token 数
    private const int FlashMaxOutputTokens = 2048;

    // 裁剪 Prompt 模板
    private const string CropPromptTemplate = """
你是一个信息裁剪专家。从以下多条原始信息中，提取与用户当前问题最相关的内容，生成简洁的「记忆片段」。

## 用户当前问题
{0}

{1}

## 裁剪要求
1. 只保留与用户问题直接相关的信息，不相干内容全部丢弃
2. 每条片段不超过 3 句话，尽量精简
3. 保持事实准确性，不编造不存在的细节
4. 如果信息间有潜在关联，可以用一句提示（例如「这可能与 X 有关」），并标注 [推测]
5. 输出格式：每条记忆片段用 --- 分隔，不要序号，不要多余说明

## 输出
""";

    public CroppedLayersProvider(
        ILlmInvocationService llmInvocationService,
        ILogger<CroppedLayersProvider> logger,
        TimeClusterAnalyzer? timeClusterAnalyzer = null,
        MemorySnippetRelevanceCalculator? relevanceCalculator = null,
        SessionSummaryStore? sessionSummaryStore = null)
    {
        _llmInvocationService = llmInvocationService;
        _logger = logger;
        _timeClusterAnalyzer = timeClusterAnalyzer;
        _relevanceCalculator = relevanceCalculator;
        _sessionSummaryStore = sessionSummaryStore;
    }

    /// <summary>
    /// 对原始层内容执行 Flash 裁剪，返回记忆片段列表。
    /// 返回 null 表示不替换原始内容（跳过裁剪或降级）。
    /// </summary>
    public async Task<List<MemorySnippet>?> CropAsync(
        List<RawContentBundle> bundles,
        string userMessage,
        string workspaceId,
        string sessionId,
        string agentTemplateId,
        string agentInstanceId,
        CancellationToken ct)
    {
        if (bundles.Count == 0 || string.IsNullOrWhiteSpace(userMessage))
            return null;

        var totalTokens = bundles.Sum(b => b.EstimatedTokens);
        if (totalTokens < SkipCropTokenThreshold)
        {
            _logger.LogDebug(
                "[MemoryCrop] Skip: totalTokens={Total} < threshold={Threshold}",
                totalTokens, SkipCropTokenThreshold);
            return null;
        }

        var sw = Stopwatch.StartNew();

        try
        {
            // 1. 拼接 Flash 裁剪 Prompt
            var prompt = BuildCropPrompt(bundles, userMessage);

            // Memory crop is a subconscious LLM task, so it goes through the
            // shared invocation facade instead of resolving provider config or
            // calling the protocol client directly. The profile resolver owns
            // the real provider/model choice; the deepseek flash identifiers are
            // only the legacy fallback when the named profile is absent.
            var response = await _llmInvocationService.InvokeAsync(new LlmInvocationRequest
            {
                WorkspaceId = workspaceId,
                SessionId = sessionId,
                AgentTemplateId = agentTemplateId,
                AgentInstanceId = agentInstanceId,
                Profile = new LlmInvocationProfile
                {
                    ProviderId = "deepseek",
                    ProfileId = "default-subconscious",
                    ModelId = "deepseek-v4-flash",
                    Role = "subconscious",
                },
                Messages =
                [
                    new ChatMessage(ChatRole.User, prompt),
                ],
            }, ct);

            if (!response.Success)
            {
                _logger.LogWarning(
                    "[MemoryCrop] Flash invocation failed: {Error}",
                    response.Error ?? "(no error)");
                return null;
            }

            var outputText = response.ReplyText;
            if (string.IsNullOrWhiteSpace(outputText))
            {
                _logger.LogWarning("[MemoryCrop] Flash returned empty response");
                return null; // 降级：保留原始内容
            }

            // 3. 解析输出为记忆片段
            var snippets = ParseSnippets(outputText);

            sw.Stop();

            _logger.LogInformation(
                "[MemoryCrop] Done: Input={In} → Output={Out} tokens, Snippets={Count}, Ratio={Ratio:P}, Elapsed={Elapsed}ms",
                totalTokens, EstimateTokenCount(outputText), snippets.Count,
                (double)EstimateTokenCount(outputText) / totalTokens, sw.ElapsedMilliseconds);

            return snippets;
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogWarning(ex,
                "[MemoryCrop] Flash crop failed after {Elapsed}ms, fallback to original content",
                sw.ElapsedMilliseconds);
            return null; // 降级：保留原始内容，不阻断流程
        }
    }

    /// <summary>
    /// 完整裁剪管线 — Flash 裁剪 + 时间聚类 + 关联度验证 + 连续性消息提取。
    /// 返回裁剪后的记忆片段、连续性消息及相关元数据。连续性消息仅在新会话的首条消息时获取。
    /// </summary>
    public async Task<CropPipelineResult> RunFullPipelineAsync(
        List<RawContentBundle> bundles,
        string userMessage,
        string workspaceId,
        string sessionId,
        string agentTemplateId,
        string agentInstanceId,
        bool isFirstMessage,
        CancellationToken ct)
    {
        var result = new CropPipelineResult();

        // Step 1: Flash 裁剪
        var snippets = await CropAsync(
            bundles, userMessage, workspaceId, sessionId,
            agentTemplateId, agentInstanceId, ct);

        result.Snippets = snippets;

        // Step 2: 时间聚类 + 连续性消息（仅新会话首条消息）
        if (isFirstMessage && _timeClusterAnalyzer is not null && snippets is not null)
        {
            // 2a. 计算关联度调整因子
            var relevanceBoost = _relevanceCalculator is not null
                ? await _relevanceCalculator.CalculateRelevanceAsync(
                    snippets, workspaceId, agentInstanceId, ct)
                : 1.0;

            result.RelevanceBoost = relevanceBoost;

            // 2b. 估算上一个会话的最后消息时间
            var currentTime = DateTime.UtcNow;
            var lastMsgTime = EstimatePreviousSessionTime(bundles, currentTime);

            var baseCount = _timeClusterAnalyzer.CalculateBaseMergeCount(lastMsgTime, currentTime);
            var adjustedCount = _timeClusterAnalyzer.ApplyRelevanceBoost(baseCount, relevanceBoost);

            _logger.LogInformation(
                "[MemoryCrop] TimeCluster: base={Base}, relevance={Relevance:F2}, adjusted={Adjusted}",
                baseCount, relevanceBoost, adjustedCount);

            // 2c. 提取上一个会话窗口的消息
            result.ContinuityMessages = await _timeClusterAnalyzer.GetPreviousWindowMessagesAsync(
                workspaceId, agentInstanceId, sessionId, currentTime, adjustedCount, ct);

            // 2d. 持久化统一历史上下文：Flash 摘要 + 最近 7 条原文
            if (_sessionSummaryStore is not null && snippets.Count > 0)
            {
                var summaryText = string.Join("\n", snippets
                    .Where(s => s.Source?.StartsWith("L2") == true)
                    .Select(s => s.Text.Trim())
                    .Where(c => !string.IsNullOrWhiteSpace(c))
                    .Select(c => $"- {c}"));

                var recentMessages = result.ContinuityMessages is { Count: > 0 }
                    ? result.ContinuityMessages
                        .TakeLast(13)
                        .Select(msg => $"- [{msg.Role}]: {Truncate(msg.Content, 200)}")
                        .ToList()
                    : null;

                var hasContent = !string.IsNullOrWhiteSpace(summaryText) || recentMessages is { Count: > 0 };

                if (hasContent)
                {
                    _ = _sessionSummaryStore.SaveAsync(
                        agentInstanceId,
                        sessionId,
                        summaryText ?? string.Empty,
                        recentMessages,
                        ct: CancellationToken.None)
                        .ContinueWith(t =>
                        {
                            if (t.IsFaulted)
                                _logger.LogWarning(t.Exception,
                                    "[MemoryCrop] Unified summary save failed");
                        }, TaskScheduler.Default);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// 从原始 bundles 估算上一个会话的最后消息时间。
    /// </summary>
    private static DateTime EstimatePreviousSessionTime(List<RawContentBundle> bundles, DateTime currentTime)
    {
        var recentBundle = bundles.FirstOrDefault(b => b.Source == "L5-RECENT");
        if (recentBundle?.Metadata?.TryGetValue("lastMessageTime", out var timeStr) == true
            && DateTime.TryParse(timeStr, out var parsed))
        {
            return parsed.ToUniversalTime();
        }
        return currentTime.AddMinutes(-5);
    }

    /// <summary>
    /// 构建裁剪 Prompt — 将用户问题与各层原始内容拼装为一个分析请求。
    /// </summary>
    private static string BuildCropPrompt(List<RawContentBundle> bundles, string userMessage)
    {
        var sourcesSection = new StringBuilder();
        foreach (var bundle in bundles)
        {
            sourcesSection.AppendLine($"## {bundle.Source}");
            sourcesSection.AppendLine(bundle.Content);
            sourcesSection.AppendLine();
        }

        return string.Format(CropPromptTemplate, userMessage, sourcesSection.ToString());
    }

    /// <summary>
    /// 解析 Flash 输出为记忆片段列表。
    /// 按 "---" 分隔，每段为一个 MemorySnippet。
    /// </summary>
    private static List<MemorySnippet> ParseSnippets(string outputText)
    {
        var snippets = new List<MemorySnippet>();

        // 按 --- 分隔（兼容各种换行变体）
        var segments = outputText.Split(
            new[] { "\n---\n", "\n---", "---\n", "\r\n---\r\n", "\r\n---", "---\r\n" },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var segment in segments)
        {
            var text = segment.Trim();
            if (string.IsNullOrWhiteSpace(text))
                continue;

            // 跳过明显的非片段输出（如 "输出："、"注意：" 等引导语）
            if (text.StartsWith("##") || text.StartsWith("```") || text.StartsWith("输出"))
                continue;

            var snippet = new MemorySnippet
            {
                Text = text,
                Source = GuessSource(text),
                Relevance = 1.0,
                IsSpeculative = text.Contains("[推测]"),
            };
            snippets.Add(snippet);
        }

        return snippets;
    }

    /// <summary>
    /// 尽力猜测片段来源层。
    /// </summary>
    private static string GuessSource(string text)
    {
        // 简单的启发式：检查文本中是否包含来源层的关键词
        if (text.Contains("L6-RECALLED") || text.Contains("L6:") || text.Contains("recall"))
            return "L6";
        if (text.Contains("AGENT-LOG") || text.Contains("L6B"))
            return "L6-LOG";
        if (text.Contains("L5") || text.Contains("RECENT") || text.Contains("recent"))
            return "L5";
        if (text.Contains("L2") || text.Contains("MEMORY") || text.Contains("summary"))
            return "L2";
        return string.Empty;
    }

    private static string Truncate(string text, int maxChars)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxChars) return text;
        return text[..maxChars] + "...";
    }

    private static int EstimateTokenCount(string text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        return ContextUsageSnapshotStore.CountTokens(text);
    }
}

/// <summary>
/// 完整裁剪管线结果 — 包含 Flash 裁剪的记忆片段 + 时间聚类后的连续性消息。
/// </summary>
public sealed class CropPipelineResult
{
    /// <summary>Flash 裁剪后的记忆片段；null 表示裁剪失败/跳过，使用原始内容降级</summary>
    public List<MemorySnippet>? Snippets { get; set; }

    /// <summary>上一个会话窗口的连续性消息（仅新 Session 首条消息）；null 表示无可用</summary>
    public List<ContinuityMessage>? ContinuityMessages { get; set; }

    /// <summary>记忆线索关联度调整因子（0.5~2.0）；默认 1.0</summary>
    public double RelevanceBoost { get; set; } = 1.0;
}
