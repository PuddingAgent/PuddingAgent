// ═══════════════════════════════════════════════════════════════
// MemoryQualityFilter — 记忆质量监控（写前校验/去重/脏词过滤）
// P2: 在记忆写入前执行三层质量检查，不阻塞写入
// ═══════════════════════════════════════════════════════════════

using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;

namespace PuddingRuntime.Services.Tools;

/// <summary>
/// 记忆质量检查结果。
/// </summary>
public sealed class MemoryQualityResult
{
    public bool HasIssues => Warnings.Count > 0;
    public List<MemoryQualityWarning> Warnings { get; set; } = [];
    public string FilteredContent { get; set; } = string.Empty;
}

/// <summary>
/// 质量警告项。
/// </summary>
public sealed class MemoryQualityWarning
{
    public string Rule { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public string Severity { get; init; } = "info"; // info / warn
}

/// <summary>
/// 记忆质量过滤器：写前校验、去重检测、脏词过滤。
/// P2 级别——只记录警告，不阻塞写入。
/// </summary>
public sealed class MemoryQualityFilter
{
    private readonly ILogger<MemoryQualityFilter> _logger;

    // ── 脏词列表（中文 + 英文常见脏词） ──
    // 仅覆盖最常见脏词，避免误判。如需扩展可配置化。
    private static readonly HashSet<string> DirtyWords = new(StringComparer.OrdinalIgnoreCase)
    {
        // 中文脏词
        "操你", "傻逼", "sb", "cnm", "fuck", "shit", "damn", "bastard",
        "他妈的", "妈的", "你妈", "尼玛", "脑残", "智障", "废物",
        "混蛋", "去死", "滚蛋",
        // 英文脏词
        "wtf", "asshole", "dick", "bitch", "crap",
    };

    // ── 最小内容长度 ──
    private const int MinContentLength = 3;

    // ── FTS5 去重相似度阈值 ──
    private const int DupCheckTopK = 3;

    public MemoryQualityFilter(ILogger<MemoryQualityFilter> logger)
    {
        _logger = logger;
    }

    // ═══════════════════════════════════════════════════════════
    // 公共 API
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 完整质量检查：校验 → 过滤脏词 → 检查结果。
    /// 不会阻塞写入，只返回警告。
    /// </summary>
    public MemoryQualityResult Check(string content, string type)
    {
        var result = new MemoryQualityResult { FilteredContent = content };

        ValidateContent(content, type, result);
        FilterDirtyWords(content, result);

        if (result.HasIssues)
        {
            foreach (var w in result.Warnings)
                _logger.LogWarning("[MemoryQuality] {Severity}: [{Rule}] {Message}", w.Severity, w.Rule, w.Message);
        }

        return result;
    }

    /// <summary>
    /// FTS5 去重检查：搜索工作区内是否有高度相似的 Chapter。
    /// 需要 IMemoryLibrary（在 SaveMemoryTool 层传入）。
    /// </summary>
    public async Task<List<MemoryQualityWarning>> CheckDuplicateAsync(
        string content, string title, string workspaceId, IMemoryLibrary lib, CancellationToken ct)
    {
        var warnings = new List<MemoryQualityWarning>();

        // 使用 FTS5 搜索相似内容
        var searchQuery = string.IsNullOrWhiteSpace(title) ? content[..Math.Min(content.Length, 100)] : title;
        try
        {
            var dupResults = await lib.SearchChaptersFtsScopedAsync(
                workspaceId, searchQuery, DupCheckTopK, ct);

            var highDupResults = dupResults
                .Where(r => r.Score > 0.7)
                .ToList();

            foreach (var dup in highDupResults)
            {
                warnings.Add(new MemoryQualityWarning
                {
                    Rule = "dedup",
                    Message = $"疑似重复：已有相似章节 \"{dup.ChapterTitle}\" (Book: \"{dup.BookTitle}\", Score: {dup.Score:F2})",
                    Severity = "warn"
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[MemoryQuality] Dedup check failed (non-blocking)");
        }

        return warnings;
    }

    // ═══════════════════════════════════════════════════════════
    // 私有方法
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 写前校验：内容不为空、长度足够、类型合法。
    /// </summary>
    private static void ValidateContent(string content, string type, MemoryQualityResult result)
    {
        // 空内容检查
        if (string.IsNullOrWhiteSpace(content))
        {
            result.Warnings.Add(new MemoryQualityWarning
            {
                Rule = "empty_content",
                Message = $"内容为空 (type={type})",
                Severity = "warn"
            });
            return;
        }

        // 最小长度检查
        var trimmed = content.Trim();
        if (trimmed.Length < MinContentLength)
        {
            result.Warnings.Add(new MemoryQualityWarning
            {
                Rule = "too_short",
                Message = $"内容过短（{trimmed.Length} 字符），可能缺乏语义价值 (type={type})",
                Severity = "info"
            });
        }

        // 纯数字/符号内容检查
        if (trimmed.All(c => char.IsDigit(c) || char.IsPunctuation(c) || char.IsSymbol(c) || char.IsWhiteSpace(c)))
        {
            result.Warnings.Add(new MemoryQualityWarning
            {
                Rule = "no_text",
                Message = $"内容仅含数字和符号，无实际文本 (type={type})",
                Severity = "warn"
            });
        }

        // 类型合法性检查
        var validTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "fact", "preference", "summary", "chapter" };
        if (!validTypes.Contains(type))
        {
            result.Warnings.Add(new MemoryQualityWarning
            {
                Rule = "unknown_type",
                Message = $"未知记忆类型 \"{type}\"，支持: fact, preference, summary, chapter",
                Severity = "info"
            });
        }
    }

    /// <summary>
    /// 脏词过滤：检查并替换脏词。
    /// </summary>
    private static void FilterDirtyWords(string content, MemoryQualityResult result)
    {
        if (string.IsNullOrWhiteSpace(content)) return;

        var lowerContent = content.ToLowerInvariant();
        var foundDirty = new List<string>();

        foreach (var word in DirtyWords)
        {
            if (lowerContent.Contains(word, StringComparison.OrdinalIgnoreCase))
            {
                foundDirty.Add(word);
            }
        }

        if (foundDirty.Count > 0)
        {
            result.Warnings.Add(new MemoryQualityWarning
            {
                Rule = "dirty_word",
                Message = $"检测到不当词汇: {string.Join(", ", foundDirty)}",
                Severity = "warn"
            });

            // 替换脏词为 ***
            var filtered = content;
            foreach (var word in foundDirty)
            {
                filtered = filtered.Replace(word, "***", StringComparison.OrdinalIgnoreCase);
            }
            result.FilteredContent = filtered;
        }
    }
}
