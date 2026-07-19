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
using PuddingPlatform.Services;
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
public sealed class ContextPipeline
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
        SubconsciousRecallPipeline? subconsciousRecallPipeline = null)
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
        _croppedLayersProvider = croppedLayersProvider;
                _subconsciousRecallPipeline = subconsciousRecallPipeline;
        _dataPaths = dataPaths ?? PuddingDataPaths.FromRoot(
            Environment.GetEnvironmentVariable("PUDDING_DATA_ROOT") ?? "data");
    }

    /// <summary>
    /// 组装完整上下文，返回拼接好的系统提示词与各层 Token 占比快照。
    /// 按 7 层模型逐层构建，每层受 Token 预算约束，超预算时触发压缩。
    /// </summary>
    public async Task<ContextAssemblyResult> AssembleAsync(ContextRequest request, CancellationToken ct)
    {
        var totalBudget = request.Template.Runtime?.MaxContextTokens ?? 0;
        var sb = new StringBuilder();
        var usedBudget = 0;
        var layers = new List<ContextLayerSnapshot>();
        var layerInfos = new List<ContextLayerInfo>();
        var assemblyStartedAt = DateTimeOffset.UtcNow;
        var assemblySw = System.Diagnostics.Stopwatch.StartNew();

        var budget = new ContextBudgetAllocator(_logger);
        var ctx = new ContextBuildContext
        {
            Request = request,
            TotalBudget = totalBudget,
        };

        try
        {
        // ── L0: 静态上下文（IDENTITY/SOUL/AGENTS）— Session 内不变，利用 KV-cache ──
        var staticCtx = await GetOrBuildStaticLayerAsync(request, ct);
        RecordLayer(sb, staticCtx, "静态上下文", "L0-STATIC", ref usedBudget, totalBudget, layers, layerInfos);

        // ── L0-ENVIRONMENT: 运行环境不变量（OS/运行时/shell）— 低变化，独立于 workspace 路径 ──
        var envCtx = GetOrBuildEnvironmentLayer(request);
        RecordLayer(sb, envCtx, "运行环境不变量", "L0-ENVIRONMENT", ref usedBudget, totalBudget, layers, layerInfos);

        // ── L0-AGENTS-ROSTER: 当前工作区可见 Agent 名册，用于 agent-to-agent 消息寻址 ──
        var workspaceAgentsCtx = _workspaceAgentsContextBuilder is null
            ? "--- LAYER: WORKSPACE AGENTS ---\n(No workspace agents available.)\n"
            : await _workspaceAgentsContextBuilder.BuildAsync(request.WorkspaceId, "default", ct);
        RecordLayer(sb, workspaceAgentsCtx, "工作区 Agents", "L0-AGENTS-ROSTER", ref usedBudget, totalBudget, layers, layerInfos);

        // ── L0-TASK-PLANNING: 系统生成的任务树位置与委派约束 ──
        var taskPlanningCtx = _taskPlannerContextBuilder is null
            ? string.Empty
            : await _taskPlannerContextBuilder.BuildAsync(request, ct);
        var taskPlanningTokens = EstimateTokens(taskPlanningCtx);
        usedBudget += taskPlanningTokens;
        if (!string.IsNullOrEmpty(taskPlanningCtx))
        {
            AppendLayer(sb, taskPlanningCtx);
            layers.Add(new ContextLayerSnapshot("任务规划约束", taskPlanningTokens, (double)taskPlanningTokens / totalBudget * 100));
            layerInfos.Add(new ContextLayerInfo
            {
                LayerName = "L0-TASK-PLANNING",
                TokenCount = taskPlanningTokens,
                ContentPreview = BuildPreview(taskPlanningCtx),
            });
        }

        // ── L0-INBOUND-MESSAGE-CONTEXT ──
        var inboundCtx = BuildInboundMessageContextLayer(request);
        var inboundTokens = EstimateTokens(inboundCtx);
        usedBudget += inboundTokens;
        ctx.InboundTokens = inboundTokens;

        // ── 更新预算上下文 ──
        ctx.UsedBudget = usedBudget;
        budget.Initialize(ctx);
        var availableBudget = ctx.AvailableBudget;
        var compactionLevel = ctx.CompactionLevel;

        // ── L3: 用户偏好 ──
        var userProfile = await GetOrBuildUserProfileAsync(request, ct);
        var userProfileTokens = EstimateTokens(userProfile);
        usedBudget += userProfileTokens;
        ctx.UsedBudget = usedBudget;
        budget.UpdateAvailable(ctx);

                // ── L1: 动态工具（5%）──
        var toolsCtx = await BuildToolsLayerAsync(request, ct);
        var toolsBudget = budget.AllocatePercent(ctx, 0.05);
        var toolsTrimmed = TrimToTokenBudget(toolsCtx, toolsBudget);
        RecordLayer(sb, toolsTrimmed, "动态工具", "L1-TOOLS", ref usedBudget, totalBudget, layers, layerInfos);

        // ── L2: 动态 Skills（5%）──
        var skillsCtx = await BuildSkillsLayerAsync(request, ct);
        var skillsBudget = budget.AllocatePercent(ctx, 0.05);
        var skillsTrimmed = TrimToTokenBudget(skillsCtx, skillsBudget);
                RecordLayer(sb, skillsTrimmed, "动态技能", "L2-SKILLS", ref usedBudget, totalBudget, layers, layerInfos);

        // ── L2-INHERITED: 父代理上下文快照（Session Fork）──
        if (!string.IsNullOrWhiteSpace(request.ParentContextSnapshot))
        {
            var inheritedBudget = budget.AllocatePercent(ctx, 0.20);
            var inheritedTrimmed = TrimToTokenBudget(request.ParentContextSnapshot, inheritedBudget);
            RecordLayer(sb, inheritedTrimmed, "继承上下文", "L2-INHERITED", ref usedBudget, totalBudget, layers, layerInfos);
        }

        // ── L2-MEMORY-SUMMARY ──
        var memorySummaryCtx = _agentMemorySummaryContextBuilder is null
            ? string.Empty
            : await _agentMemorySummaryContextBuilder.BuildAsync(
                request.SessionId, request.AgentInstanceId, request.IsFirstMessage, ct);
        var hasMemorySummary = !string.IsNullOrWhiteSpace(memorySummaryCtx);
        var memorySummaryTokens = hasMemorySummary ? EstimateTokens(memorySummaryCtx) : 0;
        usedBudget += memorySummaryTokens;
        if (hasMemorySummary)
        {
            AppendLayer(sb, memorySummaryCtx);
            layers.Add(new ContextLayerSnapshot("历史上下文", memorySummaryTokens, (double)memorySummaryTokens / totalBudget * 100));
            layerInfos.Add(new ContextLayerInfo
            {
                LayerName = "HISTORICAL-CONTEXT",
                TokenCount = memorySummaryTokens,
                ContentPreview = BuildPreview(memorySummaryCtx),
            });
            _logger.LogInformation(
                "[ContextPipeline:MemoryRecall] historicalContextInjected agent={AgentId} isFirst={IsFirst} tokens={Tokens}",
                request.AgentInstanceId, request.IsFirstMessage, memorySummaryTokens);
        }
        else if (request.IsFirstMessage)
        {
            _logger.LogInformation(
                "[ContextPipeline:MemoryRecall] historicalContextEmpty agent={AgentId}",
                request.AgentInstanceId);
        }

        // ── L3-WORKSPACE-ENVIRONMENT ──
        var workspaceEnvironmentCtx = BuildWorkspaceEnvironmentLayer(request);
        RecordLayer(sb, workspaceEnvironmentCtx, "工作区环境", "L3-WORKSPACE-ENVIRONMENT", ref usedBudget, totalBudget, layers, layerInfos);

        // ── L3: 用户偏好 ──
        AppendLayer(sb, userProfile);
        layers.Add(new ContextLayerSnapshot("用户偏好", userProfileTokens, (double)userProfileTokens / totalBudget * 100));
        layerInfos.Add(new ContextLayerInfo
        {
            LayerName = "L3-USER",
            TokenCount = userProfileTokens,
            ContentPreview = BuildPreview(userProfile),
        });

        // ── L4: 重要记忆（10%）──
        ctx.UsedBudget = usedBudget;
        budget.UpdateAvailable(ctx);
        var pinnedCtx = await GetOrBuildPinnedMemoryAsync(request, ct);
        var pinnedPercent = compactionLevel >= ContextPipelineCompactionLevel.Aggressive ? 0.05 : 0.10;
        var pinnedBudget = budget.AllocatePercent(ctx, pinnedPercent);
        var pinnedTrimmed = TrimToTokenBudget(pinnedCtx, pinnedBudget);
        RecordLayer(sb, pinnedTrimmed, "重要记忆", "L4-PINNED", ref usedBudget, totalBudget, layers, layerInfos);

        // ═══════════════════════════════════════════════════════════════
        // 收集可变层原始内容，准备 Flash 裁剪
        // L6-CONTEXT-AUGMENT 由 SubconsciousRecallPipeline 生成，不参与 Flash 裁剪
        // ═══════════════════════════════════════════════════════════════
        var cropBundles = new List<RawContentBundle>();

        // ── L6-CONTEXT-AUGMENT：潜意识召回管道（替代原 RECALLED + AGENT-LOG-RECALL）──
        string? contextAugmentStr = null;
        var contextAugmentLayerName = "L6-CONTEXT-AUGMENT";
        int contextAugmentTokens = 0;
        if (_subconsciousRecallPipeline is not null)
        {
            try
            {
                contextAugmentStr = await _subconsciousRecallPipeline.RunAsync(
                    request.UserMessage ?? "",
                    request.WorkspaceId,
                    request.AgentInstanceId ?? "",
                    request.IsFirstMessage,
                    ct);
                if (!string.IsNullOrWhiteSpace(contextAugmentStr))
                {
                    contextAugmentTokens = EstimateTokens(contextAugmentStr);
                    usedBudget += contextAugmentTokens;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ContextPipeline] SubconsciousRecallPipeline failed, skip context augment");
            }
        }
        else if (_agentLogRecallService is not null
                 && !string.IsNullOrWhiteSpace(request.AgentInstanceId)
                 && !string.IsNullOrWhiteSpace(request.UserMessage))
        {
            try
            {
                contextAugmentStr = await BuildLegacyAgentLogRecallLayerAsync(request, ct);
                if (!string.IsNullOrWhiteSpace(contextAugmentStr))
                {
                    contextAugmentLayerName = "L6-AGENT-LOG-RECALL";
                    contextAugmentTokens = EstimateTokens(contextAugmentStr);
                    usedBudget += contextAugmentTokens;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ContextPipeline] AgentLogRecallService failed, skip recall layer");
            }
        }

        // ── 调用完整裁剪管线（Flash 裁剪 + 时间聚类 + 关联度验证）──
        List<MemorySnippet>? croppedSnippets = null;
        if (_croppedLayersProvider is not null && cropBundles.Count > 0)
        {
            var pipelineResult = await _croppedLayersProvider.RunFullPipelineAsync(
                cropBundles,
                request.UserMessage,
                request.WorkspaceId,
                request.SessionId,
                request.AgentTemplateId,
                request.AgentInstanceId,
                request.IsFirstMessage,
                ct);

            croppedSnippets = pipelineResult.Snippets;
        }

        // ═══════════════════════════════════════════════════════════════
        // 注入裁剪结果（或降级回原始内容）
        // ═══════════════════════════════════════════════════════════════

        // ── L6-CONTEXT-AUGMENT 注入：潜意识召回管道输出（替代原 RECALLED + AGENT-LOG-RECALL）──
        if (!string.IsNullOrWhiteSpace(contextAugmentStr))
        {
            AppendLayer(sb, contextAugmentStr);
            layers.Add(new ContextLayerSnapshot("上下文增强", contextAugmentTokens, (double)contextAugmentTokens / totalBudget * 100));
            layerInfos.Add(new ContextLayerInfo
            {
                LayerName = contextAugmentLayerName,
                TokenCount = contextAugmentTokens,
                ContentPreview = BuildPreview(contextAugmentStr),
            });
        }

        // ── RUNTIME 层（日期、Session、流式指令等）— 在 CURRENT 之前以利用日期稳定性 ──
        var runtimeLen = sb.Length;
        AppendRuntimeLayer(sb, request);
        var runtimeTokens = EstimateTokens(sb.ToString()) - EstimateTokens(sb.ToString(0, runtimeLen));
        layers.Add(new ContextLayerSnapshot("运行时指令", Math.Max(0, runtimeTokens), (double)Math.Max(0, runtimeTokens) / totalBudget * 100));
        layerInfos.Add(new ContextLayerInfo
        {
            LayerName = "L8-RUNTIME",
            TokenCount = Math.Max(0, runtimeTokens),
            ContentPreview = BuildPreview(sb.ToString(runtimeLen, sb.Length - runtimeLen)),
        });

        // ── INBOUND-MESSAGE-CONTEXT: agent-to-agent 消息上下文 ——
        // 后移至 RUNTIME 之后：多 Agent 环境下高频变化，放末尾仅影响自身+CURRENT（~0.6K）
        if (!string.IsNullOrEmpty(inboundCtx))
        {
            AppendLayer(sb, inboundCtx);
            layers.Add(new ContextLayerSnapshot("入站消息上下文", inboundTokens, (double)inboundTokens / totalBudget * 100));
            layerInfos.Add(new ContextLayerInfo
            {
                LayerName = "L9-INBOUND",
                TokenCount = inboundTokens,
                ContentPreview = BuildPreview(inboundCtx),
            });
        }

        // ── L7: 当前消息（15%）──
        ctx.UsedBudget = usedBudget;
        budget.UpdateAvailable(ctx);
        var currentMsgBudget = budget.AllocatePercentWithFloor(ctx, 0.15, 100);
        var currentMsg = BuildCurrentMessageLayer(request, currentMsgBudget);
        RecordLayer(sb, currentMsg, "当前消息", "L9-CURRENT", ref usedBudget, totalBudget, layers, layerInfos);

        // ── 压缩指令（如触发）──
        if (compactionLevel >= ContextPipelineCompactionLevel.Aggressive)
        {
            sb.AppendLine();
            sb.AppendLine("[SYSTEM] 当前上下文即将耗尽，请在回复的最后用标记格式总结当前工作进度和待完成事项。");
        }

        // ── 压缩后附注 ──
        if (compactionLevel >= ContextPipelineCompactionLevel.Aggressive)
        {
            sb.AppendLine("更早的消息可通过记忆图书馆 Tool 召回摘要知识；需要核实会话内容时使用 query_session_logs 默认查询消息转录，只有诊断工具调用/事件证据时才显式读取 raw events。");
        }

                var result = sb.ToString();
        var estimatedTotalTokens = layerInfos.Sum(x => x.TokenCount);

        // 标记静态层并截取 FullContent（在存储前处理，避免修改 RecordLayer 签名）
        MarkStaticLayers(layerInfos, result);

        // P2 KV-cache 指纹校验：拼接所有静态层 FullContent 计算 SHA-256 hex
        var staticLayersFingerprint = ComputeStaticLayersFingerprint(layerInfos);

        var recentMessages = PruneSessionMessages(request.SessionHistory, maxMessages: 20);
        _contextAssemblyStore.Set(new ContextAssemblySnapshot
        {
            SessionId = request.SessionId,
            AssembledAt = DateTimeOffset.UtcNow,
            Layers = layerInfos,
            TotalTokens = estimatedTotalTokens,
            RecentMessages = recentMessages,
            StaticLayersFingerprint = staticLayersFingerprint,
        });

        assemblySw.Stop();
        await RecordContextAssemblyMetricAsync(
            request,
            assemblyStartedAt,
            assemblySw.ElapsedMilliseconds,
            TelemetryMetricStatuses.Succeeded,
            totalBudget,
            usedBudget,
            estimatedTotalTokens,
            compactionLevel,
            layerInfos,
            result,
            error: null,
            ct);

        _logger.LogDebug(
            "[ContextPipeline] Assembled context session={Session} totalBudget={Total} usedEstimate={Used} level={Level} len={Len}",
            request.SessionId, totalBudget, usedBudget, compactionLevel, result.Length);

        return new ContextAssemblyResult(result, totalBudget, usedBudget, layers.AsReadOnly());
        }
        catch (Exception ex)
        {
            assemblySw.Stop();
            _logger.LogError(ex,
                "[ContextPipeline] Assemble failed session={Session} ws={Ws} agent={Agent} isFirst={IsFirst} historyCount={HistoryCount} elapsedMs={ElapsedMs}",
                request.SessionId, request.WorkspaceId, request.AgentInstanceId, request.IsFirstMessage, request.SessionHistory.Count, assemblySw.ElapsedMilliseconds);
            await RecordContextAssemblyMetricAsync(
                request,
                assemblyStartedAt,
                assemblySw.ElapsedMilliseconds,
                TelemetryMetricStatuses.Failed,
                totalBudget,
                usedBudget,
                layerInfos.Sum(x => x.TokenCount),
                DetermineCompactionLevel(usedBudget, totalBudget),
                layerInfos,
                finalPrompt: null,
                error: ex,
                ct: CancellationToken.None);
            throw;
        }
    }

    private void RecordLayer(
        StringBuilder sb, string content, string snapshotLabel, string layerName,
        ref int usedBudget, int totalBudget, List<ContextLayerSnapshot> layers, List<ContextLayerInfo> layerInfos)
    {
        var tokens = EstimateTokens(content);
                sb.AppendLine($"--- CONTEXT-LAYER: {layerName} ---");
        AppendLayer(sb, content);
        usedBudget += tokens;
        layers.Add(new ContextLayerSnapshot(snapshotLabel, tokens, (double)tokens / totalBudget * 100));
                layerInfos.Add(new ContextLayerInfo
        {
            LayerName = layerName,
            TokenCount = tokens,
            ContentPreview = BuildPreview(content),
        });
    }

    /// <summary>标记静态层（L0-L2, L4-PINNED）并截取 FullContent 用于 Session Fork。</summary>
    private static void MarkStaticLayers(List<ContextLayerInfo> layerInfos, string fullAssembly)
    {
        // 静态层名称集合：这些层的文本内容在父子代理间逐字节一致
        var staticLayerNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "L0-STATIC", "L0-ENVIRONMENT", "L0-AGENTS-ROSTER",
            "L1-TOOLS", "L2-SKILLS", "L4-PINNED",
        };

        foreach (var layer in layerInfos)
        {
            if (staticLayerNames.Contains(layer.LayerName))
            {
                layer.IsStatic = true;
                // 从完整组装字符串中截取该层内容
                layer.FullContent = ExtractLayerContent(fullAssembly, layer.LayerName);
            }
        }
    }

    /// <summary>
    /// 计算静态层指纹：拼接所有 IsStatic=true 层的 FullContent（按层顺序，换行分隔），
    /// 计算 SHA-256 hex（小写）。用于 KV-cache 复用校验。
    /// 只拼接纯静态层文本，排除时间戳、SessionId、AssembledAt 等动态注入部分。
    /// </summary>
    private static string? ComputeStaticLayersFingerprint(List<ContextLayerInfo> layerInfos)
    {
        var staticContents = layerInfos
            .Where(l => l.IsStatic && !string.IsNullOrEmpty(l.FullContent))
            .Select(l => l.FullContent!)
            .ToList();

        if (staticContents.Count == 0)
            return null;

        var combined = string.Join('\n', staticContents);
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(combined));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    /// <summary>剪枝会话消息：仅保留最近 N 条 user/assistant 正文，移除 tool_call/tool_result/thinking/heartbeat。</summary>
    private static List<PrunedMessage> PruneSessionMessages(IReadOnlyList<ChatMessage> history, int maxMessages)
    {
        var candidates = new List<PrunedMessage>();
        foreach (var msg in history)
        {
            // 只保留 user 和 assistant 角色的消息正文
            if (msg.Role != ChatRole.User && msg.Role != ChatRole.Assistant)
                continue;
            if (string.IsNullOrWhiteSpace(msg.Content))
                continue;
            // 跳过心跳/系统消息
            var content = msg.Content.Trim();
            if (content.StartsWith("[HEARTBEAT]", StringComparison.OrdinalIgnoreCase) ||
                content.StartsWith("[SYSTEM]", StringComparison.OrdinalIgnoreCase))
                continue;
            // 截断过长消息
            if (content.Length > 2000)
                content = content[..2000] + "...";
            candidates.Add(new PrunedMessage
            {
                Role = msg.Role == ChatRole.User ? "user" : "assistant",
                Content = content,
                Timestamp = DateTimeOffset.UtcNow,
            });
        }
        // SessionHistory[0] 是最早的消息，取最后 maxMessages 条即最近的对话
        return candidates.TakeLast(maxMessages).ToList();
    }

    /// <summary>从完整组装字符串中提取指定层的文本内容。</summary>
    private static string? ExtractLayerContent(string fullAssembly, string layerName)
    {
        var marker = $"--- CONTEXT-LAYER: {layerName} ---";
        var idx = fullAssembly.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) return null;

        // 找到下一个上下文层标记或结束（跳过内部子层标记如 LAYER: IDENTITY）
        var nextMarker = "--- CONTEXT-LAYER:";
        var nextIdx = fullAssembly.IndexOf(nextMarker, idx + marker.Length, StringComparison.Ordinal);
        if (nextIdx < 0) nextIdx = fullAssembly.Length;

        return fullAssembly[idx..nextIdx].TrimEnd();
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
            personaFiles = _personaFileProvider.Load(request.AgentTemplateId, request.AgentInstanceId);

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
        // 实例身份锚定：告诉 Agent 它是谁、ID 是什么、负责什么
        if (!string.IsNullOrWhiteSpace(request.AgentInstanceId))
            sb.AppendLine($"AgentId: {request.AgentInstanceId}");
        if (!string.IsNullOrWhiteSpace(request.AgentTemplateId))
            sb.AppendLine($"Template: {request.AgentTemplateId}");
        if (!string.IsNullOrWhiteSpace(template.Role))
            sb.AppendLine($"Role: {template.Role}");
        if (template.Responsibilities is { Count: > 0 })
            sb.AppendLine($"Responsibilities: {string.Join("、", template.Responsibilities)}");
        // ADR-042: 明确的身份自述 —— 帮助 Agent 在冷启动时确定自己的身份
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(template.Role))
            sb.Append($"你是 {displayName}，一名 {template.Role}");
        else
            sb.Append($"你是 {displayName}");
        if (template.Responsibilities is { Count: > 0 })
            sb.Append($"，负责 {string.Join("、", template.Responsibilities)}");
        sb.Append("。");
        sb.AppendLine("请始终以这个身份和视角处理任务。");

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

        // L0: SECURITY-GUIDE — 权限申请与熔断恢复指引
        sb.AppendLine("--- LAYER: SECURITY-GUIDE ---");
        sb.AppendLine("When a tool call is rejected (error contains \"permission\", \"not allowed\", or \"rejected\"):");
        sb.AppendLine("1. STOP immediately — do NOT retry the same tool blindly.");
        sb.AppendLine("2. Call request_tool_approval(tool_id=\"...\", purpose=\"...\") to request one-time authorization.");
        sb.AppendLine("   The system may auto-approve (implicit approval) if the request matches safety rules.");
        sb.AppendLine("3. If approval is denied, try a different approach that uses allowed tools.");
        sb.AppendLine("4. Repeated failures trigger session fuse (session becomes Faulted).");
        sb.AppendLine("5. If fuse triggers, the user can send /resume to recover the session.");

        return sb.ToString();
    }

    // ═══════════════════════════════════════════════════════════════
    // L0-ENVIRONMENT: 运行环境
    // ═══════════════════════════════════════════════════════════════

    private string GetOrBuildEnvironmentLayer(ContextRequest request)
    {
        // 环境层缓存以 workspaceId + 环境指纹为键，跨 Session 复用
        var envCacheKey = $"{request.WorkspaceId}:{_envProvider.EnvironmentFingerprint}";
        if (_envCache.TryGetValue(envCacheKey, out var cached))
            return cached.Content;

        var content = BuildEnvironmentLayer(request);
        _envCache[envCacheKey] = new EnvironmentLayerCache { Content = content };
        _logger.LogDebug(
            "[ContextPipeline:L0-ENVIRONMENT] Built env layer fingerprint={Fingerprint} workspace={Workspace}",
            _envProvider.EnvironmentFingerprint, request.WorkspaceId);
        return content;
    }

    private string BuildEnvironmentLayer(ContextRequest request)
    {
        var sb = new StringBuilder();
        sb.AppendLine("--- LAYER: ENVIRONMENT ---");
        sb.AppendLine($"OS: {_envProvider.OsDescription} {_envProvider.OsArchitecture}");
        sb.AppendLine($"Runtime: .NET {_envProvider.RuntimeVersion}");
        sb.AppendLine($"PathSeparator: {_envProvider.PathSeparator}");
        sb.AppendLine($"Shell: {_envProvider.DefaultShell}");
        sb.AppendLine($"Container: {(_envProvider.IsContainer ? "true" : "false")}");

        return sb.ToString();
    }

    /// <summary>
    /// 构建 INBOUND-MESSAGE-CONTEXT 层：当 Agent 收到其他 Agent 的消息时，
    /// 明确告知发送方身份和消息意图，防止身份混淆。
    /// ADR-042: Agent 身份锚定。
    /// </summary>
    private static string BuildInboundMessageContextLayer(ContextRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.InboundSourceId)
            || !string.Equals(request.InboundSourceKind, "agent", StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        var senderName = request.InboundSourceName ?? request.InboundSourceId;

        return $"""
            --- LAYER: INBOUND-MESSAGE-CONTEXT ---
            你收到了一条来自其他 Agent 的内部消息。
            发送方: {senderName} (agent:{request.InboundSourceId})

            请根据你的角色和职责，判断如何回应这条消息。
            ---
            """;
    }

    private string BuildWorkspaceEnvironmentLayer(ContextRequest request)
    {
        var sb = new StringBuilder();
        sb.AppendLine("--- LAYER: WORKSPACE ENVIRONMENT ---");
        var workspaceRoot = _envProvider.GetWorkspaceRoot(request.WorkspaceId);
        if (workspaceRoot is not null)
        {
            sb.AppendLine($"WorkspaceRoot: {workspaceRoot}");
        }
        else
        {
            sb.AppendLine("WorkspaceRoot: unavailable");
        }

        return sb.ToString();
    }

    // ═══════════════════════════════════════════════════════════════
    // L1: 动态工具
    // ═══════════════════════════════════════════════════════════════

    private Task<string> BuildToolsLayerAsync(ContextRequest request, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine("--- LAYER: TOOLS ---");

        if (_toolRegistry is not null)
        {
            var descriptors = _toolRegistry.ListAvailable(request.Capability);
            AppendToolDescriptorList(sb, descriptors, request);
            return Task.FromResult(sb.ToString());
        }

        // Fallback for legacy tests or minimal hosts that have not registered the unified Tool registry.
        var availableSkills = _skillRuntime.GetAvailableSkills(request.Capability);
        var skillsList = availableSkills.ToList();
        if (skillsList.Count > 0)
        {
            sb.AppendLine("Available tools (use via function calling):");
            var defaultSkills = skillsList.Where(s => s.PermissionLevel == ToolPermissionLevel.Low).ToList();
            var mediumSkills = skillsList.Where(s => s.PermissionLevel == ToolPermissionLevel.Medium).ToList();
            var highSkills = skillsList.Where(s => s.PermissionLevel == ToolPermissionLevel.High).ToList();

            if (defaultSkills.Count > 0)
            {
                sb.Append("  [内置] ");
                sb.AppendLine(string.Join(", ", defaultSkills.Select(s => $"`{s.SkillId}`")));
            }
            if (mediumSkills.Count > 0)
            {
                sb.Append("  [默认授权] ");
                sb.AppendLine(string.Join(", ", mediumSkills.Select(s => $"`{s.SkillId}`")));
            }
            if (highSkills.Count > 0)
            {
                sb.Append("  [需显式授权] ");
                sb.AppendLine(string.Join(", ", highSkills.Select(s => $"`{s.SkillId}`")));
            }
            sb.AppendLine($"Total: {skillsList.Count} tools available.");
        }
        else
        {
            sb.AppendLine("(No tools available with current capability policy.)");
        }

        sb.AppendLine("Memory tool hint: use `search_memory` when you need to recall user facts from memory library; use `query_session_logs` for paged message transcripts by default, and raw event actions only for diagnostics.");
        if (ShouldShowSubAgentHint(request))
        {
            sb.AppendLine("Sub-agent (`spawn_sub_agent`) best practices:");
            sb.AppendLine("- Delegate multi-step exploration/audit/search tasks to sub-agents to keep your context clean.");
            sb.AppendLine("- Sub-agents work in parallel: launch 2-3 simultaneously for independent research paths.");
            sb.AppendLine("- Define clear task scope: what to find, how to judge, output format.");
            sb.AppendLine("- Do NOT delegate: 1-2 step operations, real-time user interaction, or decisions needing your judgment.");
        }

        return Task.FromResult(sb.ToString());
    }

    private static void AppendToolDescriptorList(StringBuilder sb, IReadOnlyList<ToolDescriptor> descriptors, ContextRequest request)
    {
        var visibleDescriptors = ShouldShowSubAgentHint(request)
            ? descriptors
            : descriptors
                .Where(d => !string.Equals(d.ToolId, "spawn_sub_agent", StringComparison.OrdinalIgnoreCase))
                .ToList();

        if (visibleDescriptors.Count > 0)
        {
            sb.AppendLine("Available tools (use via function calling):");
            var lowTools = visibleDescriptors.Where(d => d.PermissionLevel == ToolPermissionLevel.Low).ToList();
            var mediumTools = visibleDescriptors.Where(d => d.PermissionLevel == ToolPermissionLevel.Medium).ToList();
            var highTools = visibleDescriptors.Where(d => d.PermissionLevel == ToolPermissionLevel.High).ToList();

            if (lowTools.Count > 0)
            {
                sb.Append("  [内置] ");
                sb.AppendLine(string.Join(", ", lowTools.Select(t => $"`{t.ToolId}`")));
            }
            if (mediumTools.Count > 0)
            {
                sb.Append("  [默认授权] ");
                sb.AppendLine(string.Join(", ", mediumTools.Select(t => $"`{t.ToolId}`")));
            }
            if (highTools.Count > 0)
            {
                sb.Append("  [需显式授权] ");
                sb.AppendLine(string.Join(", ", highTools.Select(t => $"`{t.ToolId}`")));
            }
            sb.AppendLine($"Total: {visibleDescriptors.Count} tools available.");
        }
        else
        {
            sb.AppendLine("(No tools available with current capability policy.)");
        }

        sb.AppendLine("Memory tool hint: use `search_memory` when you need to recall user facts from memory library; use `query_session_logs` for paged message transcripts by default, and raw event actions only for diagnostics.");
        if (ShouldShowSubAgentHint(request))
        {
            sb.AppendLine("Sub-agent (`spawn_sub_agent`) best practices:");
            sb.AppendLine("- Delegate multi-step exploration/audit/search tasks to sub-agents to keep your context clean.");
            sb.AppendLine("- Sub-agents work in parallel: launch 2-3 simultaneously for independent research paths.");
            sb.AppendLine("- Define clear task scope: what to find, how to judge, output format.");
            sb.AppendLine("- Do NOT delegate: 1-2 step operations, real-time user interaction, or decisions needing your judgment.");
        }
    }

    private static bool ShouldShowSubAgentHint(ContextRequest request)
    {
        if (!request.SessionId.Contains("-sub-", StringComparison.OrdinalIgnoreCase))
            return true;

        if (request.AllowSubDelegation != true)
            return false;

        var depth = Math.Max(0, request.DelegationDepth ?? 0);
        var maxDepth = request.MaxDelegationDepth ?? 1;
        return depth < maxDepth;
    }

    // ═══════════════════════════════════════════════════════════════
    // L2: 动态 Skills
    // ═══════════════════════════════════════════════════════════════

    private async Task<string> BuildSkillsLayerAsync(ContextRequest request, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine("--- LAYER: SKILLS ---");

        // 通过 SkillRuntime 获取当前可用 Skills
        var availableSkills = _skillRuntime.GetAvailableSkills(request.Capability);

        // 区分 Skill 包和内置 Skill
        var pkgs = _skillPackageRegistry.Get(request.AgentInstanceId);

        if (availableSkills.Count > 0)
        {
            sb.AppendLine("Active agent skills:");
            foreach (var skill in availableSkills)
            {
                var level = skill.PermissionLevel switch
                {
                    ToolPermissionLevel.Low => "auto",
                    ToolPermissionLevel.High => "granted",
                    _ => "default",
                };
                sb.AppendLine($"- `{skill.SkillId}` [{level}]: {skill.Description}");
            }
        }

        if (pkgs.Count > 0)
        {
            sb.AppendLine("Additional skill packages loaded:");
            foreach (var pkg in pkgs)
                sb.AppendLine($"- {pkg.Name} (v{pkg.Version}): {pkg.Description ?? ""}");
        }

        var runtimeSkillCount = await AppendRuntimeSkillIndexAsync(sb, request.AgentInstanceId, ct);

        if (availableSkills.Count == 0 && pkgs.Count == 0 && runtimeSkillCount == 0)
            sb.AppendLine("(No skills or skill packages loaded.)");

        // Voice 语音输出能力
        sb.AppendLine();
        sb.AppendLine("Voice output:");
        sb.AppendLine("You may attach a `voice` field to messages suitable for spoken delivery.");
        sb.AppendLine("- voice.enabled: true → frontend auto-plays");
        sb.AppendLine("- voice.tts_text: optional spoken version (remove symbols, more conversational)");
        sb.AppendLine("Use for: greetings, farewells, storytelling, explanations, casual chat.");
        sb.AppendLine("Skip for: code, tables, tech specs, file paths, CLI.");

        return sb.ToString();
    }

    private async Task<int> AppendRuntimeSkillIndexAsync(
        StringBuilder sb,
        string agentInstanceId,
        CancellationToken ct)
    {
        if (_agentSkillFileService is null || string.IsNullOrWhiteSpace(agentInstanceId))
            return 0;

        try
        {
            var index = await _agentSkillFileService.GetIndexAsync(agentInstanceId, ct);
            var skills = index.Skills
                .Where(skill => skill.Enabled)
                .OrderBy(skill => skill.SkillId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(skill => skill.Version, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (skills.Count == 0)
                return 0;

            sb.AppendLine("Runtime-private SKILL index:");
            sb.AppendLine("Use `agent_skill` with action=read_file when a listed SKILL is relevant and you need its full instructions.");
            foreach (var skill in skills)
            {
                var parts = new List<string>
                {
                    $"`{skill.SkillId}`",
                    skill.Name,
                    $"v{skill.Version}",
                };
                if (!string.IsNullOrWhiteSpace(skill.Summary))
                    parts.Add(skill.Summary.Trim());
                if (skill.Tags.Count > 0)
                    parts.Add($"tags={string.Join(", ", skill.Tags)}");
                if (skill.Keywords.Count > 0)
                    parts.Add($"keywords={string.Join(", ", skill.Keywords)}");
                if (!string.IsNullOrWhiteSpace(skill.RelativePath))
                    parts.Add($"path={skill.RelativePath}");

                sb.Append("- ");
                sb.AppendLine(string.Join(" | ", parts));
            }

            return skills.Count;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "[ContextPipeline:L2-SKILLS] Failed to load runtime SKILL index agent={AgentInstanceId}",
                agentInstanceId);
            return 0;
        }
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
        var sb = new StringBuilder();
        sb.AppendLine("--- LAYER: PINNED ---");

        // ── 第一步：尝试读 Important_memory.md（主路径）──
        if (!string.IsNullOrWhiteSpace(request.AgentInstanceId) && _importantMemory is not null)
        {
            var content = _importantMemory.ReadOrNull(request.AgentInstanceId);
            if (string.IsNullOrWhiteSpace(content))
            {
                await _importantMemory.EnsureInitializedAsync(request.AgentInstanceId, ct);
                content = _importantMemory.ReadOrNull(request.AgentInstanceId);
            }

            if (!string.IsNullOrWhiteSpace(content))
            {
                sb.AppendLine("[IMPORTANT MEMORIES]");
                sb.Append(content);
                var result = sb.ToString();
                _memCache.Set($"pinned:{request.WorkspaceId}", result, MemCacheExpiration);
                return result;
            }
        }

        // ── 第二步：回退到搜索（完全不变）──
        if (_libraryConvenience is null || string.IsNullOrWhiteSpace(request.WorkspaceId))
        {
            sb.AppendLine("(No pinned memory.)");
            return sb.ToString();
        }

        var cacheKey = $"pinned:{request.WorkspaceId}";
        if (_memCache.TryGetValue<string>(cacheKey, out var cached) && cached is not null)
            return cached;

        try
        {
            var results = await _libraryConvenience.SmartSearchAsync(
                "important critical key",
                topK: 5,
                ct);

            _logger.LogDebug(
                "[ContextPipeline:Pinned] workspace={Workspace} results={Count} cache={Cached}",
                request.WorkspaceId, results.Count, cached is not null);

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

        var cachedContent = sb.ToString();
        _memCache.Set(cacheKey, cachedContent, MemCacheExpiration);
        return cachedContent;
    }

    // ═══════════════════════════════════════════════════════════════
    // L5: 近期历史
    // ═══════════════════════════════════════════════════════════════

    private string BuildRecentHistoryLayer(
        ContextRequest request,
        ContextPipelineCompactionLevel compactionLevel,
        int budgetTokens)
    {
        var sb = new StringBuilder();
        sb.AppendLine("--- LAYER: RECENT ---");

        if (request.SessionHistory is not { Count: > 0 })
        {
            // 冷启动：预填充最近 3 天的剥离消息日志
            if (request.IsFirstMessage && !string.IsNullOrWhiteSpace(request.AgentInstanceId))
            {
                var prefilled = TryBuildColdStartRecent(request, budgetTokens);
                if (!string.IsNullOrWhiteSpace(prefilled))
                    return $"{sb}--- RECENT DAYS ---\n{prefilled}";
            }

            sb.AppendLine("(No recent history.)");
            return sb.ToString();
        }

        var history = request.SessionHistory;
        var usedTokens = 0;
        var budgetChars = budgetTokens * 4; // 粗估 1 token ≈ 4 chars
        string? lastHeartbeatContent = null;

        switch (compactionLevel)
        {
            case ContextPipelineCompactionLevel.None:
                // 全部保留，最多最近 DefaultRecentMessageCount 条
                for (int i = Math.Max(0, history.Count - DefaultRecentMessageCount); i < history.Count; i++)
                {
                    var msg = history[i];

                    // 心跳去重：内容相同的心跳只保留第一条（压缩版）
                    if (IsHeartbeatContent(msg.Content))
                    {
                        if (lastHeartbeatContent == msg.Content)
                            continue; // 重复心跳，跳过
                        lastHeartbeatContent = msg.Content;
                    }

                    var entry = FormatHistoryEntry(msg);
                    if (usedTokens + entry.Length > budgetChars && i < history.Count - 2)
                        break;
                    sb.Append(entry);
                    usedTokens += entry.Length;
                }
                break;

            case ContextPipelineCompactionLevel.Gentle:
                // 最近 15 条保留原文，更早的摘要化
                var gentleKeep = Math.Min(15, history.Count);
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

            case ContextPipelineCompactionLevel.Aggressive:
                // 最近 3 条保留原文，更早的摘要化
                var aggressiveKeep = Math.Min(3, history.Count);
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
        // 检测心跳消息：内容包含 "── 系统心跳 ──"
        if (IsHeartbeatContent(msg.Content))
        {
            return $"[System:heartbeat]: 系统心跳（已忽略重复内容）\n";
        }

        var role = msg.Role switch
        {
            _ when msg.Role == ChatRole.User => "User",
            _ when msg.Role == ChatRole.Assistant => "Assistant",
            _ when msg.Role == ChatRole.System => "System",
            _ => msg.Role.ToString(),
        };
        var text = TruncateText(msg.Content ?? "", 800);
        return $"[{role}]: {text}\n";
    }

    /// <summary>检测消息内容是否为系统心跳。</summary>
    private static bool IsHeartbeatContent(string? content) =>
        content is not null && content.Contains("── 系统心跳 ──", StringComparison.Ordinal);

    private static string SummarizeOlderHistory(List<ChatMessage> olderMessages)
    {
        if (olderMessages.Count == 0) return "No prior history.";
        var userMsgs = olderMessages.Count(m => m.Role == ChatRole.User);
        var assistantMsgs = olderMessages.Count(m => m.Role == ChatRole.Assistant);
        return $"Earlier conversation: {userMsgs} user messages, {assistantMsgs} assistant replies. Topics: general discussion.";
    }

    /// <summary>
    /// 冷启动时从 Agent 私有消息日志目录读取最近 3 天的剥离后对话，
    /// 注入 L5-RECENT 层，使 Agent 在首次消息时感知最近几天的对话脉络。
    /// </summary>
    private string? TryBuildColdStartRecent(
        ContextRequest request,
        int budgetTokens)
    {
        const int maxRecentDays = 3;
        var budgetChars = budgetTokens * 4;
        var logsRoot = _dataPaths.AgentInstanceMessageLogsRoot(request.AgentInstanceId!);

        var sb = new StringBuilder();
        var totalChars = 0;
        // 锚定到当天午夜，使同一天内的冷启动 RECENT 层内容不变，保护前缀缓存
        var today = DateTimeOffset.Now.Date;

        for (int dayOffset = 1; dayOffset <= maxRecentDays; dayOffset++)
        {
            if (totalChars >= budgetChars) break;

            var day = today.AddDays(-dayOffset);
            var dayDir = Path.Combine(logsRoot, day.ToString("yyyy-MM-dd"));
            if (!Directory.Exists(dayDir)) continue;

            var files = Directory.GetFiles(dayDir, "*.md")
                .OrderByDescending(f => f)
                .ToList();

            sb.AppendLine($"--- Day {day:yyyy-MM-dd} ---");

            foreach (var file in files)
            {
                if (totalChars >= budgetChars)
                {
                    sb.AppendLine("... (truncated, use query_session_logs for more)");
                    break;
                }

                try
                {
                    var raw = File.ReadAllText(file);
                    var stripped = MessageLogStripper.Strip(raw);
                    if (string.IsNullOrWhiteSpace(stripped)) continue;

                    sb.AppendLine(stripped);
                    sb.AppendLine("---");
                    totalChars += stripped.Length;
                }
                catch (IOException ex)
                {
                    _logger.LogDebug(ex, "[ContextPipeline] Skip unreadable log file {File}", file);
                }
            }
        }

        var result = sb.ToString().Trim();
        return result.Length > 0 ? result : null;
    }

    private async Task<string> BuildLegacyAgentLogRecallLayerAsync(
        ContextRequest request,
        CancellationToken ct)
    {
        if (_agentLogRecallService is null
            || string.IsNullOrWhiteSpace(request.AgentInstanceId)
            || string.IsNullOrWhiteSpace(request.UserMessage))
        {
            return string.Empty;
        }

        var recall = await _agentLogRecallService.RecallAsync(
            new AgentLogRecallRequest(request.AgentInstanceId, request.UserMessage),
            ct);

        if (recall.RecentFiveDaysMessages.Count == 0
            && recall.RecentDailySummaries.Count == 0
            && recall.RecentThirtyDaysMessages.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        sb.AppendLine("--- LAYER: RECALLED ---");
        sb.AppendLine("[AGENT LOG RECALL]");

        if (recall.RecentFiveDaysMessages.Count > 0)
        {
            sb.AppendLine("Recent 5 days message logs:");
            foreach (var match in recall.RecentFiveDaysMessages)
                sb.AppendLine($"- {match.Day} {match.RelativePath}:{match.LineNumber}: {match.Snippet}");
        }

        if (recall.RecentThirtyDaysMessages.Count > 0)
        {
            sb.AppendLine("Recent 30 days message logs:");
            foreach (var match in recall.RecentThirtyDaysMessages)
                sb.AppendLine($"- {match.Day} {match.RelativePath}:{match.LineNumber}: {match.Snippet}");
        }

        if (recall.RecentDailySummaries.Count > 0)
        {
            sb.AppendLine("Recent 180 days daily summaries:");
            foreach (var match in recall.RecentDailySummaries)
                sb.AppendLine($"- {match.Day} {match.RelativePath}:{match.LineNumber}: {match.Snippet}");
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
            sb.AppendLine("For short inline values like paths, filenames, commands, or variable names, use inline `backticks` instead of fenced code blocks.");
            sb.AppendLine("你有访问用户记忆图书馆和会话证据的能力。可用工具：search_memory（检索记忆）、save_memory（写入/更新记忆）、grep_memory（全文检索/列出Books/目录）、manage_memory（管理Books/章节/指针）、query_session_logs（默认查询分页消息转录；raw event 动作仅用于诊断）。");
            sb.AppendLine("当需要主动记住用户信息时，使用 save_memory。当需要列出或管理记忆结构时，使用 grep_memory 或 manage_memory。当需要核实会话内容时，优先用 query_session_logs 的 messages/grep 默认消息视图；只有核实工具调用、tool_result、delta/thinking 等事件证据时才使用 raw event 动作。");
            if (request.Capability?.AllowedToolNames is { Count: > 0 })
                sb.AppendLine("If a task requires tools, explain the limitation briefly instead of emitting tool-call JSON.");
        }
        else
        {
            sb.AppendLine("你有访问用户记忆图书馆和会话证据的能力。可用工具：search_memory（检索记忆）、save_memory（写入/更新记忆）、grep_memory（全文检索/列出Books/目录）、manage_memory（管理Books/章节/指针）、query_session_logs（默认查询分页消息转录；raw event 动作仅用于诊断）。");
            sb.Append(BuildLoopInstructions(request.Capability));
        }
    }

    private string BuildLoopInstructions(CapabilityPolicy? capability)
    {
        if (_toolRegistry is null)
            return _skillRuntime.BuildLoopInstructions(capability);

        return ToolLoopInstructionBuilder.BuildFromDescriptors(_toolRegistry.ListAvailable(capability));
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

        var low = 0;
        var high = text.Length;
        while (low < high)
        {
            var mid = low + ((high - low + 1) / 2);
            if (EstimateTokens(text[..mid]) <= maxTokens)
                low = mid;
            else
                high = mid - 1;
        }

        return TruncateText(text, low) + "\n[TRUNCATED – context budget exceeded]";
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

    private async Task RecordContextAssemblyMetricAsync(
        ContextRequest request,
        DateTimeOffset startedAt,
        long durationMs,
        string status,
        int totalBudget,
        int usedBudget,
        int estimatedTotalTokens,
        ContextPipelineCompactionLevel compactionLevel,
        IReadOnlyList<ContextLayerInfo> layerInfos,
        string? finalPrompt,
        Exception? error,
        CancellationToken ct)
    {
        if (_telemetrySink is null)
            return;

        try
        {
            var dimensions = new Dictionary<string, string>
            {
                ["workspace_id"] = request.WorkspaceId,
                ["session_id"] = request.SessionId,
                ["agent_template_id"] = request.AgentTemplateId,
                ["agent_instance_id"] = request.AgentInstanceId,
                ["for_streaming"] = request.ForStreaming.ToString(),
                ["is_first_message"] = request.IsFirstMessage.ToString(),
                ["history_message_count"] = request.SessionHistory.Count.ToString(),
                ["total_budget"] = totalBudget.ToString(),
                ["used_budget"] = usedBudget.ToString(),
                ["estimated_total_tokens"] = estimatedTotalTokens.ToString(),
                ["reserved_for_reply"] = ReservedForReply.ToString(),
                ["compaction_level"] = compactionLevel.ToString(),
                ["layer_count"] = layerInfos.Count.ToString(),
                ["final_prompt_chars"] = (finalPrompt?.Length ?? 0).ToString(),
            };

            foreach (var layer in layerInfos)
            {
                var key = NormalizeMetricKey(layer.LayerName);
                dimensions[$"layer.{key}.tokens"] = layer.TokenCount.ToString();
                dimensions[$"layer.{key}.preview_chars"] = layer.ContentPreview.Length.ToString();
            }

            await _telemetrySink.RecordAsync(new TelemetryMetric
            {
                Trace = request.Trace ?? RuntimeTraceContext.CreateNew(
                    sessionId: request.SessionId,
                    workspaceId: request.WorkspaceId,
                    executionId: request.AgentInstanceId),
                Source = "backend",
                Category = TelemetryMetricCategories.Context,
                Name = "context.assembly",
                Status = status,
                OccurredAtUtc = startedAt,
                DurationMs = durationMs,
                CountValue = 1,
                Unit = "assembly",
                Severity = error is null ? "info" : "error",
                Summary = status == TelemetryMetricStatuses.Succeeded
                    ? "Context assembly completed."
                    : "Context assembly failed.",
                Dimensions = dimensions,
                DebugJson = BuildContextAssemblyDebugJson(layerInfos, finalPrompt),
                ErrorCode = error?.GetType().Name,
                ErrorMessage = TruncateDebug(error?.Message ?? "", 512),
            }, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Telemetry is best-effort and must not change context assembly behavior.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[ContextPipeline:Telemetry] Context assembly metric failed session={Session}",
                request.SessionId);
        }
    }

    private static string? BuildContextAssemblyDebugJson(
        IReadOnlyList<ContextLayerInfo> layerInfos,
        string? finalPrompt)
    {
        if (!TelemetryDebugSwitch.IsEnabled())
            return null;

        return JsonSerializer.Serialize(new
        {
            layers = layerInfos.Select(layer => new
            {
                name = layer.LayerName,
                tokenCount = layer.TokenCount,
                contentPreview = TruncateDebug(layer.ContentPreview, 4096),
            }),
            finalPromptPreview = TruncateDebug(finalPrompt ?? "", 16000),
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private static string NormalizeMetricKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "unknown";

        var chars = value
            .Select(ch => char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '_')
            .ToArray();
        return new string(chars).Trim('_');
    }

    private static string TruncateDebug(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            return value;
        return value[..maxLength];
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
