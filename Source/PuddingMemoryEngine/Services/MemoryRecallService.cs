using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;
using PuddingCode.Platform;
using PuddingMemoryEngine.Data;

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

    public MemoryRecallService(
        IMemoryLibraryConvenience library,
        IMemoryLibrary memoryLibrary,
        IDbContextFactory<MemoryDbContext> dbFactory,
        ILogger<MemoryRecallService> logger)
    {
        _library = library;
        _memoryLibrary = memoryLibrary;
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public async Task<MemoryRecallResult> RecallAsync(
        string query,
        string workspaceId,
        IReadOnlyList<string>? recentContext = null,
        int topK = 10,
        CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        _logger.LogDebug(
            "[Recall] Start workspace={Workspace} queryLen={QueryLen} topK={TopK}",
            workspaceId, query.Length, topK);

        // ── 第 1 路：Library FTS5（scoped by workspace）──
        var libraryTask = _memoryLibrary.SearchChaptersFtsScopedAsync(workspaceId, query, topK * 2, ct);

        // ── 第 2 路：MemoryFacts 模糊匹配 ──
        var factsTask = SearchFactsAsync(query, workspaceId, topK * 2, ct);

        // ── 第 3 路：MemoryPreferences 词元匹配 ──
        var prefsTask = SearchPreferencesAsync(query, workspaceId, topK, ct);

        await Task.WhenAll(libraryTask, factsTask, prefsTask);

        var libraryResults = await libraryTask;
        var factResults = await factsTask;
        var prefResults = await prefsTask;

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
        }).ToList());

        AddRrf(factResults, weight: 0.8);

        AddRrf(prefResults, weight: 0.6);

        var merged = rrfScores.Values
            .OrderByDescending(x => x.Score)
            .Take(topK)
            .Select(x => x.Memory)
            .ToList();

        sw.Stop();

        var stats = new RecallHitStats
        {
            LibraryHits = libraryResults.Count,
            FactsHits = factResults.Count,
            PreferencesHits = prefResults.Count,
        };

        _logger.LogInformation(
            "[Recall] Complete workspace={Workspace} library={Lib} facts={Facts} pref={Pref} merged={Merged} elapsed={ElapsedMs}",
            workspaceId, stats.LibraryHits, stats.FactsHits, stats.PreferencesHits,
            merged.Count, sw.ElapsedMilliseconds);

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

    private async Task<IReadOnlyList<RecalledMemory>> SearchFactsAsync(
        string query, string workspaceId, int topK, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        // 取 query 的前 3 个词做 LIKE 匹配（SQLite FTS5 中文分词弱，用 LIKE 兜底）
        var keywords = query.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 2)
            .Take(5)
            .ToArray();

        // 中文查询无法通过空格分词 → 用 2-gram + 单字滑动窗口生成候选关键词
        if (keywords.Length == 0 && query.Length >= 1)
        {
            var chars = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // 2-gram
            for (int i = 0; i <= query.Length - 2; i++)
                chars.Add(query.Substring(i, 2));
            // 单字兜底
            if (chars.Count == 0)
            {
                for (int i = 0; i < query.Length; i++)
                    chars.Add(query[i].ToString());
            }
            keywords = chars.Take(10).ToArray();
        }

        if (keywords.Length == 0)
            return Array.Empty<RecalledMemory>();

        var results = new List<RecalledMemory>();
        foreach (var kw in keywords)
        {
            var matches = await db.MemoryFacts
                .AsNoTracking()
                .Where(f => f.WorkspaceId == workspaceId
                            && f.Status == "active"
                            && f.Statement.Contains(kw))
                .OrderByDescending(f => f.Confidence)
                .ThenByDescending(f => f.CreatedAt)
                .Take(topK)
                .Select(f => new { f.FactId, f.Statement, f.Confidence })
                .ToListAsync(ct);

            foreach (var m in matches)
            {
                if (!results.Any(r => r.SourceId == m.FactId))
                {
                    results.Add(new RecalledMemory
                    {
                        Snippet = m.Statement,
                        RelevanceScore = m.Confidence,
                        Source = "fact",
                        SourceId = m.FactId,
                    });
                }
            }
        }

        return results.Take(topK).ToList();
    }

    private async Task<IReadOnlyList<RecalledMemory>> SearchPreferencesAsync(
        string query, string workspaceId, int topK, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        // 提取可能的关键词
        var tokens = query.ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 2)
            .Take(8)
            .ToHashSet();

        var allPrefs = await db.MemoryPreferences
            .AsNoTracking()
            .Where(p => p.WorkspaceId == workspaceId)
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
                SourceId = p.PreferenceId,
            })
            .ToList();

        return matched;
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
}
