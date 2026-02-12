using Microsoft.Extensions.Logging;

namespace PuddingRuntime.Services.Skills;

/// <summary>
/// PreMessageHook：在 Agent 收到用户消息后、LLM 调用前，
/// 基于技能元数据中的 Keywords 做确定性关键词匹配，
/// 自动加载匹配的技能内容并注入上下文。
/// 
/// 借鉴 Claude Code Hooks 理念："Hooks ensure things ALWAYS happen
/// rather than relying on the LLM to choose to run them."
/// </summary>
public sealed class SkillEnforcerService
{
    private readonly AgentSkillFileService _skillFileService;
    private readonly ILogger<SkillEnforcerService> _logger;

    // 缓存：避免每次请求都读磁盘
    private string? _agentInstanceId;
    private (DateTimeOffset GeneratedAt, Dictionary<string, string> Map)? _cache;

    public SkillEnforcerService(
        AgentSkillFileService skillFileService,
        ILogger<SkillEnforcerService> logger)
    {
        _skillFileService = skillFileService;
        _logger = logger;
    }

    /// <summary>
    /// 扫描用户消息中的关键词 → 返回匹配的 SKILL.md 内容列表。
    /// 返回 null 表示无匹配。
    /// </summary>
    public async Task<IReadOnlyList<SkillEnforcementResult>?> EnforceAsync(
        string agentInstanceId,
        string userMessage,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userMessage))
            return null;

        // 1. 获取或刷新关键词映射
        var map = await GetOrRefreshKeywordMapAsync(agentInstanceId, ct);
        if (map is null || map.Count == 0)
            return null;

        // 2. 匹配关键词（不区分大小写）
        var matchedSkillIds = new HashSet<string>();
        var messageLower = userMessage.ToLowerInvariant();

        foreach (var (keyword, skillId) in map)
        {
            if (messageLower.Contains(keyword.ToLowerInvariant()))
            {
                matchedSkillIds.Add(skillId);
            }
        }

        if (matchedSkillIds.Count == 0)
            return null;

        // 3. 读取匹配的 SKILL.md 内容
        var results = new List<SkillEnforcementResult>();
        foreach (var skillId in matchedSkillIds)
        {
            try
            {
                var file = await _skillFileService.ReadFileAsync(agentInstanceId, skillId, relativePath: null, ct);
                if (file?.Content is not null)
                {
                    results.Add(new SkillEnforcementResult(skillId, file.Content));
                    _logger.LogDebug("[SkillEnforcer] Injected skill={SkillId}", skillId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[SkillEnforcer] Failed to read skill={SkillId}", skillId);
            }
        }

        return results.Count > 0 ? results : null;
    }

    private async Task<Dictionary<string, string>?> GetOrRefreshKeywordMapAsync(
        string agentInstanceId, CancellationToken ct)
    {
        // 从索引文件读取所有 manifest → 构建关键词映射
        var index = await _skillFileService.GetIndexAsync(agentInstanceId, ct);
        if (index is null || index.Skills.Count == 0)
            return null;

        // 检查缓存是否仍有效
        if (_agentInstanceId == agentInstanceId && _cache is not null &&
            _cache.Value.GeneratedAt >= index.GeneratedAt)
        {
            return _cache.Value.Map;
        }

        // 重建映射
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in index.Skills)
        {
            if (!entry.Enabled) continue;

            var keywords = CollectKeywords(entry);
            foreach (var kw in keywords)
            {
                if (!string.IsNullOrWhiteSpace(kw) && !map.ContainsKey(kw))
                {
                    map[kw] = entry.SkillId;
                }
            }
        }

        _agentInstanceId = agentInstanceId;
        _cache = (index.GeneratedAt, map);
        _logger.LogDebug("[SkillEnforcer] Keyword map rebuilt: {Count} keywords → {SkillCount} skills",
            map.Count, index.Skills.Count);

        return map;
    }

    /// <summary>
    /// 从技能元数据中提取关键词：Keywords > Tags > SkillId
    /// </summary>
    private static HashSet<string> CollectKeywords(AgentSkillIndexEntry entry)
    {
        var keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. 显式 Keywords（最高优先级）
        if (entry.Keywords is { Count: > 0 })
            foreach (var kw in entry.Keywords) keywords.Add(kw);

        // 2. Tags（次级）
        if (entry.Tags is { Count: > 0 })
            foreach (var tag in entry.Tags) keywords.Add(tag);

        // 3. SkillId + Name 作为兜底关键词
        keywords.Add(entry.SkillId);
        if (!string.IsNullOrWhiteSpace(entry.Name))
        {
            keywords.Add(entry.Name);
            // 也加入单个词（如 "开发工作流" → "开发", "工作流"）
            foreach (var word in entry.Name.Split(' ', '|', ',', '/', '：', '、'))
            {
                var trimmed = word.Trim();
                if (trimmed.Length > 1)
                    keywords.Add(trimmed);
            }
        }

        return keywords;
    }
}

/// <summary>
/// 技能强制加载结果。
/// </summary>
public sealed record SkillEnforcementResult(string SkillId, string MarkdownContent);
