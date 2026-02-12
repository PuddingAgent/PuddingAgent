using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;
using PuddingCode.Platform;
using PuddingMemoryEngine.Data;
using PuddingMemoryEngine.Infrastructure.Text;

namespace PuddingMemoryEngine.Services;

/// <summary>
/// 统一记忆召回服务实现：
/// - 并行查询 MemoryLibrary (FTS5) + MemoryFacts (LIKE) + MemoryPreferences (精确)
/// - RRF (Reciprocal Rank Fusion) 排序
/// - 通过 ILogger 输出结构化诊断日志供 Admin UI 潜意识面板消费
/// </summary>
public sealed class MemoryRecallService : IMemoryRecallService
{
    private readonly IMemoryLibraryConvenience _library;
    private readonly IMemoryLibrary _memoryLibrary;
    private readonly IDbContextFactory<MemoryDbContext> _dbFactory;
    private readonly ILogger<MemoryRecallService> _logger;
    private readonly IEmbeddingService? _embeddingService;

    public MemoryRecallService(
        IMemoryLibraryConvenience library,
        IMemoryLibrary memoryLibrary,
        IDbContextFactory<MemoryDbContext> dbFactory,
        ILogger<MemoryRecallService> logger,
        IEmbeddingService? embeddingService = null)
    {
        _library = library;
        _memoryLibrary = memoryLibrary;
        _dbFactory = dbFactory;
        _logger = logger;
        _embeddingService = embeddingService;
    }

    public async Task<MemoryRecallResult> RecallAsync(
        string query,
        string workspaceId,
        string? agentInstanceId = null,
        IReadOnlyList<string>? recentContext = null,
        int topK = 10,
        CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        _logger.LogDebug(
            "[Recall] Start workspace={Workspace} agent={AgentInstanceId} queryLen={QueryLen} topK={TopK}",
            workspaceId, agentInstanceId ?? "(shared)", query.Length, topK);

        // ── 第 4 路：Embedding 向量检索（可选增强）──
        Task<IReadOnlyList<RecalledMemory>>? vectorTask = null;
        if (_embeddingService is not null)
        {
            vectorTask = SearchVectorAsync(query, workspaceId, topK * 2, ct);
        }

        // ── 第 1 路：Library FTS5（scoped by workspace + AgentInstanceId）──
        var libraryTask = _memoryLibrary.SearchChaptersFtsScopedAsync(workspaceId, query, topK * 2, ct, agentInstanceId);

        // ── 第 2 路：MemoryFacts 模糊匹配 ──
        var factsTask = SearchFactsAsync(query, workspaceId, agentInstanceId, topK * 2, ct);

        // ── 第 3 路：MemoryPreferences 词元匹配 ──
        var prefsTask = SearchPreferencesAsync(query, workspaceId, agentInstanceId, topK, ct);

        await Task.WhenAll(
            libraryTask, factsTask, prefsTask,
            vectorTask ?? Task.FromResult<IReadOnlyList<RecalledMemory>>(Array.Empty<RecalledMemory>()));

        var libraryResults = await libraryTask;
        var factResults = await factsTask;
        var prefResults = await prefsTask;
        var vectorResults = vectorTask is not null ? await vectorTask : Array.Empty<RecalledMemory>();

        // ── RRF 融合排序 ──
        const double k = 60;
        var rrfScores = new Dictionary<string, (double Score, RecalledMemory Memory)>();

        void AddRrf(IReadOnlyList<RecalledMemory> items, double weight = 1.0)
        {
            for (int i = 0; i < items.Count; i++)
            {
                var key = items[i].SourceId ?? items[i].Snippet;
                var rrf = weight / (k + i + 1);
                if (rrfScores.TryGetValue(key, out var existing))
                    rrfScores[key] = (existing.Score + rrf, existing.Memory with { RelevanceScore = existing.Score + rrf });
                else
                    rrfScores[key] = (rrf, items[i] with { RelevanceScore = rrf });
            }
        }

        AddRrf(libraryResults.Select(l => new RecalledMemory
        {
            Snippet = l.Snippet,
            Source = "library",
            SourceId = l.BookId,
            RelevanceScore = l.Score,
            BookId = l.BookId,
            ChapterId = l.ChapterId,
            TreePath = l.BookTitle,
        }).ToList());

        AddRrf(factResults, weight: 0.8);

        AddRrf(prefResults, weight: 0.6);

        AddRrf(vectorResults, weight: 0.7);

        var merged = rrfScores.Values
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Memory.SourceId ?? "")  // 确定性排序：相同 Score 时按 SourceId 稳定排列
            .Take(topK)
            .Select(x => x.Memory with { InformationDensity = ComputeInformationDensity(x.Memory.Snippet) })
            .ToList();

        sw.Stop();

        var stats = new RecallHitStats
        {
            LibraryHits = libraryResults.Count,
            FactsHits = factResults.Count,
            PreferencesHits = prefResults.Count,
            VectorHits = vectorResults.Count,
        };

        _logger.LogInformation(
            "[Recall] Complete workspace={Workspace} library={Lib} facts={Facts} pref={Pref} vector={Vec} merged={Merged} elapsed={ElapsedMs}",
            workspaceId, stats.LibraryHits, stats.FactsHits, stats.PreferencesHits,
            stats.VectorHits, merged.Count, sw.ElapsedMilliseconds);

        return new MemoryRecallResult
        {
            Items = merged,
            IsContextSufficient = merged.Count >= 3,
            ContextAssessment = merged.Count switch
            {
                0 => "无相关记忆",
                < 3 => "记忆上下文稀疏",
                _ => "上下文充足",
            },
            HitStats = stats,
            ElapsedMs = sw.ElapsedMilliseconds,
        };
    }

    /// <summary>
    /// MemoryFacts 模糊匹配。AgentInstanceId 为 null 时返回共享记忆；
    /// 指定时返回该 Agent 的私有记忆 + 共享记忆。
    /// </summary>
    private async Task<IReadOnlyList<RecalledMemory>> SearchFactsAsync(
        string query, string workspaceId, string? agentInstanceId, int topK, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        // 取 query 的前 3 个词做 LIKE 匹配（SQLite FTS5 中文分词弱，用 LIKE 兜底）
        var keywords = query.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 2)
            .Take(5)
            .ToArray();

        // 中文查询：用 jieba 分词替代 2-gram 蛮力滑动窗口
        if (keywords.Length == 0 && query.Length >= 1)
        {
            var segmenter = JiebaSegmenterPool.Instance;
            keywords = segmenter.Cut(query)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Where(t => !JiebaSegmenterPool.IsStopWord(t))
                .Where(t => t.Length >= 1)
                .Distinct()
                .Take(10)
                .ToArray();
        }

        if (keywords.Length == 0)
            return Array.Empty<RecalledMemory>();

        var results = new List<RecalledMemory>();
        foreach (var kw in keywords)
        {
            var queryable = db.MemoryFacts
                .AsNoTracking()
                .Where(f => f.WorkspaceId == workspaceId
                            && f.Status == "active"
                            && f.Statement.Contains(kw));

            // ADR-042: Agent 记忆隔离 —— 返回该 Agent 私有记忆 + 共享记忆
            if (!string.IsNullOrWhiteSpace(agentInstanceId))
                queryable = queryable.Where(f => f.AgentInstanceId == null || f.AgentInstanceId == agentInstanceId);

            var matches = await queryable
                .OrderByDescending(f => f.Confidence)
                .ThenByDescending(f => f.CreatedAt)
                .Take(topK)
                .Select(f => new { f.FactId, f.Statement, f.Confidence })
                .ToListAsync(ct);

            foreach (var m in matches)
            {
                if (!results.Any(r => r.SourceId == $"fact:{m.FactId}"))
                {
                    results.Add(new RecalledMemory
                    {
                        Snippet = m.Statement,
                        RelevanceScore = m.Confidence,
                        Source = "fact",
                        SourceId = $"fact:{m.FactId}",
                    });
                }
            }
        }

        return results.Take(topK).ToList();
    }

    /// <summary>
    /// MemoryPreferences 词元匹配。AgentInstanceId 为 null 时返回共享偏好；
    /// 指定时返回该 Agent 的私有偏好 + 共享偏好。
    /// </summary>
    private async Task<IReadOnlyList<RecalledMemory>> SearchPreferencesAsync(
        string query, string workspaceId, string? agentInstanceId, int topK, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        // 提取可能的关键词 —— jieba 中文分词 + 英文空格分词
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 先尝试 jieba 中文分词
        var hasChinese = query.Any(c => c >= 0x4E00 && c <= 0x9FFF);
        if (hasChinese)
        {
            var segmenter = JiebaSegmenterPool.Instance;
            foreach (var t in segmenter.Cut(query))
            {
                if (!string.IsNullOrWhiteSpace(t) && t.Length >= 2 && !JiebaSegmenterPool.IsStopWord(t))
                    tokens.Add(t.ToLowerInvariant());
            }
        }

        // 补充英文空格分词
        foreach (var t in query.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (t.Length >= 2)
                tokens.Add(t.ToLowerInvariant());
        }

        if (tokens.Count == 0) return Array.Empty<RecalledMemory>();

        var queryable = db.MemoryPreferences
            .AsNoTracking()
            .Where(p => p.WorkspaceId == workspaceId);

        // ADR-042: Agent 记忆隔离 —— 返回该 Agent 私有偏好 + 共享偏好
        if (!string.IsNullOrWhiteSpace(agentInstanceId))
            queryable = queryable.Where(p => p.AgentInstanceId == null || p.AgentInstanceId == agentInstanceId);

        var allPrefs = await queryable
            .Select(p => new { p.PreferenceId, p.Category, p.Key, p.Value })
            .ToListAsync(ct);

        var matched = allPrefs
            .Where(p => tokens.Any(t => p.Key.Contains(t, StringComparison.OrdinalIgnoreCase)
                                     || p.Value.Contains(t, StringComparison.OrdinalIgnoreCase)
                                     || p.Category.Contains(t, StringComparison.OrdinalIgnoreCase)))
            .Take(topK)
            .Select(p => new RecalledMemory
            {
                Snippet = $"[{p.Category}] {p.Key}: {p.Value}",
                RelevanceScore = 0.9,
                Source = "preference",
                SourceId = $"pref:{p.PreferenceId}",
            })
            .ToList();

        return matched;
    }

    // ── 第 4 路：向量检索（Embedding）──

    /// <summary>
    /// 向量检索：生成查询 Embedding → 搜索 Chapters + Facts 的余弦相似度 Top-K。
    /// 任何一步失败均优雅降级，返回空列表。
    /// </summary>
    private async Task<IReadOnlyList<RecalledMemory>> SearchVectorAsync(
        string query, string workspaceId, int topK, CancellationToken ct)
    {
        try
        {
            float[] queryEmbedding;
            try
            {
                queryEmbedding = await _embeddingService!.GenerateEmbeddingAsync(query, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Recall] Query embedding generation failed, skipping vector path");
                return Array.Empty<RecalledMemory>();
            }

            if (queryEmbedding.Length == 0)
                return Array.Empty<RecalledMemory>();

            var chapterTask = _memoryLibrary.SearchChaptersByVectorAsync(queryEmbedding, topK, ct);
            var factTask = SearchFactsByVectorAsync(queryEmbedding, workspaceId, topK, ct);

            await Task.WhenAll(chapterTask, factTask);

            var chapterResults = (await chapterTask)
                .Select(r => new RecalledMemory
                {
                    Snippet = r.Snippet,
                    RelevanceScore = r.Score,
                    Source = "library-vector",
                    SourceId = r.ChapterId,
                    BookId = r.BookId,
                    ChapterId = r.ChapterId,
                    TreePath = r.BookTitle,
                });

            var factResults = await factTask;

            return chapterResults.Concat(factResults).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Recall] Vector search failed, skipping");
            return Array.Empty<RecalledMemory>();
        }
    }

    /// <summary>
    /// Facts 向量检索：对 workspace 下 active 且有 Embedding 的 Facts 做内存余弦相似度排序。
    /// </summary>
    private async Task<IReadOnlyList<RecalledMemory>> SearchFactsByVectorAsync(
        float[] queryEmbedding, string workspaceId, int topK, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var facts = await db.MemoryFacts
            .AsNoTracking()
            .Where(f => f.WorkspaceId == workspaceId
                        && f.Status == "active"
                        && f.Embedding != null)
            .Select(f => new { f.FactId, f.Statement, f.Confidence, f.Embedding })
            .ToListAsync(ct);

        if (facts.Count == 0)
            return Array.Empty<RecalledMemory>();

        return facts
            .Select(f => new
            {
                f.FactId,
                f.Statement,
                f.Confidence,
                Similarity = VectorSimilarity.CosineSimilarity(
                    queryEmbedding,
                    VectorSimilarity.BytesToFloats(f.Embedding!))
            })
            .OrderByDescending(x => x.Similarity)
            .Take(topK)
            .Select(x => new RecalledMemory
            {
                Snippet = x.Statement,
                RelevanceScore = Math.Round(x.Similarity, 4),
                Source = "fact-vector",
                SourceId = $"fact:{x.FactId}",
            })
            .ToList();
    }

    public async Task<MemoryRecallStatus> GetStatusAsync(string workspaceId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var totalFacts = await db.MemoryFacts.CountAsync(f => f.WorkspaceId == workspaceId && f.Status == "active", ct);
        var totalPreferences = await db.MemoryPreferences.CountAsync(p => p.WorkspaceId == workspaceId, ct);

        return new MemoryRecallStatus
        {
            TotalFacts = totalFacts,
            TotalPreferences = totalPreferences,
            TotalBooks = 0, // 暂不查 Library
        };
    }

    /// <summary>
    /// 启发式计算信息密度 (0.0-1.0)。
    /// 高密度 = 内容适中、含实质信息；低密度 = 过短/过长、噪声比例高。
    /// </summary>
    private static double ComputeInformationDensity(string snippet)
    {
        if (string.IsNullOrWhiteSpace(snippet)) return 0.0;

        var len = snippet.Length;
        var score = 0.6; // 基线

        // 长度适中（100-800 字符）加分
        if (len >= 100 && len <= 800)
            score += 0.20;
        else if (len < 30 || len > 2000)
            score -= 0.25;

        // 短响应模式降分
        var lower = snippet.ToLowerInvariant();
        var shortPatterns = new[] { "好的", "收到", "没问题", "ok", "yes", "no", "got it" };
        var shortPatternScore = shortPatterns.Average(p =>
            lower.StartsWith(p) || lower == p ? 0.0 : 0.1);
        score += shortPatternScore - 0.1;

        // 符号密度过高降分
        var symbolRatio = (double)snippet.Count(c => !char.IsLetterOrDigit(c) && c != ' ' && c != '.') / len;
        if (symbolRatio > 0.15) score -= 0.15;

        return Math.Clamp(score, 0.0, 1.0);
    }
}
