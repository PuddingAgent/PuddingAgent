using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;

namespace PuddingRuntime.Services;

/// <summary>
/// 潜意识召回管道 — 「两条腿走路」的 Track 1。
///
/// 流程：关键词提取(纯算法) → 混合搜索(摘要→记忆库→日志) → Flash 判断+排名(1次调用) → 截断注入
/// 设计原则：
///   1. 确定性输出（Flash Temp=0, Seed=42）→ 相同输入相同输出 → KV Cache 可命中
///   2. 按需注入：不需要时跳过，需要时只注入 1~5 条精简结果（~2K tokens）
///   3. Session 内 30 秒缓存：同输入复用
/// </summary>
public sealed class SubconsciousRecallPipeline
{
    private readonly IMemoryRecallService _memoryRecall;
    private readonly IMemoryLlmClient _memoryLlmClient;
    private readonly SessionSummaryStore? _summaryStore;
    private readonly ILogger<SubconsciousRecallPipeline> _logger;
    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 50 });

    // Session 级状态（通过 agentInstanceId 隔离）
    private readonly ConcurrentDictionary<string, SessionRecallState> _sessionStates = new();

    // 中文停用词表
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "的","了","是","在","我","有","和","就","不","人","都","一","一个",
        "上","也","很","到","说","要","去","你","会","着","没有","看","好",
        "自己","这","他","她","它","们","那","什么","怎么","哪里","哪",
        "还","被","把","让","对","从","与","或","及","但","而","所",
        "这个","那个","可以","已经","能","能够","会","应该","需要",
        "比较","因为","所以","如果","虽然","然而","不过","并且",
        "现在","今天","昨天","明天","之前","之后","时候","时候",
        "非常","特别","真的","确实","一直","只是","还是","就是",
        "进行","使用","通过","根据","关于","对于","为了",
        "the","is","a","an","in","on","at","to","of","for","and","or",
        "this","that","it","be","are","was","were","will","would",
        "could","should","can","may","have","has","had","do","does",
        "not","no","if","we","you","he","she","they","but","with",
    };

    public SubconsciousRecallPipeline(
        IMemoryRecallService memoryRecall,
        IMemoryLlmClient memoryLlmClient,
        ILogger<SubconsciousRecallPipeline> logger,
        SessionSummaryStore? summaryStore = null)
    {
        _memoryRecall = memoryRecall;
        _memoryLlmClient = memoryLlmClient;
        _logger = logger;
        _summaryStore = summaryStore;
    }

    /// <summary>
    /// 判断是否需要检索，若需要则返回精炼的上下文增强内容。
    /// null = 不需要注入（透明跳过）。
    /// </summary>
    public async Task<string?> RunAsync(
        string userMessage,
        string workspaceId,
        string agentInstanceId,
        bool isFirstMessage,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(userMessage) || string.IsNullOrWhiteSpace(workspaceId))
            return null;

        var state = _sessionStates.GetOrAdd(agentInstanceId, _ => new SessionRecallState());

        // 冷启动首条消息：HISTORICAL-CONTEXT 已覆盖，跳过 AUGMENT
        if (isFirstMessage)
        {
            state.Reset();
            return null;
        }

        // 缓存命中检查：相同规范化输入 → 相同输出
        var normalized = userMessage.Trim();
        var cacheKey = $"{agentInstanceId}:{normalized.GetHashCode()}";
        if (_cache.TryGetValue<string>(cacheKey, out var cached) && cached is not null)
            return cached;

        // 话题转换检测：关键词重叠度 < 30% → 强制触发
        var currentKeywords = ExtractKeywords(userMessage);
        var topicChanged = state.LastKeywords.Count > 0
            && CalculateOverlap(state.LastKeywords, currentKeywords) < 0.3;
        var forceRecall = topicChanged;

        // 连续不召回兜底：每 5 轮强制触发，大小减半
        if (state.ConsecutiveNoRecall >= 5)
            forceRecall = true;

        state.LastKeywords = currentKeywords;

        try
        {
            // Step 1: 关键词提取（纯算法，无 LLM）
            var keywords = ExtractKeywords(userMessage);
            if (keywords.Count < 2 && !forceRecall)
            {
                state.ConsecutiveNoRecall++;
                return null;
            }

            // Step 2: 混合搜索（摘要 → 记忆库 → 日志）
            var searchResults = await HybridSearchAsync(keywords, workspaceId, agentInstanceId, ct);
            if (searchResults.Count == 0)
            {
                state.ConsecutiveNoRecall++;
                return null;
            }

            if (HasExplicitRecallCue(userMessage))
            {
                var directRecall = BuildAugmentContent(
                    new FlashRecallResult
                    {
                        NeedRecall = true,
                        RelevantIds = Enumerable.Range(1, Math.Min(3, searchResults.Count)).ToList(),
                        Reason = "explicit-recall-cue",
                    },
                    searchResults,
                    2000);
                if (!string.IsNullOrWhiteSpace(directRecall))
                {
                    state.ConsecutiveNoRecall = 0;
                    _cache.Set(cacheKey, directRecall, new MemoryCacheEntryOptions
                    {
                        AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30),
                        Size = 1,
                    });
                    return directRecall;
                }
            }

            // Step 3: Flash 单次调用（Judge + Rank 合一）
            var flashResult = await FlashJudgeAndRankAsync(userMessage, workspaceId, agentInstanceId, searchResults, forceRecall, ct);
            if (flashResult is null || !flashResult.NeedRecall)
            {
                state.ConsecutiveNoRecall++;
                return null;
            }

            // Step 4: 截断（最多 5 条，~2K tokens）
            var truncatedContent = BuildAugmentContent(flashResult, searchResults,
                forceRecall ? 1000 : 2000);
            if (string.IsNullOrWhiteSpace(truncatedContent))
            {
                state.ConsecutiveNoRecall++;
                return null;
            }

            state.ConsecutiveNoRecall = 0;

            _cache.Set(cacheKey, truncatedContent, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30),
                Size = 1,
            });
            return truncatedContent;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[SubconsciousRecall] Pipeline failed, skip augment");
            state.ConsecutiveNoRecall++;
            return null;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Step 1: 纯算法关键词提取（无 LLM 开销）
    // ═══════════════════════════════════════════════════════════════

    private static List<string> ExtractKeywords(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new List<string>();

        // 启发式中英文分词：按常见分隔符切分
        var segments = new List<string>();
        var current = new System.Text.StringBuilder();

        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c) || ",.!?;:，。！？；：、()（）[]【】\"'\"".Contains(c))
            {
                if (current.Length >= 2) segments.Add(current.ToString());
                current.Clear();
            }
            else if (IsCjk(c))
            {
                // 中文字符：每 2~4 个字组成一个词元候选
                if (current.Length >= 4)
                {
                    segments.Add(current.ToString());
                    current.Clear();
                }
                current.Append(c);
            }
            else
            {
                current.Append(c);
            }
        }
        if (current.Length >= 2) segments.Add(current.ToString());

        // 去停用词、去重、按优先级排列
        var results = segments
            .Select(s => s.Trim())
            .Where(s => s.Length >= 2 && !StopWords.Contains(s) && !StopWords.Contains(s.ToLowerInvariant()))
            .GroupBy(s => s.ToLowerInvariant())
            .Select(g => (Term: g.Key, Count: g.Count()))
            .OrderByDescending(x => x.Count)
            .Take(5)
            .Select(x => x.Term)
            .ToList();

        return results.Count >= 2 ? results : results.Where(s => s.Length >= 3).Take(3).ToList();
    }

    private static bool IsCjk(char c)
        => (c >= 0x4E00 && c <= 0x9FFF)
        || (c >= 0x3400 && c <= 0x4DBF)
        || (c >= 0xF900 && c <= 0xFAFF);

    // ═══════════════════════════════════════════════════════════════
    // Step 2: 混合搜索
    // ═══════════════════════════════════════════════════════════════

    private async Task<List<SearchHit>> HybridSearchAsync(
        List<string> keywords,
        string workspaceId,
        string agentInstanceId,
        CancellationToken ct)
    {
        var query = string.Join(" ", keywords);
        var results = new List<SearchHit>();
        var seen = new HashSet<string>();

        // 2a: 记忆库检索（FTS5 + Embedding RRF）
        try
        {
            var recallResult = await _memoryRecall.RecallAsync(
                query, workspaceId: workspaceId, agentInstanceId: agentInstanceId,
                topK: 15, ct: ct);
            foreach (var item in recallResult.Items.Take(15))
            {
                var fp = ComputeFingerprint(item.Snippet);
                if (seen.Add(fp))
                    results.Add(new SearchHit { Id = item.SourceId ?? fp, Source = "memory", Content = item.Snippet, Score = item.RelevanceScore });
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[SubconsciousRecall] Memory recall failed, continue");
        }

        // 2b: 每日摘要搜索（优先）
        if (_summaryStore is not null)
        {
            try
            {
                var summaryResults = await SearchDailySummariesAsync(agentInstanceId, query, 10, ct);
                foreach (var s in summaryResults)
                {
                    var fp = ComputeFingerprint(s.Summary);
                    if (seen.Add(fp))
                        results.Add(new SearchHit { Id = s.Day + "_" + seen.Count, Source = "daily-summary", Content = s.Summary, Score = s.Score * 0.9 });
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[SubconsciousRecall] Summary search failed, skip");
            }
        }

        return results.OrderByDescending(r => r.Score).Take(15).ToList();
    }

    private async Task<List<DailySummarySearchHit>> SearchDailySummariesAsync(
        string agentInstanceId, string query, int topK, CancellationToken ct)
    {
        var results = new List<DailySummarySearchHit>();
        var root = System.IO.Path.Combine(
            Environment.GetEnvironmentVariable("PUDDING_DATA_ROOT") ?? "data",
            "agents", agentInstanceId, "memory", "session-summaries");

        if (!Directory.Exists(root)) return results;

        foreach (var dateDir in Directory.GetDirectories(root).OrderByDescending(d => d).Take(7))
        {
            foreach (var file in Directory.GetFiles(dateDir, "*.summary.md").OrderByDescending(f => f).Take(5))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var content = await File.ReadAllTextAsync(file, ct);
                    var keywords = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    var matchCount = keywords.Count(k => content.Contains(k, StringComparison.OrdinalIgnoreCase));
                    if (matchCount > 0)
                    {
                        results.Add(new DailySummarySearchHit
                        {
                            Day = Path.GetFileName(dateDir),
                            Summary = TruncateText(content, 400),
                            Score = (float)matchCount / keywords.Length
                        });
                    }
                }
                catch { /* skip corrupted files */ }
            }
        }
        return results.OrderByDescending(r => r.Score).Take(topK).ToList();
    }

    // ═══════════════════════════════════════════════════════════════
    // Step 3: Flash 单次调用（Judge + Rank 合一）
    // ═══════════════════════════════════════════════════════════════

    private async Task<FlashRecallResult?> FlashJudgeAndRankAsync(
        string userMessage,
        string workspaceId,
        string agentInstanceId,
        List<SearchHit> hits,
        bool forceRecall,
        CancellationToken ct)
    {
        if (hits.Count == 0) return null;

        var itemsText = string.Join("\n",
            hits.Select((h, i) =>
                $"[{i + 1}] ({h.Source}) {TruncateText(h.Content, 200)}"));

        var needRecallPart = forceRecall ? "true" : "true 或 false";
        var prompt = $@"你是记忆检索助手。判断是否需要从历史记录检索信息，并挑出最相关的条目。

## 用户当前问题
""{userMessage}""

## 搜索结果
{itemsText}

{(forceRecall ? "## 注意：本轮强制需要检索，need_recall 必须为 true。\n" : "")}## 判断规则
需要检索（need_recall=true）：
- 提到""之前、上次、继续、回顾、讨论过、上次说、记得""
- 问题需要历史背景才能回答
- 无法判断时 → 默认需要

不需要检索（need_recall=false）：
- 简单应答（你好、谢谢、好的、明白了）
- 全新话题，与历史完全无关

## 输出格式（严格 JSON，不要其他内容）
{{""need_recall"":{needRecallPart},""relevant_ids"":[编号,最多5],""reason"":""一句话理由""}}
".Replace("\"\"", "\"");

        try
        {
            var response = await _memoryLlmClient.ChatAsync(
                "你是记忆检索助手。严格按JSON格式输出判断结果。",
                prompt,
                tools: null,
                ct: ct);

            if (string.IsNullOrWhiteSpace(response))
                return forceRecall ? new FlashRecallResult { NeedRecall = true, RelevantIds = Enumerable.Range(1, Math.Min(5, hits.Count)).ToList(), Reason = "force" } : null;

            // 尝试提取 JSON
            var jsonStart = response.IndexOf('{');
            var jsonEnd = response.LastIndexOf('}');
            if (jsonStart >= 0 && jsonEnd > jsonStart)
            {
                var json = response[jsonStart..(jsonEnd + 1)];
                var result = ParseFlashRecallResult(json);
                if (result?.RelevantIds?.Count > 0)
                {
                    var filteredIds = result.RelevantIds
                        .Where(id => id >= 1 && id <= hits.Count)
                        .Distinct()
                        .Take(5)
                        .ToList();
                    return result with
                    {
                        NeedRecall = result.NeedRecall || HasExplicitRecallCue(userMessage),
                        RelevantIds = filteredIds,
                    };
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[SubconsciousRecall] Flash parse failed, use top-3 fallback");
        }

        // 解析失败兜底：明确历史召回意图时仍返回 top-3，避免 LLM JSON 细节导致召回丢失。
        return forceRecall || HasExplicitRecallCue(userMessage)
            ? new FlashRecallResult { NeedRecall = true, RelevantIds = Enumerable.Range(1, Math.Min(3, hits.Count)).ToList(), Reason = "fallback" }
            : null;
    }

    private static bool HasExplicitRecallCue(string text)
        => text.Contains("之前", StringComparison.OrdinalIgnoreCase)
           || text.Contains("上次", StringComparison.OrdinalIgnoreCase)
           || text.Contains("继续", StringComparison.OrdinalIgnoreCase)
           || text.Contains("回顾", StringComparison.OrdinalIgnoreCase)
           || text.Contains("讨论", StringComparison.OrdinalIgnoreCase)
           || text.Contains("remember", StringComparison.OrdinalIgnoreCase)
           || text.Contains("previous", StringComparison.OrdinalIgnoreCase)
           || text.Contains("continue", StringComparison.OrdinalIgnoreCase);

    private static FlashRecallResult? ParseFlashRecallResult(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var needRecall = TryGetBoolean(root, "need_recall")
                         ?? TryGetBoolean(root, "needRecall")
                         ?? TryGetBoolean(root, "NeedRecall")
                         ?? false;
        var ids = TryGetIntArray(root, "relevant_ids")
                  ?? TryGetIntArray(root, "relevantIds")
                  ?? TryGetIntArray(root, "RelevantIds")
                  ?? [];
        var reason = TryGetString(root, "reason") ?? TryGetString(root, "Reason") ?? string.Empty;
        return new FlashRecallResult
        {
            NeedRecall = needRecall,
            RelevantIds = ids,
            Reason = reason,
        };
    }

    private static bool? TryGetBoolean(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static List<int>? TryGetIntArray(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
            return null;

        var result = new List<int>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var id))
                result.Add(id);
        }

        return result;
    }

    private static string? TryGetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    // ═══════════════════════════════════════════════════════════════
    // Step 4: 截断 + 格式化
    // ═══════════════════════════════════════════════════════════════

    private static string BuildAugmentContent(
        FlashRecallResult flashResult,
        List<SearchHit> hits,
        int maxTokens)
    {
        var selectedIndices = flashResult.RelevantIds ?? new List<int>();
        if (selectedIndices.Count == 0) return string.Empty;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("--- LAYER: L6-CONTEXT-AUGMENT ---");
        sb.AppendLine("[SYSTEM] 以下是从记忆库中检索到的相关历史信息：");

        var totalEstTokens = 50; // header
        const int maxPerItem = 350;
        var added = 0;

        foreach (var idx in selectedIndices)
        {
            if (idx < 1 || idx > hits.Count) continue;
            if (totalEstTokens >= maxTokens) break;

            var hit = hits[idx - 1];
            var content = TruncateText(hit.Content, 350);
            var estTokens = content.Length / 4 + 1;

            if (totalEstTokens + estTokens > maxTokens && added > 0) break;

            sb.AppendLine($"- [{hit.Source}] {content}");
            totalEstTokens += estTokens;
            added++;
        }

        if (added == 0) return string.Empty;

        sb.AppendLine("[SYSTEM] 上下文增强结束 —— 请结合以上信息和用户当前问题回复。");
        return sb.ToString();
    }

    // ═══════════════════════════════════════════════════════════════
    // 辅助方法
    // ═══════════════════════════════════════════════════════════════

    private static double CalculateOverlap(List<string> a, List<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        var intersection = a.Intersect(b, StringComparer.OrdinalIgnoreCase).Count();
        return (double)intersection / Math.Max(a.Count, b.Count);
    }

    private static string ComputeFingerprint(string text)
        => (text.Length > 60 ? text[..60] : text).GetHashCode().ToString();

    private static string TruncateText(string text, int maxChars)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxChars) return text;
        return text[..maxChars] + "...";
    }

    // ═══════════════════════════════════════════════════════════════
    // 数据模型
    // ═══════════════════════════════════════════════════════════════

    private sealed class SessionRecallState
    {
        public int ConsecutiveNoRecall;
        public List<string> LastKeywords = new();

        public void Reset()
        {
            ConsecutiveNoRecall = 0;
            LastKeywords.Clear();
        }
    }

    private sealed class SearchHit
    {
        public string Id { get; init; } = "";
        public string Source { get; init; } = "";
        public string Content { get; init; } = "";
        public double Score { get; init; }
    }

    private sealed record FlashRecallResult
    {
        [JsonPropertyName("need_recall")]
        public bool NeedRecall { get; init; }

        [JsonPropertyName("relevant_ids")]
        public List<int> RelevantIds { get; init; } = new();

        [JsonPropertyName("reason")]
        public string Reason { get; init; } = "";
    }

    private sealed class DailySummarySearchHit
    {
        public string Day { get; init; } = "";
        public string Summary { get; init; } = "";
        public float Score { get; init; }
    }
}
