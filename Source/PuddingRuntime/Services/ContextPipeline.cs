using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using PuddingCode.Abstractions;
using PuddingCode.Agents;
using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingCode.Observability;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingCode.Tools;
using PuddingMemoryEngine.Services;
using PuddingRuntime.Services.Skills;
using PuddingRuntime.Services.TaskPlanning;
using PuddingRuntime.Services.Tools;
using PuddingRuntime.Models;

namespace PuddingRuntime.Services;

/// <summary>
/// 上下文管道——将上下文拼装建模为 Token 预算分配问题。
/// 按缓存命中率分层：不变的放前面（利用 LLM KV-cache），易变的放后面。
/// 分层模型：STATIC → TOOLS → SKILLS → USER → PINNED → HISTORICAL-CONTEXT → AUGMENT → TASK-PLANNING → RUNTIME → INBOUND → 当前消息。
/// </summary>
public sealed partial class ContextPipeline
{
    private readonly IMemoryEngine _memory;
    private readonly SkillRuntime _skillRuntime;
    private readonly IPuddingToolRegistry? _toolRegistry;
    private readonly AgentSkillPackageRegistry _skillPackageRegistry;
    private readonly IMemoryLibraryConvenience? _libraryConvenience;
    private readonly IMemoryRecallService? _recallService;
    private readonly ISubconsciousOrchestrator? _orchestrator;
    private readonly IAgentTemplateProvider? _templateProvider;
    private readonly IWorkspaceProfileProvider? _workspaceProfileProvider;
    private readonly AgentPersonaFileProvider? _personaFileProvider;
    private readonly SystemPromptBuilder _promptBuilder;
    private readonly IMemoryCache _memCache;
    private readonly ILogger<ContextPipeline> _logger;
    private readonly ContextAssemblyStore _contextAssemblyStore;
    private readonly IExecutionEnvironmentProvider _envProvider;
    private readonly WorkspaceAgentsContextBuilder? _workspaceAgentsContextBuilder;
    private readonly TaskPlannerContextBuilder? _taskPlannerContextBuilder;
    private readonly ITelemetryMetricSink? _telemetrySink;
    private readonly ILLMConfigResolver? _llmConfigResolver;
    private readonly AgentSkillFileService? _agentSkillFileService;
    private readonly AgentMemorySummaryContextBuilder? _agentMemorySummaryContextBuilder;
    private readonly AgentLogRecallService? _agentLogRecallService;
    private readonly IImportantMemoryService? _importantMemory;
    private readonly IUserPreferenceService? _userPreferenceService;
    private readonly PuddingDataPaths _dataPaths;
        private readonly CroppedLayersProvider? _croppedLayersProvider;
    private readonly SubconsciousRecallPipeline? _subconsciousRecallPipeline;

    // 静态层缓存：sessionId → StaticContextCache
    private readonly ConcurrentDictionary<string, StaticContextCache> _staticCache = new();

    // 环境层缓存：workspaceId → EnvironmentLayerCache
    private readonly ConcurrentDictionary<string, EnvironmentLayerCache> _envCache = new();

    // Token 预算常量
    private const int ReservedForReply = 4096;
    private const double CompactionThreshold = 0.8;
    private const double GentleThreshold = 0.6;

    // RECENT 层滑动窗口常量
    private const int DefaultRecentMessageCount = 35;

    // L6-CONTEXT-AUGMENT 硬上限：防止日志召回层无限膨胀（历史最大 55,905 tokens）
    private const int MaxContextAugmentTokens = 5000;

    // 内存缓存过期
    private static readonly TimeSpan MemCacheExpiration = TimeSpan.FromSeconds(30);

    public ContextPipeline(
        IMemoryEngine memory,
        SkillRuntime skillRuntime,
        AgentSkillPackageRegistry skillPackageRegistry,
        SystemPromptBuilder promptBuilder,
        IMemoryCache memCache,
        ContextAssemblyStore contextAssemblyStore,
        ILogger<ContextPipeline> logger,
        IExecutionEnvironmentProvider envProvider,
        IMemoryLibraryConvenience? libraryConvenience = null,
        IMemoryRecallService? recallService = null,
        ISubconsciousOrchestrator? orchestrator = null,
        IAgentTemplateProvider? templateProvider = null,
        IWorkspaceProfileProvider? workspaceProfileProvider = null,
        AgentPersonaFileProvider? personaFileProvider = null,
        WorkspaceAgentsContextBuilder? workspaceAgentsContextBuilder = null,
        TaskPlannerContextBuilder? taskPlannerContextBuilder = null,
        ITelemetryMetricSink? telemetrySink = null,
        IPuddingToolRegistry? toolRegistry = null,
        ILLMConfigResolver? llmConfigResolver = null,
        AgentSkillFileService? agentSkillFileService = null,
        AgentMemorySummaryContextBuilder? agentMemorySummaryContextBuilder = null,
        AgentLogRecallService? agentLogRecallService = null,
        IImportantMemoryService? importantMemory = null,
        PuddingDataPaths? dataPaths = null,
                CroppedLayersProvider? croppedLayersProvider = null,
        SubconsciousRecallPipeline? subconsciousRecallPipeline = null,
        IUserPreferenceService? userPreferenceService = null)
    {
        _memory = memory;
        _skillRuntime = skillRuntime;
        _toolRegistry = toolRegistry;
        _skillPackageRegistry = skillPackageRegistry;
        _promptBuilder = promptBuilder;
        _memCache = memCache;
        _contextAssemblyStore = contextAssemblyStore;
        _logger = logger;
        _envProvider = envProvider;
        _libraryConvenience = libraryConvenience;
        _recallService = recallService;
        _orchestrator = orchestrator;
        _templateProvider = templateProvider;
        _workspaceProfileProvider = workspaceProfileProvider;
        _personaFileProvider = personaFileProvider;
        _workspaceAgentsContextBuilder = workspaceAgentsContextBuilder;
        _taskPlannerContextBuilder = taskPlannerContextBuilder;
        _telemetrySink = telemetrySink;
        _llmConfigResolver = llmConfigResolver;
        _agentSkillFileService = agentSkillFileService;
        _agentMemorySummaryContextBuilder = agentMemorySummaryContextBuilder;
        _agentLogRecallService = agentLogRecallService;
        _importantMemory = importantMemory;
        _userPreferenceService = userPreferenceService;
        _croppedLayersProvider = croppedLayersProvider;
                _subconsciousRecallPipeline = subconsciousRecallPipeline;
        _dataPaths = dataPaths ?? PuddingDataPaths.FromRoot(
            Environment.GetEnvironmentVariable("PUDDING_DATA_ROOT") ?? "data");
    }

    /// <summary>
    /// 组装完整上下文，返回拼接好的系统提示词与各层 Token 占比快照。
    /// 按 7 层模型逐层构建，每层受 Token 预算约束，超预算时触发压缩。
    /// </summary>
        // Layer provider methods (L0-L6) extracted to ContextPipelineLayers.cs
        // See ContextPipelineLayers.cs for all layer-building methods.
        // ===============================================================


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
    }

    /// <summary>使指定 Workspace 的环境层缓存失效。</summary>
    public void InvalidateEnvironmentCache(string workspaceId)
    {
        var prefix = $"{workspaceId}:";
        foreach (var key in _envCache.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)))
            _envCache.TryRemove(key, out _);
    }

    // ═══════════════════════════════════════════════════════════════
    // Token 预算与压缩
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Token count used for context budgeting.</summary>
    private static int EstimateTokens(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        return Math.Max(1, ContextUsageSnapshotStore.CountTokens(text));
    }

    private static ContextPipelineCompactionLevel DetermineCompactionLevel(int usedBudget, int totalBudget)
    {
        if (totalBudget <= 0) return ContextPipelineCompactionLevel.Aggressive;
        var ratio = (double)usedBudget / totalBudget;
        if (ratio >= CompactionThreshold) return ContextPipelineCompactionLevel.Aggressive;
        if (ratio >= GentleThreshold) return ContextPipelineCompactionLevel.Gentle;
        return ContextPipelineCompactionLevel.None;
    }

    private static string TrimToTokenBudget(string text, int maxTokens)
    {
        if (string.IsNullOrEmpty(text)) return text;
        if (maxTokens <= 0) return string.Empty;
        if (EstimateTokens(text) <= maxTokens) return text;

        const string suffix = "\n[TRUNCATED – context budget exceeded]";
        if (EstimateTokens(suffix) > maxTokens)
            return string.Empty;

        var low = 0;
        var high = text.Length;
        while (low < high)
        {
            var mid = low + ((high - low + 1) / 2);
            if (EstimateTokens(text[..mid] + suffix) <= maxTokens)
                low = mid;
            else
                high = mid - 1;
        }

        return text[..low] + suffix;
    }

    /// <summary>
    /// 从层内容中提取去重键并注册到去重集。
    /// 识别格式：`- (Source: library) ...` 中的内容摘要。
    /// </summary>
    private static void RegisterDedupKeys(string content, HashSet<string> dedupKeys)
    {
        var lines = content.Split('\n');
        string? currentKey = null;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            // 匹配 "- **Title**: Content" 或 "- Content (Source: xxx)"
            if (line.StartsWith("- "))
            {
                // 提取内容作为去重键（前 120 字符 + 前 60 字符的 hash）
                var text = line[2..].Trim();
                if (text.Length > 120)
                    text = text[..120];
                currentKey = text;
                dedupKeys.Add(currentKey);
            }
            else if (currentKey is not null && line.Length > 0 && !line.StartsWith("- ") && !line.StartsWith("---"))
            {
                // 续行追加到当前键
                dedupKeys.Add(currentKey + "|" + line[..Math.Min(60, line.Length)]);
            }
        }
    }

    /// <summary>
    /// 从内容中移除与去重集重复的行。
    /// 保留第一行标题（--- LAYER: xxx ---）和空行/分隔线不参与去重。
    /// </summary>
    private static string FilterDedupContent(string content, HashSet<string> dedupKeys)
    {
        if (string.IsNullOrWhiteSpace(content) || dedupKeys.Count == 0)
            return content;

        var lines = content.Split('\n');
        var sb = new StringBuilder(content.Length);
        var skipCurrent = false;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            // 标题行和分隔线始终保留
            if (line.StartsWith("---") || line.StartsWith("```") || line.StartsWith("["))
            {
                skipCurrent = false;
                sb.AppendLine(raw);
                continue;
            }
            // 空行始终保留
            if (string.IsNullOrEmpty(line))
            {
                skipCurrent = false;
                sb.AppendLine();
                continue;
            }
            // 列表条目：检查是否与已知键重复
            if (line.StartsWith("- "))
            {
                var text = line[2..].Trim();
                if (text.Length > 120) text = text[..120];
                skipCurrent = dedupKeys.Contains(text);
                if (!skipCurrent)
                {
                    dedupKeys.Add(text);
                    sb.AppendLine(raw);
                }
            }
            else if (!skipCurrent)
            {
                sb.AppendLine(raw);
            }
            // 如果 skipCurrent=true，跳过续行
        }
        return sb.ToString().Trim();
    }

    private static string TruncateText(string text, int maxChars)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxChars) return text;
        return text[..maxChars] + "...";
    }

    private static string BuildPreview(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return string.Empty;

        var normalized = content.Replace("\r", " ").Replace("\n", " ").Trim();
        return normalized.Length <= 200 ? normalized : normalized[..200];
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

    /// <summary>
    /// 将记忆片段按来源层格式化为上下文文本。
    /// </summary>
    private static string FormatCropSnippets(List<MemorySnippet> snippets, string source)
    {
        var relevant = snippets
            .Where(s => string.Equals(s.Source, source, StringComparison.OrdinalIgnoreCase))
            .Select(s => s.IsSpeculative ? $"[推测] {s.Text}" : s.Text)
            .ToList();

        if (relevant.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine($"--- LAYER: {source} (CROPPED) ---");
        foreach (var text in relevant)
            sb.AppendLine(text);
        return sb.ToString();
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
    public RuntimeTraceContext? Trace { get; init; }
    public string? TaskPlanId { get; init; }
    public string? TaskNodeId { get; init; }
    public string? ParentTaskNodeId { get; init; }
    public int? DelegationDepth { get; init; }
    public int? MaxDelegationDepth { get; init; }
    public string? RoleInPlan { get; init; }
    public bool? AllowSubDelegation { get; init; }
    public bool? AllowAgentCreation { get; init; }
    public string? AssignedObjective { get; init; }
    public string? ExpectedOutputContract { get; init; }
    /// <summary>ADR-042: 入站消息发送者类型（agent/user/system），用于构建 INBOUND-MESSAGE-CONTEXT。</summary>
    public string? InboundSourceKind { get; init; }
    /// <summary>ADR-042: 入站消息发送者 ID。</summary>
    public string? InboundSourceId { get; init; }
        /// <summary>ADR-042: 入站消息发送者名称。</summary>
    public string? InboundSourceName { get; init; }
    /// <summary>从父代理 Fork 并剪枝后的上下文快照。非空时 ContextPipeline 注入 INHERITED-CONTEXT 层。</summary>
    public string? ParentContextSnapshot { get; init; }
}

/// <summary>上下文压缩级别。</summary>
public enum ContextPipelineCompactionLevel
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

/// <summary>环境层缓存条目（跨 Session 复用，键 = workspaceId + 环境指纹）。</summary>
internal sealed class EnvironmentLayerCache
{
    public string Content { get; init; } = string.Empty;
}

/// <summary>上下文组装结果——包含系统提示词和分层 Token 统计。</summary>
public sealed record ContextAssemblyResult(
    string SystemPrompt,
    int TotalBudget,
    int UsedTokens,
    IReadOnlyList<ContextLayerSnapshot> Layers);

/// <summary>单层上下文 Token 快照。</summary>
public sealed record ContextLayerSnapshot(
    string LayerName,
    int EstimatedTokens,
    double Percentage);
