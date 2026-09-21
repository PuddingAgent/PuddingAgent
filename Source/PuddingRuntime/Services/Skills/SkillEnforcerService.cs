using System.Text;
using Microsoft.Extensions.Logging;
using PuddingRuntime.Services.Skills.Telemetry;

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
    private readonly ISkillUsageTelemetrySink? _telemetrySink;

    // 缓存：避免每次请求都读磁盘
    private string? _agentInstanceId;
    private (DateTimeOffset GeneratedAt, Dictionary<string, string> Map)? _cache;

    public SkillEnforcerService(
        AgentSkillFileService skillFileService,
        ILogger<SkillEnforcerService> logger,
        ISkillUsageTelemetrySink? telemetrySink = null)
    {
        _skillFileService = skillFileService;
        _logger = logger;
        _telemetrySink = telemetrySink;
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
        // RSI-G2 切片1：旁路收集「skill → 命中关键词」，仅供遥测，不参与匹配决策
        var matchedKeywordsBySkill = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var messageLower = userMessage.ToLowerInvariant();

        foreach (var (keyword, skillId) in map)
        {
            if (messageLower.Contains(keyword.ToLowerInvariant()))
            {
                matchedSkillIds.Add(skillId);
                if (!matchedKeywordsBySkill.TryGetValue(skillId, out var kws))
                {
                    kws = [];
                    matchedKeywordsBySkill[skillId] = kws;
                }

                kws.Add(keyword);
            }
        }

        if (matchedSkillIds.Count == 0)
            return null;

        // 3. 读取匹配的 SKILL.md 内容
        var results = new List<SkillEnforcementResult>();
        foreach (var skillId in matchedSkillIds)
        {
            matchedKeywordsBySkill.TryGetValue(skillId, out var matchedKeywords);
            try
            {
                var file = await _skillFileService.ReadFileAsync(agentInstanceId, skillId, relativePath: null, ct);
                if (file?.Content is not null)
                {
                    results.Add(new SkillEnforcementResult(skillId, file.Content));
                    _logger.LogDebug("[SkillEnforcer] Injected skill={SkillId}", skillId);
                    // 记录点①「注入成功」：每个命中技能恰好一条终态记录，避免同一命中双计数
                    await RecordUsageSafeAsync(BuildUsageRecord(
                        agentInstanceId, skillId, matchedKeywords,
                        injected: true,
                        contentBytes: Encoding.UTF8.GetByteCount(file.Content),
                        failureReason: null), ct);
                }
                else
                {
                    // 记录点②「匹配成功但正文为空」：当前 ReadFileAsync 正常路径不会返回 null，
                    // 此分支是防御性兜底，同样按读失败留痕，保证命中必有迹可循
                    await RecordUsageSafeAsync(BuildUsageRecord(
                        agentInstanceId, skillId, matchedKeywords,
                        injected: false, contentBytes: 0,
                        failureReason: "SKILL.md content is null or empty."), ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[SkillEnforcer] Failed to read skill={SkillId}", skillId);
                // 记录点③「读正文失败」：坏技能信号，FailureReason 供后续打分器归因
                await RecordUsageSafeAsync(BuildUsageRecord(
                    agentInstanceId, skillId, matchedKeywords,
                    injected: false, contentBytes: 0,
                    failureReason: ex.Message), ct);
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

    /// <summary>
    /// 组装一条终态遥测记录。Outcome 由注入结果显式给出，不用 bool 冒充两态。
    /// </summary>
    private static SkillUsageRecord BuildUsageRecord(
        string agentInstanceId,
        string skillId,
        IReadOnlyList<string>? matchedKeywords,
        bool injected,
        int contentBytes,
        string? failureReason)
    {
        return new SkillUsageRecord
        {
            SkillId = skillId,
            AgentInstanceId = agentInstanceId,
            MatchedKeywords = matchedKeywords ?? [],
            Injected = injected,
            ContentBytes = contentBytes,
            FailureReason = failureReason,
            OccurredAtUtc = DateTimeOffset.UtcNow,
            Outcome = injected ? SkillUsageOutcome.Injected : SkillUsageOutcome.ReadFailed,
        };
    }

    /// <summary>
    /// 双保险写遥测：契约要求 sink 自身 fail-open，但即便实现违约，
    /// 这里也再兜一层 —— 遥测绝不影响注入主链路与返回结果。
    /// </summary>
    private async Task RecordUsageSafeAsync(SkillUsageRecord record, CancellationToken ct)
    {
        if (_telemetrySink is null)
            return;

        try
        {
            await _telemetrySink.RecordAsync(record, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[SkillEnforcer] Skill usage telemetry failed skill={SkillId}", record.SkillId);
        }
    }
}

/// <summary>
/// 技能强制加载结果。
/// </summary>
public sealed record SkillEnforcementResult(string SkillId, string MarkdownContent);
