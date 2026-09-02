using System.Runtime.CompilerServices;
using System.Threading.Channels;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using PuddingCode.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingCode.Services;
using PuddingCode.SubAgents;
using PuddingCode.Tools;
using PuddingMemoryEngine.Data;
using PuddingRuntime.Services.AgentLoop;
using PuddingRuntime.Services.Background;
using PuddingRuntime.Services.Skills;
using PuddingRuntime.Services.Tools;
using PuddingCode.Observability;
using Serilog.Context;

namespace PuddingRuntime.Services;

public sealed partial class AgentExecutionService
{
    /// <summary>
    /// 面向 Chat UI 的流式执行路径。
    /// 它沿用 Session/Memory/LLM 配置链路，但刻意使用直接 Markdown 回复提示，
    /// 避免把结构化 Agent Loop JSON（status/tool/meta）逐 token 暴露给用户界面。
    /// </summary>
    public async IAsyncEnumerable<ServerSentEventFrame> ExecuteStreamAsync(
        RuntimeDispatchRequest request,
        [EnumeratorCancellation] CancellationToken external = default)
    {
        await using var executionLease = await _sessionExecutionGate.EnterAsync(
            request.SessionId,
            executionSource: "agent_execute_stream",
            external);

        _logger.LogInformation(
            "[AgentExec] STREAM session={Session} template={Template} msgLen={Len} hasLlmConfig={HasCfg}",
            request.SessionId, request.AgentTemplateId,
            request.MessageText.Length, request.LlmConfig is not null);
        _idleDetector?.RecordUserMessage();
        ReportMeaningfulProgress(request, "run.started", request.MessageText);

        using var logScope = LogContext.PushProperty("SessionId", request.SessionId);

        const string globalPrefix = "global:";
        var canonicalTemplateId = request.AgentTemplateId.StartsWith(globalPrefix, StringComparison.OrdinalIgnoreCase)
            ? request.AgentTemplateId[globalPrefix.Length..]
            : request.AgentTemplateId;

        var template = BuiltInAgentTemplates.FindById(canonicalTemplateId)
                       ?? BuiltInAgentTemplates.WorkspaceServiceAgent;
        var effectiveCapability = MergeCapability(request.CapabilityPolicy, template.Capability);
        var sessionTimeout = ResolveSessionTimeout(template);
        var maxElapsed = ResolveMaxElapsed(request);
        var maxToolCallsTotal = ResolveMaxToolCallsTotal(request.MaxToolCallsTotal, _guardrails);

        _contextManager.CleanupExpiredSessions(request.SessionId);

        var instance = _sessionManager.GetOrCreate(
            request.SessionId,
            request.AgentTemplateId,
            sessionTimeout,
            request.AgentInstanceId);
        _sessionManager.MarkRunning(request.SessionId);
        _contextManager.TouchHistoryAccess(request.SessionId, sessionTimeout);

        // P0-5 步骤 5：跨 1h 超时 / Core 重启后从持久化 Composition 水合工具集合（append-only）。
        // 恢复失败不阻断执行（服务内部静默降级为空集合）。
        if (_compositionRecovery is not null)
            await _compositionRecovery.RecoverAsync(request.SessionId, CancellationToken.None);

        _runtimeSessionStore.GetOrCreate(
            request.SessionId, instance.AgentInstanceId,
            request.WorkspaceId, request.AgentTemplateId);

        // ── Streaming trace context ─────────────────────────────────
        var streamTrace = CreateExecutionTrace(request);

        // ── 子代理运行归档（ADR-021）───────────────────────────────
        var streamSubAgentRunId = await TryCreateSubAgentRunAndEmitStartedAsync(
            request, instance.AgentInstanceId, CancellationToken.None);

        var skillPackages = request.SkillPackages ?? [];
        _skillPackageRegistry.Register(instance.AgentInstanceId, skillPackages);
        if (skillPackages.Count > 0)
            await _skillPackageDownloader.EnsureDownloadedAsync(skillPackages);

        var startDecision = _runtimeControl?.CanStartAgent(request.SessionId);
        if (startDecision is { Allowed: false })
        {
            yield return ServerSentEventFrame.Json(SseEventTypes.Error, new { message = startDecision.Message });
            yield break;
        }

        _runtimeControl?.MarkSessionRunning(request.SessionId);
        var runtimeSessionToken = _runtimeControl?.GetSessionCancellationToken(request.SessionId) ?? CancellationToken.None;
        using var runtimeLinkedCts = CancellationTokenSource.CreateLinkedTokenSource(external, runtimeSessionToken);
        var ct = _controlRegistry.CreateLinkedToken(request.SessionId, runtimeLinkedCts.Token);

        // ── 全管道性能诊断 ──
        var perfTotalSw = System.Diagnostics.Stopwatch.StartNew();
        var perfHistorySw = System.Diagnostics.Stopwatch.StartNew();
        var perfHistoryStartedAt = DateTimeOffset.UtcNow;
        var history = _contextManager.GetOrCreateHistory(request.SessionId);
        var rehydratedFromDbThisDispatch = false;

        // ── 入站消息去重：同一 message_id 因 Ack 丢失/重试被重复 dispatch 时，
        //     不再重复进入 LLM 历史、不再重复执行。
        if (!string.IsNullOrWhiteSpace(request.MessageId)
            && !_contextManager.TryMarkMessageDispatched(request.SessionId, request.MessageId))
        {
            _logger.LogInformation(
                "[AgentExec] STREAM duplicate message detected session={Session} messageId={MessageId} — skipping execution",
                request.SessionId, request.MessageId);
            _contextManager.MarkSessionExecutionCompleted(request.SessionId);
            _skillPackageRegistry.Remove(instance.AgentInstanceId);
            _controlRegistry.Remove(request.SessionId);
            yield return ServerSentEventFrame.Json(SseEventTypes.Done, new
            {
                reply = (string?)null,
                sessionId = request.SessionId,
                messageId = request.MessageId,
                isError = false,
                duplicateMessage = true,
                stopReason = RuntimeDispatchMarkers.DuplicateMessageStopReason,
                toolFailureCount = 0,
                toolOutputTruncatedCount = 0,
                toolOutputChars = 0L,
            });
            yield break;
        }
        try
        {
            var hydration = await _contextManager.TryHydrateStreamHistoryFromDbAsync(
                request.SessionId,
                history,
                template.Runtime?.MaxContextTokens ?? 8000,
                ct,
                query: request.MessageText,
                currentMessageId: request.MessageId,
                currentTurnId: request.ExecutionIdentity?.TurnId);
            // 冷重水合（重启/内存会话过期后从 DB 回放）后的首轮请求显式归因，
            // 供缓存报表区分"重水合全量重传"与"前缀漂移"。
            rehydratedFromDbThisDispatch = hydration.ReplacedHistory;
            perfHistorySw.Stop();
            await RecordActivityAsync(
                streamTrace,
                component: RuntimeActivityComponents.AgentExecution,
                operation: "agent.history.hydrate",
                status: hydration.Succeeded
                    ? RuntimeActivityStatuses.Succeeded
                    : RuntimeActivityStatuses.Failed,
                perfHistoryStartedAt,
                endedAt: DateTimeOffset.UtcNow,
                durationMs: perfHistorySw.ElapsedMilliseconds,
                summary: hydration.Succeeded
                    ? "Stream history evaluated before LLM preparation."
                    : "Canonical history hydration failed closed; retained in-memory history.",
                metadata: new Dictionary<string, string>
                {
                    ["history_count"] = history.Count.ToString(),
                    ["max_context_tokens"] = (template.Runtime?.MaxContextTokens ?? 8000).ToString(),
                    ["hydration_source"] = hydration.Source,
                    ["history_replaced"] = hydration.ReplacedHistory.ToString(),
                },
                error: hydration.Error,
                ct: CancellationToken.None);
        }
        catch (Exception ex)
        {
            perfHistorySw.Stop();
            await RecordActivityAsync(
                streamTrace,
                component: RuntimeActivityComponents.AgentExecution,
                operation: "agent.history.hydrate",
                status: RuntimeActivityStatuses.Failed,
                perfHistoryStartedAt,
                endedAt: DateTimeOffset.UtcNow,
                durationMs: perfHistorySw.ElapsedMilliseconds,
                summary: "Stream history hydration failed.",
                metadata: new Dictionary<string, string>
                {
                    ["max_context_tokens"] = (template.Runtime?.MaxContextTokens ?? 8000).ToString(),
                },
                error: ex,
                ct: CancellationToken.None);
            throw;
        }
        _logger.LogInformation(
            "[AgentExec:Perf] History loaded session={Session} elapsed={Ms}ms count={Count}",
            request.SessionId, perfHistorySw.ElapsedMilliseconds, history.Count);

        var perfContextSw = System.Diagnostics.Stopwatch.StartNew();
        var perfContextStartedAt = DateTimeOffset.UtcNow;
        ContextAssemblyResult streamingSystemPrompt;
        try
        {
            streamingSystemPrompt = await _contextPipeline.AssembleAsync(new ContextRequest
            {
                Template = template,
                WorkspaceId = request.WorkspaceId ?? string.Empty,
                SessionId = request.SessionId,
                LoadedToolIds = _sessionManager.GetLoadedToolIds(request.SessionId),
                AgentTemplateId = request.AgentTemplateId,
                UserMessage = request.MessageText,
                Capability = effectiveCapability,
                AgentInstanceId = instance.AgentInstanceId,
                ConfigurationAgentInstanceId = request.ConfigurationAgentInstanceId,
                ForStreaming = true,
                IsFirstMessage = history.Count == 0,
                SessionHistory = history.Where(m => m.Role != ChatRole.System).ToList(),
                Trace = streamTrace,
                TaskPlanId = request.TaskPlanId,
                TaskNodeId = request.TaskNodeId,
                ParentTaskNodeId = request.ParentTaskNodeId,
                DelegationDepth = request.DelegationDepth,
                MaxDelegationDepth = request.MaxDelegationDepth,
                RoleInPlan = request.RoleInPlan,
                AllowSubDelegation = request.AllowSubDelegation,
                AllowAgentCreation = request.AllowAgentCreation,
                AssignedObjective = request.AssignedObjective,
                ExpectedOutputContract = request.ExpectedOutputContract,
                InboundSourceKind = request.Origin?.FromKind,
                InboundSourceId = request.Origin?.FromId,
                                        InboundSourceName = request.Origin?.FromDisplayName,
                        ParentContextSnapshot = request.ParentContextSnapshot,
            }, ct);
            perfContextSw.Stop();
            await RecordActivityAsync(
                streamTrace,
                component: RuntimeActivityComponents.ContextPipeline,
                operation: "agent.context.assemble",
                status: RuntimeActivityStatuses.Succeeded,
                perfContextStartedAt,
                endedAt: DateTimeOffset.UtcNow,
                durationMs: perfContextSw.ElapsedMilliseconds,
                summary: "Streaming context assembled before LLM preparation.",
                metadata: new Dictionary<string, string>
                {
                    ["agent_template_id"] = request.AgentTemplateId,
                    ["history_count"] = history.Count.ToString(),
                    ["prompt_chars"] = streamingSystemPrompt.SystemPrompt.Length.ToString(),
                    ["for_streaming"] = "true",
                },
                error: null,
                ct: CancellationToken.None);
        }
        catch (Exception ex)
        {
            perfContextSw.Stop();
            await RecordActivityAsync(
                streamTrace,
                component: RuntimeActivityComponents.ContextPipeline,
                operation: "agent.context.assemble",
                status: RuntimeActivityStatuses.Failed,
                perfContextStartedAt,
                endedAt: DateTimeOffset.UtcNow,
                durationMs: perfContextSw.ElapsedMilliseconds,
                summary: "Streaming context assembly failed.",
                metadata: new Dictionary<string, string>
                {
                    ["agent_template_id"] = request.AgentTemplateId,
                    ["history_count"] = history.Count.ToString(),
                    ["for_streaming"] = "true",
                },
                error: ex,
                ct: CancellationToken.None);
            throw;
        }
        _logger.LogInformation(
            "[AgentExec:Perf] Context assembled session={Session} elapsed={Ms}ms promptLen={Len}",
            request.SessionId, perfContextSw.ElapsedMilliseconds, streamingSystemPrompt.SystemPrompt.Length);

        // 子代理上下文装配完毕事件（ADR-021）
        await TryEmitContextAssembledAsync(streamSubAgentRunId, request, CancellationToken.None);

        var systemPromptUpdate = EnsureFrozenSystemPrompt(
            history,
            streamingSystemPrompt.SystemPrompt);
        var systemPromptEpochChanged = systemPromptUpdate == SystemPromptUpdateKind.Replaced;
        if (systemPromptEpochChanged)
        {
            _logger.LogWarning(
                "[AgentExec:Prefix] Started explicit system-prompt epoch session={Session}",
                request.SessionId);
        }

        history.Add(BuildCurrentUserChatMessage(request, streamingSystemPrompt.UserContextPrefix));

        var loopCtx = new AgentLoopContext
        {
            SessionId       = request.SessionId,
            AgentInstanceId = instance.AgentInstanceId,
            WorkspaceId     = request.WorkspaceId,
            AgentTemplateId = request.AgentTemplateId,
            UserMessage     = request.MessageText,
            MaxRounds       = request.MaxRounds > 0 ? request.MaxRounds : 5,
        };

        var llmConfigStartedAt = DateTimeOffset.UtcNow;
        var llmConfigSw = System.Diagnostics.Stopwatch.StartNew();
        LlmConfig? effectiveLlmConfig;
        try
        {
            effectiveLlmConfig = await ResolveLlmConfigAsync(request.LlmConfig, ct);
            // 若上游 LlmConfig 未设置 ReasoningEffort，从模板定义继承
            if (effectiveLlmConfig?.ReasoningEffort is null && template.ReasoningEffort is not null)
                effectiveLlmConfig = (effectiveLlmConfig ?? new LlmConfig()) with { ReasoningEffort = template.ReasoningEffort };
            llmConfigSw.Stop();
            await RecordActivityAsync(
                streamTrace,
                component: RuntimeActivityComponents.AgentExecution,
                operation: "agent.llm_config.resolve",
                status: RuntimeActivityStatuses.Succeeded,
                llmConfigStartedAt,
                endedAt: DateTimeOffset.UtcNow,
                durationMs: llmConfigSw.ElapsedMilliseconds,
                summary: "Resolved effective LLM configuration for streaming request.",
                metadata: new Dictionary<string, string>
                {
                    ["model_id"] = effectiveLlmConfig?.ModelId ?? "",
                    ["endpoint_host"] = SafeHost(effectiveLlmConfig?.Endpoint),
                    ["has_key_vault_id"] = (!string.IsNullOrWhiteSpace(effectiveLlmConfig?.KeyVaultId)).ToString(),
                    ["has_api_key"] = (!string.IsNullOrWhiteSpace(effectiveLlmConfig?.ApiKey)).ToString(),
                    ["reasoning_effort"] = effectiveLlmConfig?.ReasoningEffort ?? "",
                },
                error: null,
                ct: CancellationToken.None);
        }
        catch (Exception ex)
        {
            llmConfigSw.Stop();
            await RecordActivityAsync(
                streamTrace,
                component: RuntimeActivityComponents.AgentExecution,
                operation: "agent.llm_config.resolve",
                status: RuntimeActivityStatuses.Failed,
                llmConfigStartedAt,
                endedAt: DateTimeOffset.UtcNow,
                durationMs: llmConfigSw.ElapsedMilliseconds,
                summary: "Failed to resolve effective LLM configuration for streaming request.",
                metadata: new Dictionary<string, string>
                {
                    ["request_model_id"] = request.LlmConfig?.ModelId ?? "",
                    ["request_endpoint_host"] = SafeHost(request.LlmConfig?.Endpoint),
                    ["template_reasoning_effort"] = template.ReasoningEffort ?? "",
                },
                error: ex,
                ct: CancellationToken.None);
            throw;
        }

        // 构建工具定义：优先用上游下发的 ToolDefinitions，否则从 SkillRuntime 构建
        var toolBuildStartedAt = DateTimeOffset.UtcNow;
        var toolBuildSw = System.Diagnostics.Stopwatch.StartNew();
                var loadedToolIds = _sessionManager.GetLoadedToolIds(request.SessionId);
        HashSet<string> availableToolNames;
        IReadOnlyList<LlmToolDefinition> allLlmTools;
        List<LlmToolDefinition> llmTools;
        FrozenToolManifest frozenTools;
        int runtimeMergedToolCount;
        try
        {
            // Dispatch 冻结 catalog/capability/schema；可见集只允许在 LLM round 边界单调追加。
            frozenTools = BuildFrozenToolManifest(effectiveCapability, template, request, loadedToolIds);
            availableToolNames = frozenTools.RuntimeTools
                .Select(t => t.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            allLlmTools = frozenTools.AllLlmTools;
            llmTools = frozenTools.VisibleTools.ToList();
            runtimeMergedToolCount = frozenTools.RuntimeMergedToolNames.Count;

            var terminalToolNames = llmTools
                .Where(t => t.Name.StartsWith("terminal_", StringComparison.OrdinalIgnoreCase))
                .Select(t => t.Name)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var terminalToolSummary = SummarizeToolNames(terminalToolNames);
            var exposesTerminalExecute = terminalToolNames.Any(
                name => name.Equals("terminal_execute", StringComparison.OrdinalIgnoreCase));
            _logger.LogInformation(
                "[AgentExec:Tools] Terminal tool visibility session={Session} agent={Agent} template={Template} terminalToolCount={TerminalToolCount} exposesTerminalExecute={ExposesTerminalExecute} terminalTools={TerminalTools}",
                request.SessionId,
                instance.AgentInstanceId,
                request.AgentTemplateId,
                terminalToolNames.Length,
                exposesTerminalExecute,
                terminalToolSummary);

                        _logger.LogDebug(
                "[AgentExec:Tools] Prepared streaming LLM tools (frozen at dispatch) session={Session} agent={Agent} template={Template} requestToolCount={RequestToolCount} runtimeToolCount={RuntimeToolCount} filteredRequestToolCount={FilteredRequestToolCount} runtimeMergedToolCount={RuntimeMergedToolCount} availableToolCount={AvailableToolCount} finalToolCount={FinalToolCount} deferredLoading={DeferredLoading} deferredToolCount={DeferredToolCount} requestTools={RequestTools} runtimeTools={RuntimeTools} mergedTools={MergedTools} finalTools={FinalTools}",
                request.SessionId,
                instance.AgentInstanceId,
                request.AgentTemplateId,
                request.ToolDefinitions?.Count ?? 0,
                frozenTools.RuntimeTools.Count,
                request.ToolDefinitions is { Count: > 0 } ? allLlmTools.Count - runtimeMergedToolCount : 0,
                runtimeMergedToolCount,
                frozenTools.ExposurePlan.AvailableToolCount,
                llmTools.Count,
                frozenTools.ExposurePlan.DeferredLoadingEnabled,
                frozenTools.ExposurePlan.DeferredToolCount,
                SummarizeToolDefinitions(request.ToolDefinitions),
                SummarizeToolDefinitions(frozenTools.RuntimeTools),
                SummarizeToolNames(frozenTools.RuntimeMergedToolNames),
                SummarizeToolDefinitions(llmTools));

            toolBuildSw.Stop();
            await RecordActivityAsync(
                streamTrace,
                component: RuntimeActivityComponents.AgentExecution,
                operation: "agent.tools.build",
                status: RuntimeActivityStatuses.Succeeded,
                toolBuildStartedAt,
                endedAt: DateTimeOffset.UtcNow,
                durationMs: toolBuildSw.ElapsedMilliseconds,
                summary: "Built LLM tool definitions for streaming request.",
                metadata: new Dictionary<string, string>
                {
                    ["available_tool_count"] = availableToolNames.Count.ToString(),
                    ["request_tool_count"] = (request.ToolDefinitions?.Count ?? 0).ToString(),
                    ["runtime_merged_tool_count"] = runtimeMergedToolCount.ToString(),
                    ["llm_tool_count"] = llmTools.Count.ToString(),
                    ["terminal_tool_count"] = terminalToolNames.Length.ToString(),
                    ["terminal_tools"] = terminalToolSummary,
                    ["exposes_terminal_execute"] = exposesTerminalExecute.ToString(),
                },
                error: null,
                ct: CancellationToken.None);
        }
        catch (Exception ex)
        {
            toolBuildSw.Stop();
            await RecordActivityAsync(
                streamTrace,
                component: RuntimeActivityComponents.AgentExecution,
                operation: "agent.tools.build",
                status: RuntimeActivityStatuses.Failed,
                toolBuildStartedAt,
                endedAt: DateTimeOffset.UtcNow,
                durationMs: toolBuildSw.ElapsedMilliseconds,
                summary: "Failed to build LLM tool definitions for streaming request.",
                metadata: new Dictionary<string, string>
                {
                    ["request_tool_count"] = (request.ToolDefinitions?.Count ?? 0).ToString(),
                    ["capability_type"] = effectiveCapability?.GetType().Name ?? "",
                },
                error: ex,
                ct: CancellationToken.None);
            throw;
        }

        var streamCompletedSuccessfully = false;
        var pipelineDiagnostics = new StreamPipelineDiagnosticsAccumulator();

        try
        {
            _contextManager.MarkSessionExecuting(request.SessionId);
            var hookStartedAt = DateTimeOffset.UtcNow;
            var hookSw = System.Diagnostics.Stopwatch.StartNew();
            await FireHooksAsync(h => h.OnLoopStartAsync(loopCtx, ct));
            hookSw.Stop();
            await RecordActivityAsync(
                streamTrace,
                component: RuntimeActivityComponents.AgentExecution,
                operation: "agent.hooks.loop_start",
                status: RuntimeActivityStatuses.Succeeded,
                hookStartedAt,
                endedAt: DateTimeOffset.UtcNow,
                durationMs: hookSw.ElapsedMilliseconds,
                summary: "Executed stream loop start hooks.",
                metadata: new Dictionary<string, string>
                {
                    ["hook_count"] = _hooks.Count.ToString(),
                },
                error: null,
                ct: CancellationToken.None);

            // ADR-056-E：prefer ISessionEventWriter (unified envelope); fallback to sessionOutputWriter / SSM for backward compat.
            async Task Append(ServerSentEventFrame frame)
            {
                // P0-4f-3: CoordinatorCanonical 时 Runtime 只产流，不写旧流表持久化。
                // 此处仅 gate 持久化写入（_eventWriter / _sessionOutputWriter / _ssm），
                // SSE 实时传输由各 yield return 完成，Channel fan-out 在下游，均不受此 gate 影响。
                if (request.OutputOwnership == TurnOutputOwnership.CoordinatorCanonical)
                    return;

                try
                {
                    var appendStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                    var scopedFrame = EnsureFrameMessageId(frame, request.MessageId);

                    if (_eventWriter is not null)
                    {
                        var payload = JsonSerializer.Deserialize<JsonElement>(scopedFrame.Data);
                        var draft = new SessionEventDraft(
                            EventType: scopedFrame.Event,
                            SchemaVersion: 1,
                            CommandId: null,
                            TurnId: null,
                            MessageId: request.MessageId,
                            AgentId: null,
                            Payload: payload,
                            Trace: null,
                            ToolCallId: TryReadToolCallId(payload));
                        await _eventWriter.AppendAsync(
                            request.SessionId,
                            request.WorkspaceId ?? "",
                            draft,
                            CancellationToken.None);
                    }
                    else if (_sessionOutputWriter is not null)
                    {
                        await _sessionOutputWriter.WriteFrameAsync(
                            request.SessionId,
                            request.WorkspaceId ?? "",
                            scopedFrame,
                            trace: null,
                            component: RuntimeActivityComponents.AgentExecution,
                            operation: $"chat.stream.{scopedFrame.Event}",
                            ct: CancellationToken.None);
                    }
                    else if (_ssm is not null)
                    {
                        _logger.LogWarning("[AgentExec:Append] No event writer, falling back to SSM direct — session={Session}", request.SessionId);
                        await _ssm.AppendAsync(request.SessionId, request.WorkspaceId ?? "", scopedFrame, CancellationToken.None);
                    }
                    else
                    {
                        _logger.LogWarning("[AgentExec:Append] No output writer available, cannot push frame type={Type} session={Session}", frame.Event, request.SessionId);
                    }
                    pipelineDiagnostics.ObserveSsmAppend(
                        ElapsedMilliseconds(appendStartedAt),
                        scopedFrame.Event,
                        scopedFrame.Data.Length);
                }
                catch (Exception ex) { _logger.LogWarning(ex, "[AgentExec:Append] AppendAsync failed session={Session}", request.SessionId); }
            }

            // T00/T05: 从 tool_call/tool_result 帧 payload 提取 toolCallId，回填统一事件信封。
            static string? TryReadToolCallId(JsonElement payload)
            {
                if (payload.ValueKind != JsonValueKind.Object)
                    return null;
                if (payload.TryGetProperty("toolCallId", out var toolCallId)
                    && toolCallId.ValueKind == JsonValueKind.String)
                    return toolCallId.GetString();
                return null;
            }

            // ── 流式 Agent Loop（与同步路径共享护栏参数）──────
            var maxRounds = request.MaxRounds > 0
                ? Math.Min(request.MaxRounds, _guardrails.MaxRounds)
                : _guardrails.MaxRounds;
            var reply = "(no response)";
            TokenUsageDto? usage = null;
            var usageBudgetTracker = new ExecutionUsageBudgetTracker(request.UsageBudget);
            PromptPrefixSnapshot? lastPrefixSnapshot = null;
            var hasExecutedAnyTool = false;
            var lastToolResult = "(未执行任何工具)";
            var consecutiveShortReplies = 0;
            var totalToolCalls = 0;
            var failedToolCallTracker = new FailedToolCallTracker();
            var toolDiscoveryLoopTracker = new ToolDiscoveryLoopTracker(
                _guardrails.MaxConsecutiveToolDiscoveryCalls);
            var faultedByFuse = false;
            string? faultSummary = null;
            var toolFailureCount = 0;
            var toolOutputTruncatedCount = 0;
            long toolOutputChars = 0;
            string? firstToolFailureSummary = null;
            // 连续 LLM 失败计数：外部 API 瞬时故障时，同一故障导致的多次重试只计 1 次 fuse 错误
            var consecutiveLlmFailures = 0;
            StreamErrorDiagnostic? terminalStreamError = null;
            string? terminalStreamStatus = null;
            var streamRoundsStarted = 0;
            var providerInputRecoveryAttempted = false;
            var warmPrefixCompactionAttempted = false;
            var toolSpecChangedForNextRound = false;
            var outputTruncationRecoveryAttempts = 0;

            for (int round = 0; round < maxRounds; round++)
            {
                // Usage is per provider invocation. Never carry a previous round's usage
                // into a round whose provider omitted usage.
                usage = null;
                var roundSw = System.Diagnostics.Stopwatch.StartNew();
                int roundDeltaFrames = 0;
                int roundThinkingFrames = 0;

                // ── 检查点：取消 / 最大耗时 / 最大工具调用 ──
                if (ct.IsCancellationRequested)
                {
                    var deadlineReached =
                        request.ExecutionDeadlineUtc is { } deadline &&
                        DateTimeOffset.UtcNow >= deadline.AddMilliseconds(-250);
                    terminalStreamStatus = deadlineReached ? "timed_out" : "cancelled";
                    _logger.LogInformation(
                        "[AgentExec:Stream] {Termination} session={Session}",
                        deadlineReached ? "Timed out" : "Cancelled",
                        request.SessionId);
                    var cancelledFrame = deadlineReached
                        ? ServerSentEventFrame.Json(
                            SseEventTypes.Error,
                            new
                            {
                                code = TerminalErrorCodes.ExecutionTimeout,
                                message = $"执行超时 ({maxElapsed.TotalSeconds}s)",
                                status = terminalStreamStatus,
                            })
                        : ServerSentEventFrame.Json(
                            SseEventTypes.Cancelled,
                            new { message = "已取消", status = terminalStreamStatus });
                    await Append(cancelledFrame);
                    yield return cancelledFrame;
                    break;
                }
                if (perfTotalSw.Elapsed > maxElapsed)
                {
                    terminalStreamStatus = "timed_out";
                    reply = $"Execution exceeded max elapsed time ({maxElapsed.TotalSeconds}s).";
                    _logger.LogWarning("[AgentExec:Stream] MaxElapsed={Max} exceeded", maxElapsed);
                    var timeoutFrame = ServerSentEventFrame.Json(
                        SseEventTypes.Error,
                        new
                        {
                            code = TerminalErrorCodes.ExecutionTimeout,
                            message = $"执行超时 ({maxElapsed.TotalSeconds}s)",
                            status = terminalStreamStatus,
                        });
                    await Append(timeoutFrame);
                    yield return timeoutFrame;
                    break;
                }
                var budgetBeforeRound = usageBudgetTracker.EvaluateBeforeRound();
                if (budgetBeforeRound.ShouldStop)
                {
                    terminalStreamStatus = "budget_exhausted";
                    reply = budgetBeforeRound.Message ?? "WorkUnit usage budget exhausted.";
                    _logger.LogError(
                        "[AgentExec:WorkUnitBudget] Refused streaming LLM round session={Session} round={Round} code={Code} input={InputTokens} output={OutputTokens} cost={Cost}",
                        request.SessionId,
                        round + 1,
                        budgetBeforeRound.ErrorCode,
                        budgetBeforeRound.InputTokens,
                        budgetBeforeRound.OutputTokens,
                        budgetBeforeRound.Cost);
                    var budgetFrame = ServerSentEventFrame.Json(
                        SseEventTypes.Error,
                        new
                        {
                            code = budgetBeforeRound.ErrorCode,
                            message = reply,
                            status = terminalStreamStatus,
                        });
                    await Append(budgetFrame);
                    yield return budgetFrame;
                    break;
                }
                if (totalToolCalls >= maxToolCallsTotal)
                {
                    _logger.LogWarning(
                        "[AgentExec:Stream] MaxToolCallsTotal={Max} reached",
                        maxToolCallsTotal);
                    var maxToolFrame = ServerSentEventFrame.Json(
                        SseEventTypes.Error,
                        new { message = $"工具调用次数已达上限 ({maxToolCallsTotal})" });
                    await Append(maxToolFrame);
                    yield return maxToolFrame;
                    break;
                }

                streamRoundsStarted = round + 1;

                // 发送 context 帧（仅第1轮）
                if (round == 0)
                {
                    _logger.LogInformation(
                        "[AgentExec:Perf] FIRST_TOKEN session={Session} totalElapsed={Ms}ms historyLoad={HistoryMs}ms contextBuild={ContextMs}ms",
                        request.SessionId, perfTotalSw.ElapsedMilliseconds,
                        perfHistorySw.ElapsedMilliseconds, perfContextSw.ElapsedMilliseconds);
                    _logger.LogDebug("[Diag] Stream round={Round} session={Session} tools={ToolCount} maxRounds={MaxRounds}",
                        round, request.SessionId, llmTools.Count, maxRounds);
                    var contextFrame = BuildStreamContextFrame(history, template, effectiveCapability);
                    yield return ServerSentEventFrame.Json(SseEventTypes.Context, contextFrame);
                }

                var hasToolCalls = false;
                var accumulatedToolCalls = new List<AccumulatedToolCall>();
                var synthesizedToolCallIndexes = new HashSet<int>();
                LlmContinuationState? continuationState = null;
                string? llmFinishReason = null;
                var replyBuf = new StringBuilder();
                var reasoningBuf = new StringBuilder();

                var llmPrepareStartedAt = DateTimeOffset.UtcNow;
                var llmPrepareSw = System.Diagnostics.Stopwatch.StartNew();
                var injectStartedAt = DateTimeOffset.UtcNow;
                var injectSw = System.Diagnostics.Stopwatch.StartNew();
                await TryInjectSteeringMessageAsync(
                    request,
                    instance.AgentInstanceId,
                    history,
                    round,
                    streamTrace,
                    ct);
                var injectedHistory = (await BuildInjectedHistoryAsync(history, ct)).ToList();
                injectSw.Stop();
                await RecordActivityAsync(
                    streamTrace,
                    component: RuntimeActivityComponents.AgentExecution,
                    operation: "agent.history.inject_secrets",
                    status: RuntimeActivityStatuses.Succeeded,
                    injectStartedAt,
                    endedAt: DateTimeOffset.UtcNow,
                    durationMs: injectSw.ElapsedMilliseconds,
                    summary: "Injected KeyVault placeholders into outbound LLM history.",
                    metadata: new Dictionary<string, string>
                    {
                        ["round"] = (round + 1).ToString(),
                        ["message_count"] = injectedHistory.Count.ToString(),
                        ["system_user_message_count"] = injectedHistory.Count(m => m.Role is ChatRole.System or ChatRole.User).ToString(),
                    },
                    error: null,
                    ct: CancellationToken.None);

                ContextUsageSnapshot? contextUsageSnapshot = null;
                var toolSpecChangedThisRound = toolSpecChangedForNextRound;
                toolSpecChangedForNextRound = false;
                string? roundPrefixChangeReason = rehydratedFromDbThisDispatch && round == 0
                    ? PrefixChangeReasons.SessionRehydrated
                    : systemPromptEpochChanged && round == 0
                        ? PrefixChangeReasons.SystemPromptChanged
                        : toolSpecChangedThisRound
                            ? PrefixChangeReasons.ToolSpecChanged
                            : null;
                if (_contextUsageSnapshotStore is not null)
                {
                    // 与 Buffered 共用 warm-prefix checkpoint；失败保留原历史，不再静默驱逐。
                    if (history.Count > 12 && !warmPrefixCompactionAttempted)
                    {
                        var compaction = await TryWarmPrefixCompactionAsync(
                            request,
                            instance.AgentInstanceId,
                            history,
                            injectedHistory,
                            llmTools,
                            effectiveLlmConfig,
                            round,
                            ct);
                        warmPrefixCompactionAttempted = compaction.Plan is not null;
                        if (compaction.Compacted)
                        {
                            roundPrefixChangeReason = PrefixChangeReasons.CompactionCheckpoint;
                            injectedHistory = (await BuildInjectedHistoryAsync(history, ct)).ToList();
                        }
                    }

                    var budgetedRequest = LlmRequestBudgetGuard.Prepare(
                        _contextUsageSnapshotStore,
                        request.SessionId,
                        injectedHistory,
                        llmTools,
                        effectiveLlmConfig);
                    injectedHistory = budgetedRequest.Messages.ToList();
                    contextUsageSnapshot = budgetedRequest.Snapshot;
                    if (budgetedRequest.RemovedMessageCount > 0)
                    {
                        _logger.LogWarning(
                            "[AgentExec:ContextBudget] Trimmed outbound history session={Session} round={Round} removed={Removed} estimated={Estimated} rawEstimated={RawEstimated} inputLimit={InputLimit} calibration={Calibration:F3}",
                            request.SessionId,
                            round + 1,
                            budgetedRequest.RemovedMessageCount,
                            budgetedRequest.Snapshot.UsedTokens,
                            budgetedRequest.Snapshot.RawEstimatedTokens,
                            budgetedRequest.EffectiveInputLimit,
                            budgetedRequest.Snapshot.PromptCalibrationRatio);
                    }
                }

                await EnsureCurrentTurnInputPresentWithRecoveryAsync(
            injectedHistory,
            request,
            recoveryCt => _contextManager.TryFindCurrentTurnMessageByInputHashAsync(
                request.SessionId,
                ComputeCurrentTurnInputHash(request),
                recoveryCt),
            _logger,
            ct);

                var prefixStartedAt = DateTimeOffset.UtcNow;
                var prefixSw = System.Diagnostics.Stopwatch.StartNew();
                var prefixSnapshot = PrefixCacheSnapshotBuilder.Build(
                    injectedHistory,
                    llmTools,
                    prefixChangeReason: roundPrefixChangeReason);
                prefixSw.Stop();
                await RecordActivityAsync(
                    streamTrace,
                    component: RuntimeActivityComponents.AgentExecution,
                    operation: "agent.prefix_snapshot.build",
                    status: RuntimeActivityStatuses.Succeeded,
                    prefixStartedAt,
                    endedAt: DateTimeOffset.UtcNow,
                    durationMs: prefixSw.ElapsedMilliseconds,
                    summary: "Built prompt prefix snapshot for cache diagnostics.",
                    metadata: new Dictionary<string, string>
                    {
                        ["round"] = (round + 1).ToString(),
                        ["message_count"] = injectedHistory.Count.ToString(),
                        ["tool_count"] = llmTools.Count.ToString(),
                        ["prefix_hash"] = prefixSnapshot.PrefixHash ?? "",
                        ["history_anchor_hash"] = prefixSnapshot.HistoryAnchorHash ?? "",
                        ["prefix_change_reason"] = prefixSnapshot.PrefixChangeReason ?? "",
                        ["serialization_version"] = prefixSnapshot.SerializationVersion,
                    },
                    error: null,
                    ct: CancellationToken.None);
                lastPrefixSnapshot = prefixSnapshot;
                IAsyncEnumerator<StreamDelta> llmEnumerator;
                ReportLiveness(request, "llm.started");
                if (_llmInvocationService is not null)
                {
                    llmEnumerator = _llmInvocationService.InvokeStreamAsync(new PuddingCode.Runtime.LlmInvocationRequest
                    {
                        WorkspaceId = request.WorkspaceId,
                        SessionId = request.SessionId,
                        AgentInstanceId = instance.AgentInstanceId,
                        AgentTemplateId = request.AgentTemplateId,
                        Profile = RequireInvocationProfile(request),
                        Messages = injectedHistory,
                        Tools = llmTools,
                        PrefixSnapshot = prefixSnapshot,
                        ConfigOverride = effectiveLlmConfig,
                    }, ct).GetAsyncEnumerator(ct);
                }
                else
                {
                    // ADR-027 legacy fallback for tests only (LLM client)
                    _logger.LogWarning("[AgentExec:Stream] LlmInvocationService not wired, falling back to direct LLM client — session={Session}", request.SessionId);
                    llmEnumerator = _llmClient.ChatStreamAsync(
                        request.WorkspaceId,
                        request.SessionId,
                        request.AgentTemplateId,
                        injectedHistory,
                        tools: llmTools,
                        llmConfig: effectiveLlmConfig,
                        ct: ct).GetAsyncEnumerator(ct);
                }
                llmPrepareSw.Stop();
                await RecordActivityAsync(
                    streamTrace,
                    component: RuntimeActivityComponents.AgentExecution,
                    operation: "agent.llm.prepare",
                    status: RuntimeActivityStatuses.Succeeded,
                    llmPrepareStartedAt,
                    endedAt: DateTimeOffset.UtcNow,
                    durationMs: llmPrepareSw.ElapsedMilliseconds,
                    summary: "Prepared streaming LLM invocation before provider read.",
                    metadata: new Dictionary<string, string>
                    {
                        ["round"] = (round + 1).ToString(),
                        ["message_count"] = injectedHistory.Count.ToString(),
                        ["tool_count"] = llmTools.Count.ToString(),
                        ["estimated_context_tokens"] = (contextUsageSnapshot?.UsedTokens ?? 0).ToString(),
                        ["model_id"] = effectiveLlmConfig?.ModelId ?? "",
                        ["endpoint_host"] = SafeHost(effectiveLlmConfig?.Endpoint),
                        ["path"] = _llmInvocationService is not null ? "llm_invocation_service" : "direct_llm_client",
                        ["current_message_id"] = request.MessageId ?? "",
                        ["current_input_sha256"] = ComputeCurrentTurnInputHash(request),
                    },
                    error: null,
                    ct: CancellationToken.None);
                Exception? llmException = null;
                try
                {
                    while (true)
                    {
                        StreamDelta delta;
                        try
                        {
                            if (!await llmEnumerator.MoveNextAsync())
                                break;
                            delta = llmEnumerator.Current;
                        }
                        catch (Exception ex)
                        {
                            llmException = ex;
                            break;
                        }

                        // 思维链增量 → thinking 事件
                        if (!string.IsNullOrEmpty(delta.ReasoningDelta))
                        {
                            roundThinkingFrames++;
                            reasoningBuf.Append(delta.ReasoningDelta);
                            ReportMeaningfulProgress(
                                request,
                                "llm.streaming.reasoning",
                                delta.ReasoningDelta);
                            var thinkingFrame = ServerSentEventFrame.Json(SseEventTypes.Thinking,
                                new { delta = delta.ReasoningDelta });
                            await Append(thinkingFrame);
                            yield return thinkingFrame;
                            _ = _eventBus?.EmitAsync(new StreamingEvent
                            {
                                Type = StreamingEventTypes.AgentThinking,
                                Data = new { delta = delta.ReasoningDelta }
                            }, ct);
                        }

                        // 文本增量 → delta 事件
                        if (!string.IsNullOrEmpty(delta.ContentDelta))
                        {
                            roundDeltaFrames++;
                            var originalDelta = delta.ContentDelta;
                            replyBuf.Append(originalDelta);
                            ReportMeaningfulProgress(
                                request,
                                "llm.streaming.content",
                                originalDelta);
                            var deltaFrame = ServerSentEventFrame.Json(SseEventTypes.Delta,
                                new { delta = originalDelta });
                            await Append(deltaFrame);
                            yield return deltaFrame;
                            _ = _eventBus?.EmitAsync(new StreamingEvent
                            {
                                Type = StreamingEventTypes.AgentDelta,
                                Data = new { delta = originalDelta }
                            }, ct);
                        }

                        // 工具调用增量 → 累积
                        if (delta.ToolCallIndex != null)
                        {
                            AccumulateToolCall(accumulatedToolCalls, delta);
                            if (delta.ToolCallIdWasSynthesized)
                                synthesizedToolCallIndexes.Add(delta.ToolCallIndex.Value);
                            hasToolCalls = true;
                            ReportMeaningfulProgress(
                                request,
                                "llm.streaming.tool_call",
                                $"{delta.ToolCallIndex}\u001f{delta.ToolCallId}\u001f" +
                                $"{delta.ToolCallNameDelta}\u001f{delta.ToolCallArgsDelta}");
                        }

                        if (delta.Usage is not null)
                        {
                            usage = ApplyResolvedModelCapacity(delta.Usage, effectiveLlmConfig);
                            RecordProviderContextUsageSnapshot(request.SessionId, usage, _contextUsageSnapshotStore);
                        }

                        if (delta.ContinuationState is not null)
                            continuationState = delta.ContinuationState;

                        if (!string.IsNullOrWhiteSpace(delta.FinishReason))
                            llmFinishReason = delta.FinishReason;

                        if (string.IsNullOrEmpty(delta.ReasoningDelta)
                            && string.IsNullOrEmpty(delta.ContentDelta)
                            && delta.ToolCallIndex is null)
                            ReportLiveness(request, "llm.streaming.keepalive");
                    }
                }
                finally
                {
                    await llmEnumerator.DisposeAsync();
                }

                if (llmException is OperationCanceledException && ct.IsCancellationRequested)
                {
                    var deadlineReached =
                        request.ExecutionDeadlineUtc is { } deadline &&
                        DateTimeOffset.UtcNow >= deadline.AddMilliseconds(-250);
                    terminalStreamStatus = deadlineReached ? "timed_out" : "cancelled";
                    reply = deadlineReached
                        ? $"Execution timed out at {request.ExecutionDeadlineUtc:O}."
                        : "Execution cancelled.";
                    var terminationFrame = deadlineReached
                        ? ServerSentEventFrame.Json(
                            SseEventTypes.Error,
                            new
                            {
                                code = TerminalErrorCodes.ExecutionTimeout,
                                message = reply,
                                status = terminalStreamStatus,
                            })
                        : ServerSentEventFrame.Json(
                            SseEventTypes.Cancelled,
                            new { message = reply, status = terminalStreamStatus });
                    await Append(terminationFrame);
                    yield return terminationFrame;
                    break;
                }

                // LLM API 出错 → 发送结构化 error，并将本 turn 标记为终止错误。
                if (llmException != null)
                {
                    if (!providerInputRecoveryAttempted
                        && LlmRequestBudgetGuard.TryGetProviderMaxInputTokens(llmException, out var providerMaxInputTokens))
                    {
                        providerInputRecoveryAttempted = true;
                        _contextUsageSnapshotStore?.RecordProviderInputLimitFailure(
                            request.SessionId,
                            providerMaxInputTokens);
                        _logger.LogWarning(
                            llmException,
                            "[AgentExec:ContextBudget] Provider rejected input length; recalibrating and retrying once session={Session} round={Round} providerMaxInput={ProviderMaxInput}",
                            request.SessionId,
                            round + 1,
                            providerMaxInputTokens);
                        round--;
                        continue;
                    }

                    consecutiveLlmFailures++;
                    var errorTimestampUtc = DateTimeOffset.UtcNow;
                    _logger.LogError(llmException,
                        "[AgentExec] LLM API error in streaming loop, round={Round} consecutiveLlmFailures={ConsecutiveFailures}",
                        round, consecutiveLlmFailures);
                    // 同一外部 API 瞬时故障导致的多次重试只计 1 次 fuse 错误
                    RuntimeFuseResult? fuse = null;
                    if (consecutiveLlmFailures == 1)
                    {
                        fuse = _runtimeControl?.RecordError(
                            request.SessionId,
                            RuntimeErrorKind.Api,
                            "llm",
                            llmException.Message);
                    }
                    else
                    {
                        // 后续连续失败仍检查 fuse 状态（可能被其他错误触发），但不重复计数
                        var status = _runtimeControl?.GetStatus(request.SessionId).Session;
                        if (status?.State == SessionState.Faulted)
                        {
                            fuse = new RuntimeFuseResult
                            {
                                Triggered = true,
                                Summary = status.FaultSummary ?? "Session faulted.",
                                RecentErrors = status.RecentErrors,
                                WarningLevel = FuseWarningLevel.Critical,
                                WindowErrorCount = status.WindowErrorCount,
                                SameFingerprintCount = status.SameFingerprintCount,
                            };
                        }
                    }
                    string errMessage;
                    if (fuse is { Triggered: true })
                    {
                        errMessage = fuse.Summary;
                    }
                    else if (consecutiveLlmFailures > 1)
                    {
                        errMessage = $"LLM 调用失败（第 {consecutiveLlmFailures} 次重试）: {llmException.Message}";
                    }
                    else
                    {
                        errMessage = fuse is { WarningLevel: not FuseWarningLevel.None }
                            ? $"{fuse.Summary}\n{llmException.Message}"
                            : $"LLM 调用失败: {llmException.Message}";
                    }
                    terminalStreamError = BuildStreamErrorDiagnostic(
                        request,
                        traceId: streamTrace.TraceId,
                        agentInstanceId: instance.AgentInstanceId,
                        llmConfig: effectiveLlmConfig,
                        round: round + 1,
                        maxRounds,
                        consecutiveFailures: consecutiveLlmFailures,
                        exception: llmException,
                        message: errMessage,
                        timestampUtc: errorTimestampUtc);
                    _logger.LogError(llmException,
                        "[AgentExec] Terminal stream error errorId={ErrorId} session={Session} messageId={MessageId} traceId={TraceId} location={Location}",
                        terminalStreamError.ErrorId,
                        terminalStreamError.SessionId,
                        terminalStreamError.MessageId ?? "",
                        terminalStreamError.TraceId ?? "",
                        terminalStreamError.Location);

                    var errFrame = ServerSentEventFrame.Json(SseEventTypes.Error, terminalStreamError);
                    await Append(errFrame);
                    yield return errFrame;
                    if (fuse is { Triggered: true })
                    {
                        faultedByFuse = true;
                        faultSummary = fuse.Summary;
                    }
                    reply = BuildStreamErrorDiagnosticMarkdown(terminalStreamError);
                    break;
                }

                // LLM 调用成功 → 重置连续失败计数
                consecutiveLlmFailures = 0;
                var llmOutputTruncated = AgentOutputTruncationPolicy.IsTruncated(llmFinishReason);
                if (llmOutputTruncated)
                {
                    // Responses may finish while a function_call's JSON arguments are still
                    // incomplete. Preserve the provider output for audit/replay, but never run
                    // a truncated call in the current agent round.
                    hasToolCalls = false;
                    accumulatedToolCalls.Clear();
                    synthesizedToolCallIndexes.Clear();
                    _logger.LogWarning(
                        "[AgentExec:Stream] LLM response truncated finishReason={FinishReason} session={Session} round={Round} contentChars={ContentChars} reasoningChars={ReasoningChars}",
                        llmFinishReason,
                        request.SessionId,
                        round + 1,
                        replyBuf.Length,
                        reasoningBuf.Length);
                }
                ReportMeaningfulProgress(
                    request,
                    "llm.completed",
                    replyBuf + "\u001f" + string.Join(
                        "\u001e",
                        accumulatedToolCalls.Select(call => $"{call.Name}:{call.Arguments}")));

                // ADR-043 Prefix Hash 数据流修复：LLM 流式调用完成后将本轮 prefix snapshot
                // 与 usage 一起写入 TokenUsageEvents，供 agent_diagnostics cache_health
                // 统计 distinct_prefix_hashes（此前快照只写入活动/遥测日志，从未落库）。必须先于
                // usage SSE 暴露给 ConversationProjector，避免投影 fallback 与 direct row 发生竞态双写。
                if (usage is not null && _tokenUsageRecorder is not null)
                {
                    try
                    {
                        var canonicalToolNames = accumulatedToolCalls
                            .Select(call => HarnessToolCompatibilityAdapter
                                .Normalize(call.Name, call.Arguments)
                                .ToolName);
                        await _tokenUsageRecorder.RecordAttributedRequiredAsync(
                            usage,
                            sourceType: "agent_llm",
                            sourceId: $"{request.SessionId}:{streamTrace.TraceId}:{round + 1}",
                            workspaceId: request.WorkspaceId,
                            sessionId: request.SessionId,
                            providerId: request.LlmProfile?.ProviderId ?? request.LlmConfig?.Endpoint,
                            modelId: request.LlmProfile?.ModelId ?? request.LlmConfig?.ModelId,
                            attribution: BuildTokenUsageAttribution(
                                request,
                                round,
                                canonicalToolNames),
                            prefixSnapshot: prefixSnapshot,
                            occurredAtUtc: DateTimeOffset.UtcNow);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "[AgentExec:Stream] Token usage recording deferred session={Session} round={Round}",
                            request.SessionId, round + 1);
                    }
                }

                // direct attribution 已提交（或明确失败）后再发送 usage；失败时投影器仍可补记。
                if (usage is not null)
                {
                    var usageFrame = ServerSentEventFrame.Json(SseEventTypes.Usage, usage);
                    await Append(usageFrame);
                    yield return usageFrame;
                }

                var budgetAfterRound = usageBudgetTracker.Record(usage);
                if (budgetAfterRound.ShouldStop)
                {
                    terminalStreamStatus = "budget_exhausted";
                    reply = budgetAfterRound.Message ?? "WorkUnit usage budget exhausted.";
                    _logger.LogWarning(
                        "[AgentExec:WorkUnitBudget] Stopped after streaming LLM round session={Session} round={Round} code={Code} input={InputTokens} output={OutputTokens} cacheHit={CacheHitTokens} cost={Cost}",
                        request.SessionId,
                        round + 1,
                        budgetAfterRound.ErrorCode,
                        budgetAfterRound.InputTokens,
                        budgetAfterRound.OutputTokens,
                        budgetAfterRound.CacheHitTokens,
                        budgetAfterRound.Cost);
                    var budgetFrame = ServerSentEventFrame.Json(
                        SseEventTypes.Error,
                        new
                        {
                            code = budgetAfterRound.ErrorCode,
                            message = reply,
                            status = terminalStreamStatus,
                        });
                    await Append(budgetFrame);
                    yield return budgetFrame;
                    break;
                }

                if (llmOutputTruncated)
                {
                    if (AgentOutputTruncationPolicy.ShouldRetry(
                            outputTruncationRecoveryAttempts,
                            round,
                            maxRounds))
                    {
                        outputTruncationRecoveryAttempts++;
                        history.Add(new ChatMessage(
                            ChatRole.User,
                            AgentOutputTruncationPolicy.RecoveryPrompt(replyBuf.Length > 0)));
                        _logger.LogWarning(
                            "[AgentExec:Stream] Recovering truncated provider output session={Session} round={Round} attempt={Attempt} contentChars={ContentChars} reasoningChars={ReasoningChars}",
                            request.SessionId,
                            round + 1,
                            outputTruncationRecoveryAttempts,
                            replyBuf.Length,
                            reasoningBuf.Length);
                        continue;
                    }

                    terminalStreamStatus = "llm_output_truncated";
                    reply = "Model output reached its limit before producing a complete action or answer.";
                    var truncatedFrame = ServerSentEventFrame.Json(
                        SseEventTypes.Error,
                        new
                        {
                            code = "llm_output_truncated",
                            message = reply,
                            status = terminalStreamStatus,
                        });
                    await Append(truncatedFrame);
                    yield return truncatedFrame;
                    break;
                }

                // 无工具调用 → 终止循环，replyBuf 即为最终回复
                if (!hasToolCalls)
                {
                    reply = replyBuf.Length > 0
                        ? replyBuf.ToString()
                        : "（Agent 未返回可展示文本）";

                    // 极短回复保护：如果已执行过工具但回复太短且未到达最后轮，继续Loop给LLM机会补充
                    if (hasExecutedAnyTool && reply.Length < 30 && round < maxRounds - 1)
                    {
                        consecutiveShortReplies++;
                        if (consecutiveShortReplies <= 2)
                        {
                            _logger.LogWarning("[AgentExec] Short reply={Len} chars, retrying round={Round}",
                                reply.Length, round);
                            history.Add(new ChatMessage(ChatRole.User,
                                $"[SYSTEM] Your response was very short ({reply.Length} chars). " +
                                "Please provide a complete, helpful response summarizing the tool results."));
                            continue;
                        }
                    }

                    var assistantReasoningContent = reasoningBuf.Length > 0
                        ? reasoningBuf.ToString()
                        : null;
                    history.Add(new ChatMessage(ChatRole.Assistant, reply,
                        ReasoningContent: assistantReasoningContent,
                        ContinuationState: continuationState));
                    if (round < maxRounds - 1)
                    {
                        var lateSteeringCount = await TryInjectSteeringMessageAsync(
                            request,
                            instance.AgentInstanceId,
                            history,
                            round,
                            streamTrace,
                            ct);
                        if (lateSteeringCount > 0)
                        {
                            _logger.LogInformation(
                                "[AgentExec:Steering] Continuing after {Count} late steering message(s) session={Session} round={Round}",
                                lateSteeringCount,
                                request.SessionId,
                                round + 1);
                            continue;
                        }
                    }
                    break;
                }
                consecutiveShortReplies = 0;

                // 诊断：轮次帧统计
                _logger.LogInformation(
                    "[AgentExec:Stream:Round] session={Session} round={Round} deltaFrames={Deltas} thinkingFrames={Think} toolCalls={Tools} elapsedMs={Ms}",
                    request.SessionId, round, roundDeltaFrames, roundThinkingFrames, accumulatedToolCalls.Count, roundSw.ElapsedMilliseconds);

                // 有工具调用 → 构建 Assistant 消息 + 发送 tool_call/tool_result 帧
                _logger.LogDebug("[Diag] Tool calls found session={Session} round={Round} count={Count} names={Names}",
                    request.SessionId, round, accumulatedToolCalls.Count,
                    string.Join(",", accumulatedToolCalls.Select(t => t.Name)));
                if (synthesizedToolCallIndexes.Count > 0)
                {
                    _logger.LogWarning(
                        "[LlmProtocolCompat] Synthesized tool call IDs session={Session} round={Round} count={Count} model={Model} endpointHost={EndpointHost}",
                        request.SessionId,
                        round,
                        synthesizedToolCallIndexes.Count,
                        effectiveLlmConfig?.ModelId ?? "",
                        SafeHost(effectiveLlmConfig?.Endpoint));
                }
                var assistantToolCalls = accumulatedToolCalls
                    .Select(tc =>
                    {
                        var compatibility = HarnessToolCompatibilityAdapter.Normalize(tc.Name, tc.Arguments);
                        return new ToolCall(tc.Id, compatibility.ToolName, compatibility.ArgumentsJson);
                    })
                    .ToList();
                var assistantContent = replyBuf.Length > 0
                    ? replyBuf.ToString()
                    : null;
                var toolRoundMessages = new List<ChatMessage>
                {
                    new(
                        ChatRole.Assistant,
                        assistantContent,
                        ToolCalls: assistantToolCalls,
                        ReasoningContent: reasoningBuf.Length > 0 ? reasoningBuf.ToString() : null,
                        ContinuationState: continuationState),
                };

                // 逐个工具调用：发送 tool_call → 执行 → 发送 tool_result
                var stopAfterTool = false;
                foreach (var tc in assistantToolCalls)
                {
                    var toolDecision = _runtimeControl?.CanInvokeTool(request.SessionId, tc.Name);
                    if (toolDecision is { Allowed: false })
                    {
                        var fuse = _runtimeControl!.RecordError(
                            request.SessionId,
                            RuntimeErrorKind.Tool,
                            tc.Name,
                            toolDecision.Message);
                        faultedByFuse = fuse.Triggered || _runtimeControl.GetStatus(request.SessionId).Session?.State == SessionState.Faulted;
                        faultSummary = fuse.Summary;
                        reply = fuse.Summary;
                        var blockedFrame = ServerSentEventFrame.Json(SseEventTypes.Error, new { message = fuse.Summary });
                        await Append(blockedFrame);
                        yield return blockedFrame;
                        stopAfterTool = true;
                        break;
                    }

                    var toolCallFrame = ServerSentEventFrame.Json(SseEventTypes.ToolCall,
                        new { name = tc.Name, arguments = tc.ArgumentsJson, toolCallId = tc.Id });
                    ReportLiveness(request, $"tool.started:{tc.Name}");
                    await Append(toolCallFrame);
                    yield return toolCallFrame;

                    _runtimeControl?.MarkSessionWaitingForTool(request.SessionId);
                    _ = _eventBus?.EmitAsync(new StreamingEvent
                    {
                        Type = StreamingEventTypes.AgentToolCall,
                        Data = new { name = tc.Name, arguments = tc.ArgumentsJson, toolCallId = tc.Id }
                    }, ct);

                    var injectedArgsJson = await _keyVaultService.InjectAsync(tc.ArgumentsJson, ct);
                    var safeToolArgs = await _keyVaultService.StripAsync(injectedArgsJson, ct);
                    var repeatKey = $"{tc.Name}|{injectedArgsJson}";
                    var toolStartedAt = DateTimeOffset.UtcNow;
                    var toolSw = System.Diagnostics.Stopwatch.StartNew();
                    SkillResult result;
                    TokenUsageDto? delegatedUsage = null;
                    var executionBlocked = failedToolCallTracker.TryCreateBlockedResult(
                        repeatKey,
                        out result);
                    if (!executionBlocked && _toolInvocationService is not null)
                    {
                        var toolResult = await _toolInvocationService.InvokeAsync(new PuddingCode.Runtime.ToolInvocationRequest
                        {
                            WorkspaceId = request.WorkspaceId ?? string.Empty,
                            SessionId = request.SessionId,
                            AgentInstanceId = instance.AgentInstanceId,
                            ConfigurationAgentInstanceId =
                                request.ConfigurationAgentInstanceId ?? instance.AgentInstanceId,
                            WorkingDirectory = request.WorkingDirectory,
                            AgentTemplateId = request.AgentTemplateId,
                            ToolCallId = tc.Id,
                            ToolName = tc.Name,
                            ArgumentsJson = injectedArgsJson,
                            CapabilityPolicy = effectiveCapability,
                            Trace = streamTrace,
                            ExecutionIdentity = request.ExecutionIdentity,
                            ExecutionDeadlineUtc = request.ExecutionDeadlineUtc,
                            DelegationDepth = request.DelegationDepth,
                            MaxDelegationDepth = request.MaxDelegationDepth,
                            AllowSubDelegation = request.AllowSubDelegation,
                            RoleInPlan = request.RoleInPlan,
                            UsageBudget = usageBudgetTracker.CreateRemainingBudget(),
                            ActiveTask = request.ActiveTask,
                            CallerLlmSnapshot = request.CallerLlmSnapshot,
                            CallerVisionHelperRoute = request.CallerVisionHelperRoute,
                        }, ct);
                        result = new SkillResult
                        {
                            Success = toolResult.Success,
                            Output = toolResult.Output ?? "",
                            Error = toolResult.Error,
                            ExitCode = toolResult.Success ? 0 : 1,
                            ContentParts = toolResult.ToolContentParts,
                        };
                        delegatedUsage = toolResult.DelegatedUsage;
                    }
                    else if (!executionBlocked)
                    {
                        // ADR-027 legacy fallback for tests only (SkillRuntime streaming)
                        result = await _skillRuntime.InvokeAsync(
                            tc.Name,
                            new SkillInvokeRequest
                            {
                                AgentInstanceId = instance.AgentInstanceId,
                                WorkspaceId = request.WorkspaceId ?? string.Empty,
                                SessionId = request.SessionId,
                                Input = ExtractInputFromJson(injectedArgsJson),
                                Parameters = ExtractParametersFromJson(injectedArgsJson),
                            },
                            effectiveCapability,
                            ct);
                    }
                    if (!executionBlocked)
                        result = failedToolCallTracker.Observe(repeatKey, result);
                    toolSw.Stop();

                    await RecordToolMetricAsync(
                        streamTrace,
                        tc.Name,
                        tc.Id,
                        instance.AgentInstanceId,
                        request.SessionId,
                        round,
                        totalToolCalls + 1,
                        toolStartedAt,
                        toolSw.ElapsedMilliseconds,
                        result.Success
                            ? RuntimeActivityStatuses.Succeeded
                            : RuntimeActivityStatuses.Failed,
                        injectedArgsJson,
                        safeToolArgs,
                        result,
                        error: null,
                        ct: CancellationToken.None);

                    hasExecutedAnyTool = true;
                    totalToolCalls++;
                    ObserveToolExecutionFacts(
                        tc.Name,
                        result.Success,
                        result.Output,
                        result.Error,
                        ref toolFailureCount,
                        ref toolOutputTruncatedCount,
                        ref toolOutputChars,
                        ref firstToolFailureSummary);
                    lastToolResult = result.Success
                        ? $"已完成: {(result.Output?.Length > 0 ? Truncate(result.Output!, 200) : "(空输出)")}"
                        : $"执行失败: {(result.Error?.Length > 0 ? Truncate(result.Error!, 200) : "(未知错误)")}";
                    ReportMeaningfulProgress(
                        request,
                        $"tool.completed:{tc.Name}",
                        $"{tc.Name}\u001f{injectedArgsJson}\u001f{result.Output}\u001f{result.Error}");

                    var toolResultFrame = ServerSentEventFrame.Json(SseEventTypes.ToolResult, new
                    {
                        name = tc.Name,
                        toolCallId = tc.Id,
                        exitCode = result.ExitCode,
                        output = result.Output,
                        error = result.Error,
                    });
                    await Append(toolResultFrame);
                    yield return toolResultFrame;

                    _ = _eventBus?.EmitAsync(new StreamingEvent
                    {
                        Type = StreamingEventTypes.AgentToolResult,
                        Data = new { name = tc.Name, toolCallId = tc.Id, exitCode = result.ExitCode, output = result.Output, error = result.Error }
                    }, ct);

                    var newlyLoadedToolCount = ToolExposurePlanner.RegisterSearchResult(
                        tc.Name,
                        result.Success,
                        result.Output,
                        loadedToolIds,
                        allLlmTools);
                    if (newlyLoadedToolCount > 0)
                    {
                        _sessionManager.RememberLoadedToolIds(request.SessionId, loadedToolIds);
                        var promotedToolCount = PromoteLoadedToolsForNextRound(
                            frozenTools,
                            loadedToolIds,
                            llmTools);
                        toolSpecChangedForNextRound |= promotedToolCount > 0;
                        _logger.LogInformation(
                            "[AgentExec:ToolDiscovery] Stream loaded {AddedCount} and promoted {PromotedCount} tool definition(s) for next LLM round session={Session} visibleToolCount={VisibleToolCount} loadedTools={LoadedTools}",
                            newlyLoadedToolCount,
                            promotedToolCount,
                            request.SessionId,
                            llmTools.Count,
                            SummarizeToolNames(loadedToolIds));
                    }
                    var toolDiscoveryStalled = toolDiscoveryLoopTracker.Observe(tc.Name);

                    // ── terminal_execute 兼容入口：立即转为后台 terminal job ──
                    if (tc.Name is "terminal_execute" && result.Success)
                    {
                        var pid = (result.Output ?? string.Empty).Trim();
                        var finalInfo = _terminalManager.ListProcesses(request.SessionId)
                            .FirstOrDefault(p => p.ProcessId == pid);
                        var snapshot = await _terminalManager.ReadOutputAsync(
                            pid,
                            offset: 0,
                            maxLines: 40,
                            maxChars: 4_000,
                            ct);
                        yield return ServerSentEventFrame.Json(SseEventTypes.Terminal,
                            new
                            {
                                pid,
                                type = "background",
                                exitCode = finalInfo?.ExitCode,
                                status = finalInfo?.Status.ToString(),
                                nextOffset = snapshot?.NextOffset,
                            });

                        // 工具结果追加到历史：只携带后台 job 摘要，后续由模型用 terminal_wait 轮询。
                        var terminalPayload = BuildTerminalExecuteToolPayload(
                            pid,
                            finalInfo,
                            snapshot is null ? string.Empty : string.Join(Environment.NewLine, snapshot.Lines),
                            snapshot?.NextOffset ?? 0);
                        var boundedTerminalPayload = await ToolResultContextPolicy.MaterializeAsync(
                            terminalPayload,
                            request.WorkingDirectory,
                            request.SessionId,
                            tc.Name,
                            tc.Id,
                            _logger,
                            ct);
                        toolRoundMessages.Add(new ChatMessage(ChatRole.Tool, boundedTerminalPayload, ToolCallId: tc.Id));
                        _runtimeControl?.MarkSessionRunning(request.SessionId);
                        continue;
                    }

                    // 工具结果追加到历史（非 terminal 工具）
                    var toolPayloadRaw = result.Success
                        ? $"✅ Tool '{tc.Name}' succeeded (exit={result.ExitCode}):\n{result.Output}"
                        : BuildToolFailurePayload(tc.Name, result, request.SessionId, isPermissionError:
                            result.Error?.Contains("permission", StringComparison.OrdinalIgnoreCase) == true ||
                            result.Error?.Contains("not allowed", StringComparison.OrdinalIgnoreCase) == true ||
                            result.Error?.Contains("rejected", StringComparison.OrdinalIgnoreCase) == true);
                    var toolPayload = await ToolResultContextPolicy.MaterializeAsync(
                        toolPayloadRaw,
                        request.WorkingDirectory,
                        request.SessionId,
                        tc.Name,
                        tc.Id,
                        _logger,
                        ct);
                    toolRoundMessages.Add(new ChatMessage(ChatRole.Tool, toolPayload, ToolCallId: tc.Id,
                        ContentParts: result.ContentParts));
                    if (delegatedUsage is not null)
                    {
                        var delegatedBudget = usageBudgetTracker.Record(delegatedUsage);
                        if (delegatedBudget.ShouldStop)
                        {
                            terminalStreamStatus = "budget_exhausted";
                            reply = delegatedBudget.Message ?? "WorkUnit usage budget exhausted by delegated execution.";
                            stopAfterTool = true;
                            _logger.LogWarning(
                                "[AgentExec:WorkUnitBudget] Stopped after delegated execution session={Session} round={Round} code={Code} input={InputTokens} output={OutputTokens} cacheHit={CacheHitTokens} cost={Cost}",
                                request.SessionId,
                                round + 1,
                                delegatedBudget.ErrorCode,
                                delegatedBudget.InputTokens,
                                delegatedBudget.OutputTokens,
                                delegatedBudget.CacheHitTokens,
                                delegatedBudget.Cost);
                            var delegatedBudgetFrame = ServerSentEventFrame.Json(
                                SseEventTypes.Error,
                                new
                                {
                                    code = delegatedBudget.ErrorCode,
                                    message = reply,
                                    status = terminalStreamStatus,
                                });
                            await Append(delegatedBudgetFrame);
                            yield return delegatedBudgetFrame;
                            break;
                        }
                    }
                    if (toolDiscoveryStalled)
                    {
                        faultedByFuse = true;
                        faultSummary = BuildToolDiscoveryStalledMessage(
                            toolDiscoveryLoopTracker.ConsecutiveCalls);
                        reply = faultSummary;
                        terminalStreamStatus = "failed";
                        stopAfterTool = true;
                        _logger.LogError(
                            "[AgentExec:ToolDiscovery] {Error} session={Session} round={Round}",
                            faultSummary,
                            request.SessionId,
                            round + 1);
                        var stalledFrame = ServerSentEventFrame.Json(
                            SseEventTypes.Error,
                            new
                            {
                                code = TerminalErrorCodes.ExecutionStalled,
                                message = faultSummary,
                                status = terminalStreamStatus,
                            });
                        await Append(stalledFrame);
                        yield return stalledFrame;
                        break;
                    }
                    var controlSnapshot = _runtimeControl?.GetStatus(request.SessionId).Session;
                    if (controlSnapshot?.State == SessionState.Faulted)
                    {
                        faultedByFuse = true;
                        faultSummary = controlSnapshot.FaultSummary ?? result.Error;
                        reply = faultSummary ?? lastToolResult;
                        stopAfterTool = true;
                        break;
                    }
                    _runtimeControl?.MarkSessionRunning(request.SessionId);
                }
                if (stopAfterTool)
                    break;

                // Commit the tool protocol round only after every advertised
                // tool call has a matching result. Frames may already have been
                // emitted, but incomplete provider history is never published.
                history.AddRange(toolRoundMessages);
                // 下一轮 LLM 调用，模型可根据工具结果继续生成
            }

            // ── 后处理：记忆写入 + JSONL + 历史裁剪 ─────────────────
            if (faultedByFuse && !string.IsNullOrWhiteSpace(faultSummary))
            {
                history.Add(new ChatMessage(ChatRole.Assistant, faultSummary));
            }
            if (terminalStreamStatus is null)
                TryEnqueueStreamJsonl(request, instance.AgentInstanceId, reply, usage);

            if (terminalStreamStatus is null
                && (template.Memory?.EnableSessionMemory == true
                 || template.Memory?.EnableWorkspaceMemory == true))
            {
                _memory.WriteBack(
                    reply,
                    request.SessionId,
                    request.WorkspaceId,
                    instance.AgentInstanceId,
                    instance.AgentInstanceId);
            }

            // 终端执行记录持久化到记忆图书馆（fire-and-forget）
            if (terminalStreamStatus is null && _libraryConvenience is not null)
            {
                var terminalProcesses = _terminalManager.ListProcesses(request.SessionId);
                foreach (var tp in terminalProcesses.Where(p => p.Status != TerminalProcessStatus.Running))
                {
                    var summary = tp.Command.Length > 500 ? tp.Command[..500] : tp.Command;
                    _ = _libraryConvenience.UpsertExperienceAsync(
                        request.WorkspaceId ?? string.Empty,
                        new ExperiencePackage
                        {
                            Title = $"终端执行: {summary}",
                            Content = $"[终端执行记录]\n命令: {tp.Command}\n工作目录: {tp.WorkingDir}\n退出码: {tp.ExitCode}\n状态: {tp.Status}\n时间: {tp.StartedAt:O}",
                            SuggestedTags = ["终端/执行记录"],
                            SourceSessionId = request.SessionId,
                        },
                        CancellationToken.None);
                }
            }

            var postLoopCt =
                faultedByFuse || terminalStreamStatus is not null
                    ? CancellationToken.None
                    : ct;
            if (terminalStreamStatus is null
                && !request.SuppressContextAutoCompaction)
            {
                await _contextManager.TrimHistoryAsync(
                    request.SessionId,
                    history,
                    effectiveLlmConfig?.MaxContextTokens ?? 0,
                    preferDbContextWindow: true,
                    request.WorkspaceId,
                    instance.AgentInstanceId,
                    postLoopCt,
                    maxOutputTokens: effectiveLlmConfig?.MaxOutputTokens,
                    maxInputTokens: effectiveLlmConfig?.MaxInputTokens,
                    agentTemplateId: request.AgentTemplateId,
                    traceId: request.ExecutionIdentity?.TraceId,
                    query: request.MessageText,
                    currentMessageId: request.MessageId,
                    currentTurnId: request.ExecutionIdentity?.TurnId);
            }
            _contextManager.TouchHistoryAccess(request.SessionId, sessionTimeout);
            _sessionManager.Touch(request.SessionId);
            _runtimeSessionStore.Touch(request.SessionId);

            if (terminalStreamStatus is null)
            {
                await FireHooksAsync(h => h.OnCompletedAsync(loopCtx, reply, postLoopCt));
                await FireHooksAsync(h => h.OnLoopCompleteAsync(loopCtx, reply, AgentLoopStopReason.Done, postLoopCt));
                TryEnqueueSubconsciousConsolidationFallback(request, instance.AgentInstanceId, reply);
            }

            _logger.LogInformation(
                "[AgentExec] STREAM end session={Session} replyLen={Len} usage={Usage}",
                request.SessionId, reply.Length, usage?.TotalTokens);

            // 异步归档会话（不阻塞主流程）
            var archiver = _sessionArchiver;
            if (archiver is not null)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var msgs = history.Select(h => (
                            Role: h.Role.ToString(),
                            Content: h.Content ?? "",
                            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                        )).ToList();
                        await archiver.ArchiveAsync(request.SessionId, request.WorkspaceId ?? "default",
                            template?.DisplayName ?? request.AgentTemplateId, msgs, CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[AgentExec] Stream session archive failed");
                    }
                });
            }

            var finalUsage = usage is not null
                ? usage
                : new TokenUsageDto { ContextWindowTokens = effectiveLlmConfig?.MaxContextTokens ?? 0 };
            // 如果 LLM 未产生有效回复，但工具已执行，用最后工具结果作为回复
            if (reply == "(no response)" && hasExecutedAnyTool)
            {
                reply = lastToolResult;
            }
            _logger.LogDebug("[Diag] Stream done session={Session} replyLen={Len} totalToolCalls={Ttc}",
                request.SessionId, reply.Length, totalToolCalls);

            // 完成子代理运行归档（ADR-021）
            // 子代理的 terminal 状态必须来自执行事实，而不是只看最终是否有文本。
            // 这能阻止 “工具超时/失败后 LLM 输出一段解释文本” 被上层误标为成功。
            var streamHasFailureReply = LooksLikeFailureReply(reply);
            var streamSuccess = terminalStreamStatus is null
                && terminalStreamError is null
                && (reply != "(no response)" || hasExecutedAnyTool)
                && !(toolFailureCount > 0 && streamHasFailureReply);
            var streamError = streamSuccess
                ? null
                : terminalStreamError?.Message
                  ?? firstToolFailureSummary
                  ?? (streamHasFailureReply ? reply : "No response generated");
            await TryCompleteSubAgentRunAsync(
                streamSubAgentRunId, request.SessionId, streamSuccess,
                reply, streamError,
                streamRoundsStarted, totalToolCalls, perfTotalSw.ElapsedMilliseconds,
                toolFailureCount, toolOutputTruncatedCount, toolOutputChars, firstToolFailureSummary,
                terminalStreamStatus,
                request.ExecutionIdentity,
                CancellationToken.None);

                        // T-301: voice.enabled — 从 Agent 配置中读取。
            // 当 Agent 在消息中设置 voice 标记时自动触发 TTS 朗读。
            var voiceEnabled = true;
            var voiceTtsText = (string?)null;

            var doneFrame = ServerSentEventFrame.Json(SseEventTypes.Done, new
            {
                reply,
                usage = finalUsage,
                prefixSnapshot = lastPrefixSnapshot,
                traceId = streamTrace.TraceId,
                sessionId = request.SessionId,
                messageId = request.MessageId,
                isError = terminalStreamError is not null || terminalStreamStatus is not null,
                error = terminalStreamError,
                errorId = terminalStreamError?.ErrorId,
                errorMessage = terminalStreamError?.Message
                    ?? (terminalStreamStatus is null ? null : reply),
                terminalStatus = terminalStreamStatus,
                errorLocation = terminalStreamError?.Location,
                errorTimestampUtc = terminalStreamError?.TimestampUtc,
                errorCode = terminalStreamError?.ErrorCode,
                toolFailureCount,
                toolOutputTruncatedCount,
                toolOutputChars,
                toolFailureSummary = firstToolFailureSummary,
                voice = voiceEnabled ? new { enabled = true, tts_text = voiceTtsText } : new { enabled = false, tts_text = (string?)null },
            });
            await Append(doneFrame);
            streamCompletedSuccessfully =
                terminalStreamError is null && terminalStreamStatus is null;
            if (!faultedByFuse
                && terminalStreamError is null
                && terminalStreamStatus is null)
                _runtimeControl?.MarkSessionCompleted(request.SessionId);
            yield return doneFrame;
        }
        finally
        {
            if (ct.IsCancellationRequested)
            {
                _logger.LogInformation("[AgentExec] STREAM cancelled session={Session}", request.SessionId);
                await FireHooksAsync(h => h.OnCancelledAsync(loopCtx, CancellationToken.None));
            }

            _controlRegistry.Remove(request.SessionId);
            _skillPackageRegistry.Remove(instance.AgentInstanceId);
            _contextManager.MarkSessionExecutionCompleted(request.SessionId);
            await RecordStreamPipelineDiagnosticsAsync(
                streamTrace,
                pipelineDiagnostics,
                streamCompletedSuccessfully
                    ? TelemetryMetricStatuses.Succeeded
                    : ct.IsCancellationRequested
                        ? TelemetryMetricStatuses.Cancelled
                        : TelemetryMetricStatuses.Recorded,
                CancellationToken.None);
        }
    }
}
