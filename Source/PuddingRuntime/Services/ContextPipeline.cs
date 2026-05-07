using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using PuddingCode.Abstractions;
using PuddingCode.Agents;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingRuntime.Services.Skills;

namespace PuddingRuntime.Services;

/// <summary>
/// 上下文管道——将上下文拼装建模为 Token 预算分配问题。
/// 按缓存命中率分层：不变的放前面（利用 LLM KV-cache），易变的放后面。
/// 7 层模型：STATIC → TOOLS → SKILLS → USER → PINNED → RECENT → RECALLED → 当前消息。
/// </summary>
public sealed class ContextPipeline
{
    private readonly IMemoryEngine _memory;
    private readonly SkillRuntime _skillRuntime;
    private readonly AgentSkillPackageRegistry _skillPackageRegistry;
    private readonly IMemoryLibraryConvenience? _libraryConvenience;
    private readonly IAgentTemplateProvider? _templateProvider;
    private readonly IWorkspaceProfileProvider? _workspaceProfileProvider;
    private readonly AgentPersonaFileProvider? _personaFileProvider;
    private readonly SystemPromptBuilder _promptBuilder;
    private readonly IMemoryCache _memCache;
    private readonly ILogger<ContextPipeline> _logger;

    // 静态层缓存：sessionId → StaticContextCache
    private readonly ConcurrentDictionary<string, StaticContextCache> _staticCache = new();

    // 动态工具/技能缓存：sessionId+topicKey → DynamicToolsCache
    private readonly ConcurrentDictionary<string, DynamicToolsCache> _toolsCache = new();

    // Token 预算常量
    private const int ReservedForReply = 4096;
    private const double CompactionThreshold = 0.8;
    private const double GentleThreshold = 0.6;

    // 内存缓存过期
    private static readonly TimeSpan MemCacheExpiration = TimeSpan.FromSeconds(30);

    public ContextPipeline(
        IMemoryEngine memory,
        SkillRuntime skillRuntime,
        AgentSkillPackageRegistry skillPackageRegistry,
        SystemPromptBuilder promptBuilder,
        IMemoryCache memCache,
        ILogger<ContextPipeline> logger,
        IMemoryLibraryConvenience? libraryConvenience = null,
        IAgentTemplateProvider? templateProvider = null,
        IWorkspaceProfileProvider? workspaceProfileProvider = null,
        AgentPersonaFileProvider? personaFileProvider = null)
    {
        _memory = memory;
        _skillRuntime = skillRuntime;
        _skillPackageRegistry = skillPackageRegistry;
        _promptBuilder = promptBuilder;
        _memCache = memCache;
        _logger = logger;
        _libraryConvenience = libraryConvenience;
        _templateProvider = templateProvider;
        _workspaceProfileProvider = workspaceProfileProvider;
        _personaFileProvider = personaFileProvider;
    }

    /// <summary>
    /// 组装完整上下文，返回拼接好的系统提示词。
    /// 按 7 层模型逐层构建，每层受 Token 预算约束，超预算时触发压缩。
    /// </summary>
    public async Task<string> AssembleAsync(ContextRequest request, CancellationToken ct)
    {
        var totalBudget = request.Template.Runtime?.MaxContextTokens ?? 8000;
        var sb = new StringBuilder();
        var usedBudget = 0;

        // ── L0: 静态上下文（IDENTITY/SOUL/AGENTS）— Session 内不变，利用 KV-cache ──
        var staticCtx = await GetOrBuildStaticLayerAsync(request, ct);
        AppendLayer(sb, staticCtx);
        usedBudget += EstimateTokens(staticCtx);

        // ── 计算可用预算 ──
        var availableBudget = Math.Max(totalBudget - ReservedForReply - usedBudget, 500);
        var compactionLevel = DetermineCompactionLevel(usedBudget, totalBudget);

        // ── L3: 用户偏好（提前计算，因为预算公式需要扣除）──
        var userProfile = await GetOrBuildUserProfileAsync(request, ct);
        var userProfileTokens = EstimateTokens(userProfile);
        usedBudget += userProfileTokens;
        availableBudget = Math.Max(totalBudget - ReservedForReply - usedBudget, 500);

        // ── L1: 动态工具（5%）──
        var toolsCtx = await GetOrBuildToolsLayerAsync(request, ct);
        var toolsBudget = (int)(availableBudget * 0.05);
        var toolsTrimmed = TrimToTokenBudget(toolsCtx, toolsBudget);
        AppendLayer(sb, toolsTrimmed);
        usedBudget += EstimateTokens(toolsTrimmed);

        // ── L2: 动态 Skills（5%）──
        var skillsCtx = BuildSkillsLayer(request);
        var skillsBudget = (int)(availableBudget * 0.05);
        var skillsTrimmed = TrimToTokenBudget(skillsCtx, skillsBudget);
        AppendLayer(sb, skillsTrimmed);
        usedBudget += EstimateTokens(skillsTrimmed);

        // ── L3: 用户偏好（输出）──
        AppendLayer(sb, userProfile);

        // ── L4: 重要记忆（10%）──
        availableBudget = Math.Max(totalBudget - ReservedForReply - usedBudget, 200);
        var pinnedCtx = await GetOrBuildPinnedMemoryAsync(request, ct);
        var pinnedBudget = compactionLevel >= ContextCompactionLevel.Aggressive
            ? (int)(availableBudget * 0.05)
            : (int)(availableBudget * 0.10);
        var pinnedTrimmed = TrimToTokenBudget(pinnedCtx, pinnedBudget);
        AppendLayer(sb, pinnedTrimmed);
        usedBudget += EstimateTokens(pinnedTrimmed);

        // ── L5: 近期历史（40%）──
        availableBudget = Math.Max(totalBudget - ReservedForReply - usedBudget, 200);
        var recentBudget = (int)(availableBudget * 0.40);
        var recentCtx = BuildRecentHistoryLayer(request, compactionLevel, recentBudget);
        AppendLayer(sb, recentCtx);
        usedBudget += EstimateTokens(recentCtx);

        // ── L6: 召回记忆（25%）──
        availableBudget = Math.Max(totalBudget - ReservedForReply - usedBudget, 200);
        var recalledBudget = (int)(availableBudget * 0.25);
        var recalledCtx = await BuildRecalledMemoryLayerAsync(request, ct, compactionLevel);
        var recalledTrimmed = TrimToTokenBudget(recalledCtx, recalledBudget);
        AppendLayer(sb, recalledTrimmed);
        usedBudget += EstimateTokens(recalledTrimmed);

        // ── L7: 当前消息（15%）──
        availableBudget = Math.Max(totalBudget - ReservedForReply - usedBudget, 100);
        var currentMsgBudget = (int)(availableBudget * 0.15);
        var currentMsg = BuildCurrentMessageLayer(request, currentMsgBudget);
        AppendLayer(sb, currentMsg);

        // ── 压缩指令（如触发）──
        if (compactionLevel >= ContextCompactionLevel.Aggressive)
        {
            sb.AppendLine();
            sb.AppendLine("[SYSTEM] 当前上下文即将耗尽，请在回复的最后用标记格式总结当前工作进度和待完成事项。");
        }

        // ── RUNTIME 层（日期、Session、流式指令等）──
        AppendRuntimeLayer(sb, request);

        // ── 压缩后附注 ──
        if (compactionLevel >= ContextCompactionLevel.Aggressive)
        {
            sb.AppendLine("更早的消息可通过记忆图书馆 Tool 查询恢复。");
        }

        var result = sb.ToString();
        _logger.LogDebug(
            "[ContextPipeline] Assembled context session={Session} totalBudget={Total} usedEstimate={Used} level={Level} len={Len}",
            request.SessionId, totalBudget, usedBudget, compactionLevel, result.Length);

        return result;
    }

    // ═══════════════════════════════════════════════════════════════
    // L0: 静态上下文
    // ═══════════════════════════════════════════════════════════════

    private async Task<string> GetOrBuildStaticLayerAsync(ContextRequest request, CancellationToken ct)
    {
        if (_staticCache.TryGetValue(request.SessionId, out var cached)
            && cached.TemplateId == request.AgentTemplateId)
        {
            return cached.Content;
        }

        var content = await BuildStaticLayerAsync(request, ct);
        _staticCache[request.SessionId] = new StaticContextCache
        {
            TemplateId = request.AgentTemplateId,
            Content = content,
        };
        return content;
    }

    private async Task<string> BuildStaticLayerAsync(ContextRequest request, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var template = request.Template;

        // Persona 优先级：MD 文件 > DB > 内置模板
        AgentPersonaFiles? personaFiles = null;
        if (!string.IsNullOrWhiteSpace(request.AgentTemplateId) && _personaFileProvider is not null)
            personaFiles = _personaFileProvider.Load(request.AgentTemplateId);

        string? dbPersonaPrompt = null;
        string? dbToolsDescription = null;
        string? dbAvatarEmoji = null;
        string? dbBootstrapTemplate = null;
        string? dbDisplayNameOverride = null;

        if (!string.IsNullOrWhiteSpace(request.AgentTemplateId) && _templateProvider is not null)
        {
            try
            {
                var persona = await _templateProvider.GetPersonaAsync(
                    request.AgentTemplateId, request.WorkspaceId, ct);
                if (persona is not null)
                {
                    dbPersonaPrompt = persona.PersonaPrompt;
                    dbToolsDescription = persona.ToolsDescription;
                    dbAvatarEmoji = persona.AvatarEmoji;
                    dbBootstrapTemplate = persona.BootstrapTemplate;
                    dbDisplayNameOverride = persona.DisplayName;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "[ContextPipeline] Load persona DB failed templateId={Id}", request.AgentTemplateId);
            }
        }

        var personaPrompt = personaFiles?.Soul ?? dbPersonaPrompt;
        var toolsDescription = personaFiles?.Tools ?? dbToolsDescription;
        var bootstrapTemplate = personaFiles?.Bootstrap ?? dbBootstrapTemplate;

        // L0: IDENTITY
        sb.AppendLine("--- LAYER: IDENTITY ---");
        var displayName = string.IsNullOrWhiteSpace(dbDisplayNameOverride)
            ? (string.IsNullOrWhiteSpace(template.DisplayName) ? template.Name : template.DisplayName)
            : dbDisplayNameOverride;
        if (!string.IsNullOrWhiteSpace(displayName))
            sb.AppendLine($"Name: {displayName}");
        var effectiveAvatar = string.IsNullOrWhiteSpace(dbAvatarEmoji) ? template.AvatarEmoji : dbAvatarEmoji;
        if (!string.IsNullOrWhiteSpace(effectiveAvatar))
            sb.AppendLine($"Avatar: {effectiveAvatar}");
        if (!string.IsNullOrWhiteSpace(personaFiles?.Identity))
            sb.AppendLine(personaFiles.Identity);

        // L0: SOUL
        sb.AppendLine("--- LAYER: SOUL ---");
        var effectivePersona = string.IsNullOrWhiteSpace(personaPrompt) ? template.PersonaPrompt : personaPrompt;
        if (!string.IsNullOrWhiteSpace(effectivePersona))
            sb.AppendLine(effectivePersona);

        // L0: AGENTS
        sb.AppendLine("--- LAYER: AGENTS ---");
        if (!string.IsNullOrWhiteSpace(personaFiles?.Agents))
            sb.AppendLine(personaFiles.Agents);
        else
            sb.AppendLine(template.SystemPrompt ?? "You are a helpful assistant.");
        if (!string.IsNullOrWhiteSpace(bootstrapTemplate))
        {
            sb.AppendLine("Bootstrap:");
            sb.AppendLine(bootstrapTemplate);
        }

        return sb.ToString();
    }

    // ═══════════════════════════════════════════════════════════════
    // L1: 动态工具
    // ═══════════════════════════════════════════════════════════════

    private async Task<string> GetOrBuildToolsLayerAsync(ContextRequest request, CancellationToken ct)
    {
        var topicKey = DeriveTopicKey(request.UserMessage);
        var cacheKey = $"{request.SessionId}:{topicKey}";

        if (_toolsCache.TryGetValue(cacheKey, out var cached)
            && !IsExpired(cached.CreatedAt))
        {
            return cached.Content;
        }

        var content = await BuildToolsLayerAsync(request, ct);
        _toolsCache[cacheKey] = new DynamicToolsCache
        {
            Content = content,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        return content;
    }

    private Task<string> BuildToolsLayerAsync(ContextRequest request, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine("--- LAYER: TOOLS ---");

        var pkgs = _skillPackageRegistry.Get(request.AgentInstanceId);
        if (pkgs.Count > 0)
        {
            sb.AppendLine("Available tools via packages:");
            foreach (var pkg in pkgs)
            {
                sb.Append($"- **{pkg.Name}**");
                if (!string.IsNullOrWhiteSpace(pkg.Description))
                    sb.Append($": {pkg.Description}");
                sb.AppendLine();
            }
            // find-tool 兜底：确保至少暴露 5 个工具
            if (pkgs.Count < 5)
            {
                sb.AppendLine("(Minimal tool set; additional built-in tools: read_file, grep, bash, file_search, etc.)");
            }
        }
        else
        {
            sb.AppendLine("Standard built-in tools available (read_file, grep, bash, file_search, etc.)");
        }

        return Task.FromResult(sb.ToString());
    }

    // ═══════════════════════════════════════════════════════════════
    // L2: 动态 Skills
    // ═══════════════════════════════════════════════════════════════

    private string BuildSkillsLayer(ContextRequest request)
    {
        var sb = new StringBuilder();
        sb.AppendLine("--- LAYER: SKILLS ---");

        var pkgs = _skillPackageRegistry.Get(request.AgentInstanceId);
        if (pkgs.Count > 0)
        {
            sb.AppendLine("Available Skill Packages:");
            foreach (var pkg in pkgs)
            {
                sb.Append($"- **{pkg.Name}** (`/skills/{pkg.SkillPackageId}/`)");
                if (!string.IsNullOrWhiteSpace(pkg.Description))
                    sb.Append($": {pkg.Description}");
                sb.AppendLine();
            }
        }
        else
        {
            sb.AppendLine("(No additional skill packages loaded.)");
        }

        return sb.ToString();
    }

    // ═══════════════════════════════════════════════════════════════
    // L3: 用户偏好
    // ═══════════════════════════════════════════════════════════════

    private async Task<string> GetOrBuildUserProfileAsync(ContextRequest request, CancellationToken ct)
    {
        var cacheKey = $"user_profile:{request.SessionId}";
        if (_memCache.TryGetValue<string>(cacheKey, out var cached) && cached is not null)
            return cached;

        var profile = await _promptBuilder.LoadWorkspaceUserProfileAsync(request.WorkspaceId, ct);
        var result = new StringBuilder();
        result.AppendLine("--- LAYER: USER ---");
        if (!string.IsNullOrWhiteSpace(profile))
            result.AppendLine(profile);
        else
            result.AppendLine("(No user profile configured.)");

        var content = result.ToString();
        _memCache.Set(cacheKey, content, MemCacheExpiration);
        return content;
    }

    // ═══════════════════════════════════════════════════════════════
    // L4: 重要记忆（importance > 0.8）
    // ═══════════════════════════════════════════════════════════════

    private async Task<string> GetOrBuildPinnedMemoryAsync(ContextRequest request, CancellationToken ct)
    {
        if (_libraryConvenience is null || string.IsNullOrWhiteSpace(request.WorkspaceId))
            return "--- LAYER: PINNED ---\n(No pinned memory.)\n";

        var cacheKey = $"pinned:{request.WorkspaceId}";
        if (_memCache.TryGetValue<string>(cacheKey, out var cached) && cached is not null)
            return cached;

        var sb = new StringBuilder();
        sb.AppendLine("--- LAYER: PINNED ---");

        try
        {
            var results = await _libraryConvenience.SmartSearchAsync(
                "important critical key",
                topK: 5,
                ct);
            if (results.Count > 0)
            {
                sb.AppendLine("[IMPORTANT MEMORIES]");
                foreach (var r in results)
                {
                    sb.AppendLine($"- {r.BookTitle}: {r.Snippet}");
                }
            }
            else
            {
                sb.AppendLine("(No pinned memories.)");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[ContextPipeline] Pinned memory recall failed workspace={Workspace}", request.WorkspaceId);
            sb.AppendLine("(Memory recall unavailable.)");
        }

        var content = sb.ToString();
        _memCache.Set(cacheKey, content, MemCacheExpiration);
        return content;
    }

    // ═══════════════════════════════════════════════════════════════
    // L5: 近期历史
    // ═══════════════════════════════════════════════════════════════

    private string BuildRecentHistoryLayer(
        ContextRequest request,
        ContextCompactionLevel compactionLevel,
        int budgetTokens)
    {
        var sb = new StringBuilder();
        sb.AppendLine("--- LAYER: RECENT ---");

        if (request.SessionHistory is not { Count: > 0 })
        {
            sb.AppendLine("(No recent history.)");
            return sb.ToString();
        }

        var history = request.SessionHistory;
        var usedTokens = 0;
        var budgetChars = budgetTokens * 4; // 粗估 1 token ≈ 4 chars

        switch (compactionLevel)
        {
            case ContextCompactionLevel.None:
                // 全部保留，最多最近 20 条
                for (int i = Math.Max(0, history.Count - 20); i < history.Count; i++)
                {
                    var entry = FormatHistoryEntry(history[i]);
                    if (usedTokens + entry.Length > budgetChars && i < history.Count - 2)
                        break;
                    sb.Append(entry);
                    usedTokens += entry.Length;
                }
                break;

            case ContextCompactionLevel.Gentle:
                // 最近 10 条保留原文，更早的摘要化
                var gentleKeep = Math.Min(10, history.Count);
                for (int i = history.Count - gentleKeep; i < history.Count; i++)
                {
                    var entry = FormatHistoryEntry(history[i]);
                    if (usedTokens + entry.Length > budgetChars && i < history.Count - 2)
                        break;
                    sb.Append(entry);
                    usedTokens += entry.Length;
                }
                // 更早的消息摘要化
                if (history.Count > gentleKeep && usedTokens < budgetChars * 0.7)
                {
                    var olderSummary = SummarizeOlderHistory(history.Take(history.Count - gentleKeep).ToList());
                    sb.AppendLine($"[SUMMARY] {olderSummary}");
                }
                break;

            case ContextCompactionLevel.Aggressive:
                // 最近 2 条保留原文，更早的摘要化
                var aggressiveKeep = Math.Min(2, history.Count);
                for (int i = history.Count - aggressiveKeep; i < history.Count; i++)
                {
                    sb.Append(FormatHistoryEntry(history[i]));
                }
                if (history.Count > aggressiveKeep)
                {
                    var olderSummary = SummarizeOlderHistory(history.Take(history.Count - aggressiveKeep).ToList());
                    sb.AppendLine($"[SUMMARY] {olderSummary}");
                }
                break;
        }

        return sb.ToString();
    }

    private static string FormatHistoryEntry(ChatMessage msg)
    {
        var role = msg.Role switch
        {
            _ when msg.Role == ChatRole.User => "User",
            _ when msg.Role == ChatRole.Assistant => "Assistant",
            _ when msg.Role == ChatRole.System => "System",
            _ => msg.Role.ToString(),
        };
        var text = TruncateText(msg.Content ?? "", 500);
        return $"[{role}]: {text}\n";
    }

    private static string SummarizeOlderHistory(List<ChatMessage> olderMessages)
    {
        if (olderMessages.Count == 0) return "No prior history.";
        var userMsgs = olderMessages.Count(m => m.Role == ChatRole.User);
        var assistantMsgs = olderMessages.Count(m => m.Role == ChatRole.Assistant);
        return $"Earlier conversation: {userMsgs} user messages, {assistantMsgs} assistant replies. Topics: general discussion.";
    }

    // ═══════════════════════════════════════════════════════════════
    // L6: 召回记忆
    // ═══════════════════════════════════════════════════════════════

    private async Task<string> BuildRecalledMemoryLayerAsync(
        ContextRequest request,
        CancellationToken ct,
        ContextCompactionLevel compactionLevel)
    {
        var sb = new StringBuilder();
        sb.AppendLine("--- LAYER: RECALLED ---");

        if (_libraryConvenience is null || string.IsNullOrWhiteSpace(request.WorkspaceId))
        {
            sb.AppendLine("(No memory library available.)");
            return sb.ToString();
        }

        try
        {
            var results = await _libraryConvenience.SmartSearchAsync(
                request.UserMessage,
                topK: compactionLevel >= ContextCompactionLevel.Aggressive ? 5 : 10,
                ct);

            if (results.Count == 0)
            {
                sb.AppendLine("(No relevant memories found.)");
                return sb.ToString();
            }

            sb.AppendLine("[RECALLED MEMORIES]");
            foreach (var r in results)
            {
                switch (compactionLevel)
                {
                    case ContextCompactionLevel.Aggressive:
                        // 高分保留原文，其余摘要
                        if (r.Score >= 0.8)
                            sb.AppendLine($"- **{r.BookTitle}** (score:{r.Score:F2}): {TruncateText(r.Snippet, 200)}");
                        else
                            sb.AppendLine($"- **{r.BookTitle}**: {TruncateText(r.Snippet, 80)}");
                        break;
                    default:
                        sb.AppendLine($"- **{r.BookTitle}** (score:{r.Score:F2}): {TruncateText(r.Snippet, 300)}");
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[ContextPipeline] Recalled memory failed workspace={Workspace}", request.WorkspaceId);
            sb.AppendLine("(Memory recall temporary unavailable.)");
        }

        return sb.ToString();
    }

    // ═══════════════════════════════════════════════════════════════
    // L7: 当前消息
    // ═══════════════════════════════════════════════════════════════

    private static string BuildCurrentMessageLayer(ContextRequest request, int budgetTokens)
    {
        var sb = new StringBuilder();
        sb.AppendLine("--- LAYER: CURRENT ---");
        var msg = request.UserMessage;
        var maxChars = budgetTokens * 4;
        sb.AppendLine(TruncateText(msg, maxChars));
        return sb.ToString();
    }

    // ═══════════════════════════════════════════════════════════════
    // RUNTIME 层
    // ═══════════════════════════════════════════════════════════════

    private void AppendRuntimeLayer(StringBuilder sb, ContextRequest request)
    {
        sb.AppendLine("--- LAYER: RUNTIME ---");
        sb.AppendLine($"Date: {DateTimeOffset.Now:yyyy-MM-dd}");
        sb.AppendLine($"Session: {request.SessionId}");

        if (request.ForStreaming)
        {
            sb.AppendLine("Respond directly to the user in Markdown.");
            sb.AppendLine("Do not output JSON control structures such as status/tool/meta.");
            sb.AppendLine("Use concise explanations, fenced code blocks, Markdown tables, and LaTeX when helpful.");
            if (request.Capability?.AllowedToolNames is { Count: > 0 })
                sb.AppendLine("If a task requires tools, explain the limitation briefly instead of emitting tool-call JSON.");
        }
        else
        {
            sb.Append(_skillRuntime.BuildLoopInstructions(request.Capability));
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // 话题切换检测（零成本）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 判断是否发生话题切换。
    /// 策略：前后消息的简单关键词重叠率判断。
    /// </summary>
    public bool IsTopicSwitch(string? previousMessage, string currentMessage)
    {
        if (string.IsNullOrWhiteSpace(previousMessage))
            return false;

        var prevWords = TokenizeSimple(previousMessage);
        var currWords = TokenizeSimple(currentMessage);

        if (prevWords.Count == 0 || currWords.Count == 0)
            return false;

        // 关键词重叠数
        var overlap = prevWords.Intersect(currWords).Count();
        // 重叠率 < 0.15 或重叠词 < 2 视为话题切换
        var overlapRatio = (double)overlap / Math.Min(prevWords.Count, currWords.Count);
        return overlapRatio < 0.15 && overlap < 2;
    }

    /// <summary>
    /// 简单分词：按空格/标点拆分，取长度 ≥ 3 的词，去重转小写。
    /// </summary>
    private static HashSet<string> TokenizeSimple(string text)
    {
        var words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text)) return words;

        var span = text.AsSpan();
        var start = 0;
        for (int i = 0; i <= span.Length; i++)
        {
            if (i == span.Length || !char.IsLetterOrDigit(span[i]))
            {
                if (i - start >= 3)
                    words.Add(span.Slice(start, i - start).ToString());
                start = i + 1;
            }
        }
        return words;
    }

    private static string DeriveTopicKey(string message)
    {
        var words = TokenizeSimple(message);
        if (words.Count == 0) return "default";
        // 取前 5 个关键词做 topic key
        return string.Join(":", words.OrderBy(w => w).Take(5));
    }

    // ═══════════════════════════════════════════════════════════════
    // 缓存失效辅助
    // ═══════════════════════════════════════════════════════════════

    /// <summary>使指定 Session 的所有缓存失效。</summary>
    public void InvalidateSession(string sessionId)
    {
        _staticCache.TryRemove(sessionId, out _);
        // 清理 tools 缓存中该 session 的所有条目
        var prefix = $"{sessionId}:";
        foreach (var key in _toolsCache.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)))
            _toolsCache.TryRemove(key, out _);
    }

    // ═══════════════════════════════════════════════════════════════
    // Token 预算与压缩
    // ═══════════════════════════════════════════════════════════════

    /// <summary>粗估 Token 数：约 4 字符 ≈ 1 Token。</summary>
    private static int EstimateTokens(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        return Math.Max(1, text.Length / 4);
    }

    private static ContextCompactionLevel DetermineCompactionLevel(int usedBudget, int totalBudget)
    {
        if (totalBudget <= 0) return ContextCompactionLevel.Aggressive;
        var ratio = (double)usedBudget / totalBudget;
        if (ratio >= CompactionThreshold) return ContextCompactionLevel.Aggressive;
        if (ratio >= GentleThreshold) return ContextCompactionLevel.Gentle;
        return ContextCompactionLevel.None;
    }

    private static string TrimToTokenBudget(string text, int maxTokens)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var maxChars = maxTokens * 4;
        if (text.Length <= maxChars) return text;
        return TruncateText(text, maxChars) + "\n[TRUNCATED – context budget exceeded]";
    }

    private static string TruncateText(string text, int maxChars)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxChars) return text;
        return text[..maxChars] + "...";
    }

    private static bool IsExpired(DateTimeOffset createdAt)
    {
        return DateTimeOffset.UtcNow - createdAt > MemCacheExpiration;
    }

    private static void AppendLayer(StringBuilder sb, string content)
    {
        if (!string.IsNullOrWhiteSpace(content))
        {
            sb.Append(content);
            if (!content.EndsWith('\n'))
                sb.AppendLine();
        }
    }
}

// ═══════════════════════════════════════════════════════════════
// 支持类型
// ═══════════════════════════════════════════════════════════════

/// <summary>上下文管道请求参数。</summary>
public sealed record ContextRequest
{
    public AgentTemplateDefinition Template { get; init; } = null!;
    public string WorkspaceId { get; init; } = string.Empty;
    public string SessionId { get; init; } = string.Empty;
    public string AgentTemplateId { get; init; } = string.Empty;
    public string UserMessage { get; init; } = string.Empty;
    public CapabilityPolicy? Capability { get; init; }
    public string AgentInstanceId { get; init; } = string.Empty;
    public bool ForStreaming { get; init; }
    public bool IsFirstMessage { get; init; }
    public string? PreviousMessage { get; init; }
    public IReadOnlyList<ChatMessage> SessionHistory { get; init; } = Array.Empty<ChatMessage>();
}

/// <summary>上下文压缩级别。</summary>
public enum ContextCompactionLevel
{
    /// <summary>budget &lt; 60%，无需压缩。</summary>
    None,
    /// <summary>60%-80%：摘要化远期历史。</summary>
    Gentle,
    /// <summary>&gt;80%：触发主代理自总结 + 大幅压缩。</summary>
    Aggressive,
}

/// <summary>静态上下文缓存条目。</summary>
internal sealed class StaticContextCache
{
    public string TemplateId { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
}

/// <summary>动态工具/技能缓存条目。</summary>
internal sealed class DynamicToolsCache
{
    public string Content { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; }
}
