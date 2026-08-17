using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PuddingCode.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingCode.Observability;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingRuntime.Models;

namespace PuddingRuntime.Services;

// ═══════════════════════════════════════════════════════════════
// ContextPipeline — Orchestration (AssembleAsync + helpers)
// Extracted from ContextPipeline.cs to eliminate god-class (P0 audit #9).
// ═══════════════════════════════════════════════════════════════

public sealed partial class ContextPipeline
{
    /// <summary>
    /// 组装完整上下文，返回拼接好的系统提示词与各层 Token 占比快照。
    /// 按 7 层模型逐层构建，每层受 Token 预算约束，超预算时触发压缩。
    /// </summary>
    public async Task<ContextAssemblyResult> AssembleAsync(ContextRequest request, CancellationToken ct)
    {
        var totalBudget = request.Template.Runtime?.MaxContextTokens ?? 0;
        var sb = new StringBuilder();
        var userContextBuilder = new StringBuilder();
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
                FullContent = taskPlanningCtx,
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

        // ── L2: 动态 Skills 与多模态渠道协议（8%）──
        var skillsCtx = await BuildSkillsLayerAsync(request, ct);
        var skillsBudget = budget.AllocatePercent(ctx, 0.08);
        var skillsTrimmed = TrimToTokenBudget(skillsCtx, skillsBudget);
                RecordLayer(sb, skillsTrimmed, "动态技能", "L2-SKILLS", ref usedBudget, totalBudget, layers, layerInfos);

        // ── L3-WORKSPACE-ENVIRONMENT ──
        var workspaceEnvironmentCtx = BuildWorkspaceEnvironmentLayer(request);
        RecordLayer(sb, workspaceEnvironmentCtx, "工作区环境", "L3-WORKSPACE-ENVIRONMENT", ref usedBudget, totalBudget, layers, layerInfos);

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
                request.SessionId, request.PersistentAgentInstanceId, request.IsFirstMessage, ct);
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
                FullContent = memorySummaryCtx,
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

        // ── L3: 用户偏好 ──
        AppendLayer(sb, userProfile);
        layers.Add(new ContextLayerSnapshot("用户偏好", userProfileTokens, (double)userProfileTokens / totalBudget * 100));
        layerInfos.Add(new ContextLayerInfo
        {
            LayerName = "L3-USER",
            TokenCount = userProfileTokens,
            ContentPreview = BuildPreview(userProfile),
            FullContent = userProfile,
        });

        // ── L3-USER-PREFERENCES: 记忆库用户偏好预取（会话启动自动注入，Prefetch）──
        if (_userPreferenceService is not null)
        {
            var prefsCtx = await GetOrBuildUserPreferencesAsync(request, ct);
            if (!string.IsNullOrWhiteSpace(prefsCtx))
            {
                var prefsTokens = EstimateTokens(prefsCtx);
                usedBudget += prefsTokens;
                ctx.UsedBudget = usedBudget;
                budget.UpdateAvailable(ctx);
                AppendLayer(sb, prefsCtx);
                layers.Add(new ContextLayerSnapshot("用户偏好记忆", prefsTokens, (double)prefsTokens / totalBudget * 100));
                layerInfos.Add(new ContextLayerInfo
                {
                    LayerName = "L3-USER-PREFERENCES",
                    TokenCount = prefsTokens,
                    ContentPreview = BuildPreview(prefsCtx),
                    FullContent = prefsCtx,
                });
            }
        }

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
                    request.PersistentAgentInstanceId,
                    request.IsFirstMessage,
                    ct);
                if (!string.IsNullOrWhiteSpace(contextAugmentStr))
                {
                    contextAugmentTokens = EstimateTokens(contextAugmentStr);
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
                 && !string.IsNullOrWhiteSpace(request.PersistentAgentInstanceId)
                 && !string.IsNullOrWhiteSpace(request.UserMessage))
        {
            try
            {
                contextAugmentStr = await BuildLegacyAgentLogRecallLayerAsync(request, ct);
                if (!string.IsNullOrWhiteSpace(contextAugmentStr))
                {
                    contextAugmentLayerName = "L6-AGENT-LOG-RECALL";
                    contextAugmentTokens = EstimateTokens(contextAugmentStr);
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

        if (!string.IsNullOrWhiteSpace(contextAugmentStr))
        {
            var rawContextAugmentTokens = contextAugmentTokens;
            contextAugmentStr = TrimToTokenBudget(contextAugmentStr, MaxContextAugmentTokens);
            contextAugmentTokens = EstimateTokens(contextAugmentStr);
            usedBudget += contextAugmentTokens;

            if (contextAugmentTokens < rawContextAugmentTokens)
            {
                _logger.LogInformation(
                    "[ContextPipeline] Trimmed {LayerName} before budget accounting session={Session} rawTokens={RawTokens} retainedTokens={RetainedTokens} limit={Limit}",
                    contextAugmentLayerName,
                    request.SessionId,
                    rawContextAugmentTokens,
                    contextAugmentTokens,
                    MaxContextAugmentTokens);
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
                request.PersistentAgentInstanceId,
                request.IsFirstMessage,
                ct);

            croppedSnippets = pipelineResult.Snippets;
        }

        // ═══════════════════════════════════════════════════════════════
        // 注入裁剪结果（或降级回原始内容）
        // ═══════════════════════════════════════════════════════════════

        // ── L6-CONTEXT-AUGMENT：随当前用户消息追加到缓存尾部。
        // 召回内容取决于本轮 query，不能写入 system prompt，否则会使其后的整段会话历史失去前缀缓存。
        if (!string.IsNullOrWhiteSpace(contextAugmentStr))
        {
            AppendLayer(userContextBuilder, contextAugmentStr);
            layers.Add(new ContextLayerSnapshot("上下文增强", contextAugmentTokens, (double)contextAugmentTokens / totalBudget * 100));
            layerInfos.Add(new ContextLayerInfo
            {
                LayerName = contextAugmentLayerName,
                TokenCount = contextAugmentTokens,
                ContentPreview = BuildPreview(contextAugmentStr),
                FullContent = contextAugmentStr,
            });
        }

        // ── RUNTIME 层：仅保留跨 Turn 稳定的行为指令。日期等本轮元数据由用户消息尾部承载。──
        var runtimeLen = sb.Length;
        AppendRuntimeLayer(sb, request);
        var runtimeTokens = EstimateTokens(sb.ToString()) - EstimateTokens(sb.ToString(0, runtimeLen));
        layers.Add(new ContextLayerSnapshot("运行时指令", Math.Max(0, runtimeTokens), (double)Math.Max(0, runtimeTokens) / totalBudget * 100));
        layerInfos.Add(new ContextLayerInfo
        {
            LayerName = "L8-RUNTIME",
            TokenCount = Math.Max(0, runtimeTokens),
            ContentPreview = BuildPreview(sb.ToString(runtimeLen, sb.Length - runtimeLen)),
            FullContent = sb.ToString(runtimeLen, sb.Length - runtimeLen),
        });

        // ── INBOUND-MESSAGE-CONTEXT：高频变化，随当前用户消息追加到缓存尾部。──
        if (!string.IsNullOrEmpty(inboundCtx))
        {
            AppendLayer(userContextBuilder, inboundCtx);
            layers.Add(new ContextLayerSnapshot("入站消息上下文", inboundTokens, (double)inboundTokens / totalBudget * 100));
            layerInfos.Add(new ContextLayerInfo
            {
                LayerName = "L9-INBOUND",
                TokenCount = inboundTokens,
                ContentPreview = BuildPreview(inboundCtx),
                FullContent = inboundCtx,
            });
        }

        // ── L9-CURRENT：只做预算与可观测性计量。
        // 实际正文由 AgentExecutionService 作为唯一一条 User message 发送，禁止再次复制到 system prompt。
        var currentMessage = request.UserMessage ?? string.Empty;
        var currentMsgTokens = EstimateTokens(currentMessage);
        usedBudget += currentMsgTokens;
        layers.Add(new ContextLayerSnapshot("当前消息", currentMsgTokens, (double)currentMsgTokens / totalBudget * 100));
        layerInfos.Add(new ContextLayerInfo
        {
            LayerName = "L9-CURRENT",
            TokenCount = currentMsgTokens,
            ContentPreview = BuildPreview(currentMessage),
            FullContent = currentMessage,
        });

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

        return new ContextAssemblyResult(
            result,
            totalBudget,
            usedBudget,
            layers.AsReadOnly(),
            layerInfos.AsReadOnly(),
            userContextBuilder.Length == 0 ? null : userContextBuilder.ToString());
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
            FullContent = content,
        });
    }

    /// <summary>标记静态层（L0-L2, L4-PINNED）并截取 FullContent 用于 Session Fork。</summary>
    private static void MarkStaticLayers(List<ContextLayerInfo> layerInfos, string fullAssembly)
    {
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
                layer.FullContent = ExtractLayerContent(fullAssembly, layer.LayerName);
            }
        }
    }

    /// <summary>
    /// 计算静态层指纹：拼接所有 IsStatic=true 层的 FullContent（按层顺序，换行分隔），
    /// 计算 SHA-256 hex（小写）。用于 KV-cache 复用校验。
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

    /// <summary>剪枝会话消息：仅保留最近 N 条 user/assistant 正文。</summary>
    internal static List<PrunedMessage> PruneSessionMessages(IReadOnlyList<ChatMessage> history, int maxMessages)
    {
        var candidates = new List<PrunedMessage>();
        foreach (var msg in history)
        {
            if (msg.Role != ChatRole.User && msg.Role != ChatRole.Assistant)
                continue;
            if (string.IsNullOrWhiteSpace(msg.Content))
                continue;
            var content = msg.Content.Trim();
            if (content.StartsWith("[HEARTBEAT]", StringComparison.OrdinalIgnoreCase) ||
                content.StartsWith("[SYSTEM]", StringComparison.OrdinalIgnoreCase))
                continue;
            if (content.Length > 2000)
                content = content[..2000] + "...";
            candidates.Add(new PrunedMessage
            {
                Role = msg.Role == ChatRole.User ? "user" : "assistant",
                Content = content,
                Timestamp = DateTimeOffset.UtcNow,
            });
        }
        return candidates.TakeLast(maxMessages).ToList();
    }

    /// <summary>从完整组装字符串中提取指定层的文本内容。</summary>
    private static string? ExtractLayerContent(string fullAssembly, string layerName)
    {
        var marker = $"--- CONTEXT-LAYER: {layerName} ---";
        var idx = fullAssembly.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) return null;

        var nextMarker = "--- CONTEXT-LAYER:";
        var nextIdx = fullAssembly.IndexOf(nextMarker, idx + marker.Length, StringComparison.Ordinal);
        if (nextIdx < 0) nextIdx = fullAssembly.Length;

        return fullAssembly[idx..nextIdx].TrimEnd();
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
}
