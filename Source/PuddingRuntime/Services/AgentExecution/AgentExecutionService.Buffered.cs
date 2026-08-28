using System.Threading.Channels;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
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
    /// 执行 Agent Loop：
    ///   User Message → LLM → [CompletionPolicy → 工具调用 → LLM] × N → 终止
    /// </summary>
    public async Task<RuntimeDispatchResult> ExecuteAsync(
        RuntimeDispatchRequest request,
        CancellationToken external = default)
    {
        await using var executionLease = await _sessionExecutionGate.EnterAsync(
            request.SessionId,
            executionSource: "agent_execute",
            external);

        _logger.LogInformation(
            "[AgentExec] session={Session} template={Template} msgLen={Len} hasLlmConfig={HasCfg}",
            request.SessionId, request.AgentTemplateId,
            request.MessageText.Length, request.LlmConfig is not null);
        _idleDetector?.RecordUserMessage();
        ReportMeaningfulProgress(request, "run.started", request.MessageText);

        using var logScope = LogContext.PushProperty("SessionId", request.SessionId);

        // 前端给全局模板附加了 "global:" 前缀（用于在 UI 中区分工作区模板），
        // Runtime 侧通过 ResolveBest 统一处理各种模板 ID 格式。
        var template = BuiltInAgentTemplates.ResolveBest(request.AgentTemplateId)
                       ?? BuiltInAgentTemplates.WorkspaceServiceAgent;
        var effectiveCapability = MergeCapability(request.CapabilityPolicy, template.Capability);
        var sessionTimeout = ResolveSessionTimeout(template);
        var maxElapsed = ResolveMaxElapsed(request);
        var maxToolCallsTotal = ResolveMaxToolCallsTotal(request.MaxToolCallsTotal, _guardrails);

        var execTrace = CreateExecutionTrace(request);
        var execStartedAt = DateTimeOffset.UtcNow;
        var maxRoundsForActivity = request.MaxRounds > 0
            ? Math.Min(request.MaxRounds, _guardrails.MaxRounds)
            : _guardrails.MaxRounds;
        await RecordActivityAsync(
            execTrace,
            component: RuntimeActivityComponents.AgentExecution,
            operation: "execute",
            status: RuntimeActivityStatuses.Started,
            execStartedAt,
            endedAt: null,
            durationMs: null,
            summary: "Agent execution started.",
            metadata: new Dictionary<string, string>
            {
                ["agent_template_id"] = request.AgentTemplateId,
                ["session_id"] = request.SessionId,
                ["max_rounds"] = maxRoundsForActivity.ToString(),
            },
            error: null,
            ct: CancellationToken.None);

        _contextManager.CleanupExpiredSessions(request.SessionId);

        // ── 获取/创建 Agent 实例 ──────────────────────────────────────
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

        // ── 子代理运行归档（ADR-021）───────────────────────────────
        var subAgentRunId = await TryCreateSubAgentRunAndEmitStartedAsync(
            request, instance.AgentInstanceId, CancellationToken.None);

        // ── 注册并预下载 Skill 包────────────────────────────────────
        var skillPackages = request.SkillPackages ?? [];
        _skillPackageRegistry.Register(instance.AgentInstanceId, skillPackages);
        if (skillPackages.Count > 0)
            await _skillPackageDownloader.EnsureDownloadedAsync(skillPackages);

        // ── 创建与外部令牌联结的执行控制令牌 ─────────────────────────
        var ct = _controlRegistry.CreateLinkedToken(request.SessionId, external);

        // ── 构建对话历史 ─────────────────────────────────────────────
        var history = _contextManager.GetOrCreateHistory(request.SessionId);
        var rehydratedFromDbThisDispatch = false;
        var systemPromptEpochChanged = false;
        string? userContextPrefix = null;

        // ── 入站消息去重：同一 message_id 因 Ack 丢失/重试被重复 dispatch 时，
        //     不再重复进入 LLM 历史、不再重复执行。
        if (!string.IsNullOrWhiteSpace(request.MessageId)
            && !_contextManager.TryMarkMessageDispatched(request.SessionId, request.MessageId))
        {
            _logger.LogInformation(
                "[AgentExec] Duplicate message detected session={Session} messageId={MessageId} — skipping execution",
                request.SessionId, request.MessageId);
            return new RuntimeDispatchResult
            {
                SessionId = request.SessionId,
                AgentInstanceId = instance.AgentInstanceId,
                ReplyText = null,
                IsSuccess = true,
                ExecutionState = AgentExecutionState.Completed,
                StopReason = RuntimeDispatchMarkers.DuplicateMessageStopReason,
            };
        }
        if (history.Count == 0)
        {
            var ctxAssembleStartedAt = DateTimeOffset.UtcNow;
            var ctxAssembleSw = System.Diagnostics.Stopwatch.StartNew();
            string systemPromptText;
            try
            {
                if (_contextAssemblyService is not null)
                {
                    var facadeResult = await _contextAssemblyService.AssembleAsync(new PuddingCode.Runtime.ContextAssemblyRequest
                    {
                        WorkspaceId = request.WorkspaceId ?? string.Empty,
                        SessionId = request.SessionId,
                        AgentInstanceId = instance.AgentInstanceId,
                        ConfigurationAgentInstanceId = request.ConfigurationAgentInstanceId,
                        AgentTemplateId = request.AgentTemplateId,
                        UserMessage = request.MessageText,
                        LlmProfileId = request.LlmConfig?.ModelId ?? "default",
                        MaxContextTokens = 8192,
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
                        TraceId = request.ExecutionIdentity?.TraceId,
                        LoadedToolIds = _sessionManager.GetLoadedToolIds(request.SessionId),
                        Capability = effectiveCapability,
                    }, ct);
                    systemPromptText = facadeResult.Messages.FirstOrDefault(m => m.Role == ChatRole.System)?.Content ?? string.Empty;
                    userContextPrefix = facadeResult.UserContextPrefix;
                }
                else
                {
                    var pipelineResult = await _contextPipeline.AssembleAsync(new ContextRequest
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
                        ForStreaming = false,
                        IsFirstMessage = true,
                        SessionHistory = Array.Empty<ChatMessage>(),
                        Trace = execTrace,
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
                    systemPromptText = pipelineResult.SystemPrompt;
                    userContextPrefix = pipelineResult.UserContextPrefix;
                }
                ctxAssembleSw.Stop();
                await RecordActivityAsync(
                    execTrace,
                    component: RuntimeActivityComponents.ContextPipeline,
                    operation: "assemble_context",
                    status: RuntimeActivityStatuses.Succeeded,
                    ctxAssembleStartedAt,
                    endedAt: DateTimeOffset.UtcNow,
                    durationMs: ctxAssembleSw.ElapsedMilliseconds,
                    summary: "Context pipeline assembled system prompt.",
                    metadata: new Dictionary<string, string>
                    {
                        ["agent_template_id"] = request.AgentTemplateId,
                        ["session_id"] = request.SessionId,
                        ["is_first_message"] = "true",
                        ["estimated_bytes"] = (systemPromptText?.Length ?? 0).ToString(),
                    },
                    error: null,
                    ct: CancellationToken.None);

                // 子代理上下文装配完毕事件（ADR-021）
                await TryEmitContextAssembledAsync(subAgentRunId, request, CancellationToken.None);
            }
            catch (Exception ex)
            {
                ctxAssembleSw.Stop();
                await RecordActivityAsync(
                    execTrace,
                    component: RuntimeActivityComponents.ContextPipeline,
                    operation: "assemble_context",
                    status: RuntimeActivityStatuses.Failed,
                    ctxAssembleStartedAt,
                    endedAt: DateTimeOffset.UtcNow,
                    durationMs: ctxAssembleSw.ElapsedMilliseconds,
                    summary: "Context pipeline assembly failed.",
                    metadata: new Dictionary<string, string>
                    {
                        ["agent_template_id"] = request.AgentTemplateId,
                        ["session_id"] = request.SessionId,
                        ["is_first_message"] = "true",
                    },
                    error: ex,
                    ct: CancellationToken.None);
                throw;
            }
            history.Add(new ChatMessage(ChatRole.System, systemPromptText));
            if (request.IsResumedSubAgentRun)
            {
                // A resumed child normally still has its in-memory history because SessionId is stable.
                // If process restart or history expiry cleared it, rehydrate the persisted conversation
                // before adding the new continuation task. The freshly assembled system prompt remains first.
                var persistedHistory = new List<ChatMessage>();
                var hydration = await _contextManager.TryHydrateStreamHistoryFromDbAsync(
                    request.SessionId,
                    persistedHistory,
                                        request.LlmConfig?.MaxInputTokens
                        ?? template.Runtime?.MaxContextTokens
                        ?? 8192,
                    ct,
                    query: request.MessageText,
                    currentMessageId: request.MessageId,
                    currentTurnId: request.ExecutionIdentity?.TurnId);
                if (hydration.ReplacedHistory && persistedHistory.Count > 0)
                {
                    history.AddRange(persistedHistory.Where(message => message.Role != ChatRole.System));
                    rehydratedFromDbThisDispatch = true;
                    _logger.LogInformation(
                        "[AgentExec:SubAgent] Rehydrated {Count} persisted messages for resumed child session={Session}",
                        persistedHistory.Count,
                        request.SessionId);
                }
            }
        }
        else if (template.Memory?.EnableSessionMemory == true
              || template.Memory?.EnableWorkspaceMemory == true)
        {
            if (history[0].Role == ChatRole.System)
            {
                var ctxReAssembleStartedAt = DateTimeOffset.UtcNow;
                var ctxReAssembleSw = System.Diagnostics.Stopwatch.StartNew();
                ContextAssemblyResult systemPrompt;
                try
                {
                    systemPrompt = await _contextPipeline.AssembleAsync(new ContextRequest
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
                        ForStreaming = false,
                        IsFirstMessage = false,
                        SessionHistory = history.Where(m => m.Role != ChatRole.System).ToList(),
                        Trace = execTrace,
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
                    ctxReAssembleSw.Stop();
                    await RecordActivityAsync(
                        execTrace,
                        component: RuntimeActivityComponents.ContextPipeline,
                        operation: "assemble_context",
                        status: RuntimeActivityStatuses.Succeeded,
                        ctxReAssembleStartedAt,
                        endedAt: DateTimeOffset.UtcNow,
                        durationMs: ctxReAssembleSw.ElapsedMilliseconds,
                        summary: "Context pipeline re-assembled with memory.",
                        metadata: new Dictionary<string, string>
                        {
                            ["agent_template_id"] = request.AgentTemplateId,
                            ["session_id"] = request.SessionId,
                            ["is_first_message"] = "false",
                            ["estimated_bytes"] = (systemPrompt.SystemPrompt?.Length ?? 0).ToString(),
                        },
                        error: null,
                        ct: CancellationToken.None);

                // 子代理上下文重新装配完毕事件（ADR-021）
                await TryEmitContextAssembledAsync(subAgentRunId, request, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    ctxReAssembleSw.Stop();
                    await RecordActivityAsync(
                        execTrace,
                        component: RuntimeActivityComponents.ContextPipeline,
                        operation: "assemble_context",
                        status: RuntimeActivityStatuses.Failed,
                        ctxReAssembleStartedAt,
                        endedAt: DateTimeOffset.UtcNow,
                        durationMs: ctxReAssembleSw.ElapsedMilliseconds,
                        summary: "Context pipeline re-assembly failed.",
                        metadata: new Dictionary<string, string>
                        {
                            ["agent_template_id"] = request.AgentTemplateId,
                            ["session_id"] = request.SessionId,
                            ["is_first_message"] = "false",
                        },
                        error: ex,
                        ct: CancellationToken.None);
                    throw;
                }
                // Same-epoch bytes stay frozen. A real stable-header change is committed once
                // as an explicit prefix epoch; volatile recall/inbound data remains in User tail.
                var systemPromptUpdate = EnsureFrozenSystemPrompt(
                    history,
                    systemPrompt.SystemPrompt ?? string.Empty);
                systemPromptEpochChanged = systemPromptUpdate == SystemPromptUpdateKind.Replaced;
                if (systemPromptEpochChanged)
                {
                    _logger.LogWarning(
                        "[AgentExec:Prefix] Started explicit system-prompt epoch session={Session}",
                        request.SessionId);
                }
                userContextPrefix = systemPrompt.UserContextPrefix;
            }
        }
                history.Add(BuildCurrentUserChatMessage(request, userContextPrefix));

        // ── 初始化 Loop 上下文 ────────────────────────────────────────
        var maxRounds = request.MaxRounds > 0
            ? Math.Min(request.MaxRounds, _guardrails.MaxRounds)
            : _guardrails.MaxRounds;
        var isSubAgentExecution = request.ExecutionIdentity?.Kind == RuntimeExecutionKind.SubAgent;
        var subAgentBudget = isSubAgentExecution
            ? new SubAgentBudgetLifecycle(
                maxRounds,
                request.BudgetGraceRounds > 0
                    ? request.BudgetGraceRounds
                    : SubAgentExecutionOptions.DefaultBudgetGraceRounds,
                maxElapsed,
                request.BudgetGraceTimeoutSeconds > 0
                    ? request.BudgetGraceTimeoutSeconds
                    : SubAgentExecutionOptions.DefaultBudgetGraceTimeoutSeconds,
                maxToolCallsTotal,
                request.IsResumedSubAgentRun)
            : null;
        var hardLoopRounds = subAgentBudget is null
            ? maxRounds
            // The final iteration performs only the grace-exhaustion check. It does not
            // start another LLM round, so the child still receives exactly GraceRounds
            // cleanup rounds and reaches BudgetExhausted before terminal post-processing.
            : checked(maxRounds + subAgentBudget.GraceRounds + 1);

        var loopCtx = new AgentLoopContext
        {
            SessionId       = request.SessionId,
            AgentInstanceId = instance.AgentInstanceId,
            WorkspaceId     = request.WorkspaceId,
            AgentTemplateId = request.AgentTemplateId,
            UserMessage     = request.MessageText,
            MaxRounds       = maxRounds,
        };

        var effectiveLlmConfig = await ResolveLlmConfigAsync(request.LlmConfig, ct);
        // 若上游 LlmConfig 未设置 ReasoningEffort，从模板定义继承
        if (effectiveLlmConfig?.ReasoningEffort is null && template.ReasoningEffort is not null)
            effectiveLlmConfig = (effectiveLlmConfig ?? new LlmConfig()) with { ReasoningEffort = template.ReasoningEffort };

        string             finalMessage   = "(no response)";
        var                stopReason     = AgentLoopStopReason.MaxRoundsReached;
        var                execState      = AgentExecutionState.Running;
        string?            executionError = null;
        string?            subAgentTerminalStatus = null;
        string?            resumeAnchorId = null;
        var                runtimeFuseFaulted = false;
        TokenUsageDto?     usage          = null;
        PromptPrefixSnapshot? lastPrefixSnapshot = null;
        var expectedOutputTracker =
            new ExpectedOutputCandidateTracker(request.ExpectedOutputContract);
        var usageBudgetTracker = new ExecutionUsageBudgetTracker(request.UsageBudget);

        // 记录本次 dispatch 前已有的 journal 条数，用于在结束时截取本次新增的 turns
        var journalStartCount = _journal.GetTurns(request.SessionId).Count;

        // 护栏状态
        var  totalSw          = System.Diagnostics.Stopwatch.StartNew();
        int  totalToolCalls   = 0;
        int  roundsStarted    = 0;
        int  noProgressCount  = 0;   // 连续无工具调用进展的轮次计数
        var  toolRepeatMap    = new Dictionary<string, int>(StringComparer.Ordinal);
        var  failedToolCallTracker = new FailedToolCallTracker();
        var  toolDiscoveryLoopTracker = new ToolDiscoveryLoopTracker(
            _guardrails.MaxConsecutiveToolDiscoveryCalls);
        int  toolFailureCount = 0;
        int  toolOutputTruncatedCount = 0;
        long toolOutputChars = 0;
        string? firstToolFailureSummary = null;
                var loadedToolIds = _sessionManager.GetLoadedToolIds(request.SessionId);
        // Dispatch 冻结 catalog/capability/schema；可见集只允许在 LLM round 边界单调追加。
        var frozenTools = BuildFrozenToolManifest(effectiveCapability, template, request, loadedToolIds);
        var allLlmTools = frozenTools.AllLlmTools;
        var llmTools = frozenTools.VisibleTools.ToList();
        _logger.LogDebug(
            "[AgentExec:Tools] Prepared LLM tools (frozen at dispatch) session={Session} agent={Agent} template={Template} requestToolCount={RequestToolCount} runtimeToolCount={RuntimeToolCount} filteredRequestToolCount={FilteredRequestToolCount} runtimeMergedToolCount={RuntimeMergedToolCount} availableToolCount={AvailableToolCount} finalToolCount={FinalToolCount} deferredLoading={DeferredLoading} deferredToolCount={DeferredToolCount} requestTools={RequestTools} runtimeTools={RuntimeTools} mergedTools={MergedTools} finalTools={FinalTools}",
            request.SessionId,
            instance.AgentInstanceId,
            request.AgentTemplateId,
            request.ToolDefinitions?.Count ?? 0,
            frozenTools.RuntimeTools.Count,
            request.ToolDefinitions is { Count: > 0 } ? frozenTools.AllLlmTools.Count - frozenTools.RuntimeMergedToolNames.Count : 0,
            frozenTools.RuntimeMergedToolNames.Count,
            frozenTools.ExposurePlan.AvailableToolCount,
            llmTools.Count,
            frozenTools.ExposurePlan.DeferredLoadingEnabled,
            frozenTools.ExposurePlan.DeferredToolCount,
            SummarizeToolDefinitions(request.ToolDefinitions),
            SummarizeToolDefinitions(frozenTools.RuntimeTools),
            SummarizeToolNames(frozenTools.RuntimeMergedToolNames),
            SummarizeToolDefinitions(llmTools));
        var providerInputRecoveryAttempted = false;
        var warmPrefixCompactionAttempted = false;
        var toolSpecChangedForNextRound = false;

        try
        {
            _contextManager.MarkSessionExecuting(request.SessionId);
            await FireHooksAsync(h => h.OnLoopStartAsync(loopCtx, ct));

            for (int round = 0; round < hardLoopRounds; round++)
            {
                // ── 检查点 A：取消 / 冻结 ─────────────────────────────
                if (ct.IsCancellationRequested || _controlRegistry.IsFrozen(request.SessionId))
                {
                    var deadlineReached =
                        request.ExecutionDeadlineUtc is { } deadline &&
                        DateTimeOffset.UtcNow >= deadline.AddMilliseconds(-250);
                    // A child that reaches its absolute deadline is still a resumable budget
                    // checkpoint even if one long provider/tool operation consumed the reserved
                    // cleanup window before the next loop boundary could inject the grace notice.
                    var subAgentDeadlineReached = deadlineReached && subAgentBudget is not null;
                    stopReason = subAgentDeadlineReached
                        ? AgentLoopStopReason.BudgetExhausted
                        : deadlineReached
                            ? AgentLoopStopReason.MaxElapsedReached
                        : AgentLoopStopReason.Cancelled;
                    execState = subAgentDeadlineReached
                        ? AgentExecutionState.BudgetExhausted
                        : deadlineReached
                            ? AgentExecutionState.Failed
                        : AgentExecutionState.Cancelled;
                    subAgentTerminalStatus = subAgentDeadlineReached
                        ? "budget_exhausted"
                        : deadlineReached
                            ? "timed_out"
                        : "cancelled";
                    executionError = subAgentDeadlineReached
                        ? "Sub-agent cleanup grace reached the hard execution deadline; the preserved session can be resumed with a fresh system budget."
                        : deadlineReached
                            ? $"Execution timed out at {request.ExecutionDeadlineUtc:O}."
                        : "Execution cancelled.";
                    if (finalMessage == "(no response)")
                        finalMessage = executionError;
                    await FireHooksAsync(h => h.OnCancelledAsync(loopCtx, default));
                    break;
                }

                // ── 检查点 B：最大总耗时 ──────────────────────────────
                if (subAgentBudget is not null)
                {
                    var budgetDecision = subAgentBudget.EvaluateBeforeRound(round, totalSw.Elapsed, totalToolCalls);
                    foreach (var notice in budgetDecision.Notices)
                    {
                        history.Add(new ChatMessage(ChatRole.System, notice.Message));
                        await TryAppendSubAgentEventAsync(
                            subAgentRunId,
                            ConversationEventTypes.SubAgentBudgetNotice,
                            new
                            {
                                sub_agent_id = request.SessionId,
                                kind = notice.Kind,
                                round = round + 1,
                                primary_max_rounds = subAgentBudget.PrimaryMaxRounds,
                                grace_rounds = subAgentBudget.GraceRounds,
                                remaining_grace_rounds = budgetDecision.RemainingGraceRounds,
                                elapsed_ms = totalSw.ElapsedMilliseconds,
                            });
                    }

                    if (budgetDecision.ShouldStop)
                    {
                        stopReason = AgentLoopStopReason.BudgetExhausted;
                        execState = AgentExecutionState.BudgetExhausted;
                        subAgentTerminalStatus = "budget_exhausted";
                        executionError =
                            $"Sub-agent exhausted its cleanup grace ({subAgentBudget.GraceRounds} rounds); " +
                            "resume the preserved child session to continue with a fresh system budget.";
                        if (finalMessage == "(no response)")
                            finalMessage = executionError;
                        await FireHooksAsync(h => h.OnMaxRoundsReachedAsync(loopCtx, default));
                        break;
                    }
                }
                else if (totalSw.Elapsed > maxElapsed)
                {
                    _logger.LogWarning(
                        "[AgentExec] MaxElapsed={Max} exceeded session={Session}",
                        maxElapsed, request.SessionId);
                    stopReason = AgentLoopStopReason.MaxElapsedReached;
                    execState  = AgentExecutionState.Failed;
                    subAgentTerminalStatus = "timed_out";
                    executionError = $"Execution exceeded max elapsed time ({maxElapsed.TotalSeconds}s).";
                    finalMessage = executionError;
                    await FireHooksAsync(h => h.OnMaxRoundsReachedAsync(loopCtx, ct));
                    break;
                }

                var budgetBeforeRound = usageBudgetTracker.EvaluateBeforeRound();
                if (budgetBeforeRound.ShouldStop)
                {
                    stopReason = AgentLoopStopReason.BudgetExhausted;
                    execState = AgentExecutionState.BudgetExhausted;
                    subAgentTerminalStatus = "budget_exhausted";
                    executionError = budgetBeforeRound.Message;
                    finalMessage = budgetBeforeRound.Message ?? "WorkUnit usage budget exhausted.";
                    _logger.LogError(
                        "[AgentExec:WorkUnitBudget] Refused LLM round session={Session} round={Round} code={Code} input={InputTokens} output={OutputTokens} cost={Cost}",
                        request.SessionId,
                        round + 1,
                        budgetBeforeRound.ErrorCode,
                        budgetBeforeRound.InputTokens,
                        budgetBeforeRound.OutputTokens,
                        budgetBeforeRound.Cost);
                    await FireHooksAsync(h => h.OnMaxRoundsReachedAsync(loopCtx, ct));
                    break;
                }

                roundsStarted = round + 1;
                await FireHooksAsync(h => h.OnRoundStartAsync(loopCtx, round, ct));
                var turnStart = DateTimeOffset.UtcNow;
                await TryAppendSubAgentEventAsync(subAgentRunId, "subagent.round.started", new
                {
                    sub_agent_id = request.SessionId,
                    round = round + 1,
                    max_rounds = maxRounds,
                    grace_rounds = subAgentBudget?.GraceRounds ?? 0,
                    in_grace = subAgentBudget?.IsInGrace == true,
                    tool_calls = totalToolCalls,
                });

                // ── LLM 调用 ──────────────────────────────────────────
                var llmSw = System.Diagnostics.Stopwatch.StartNew();
                ReportLiveness(request, "llm.started");
                // 当前 provider request 的工具定义已冻结；上轮 search_tools 的单调追加从本轮生效。

                await TryInjectSteeringMessageAsync(
                    request,
                    instance.AgentInstanceId,
                    history,
                    round,
                    execTrace,
                    ct);
                var injectedHistory = (await BuildInjectedHistoryAsync(history, ct)).ToList();
                // PreMessageHook: 自动加载匹配的技能（借鉴 Claude Code Hooks 理念）
                if (_skillEnforcer is not null)
                {
                    var enforced = await _skillEnforcer.EnforceAsync(
                        request.AgentInstanceId, request.MessageText, ct);
                    if (enforced is { Count: > 0 })
                    {
                        foreach (var result in enforced)
                        {
                            history.Insert(history.Count - 1, new ChatMessage(
                                ChatRole.System,
                                $"[AUTO-LOADED SKILL: {result.SkillId}]\n{result.MarkdownContent}"));
                        }
                        injectedHistory = (await BuildInjectedHistoryAsync(history, ct)).ToList();
                    }
                }
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
                    // Warm-prefix checkpoint：原样重放当前 provider 请求并只在尾部追加固定摘要
                    // 指令；摘要成功且确实缩小时才一次性替换旧历史。失败保留原 surface，
                    // 禁止旧实现直接逐轮删除历史头部造成语义丢失和全前缀 miss。
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
                        if (compaction.Compacted && compaction.Plan is not null)
                        {
                            roundPrefixChangeReason = PrefixChangeReasons.CompactionCheckpoint;
                            injectedHistory = (await BuildInjectedHistoryAsync(history, ct)).ToList();
                            await TryAppendSubAgentEventAsync(subAgentRunId, "subagent.context.compacted", new
                            {
                                sub_agent_id = request.SessionId,
                                round = round + 1,
                                mode = "warm_prefix_checkpoint",
                                removed_messages = compaction.Plan.RemovedMessageCount,
                                messages_before = compaction.Plan.InitialMessageCount,
                                messages_after = history.Count,
                                estimated_tokens_before = compaction.Plan.InitialUsedTokens,
                                estimated_tokens_after = compaction.Plan.EstimatedRetainedSnapshot.UsedTokens,
                                effective_input_limit = compaction.Plan.EffectiveInputLimit,
                                trigger_ratio = compaction.TriggerRatio,
                                target_ratio = compaction.TargetRatio,
                            });
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
                var prefixSnapshot = PrefixCacheSnapshotBuilder.Build(
                    injectedHistory,
                    llmTools,
                    prefixChangeReason: roundPrefixChangeReason);
                lastPrefixSnapshot = prefixSnapshot;
                await TryAppendSubAgentEventAsync(subAgentRunId, "subagent.llm.started", new
                {
                    sub_agent_id = request.SessionId,
                    round = round + 1,
                    provider_id = request.LlmProfile?.ProviderId,
                    profile_id = request.LlmProfile?.ProfileId,
                    model_id = request.LlmProfile?.ModelId,
                    message_count = injectedHistory.Count,
                    tool_count = llmTools.Count,
                    prefix_hash = prefixSnapshot.PrefixHash,
                    history_anchor_hash = prefixSnapshot.HistoryAnchorHash,
                    prefix_change_reason = prefixSnapshot.PrefixChangeReason,
                    serialization_version = prefixSnapshot.SerializationVersion,
                    estimated_context_tokens = contextUsageSnapshot?.UsedTokens,
                    current_message_id = request.MessageId,
                    current_input_sha256 = ComputeCurrentTurnInputHash(request),
                });
                                var llmResult = await LlmInvoker.InvokeAsync(
                    request,
                    instance.AgentInstanceId,
                    injectedHistory,
                    llmTools,
                    effectiveLlmConfig,
                    prefixSnapshot,
                    providerInputRecoveryAttempted,
                    round,
                    ct);

                if (llmResult.ShouldRetryRound)
                {
                    providerInputRecoveryAttempted = true;
                    round--;
                    continue;
                }

                if (!llmResult.Success)
                {
                    executionError = llmResult.ExecutionError!;
                    finalMessage = llmResult.FinalMessage!;
                    stopReason = AgentLoopStopReason.Failed;
                    execState = AgentExecutionState.Failed;
                    _journal.Record(request.SessionId, new TurnRecord
                    {
                        Round = round,
                        StartedAt = turnStart,
                        CompletedAt = DateTimeOffset.UtcNow,
                        Status = "FAILED",
                        MessageSummary = Truncate(executionError, 512),
                        ToolError = executionError,
                    });
                    await FireHooksAsync(h => h.OnFailedAsync(loopCtx, executionError, null, ct));
                    await TryAppendSubAgentEventAsync(subAgentRunId, "subagent.llm.failed", new
                    {
                        sub_agent_id = request.SessionId,
                        round = round + 1,
                        duration_ms = llmSw.ElapsedMilliseconds,
                        error = Truncate(llmResult.ExecutionError ?? "LLM invocation failed.", 500),
                    });
                    break;
                }

                var llmResp = llmResult.Response!;
                usage = llmResult.Usage;

                // ADR-043 Prefix Hash 数据流修复：LLM 调用成功后将本轮 prefix snapshot
                // 与 usage 一起写入 TokenUsageEvents，供 agent_diagnostics cache_health
                // 统计 distinct_prefix_hashes（此前快照只写入活动/遥测日志，从未落库）。
                if (usage is not null && _tokenUsageRecorder is not null)
                {
                    try
                    {
                        var canonicalToolNames = llmResp.ToolCalls?
                            .Select(call => HarnessToolCompatibilityAdapter
                                .Normalize(call.Name, call.ArgumentsJson)
                                .ToolName)
                            .ToArray() ?? [];
                        if (canonicalToolNames.Length == 0)
                        {
                            var legacyLoopResponse = AgentLoopResponse.Parse(llmResp.Content ?? string.Empty);
                            if (legacyLoopResponse.IsStructured
                                && legacyLoopResponse.Status.Equals("CONTINUE", StringComparison.OrdinalIgnoreCase)
                                && !string.IsNullOrWhiteSpace(legacyLoopResponse.Tool?.Name))
                            {
                                canonicalToolNames =
                                [
                                    HarnessToolCompatibilityAdapter.Normalize(
                                        legacyLoopResponse.Tool.Name,
                                        legacyLoopResponse.Tool.Args?.GetRawText() ?? "{}")
                                    .ToolName,
                                ];
                            }
                        }
                        await _tokenUsageRecorder.RecordAttributedRequiredAsync(
                            usage,
                            sourceType: "agent_llm",
                            sourceId: $"{request.SessionId}:{execTrace.TraceId}:{round + 1}",
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
                            "[AgentExec] Token usage recording deferred session={Session} round={Round}",
                            request.SessionId, round + 1);
                    }
                }

                var budgetAfterRound = usageBudgetTracker.Record(usage);
                if (budgetAfterRound.ShouldStop)
                {
                    stopReason = AgentLoopStopReason.BudgetExhausted;
                    execState = AgentExecutionState.BudgetExhausted;
                    subAgentTerminalStatus = "budget_exhausted";
                    executionError = budgetAfterRound.Message;
                    finalMessage = budgetAfterRound.Message ?? "WorkUnit usage budget exhausted.";
                    _logger.LogWarning(
                        "[AgentExec:WorkUnitBudget] Stopped after LLM round session={Session} round={Round} code={Code} input={InputTokens} output={OutputTokens} cacheHit={CacheHitTokens} cost={Cost}",
                        request.SessionId,
                        round + 1,
                        budgetAfterRound.ErrorCode,
                        budgetAfterRound.InputTokens,
                        budgetAfterRound.OutputTokens,
                        budgetAfterRound.CacheHitTokens,
                        budgetAfterRound.Cost);
                    await FireHooksAsync(h => h.OnMaxRoundsReachedAsync(loopCtx, ct));
                    break;
                }

                // Note: the LLM call (facade / legacy) and error handling
                // are now delegated to AgentExecutionLlmInvoker.
                llmSw.Stop();
                // Model output is execution data, not a diagnostic copy. Preserve it verbatim:
                // redaction here can corrupt JSON/tool decisions and prevents confidential tasks.
                var rawText = llmResp.Content ?? "{}";
                // The sub-agent inspector is an explicit execution-audit surface. Preserve the
                // provider's reasoning payload verbatim so operators can inspect what the model
                // actually returned; only bound the event size below.
                var rawReasoning = llmResp.ReasoningContent ?? "";
                ReportMeaningfulProgress(
                    request,
                    "llm.completed",
                    rawText + "\u001f" + string.Join(
                        "\u001e",
                        llmResp.ToolCalls?.Select(call => $"{call.Name}:{call.ArgumentsJson}") ?? []));
                const int subAgentMessagePreviewLimit = 2048;
                const int subAgentReasoningPreviewLimit = 4096;
                await TryAppendSubAgentEventAsync(subAgentRunId, "subagent.llm.completed", new
                {
                    sub_agent_id = request.SessionId,
                    round = round + 1,
                    duration_ms = llmSw.ElapsedMilliseconds,
                    prompt_tokens = usage?.PromptTokens,
                    completion_tokens = usage?.CompletionTokens,
                    total_tokens = usage?.TotalTokens,
                    cache_hit_tokens = usage?.PromptCacheHitTokens,
                    cache_miss_tokens = usage?.PromptCacheMissTokens,
                    tool_call_count = llmResp.ToolCalls?.Count ?? 0,
                    message_preview = Truncate(rawText, subAgentMessagePreviewLimit),
                    message_truncated = rawText.Length > subAgentMessagePreviewLimit,
                    reasoning_available = !string.IsNullOrWhiteSpace(rawReasoning),
                    reasoning_chars = rawReasoning.Length,
                    reasoning_preview = Truncate(rawReasoning, subAgentReasoningPreviewLimit),
                    reasoning_truncated = rawReasoning.Length > subAgentReasoningPreviewLimit,
                });

                _logger.LogInformation(
                    "[AgentExec] LLM round={Round}/{Max} session={Session} elapsed={Ms}ms",
                    round + 1, maxRounds, request.SessionId, llmSw.ElapsedMilliseconds);

                // 优先走 function-call 闭环：Assistant(tool_calls) -> Tool(result) -> 下一轮
                if (llmResp.ToolCalls is { Count: > 0 })
                {
                    var toolRoundMessages = new List<ChatMessage>
                    {
                        new(
                            ChatRole.Assistant,
                            rawText,
                            ToolCalls: llmResp.ToolCalls,
                            ReasoningContent: llmResp.ReasoningContent,
                            ContinuationState: llmResp.ContinuationState),
                    };

                    noProgressCount = 0;
                    foreach (var call in llmResp.ToolCalls)
                    {
                        if (totalToolCalls >= maxToolCallsTotal)
                        {
                            if (subAgentBudget is not null)
                            {
                                // 工具调用硬上限已用尽：与轮次/时间耗尽语义统一，进入收尾宽限窗口
                                // （可续跑 BudgetExhausted），而不是直接 Failed。阻止本次及剩余工具执行；
                                // 下一轮循环边界处 SubAgentBudgetLifecycle 会以 cause="tools" 注入收尾提示。
                                _logger.LogWarning(
                                    "[AgentExec] MaxToolCallsTotal={Max} reached session={Session} entering cleanup grace",
                                    maxToolCallsTotal, request.SessionId);
                                toolRoundMessages.Add(new ChatMessage(
                                    ChatRole.Tool,
                                    $"[SYSTEM] 工具调用预算已用尽（{maxToolCallsTotal} 次）。" +
                                    "系统已进入收尾宽限窗口，本工具及后续工具调用将不再执行。" +
                                    "请立即停止扩展任务、保存可恢复现场，并输出阶段性报告（SUMMARY、CHANGES、EVIDENCE、RISKS、BLOCKERS）。",
                                    ToolCallId: call.Id));
                                break;
                            }

                            _logger.LogWarning(
                                "[AgentExec] MaxToolCallsTotal={Max} reached session={Session}",
                                maxToolCallsTotal, request.SessionId);
                            stopReason = AgentLoopStopReason.MaxRoundsReached;
                            execState = AgentExecutionState.Failed;
                            break;
                        }

                        var injectedArgsJson = await _keyVaultService.InjectAsync(call.ArgumentsJson ?? "{}", ct);
                        var compatibility = HarnessToolCompatibilityAdapter.Normalize(call.Name, injectedArgsJson);
                        var canonicalCall = call with
                        {
                            Name = compatibility.ToolName,
                            ArgumentsJson = compatibility.ArgumentsJson,
                        };
                        injectedArgsJson = canonicalCall.ArgumentsJson;
                        var safeToolArgs = await _keyVaultService.StripAsync(injectedArgsJson, ct);

                        var repeatKey = $"{canonicalCall.Name}|{injectedArgsJson}";
                        toolRepeatMap.TryGetValue(repeatKey, out var repeatCount);
                        if (repeatCount >= _guardrails.MaxSameToolRepeat)
                        {
                            toolRoundMessages.Add(new ChatMessage(ChatRole.Tool,
                                $"Tool '{canonicalCall.Name}' blocked: repeated identical arguments {repeatCount} times.",
                                ToolCallId: call.Id));
                            continue;
                        }
                        toolRepeatMap[repeatKey] = repeatCount + 1;

                        totalToolCalls++;
                        await FireHooksAsync(h => h.OnToolCallAsync(loopCtx, round, canonicalCall.Name, safeToolArgs, ct));
                        ReportLiveness(request, $"tool.started:{canonicalCall.Name}");
                        var subAgentToolSw = System.Diagnostics.Stopwatch.StartNew();
                        var subAgentToolArgsHash = ComputeSha256Hash(injectedArgsJson ?? "");
                        await TryAppendSubAgentEventAsync(subAgentRunId, "subagent.tool.started", new
                        {
                            sub_agent_id = request.SessionId,
                            round = round + 1,
                            tool_call_id = call.Id,
                            tool_name = canonicalCall.Name,
                            args_hash = subAgentToolArgsHash,
                            arguments_preview = Truncate(safeToolArgs, 1024),
                            arguments_truncated = safeToolArgs.Length > 1024,
                            tool_call_index = totalToolCalls,
                        });

                        // 统一 Tool 执行服务已经按 CapabilityPolicy 做模板授权门控。
                        // 仅 legacy fallback 保留旧的用户确认占位逻辑，避免非流式路径绕过新工具注册表。
                        var skill = _skillRuntime.TryGetSkill(canonicalCall.Name);
                        SkillResult skillResult;
                        var executionBlocked = failedToolCallTracker.TryCreateBlockedResult(
                            repeatKey,
                            out skillResult);
                        if (!executionBlocked
                            && _toolInvocationService is null
                            && skill is not null
                            && !await CheckToolPermissionAsync(skill, request.SessionId, ct))
                        {
                            skillResult = new SkillResult
                            {
                                Success = false,
                                Output = "",
                                Error = $"Tool '{canonicalCall.Name}' requires user confirmation (High permission). Execution denied.",
                                ExitCode = 1,
                            };
                        }
                        else if (!executionBlocked)
                        {
                            var toolStartedAt = DateTimeOffset.UtcNow;
                            var toolSw = System.Diagnostics.Stopwatch.StartNew();
                            try
                            {
                                if (_toolInvocationService is not null)
                                {
                                    var toolResult = await _toolInvocationService.InvokeAsync(new PuddingCode.Runtime.ToolInvocationRequest
                                    {
                                        WorkspaceId = request.WorkspaceId,
                                        SessionId = request.SessionId,
                                        AgentInstanceId = instance.AgentInstanceId,
                                        ConfigurationAgentInstanceId =
                                            request.ConfigurationAgentInstanceId ?? instance.AgentInstanceId,
                                        WorkingDirectory = request.WorkingDirectory,
                                        AgentTemplateId = request.AgentTemplateId,
                                        ToolCallId = call.Id,
                                        ToolName = canonicalCall.Name,
                                        ArgumentsJson = injectedArgsJson,
                                        CapabilityPolicy = effectiveCapability,
                                        Trace = execTrace,
                                        ExecutionIdentity = request.ExecutionIdentity,
                                        ExecutionDeadlineUtc = request.ExecutionDeadlineUtc,
                                        DelegationDepth = request.DelegationDepth,
                                        MaxDelegationDepth = request.MaxDelegationDepth,
                                        AllowSubDelegation = request.AllowSubDelegation,
                                        RoleInPlan = request.RoleInPlan,
                                        ActiveTask = request.ActiveTask,
                                        CallerLlmSnapshot = request.CallerLlmSnapshot,
                                        CallerVisionHelperRoute = request.CallerVisionHelperRoute,
                                    }, ct);
                                    skillResult = new SkillResult
                                    {
                                        Success = toolResult.Success,
                                        Output = toolResult.Output ?? "",
                                        Error = toolResult.Error,
                                        ExitCode = toolResult.Success ? 0 : 1,
                                        ContentParts = toolResult.ToolContentParts,
                                    };
                                }
                                else
                                {
                                    // ADR-027 legacy fallback for tests only (SkillRuntime)
                                    skillResult = await _skillRuntime.InvokeAsync(
                                        canonicalCall.Name,
                                        new SkillInvokeRequest
                                        {
                                            AgentInstanceId = instance.AgentInstanceId,
                                            WorkspaceId = request.WorkspaceId,
                                            SessionId = request.SessionId,
                                            Input = ExtractInputFromJson(injectedArgsJson),
                                            Parameters = ExtractParametersFromJson(injectedArgsJson),
                                        },
                                        effectiveCapability,
                                        ct);
                                }
                                toolSw.Stop();
                                var toolArgsHash = ComputeSha256Hash(injectedArgsJson ?? "");
                                if (skillResult.Success)
                                {
                                    await RecordActivityAsync(
                                        execTrace,
                                        component: RuntimeActivityComponents.ToolRunner,
                                        operation: "execute_tool",
                                        status: RuntimeActivityStatuses.Succeeded,
                                        toolStartedAt,
                                        endedAt: DateTimeOffset.UtcNow,
                                        durationMs: toolSw.ElapsedMilliseconds,
                                        summary: $"Tool '{canonicalCall.Name}' executed successfully.",
                                        metadata: new Dictionary<string, string>
                                        {
                                            ["tool_name"] = canonicalCall.Name,
                                            ["tool_args_hash"] = toolArgsHash,
                                            ["tool_args_length"] = (injectedArgsJson?.Length ?? 0).ToString(),
                                            ["tool_duration_ms"] = toolSw.ElapsedMilliseconds.ToString(),
                                            ["tool_output_length"] = (skillResult.Output?.Length ?? 0).ToString(),
                                            ["session_id"] = request.SessionId,
                                        },
                                        error: null,
                                        ct: CancellationToken.None);
                                    await RecordToolMetricAsync(
                                        execTrace,
                                        canonicalCall.Name,
                                        call.Id,
                                        instance.AgentInstanceId,
                                        request.SessionId,
                                        round,
                                        totalToolCalls,
                                        toolStartedAt,
                                        toolSw.ElapsedMilliseconds,
                                        RuntimeActivityStatuses.Succeeded,
                                        injectedArgsJson,
                                        safeToolArgs,
                                        skillResult,
                                        error: null,
                                        ct: CancellationToken.None);
                                }
                                else
                                {
                                    await RecordActivityAsync(
                                        execTrace,
                                        component: RuntimeActivityComponents.ToolRunner,
                                        operation: "execute_tool",
                                        status: RuntimeActivityStatuses.Failed,
                                        toolStartedAt,
                                        endedAt: DateTimeOffset.UtcNow,
                                        durationMs: toolSw.ElapsedMilliseconds,
                                        summary: $"Tool '{canonicalCall.Name}' execution failed.",
                                        metadata: new Dictionary<string, string>
                                        {
                                            ["tool_name"] = canonicalCall.Name,
                                            ["tool_args_hash"] = toolArgsHash,
                                            ["tool_args_length"] = (injectedArgsJson?.Length ?? 0).ToString(),
                                            ["tool_duration_ms"] = toolSw.ElapsedMilliseconds.ToString(),
                                            ["error_code"] = "tool_failed",
                                            ["error_message"] = Truncate(skillResult.Error ?? "", 500),
                                            ["session_id"] = request.SessionId,
                                        },
                                        error: new Exception(skillResult.Error),
                                        ct: CancellationToken.None);
                                    await RecordToolMetricAsync(
                                        execTrace,
                                        canonicalCall.Name,
                                        call.Id,
                                        instance.AgentInstanceId,
                                        request.SessionId,
                                        round,
                                        totalToolCalls,
                                        toolStartedAt,
                                        toolSw.ElapsedMilliseconds,
                                        RuntimeActivityStatuses.Failed,
                                        injectedArgsJson,
                                        safeToolArgs,
                                        skillResult,
                                        error: null,
                                        ct: CancellationToken.None);
                                }
                                _logger.LogInformation(
                                    "[AgentExec:ToolAudit] Tool={ToolName} Success={Success} DurationMs={DurationMs} ArgsHash={ArgsHash} OutputLen={OutputLen} Session={SessionId}",
                                    canonicalCall.Name, skillResult.Success, toolSw.ElapsedMilliseconds, toolArgsHash,
                                    skillResult.Output?.Length ?? 0, request.SessionId);
                            }
                            catch (Exception ex)
                            {
                                toolSw.Stop();
                                var toolArgsHash = ComputeSha256Hash(injectedArgsJson ?? "");
                                await RecordActivityAsync(
                                    execTrace,
                                    component: RuntimeActivityComponents.ToolRunner,
                                    operation: "execute_tool",
                                    status: RuntimeActivityStatuses.Failed,
                                    toolStartedAt,
                                    endedAt: DateTimeOffset.UtcNow,
                                    durationMs: toolSw.ElapsedMilliseconds,
                                    summary: $"Tool '{canonicalCall.Name}' threw exception.",
                                    metadata: new Dictionary<string, string>
                                    {
                                        ["tool_name"] = canonicalCall.Name,
                                        ["tool_args_hash"] = toolArgsHash,
                                        ["tool_args_length"] = (injectedArgsJson?.Length ?? 0).ToString(),
                                        ["tool_duration_ms"] = toolSw.ElapsedMilliseconds.ToString(),
                                        ["error_code"] = ex.GetType().Name,
                                        ["error_message"] = Truncate(ex.Message, 500),
                                        ["session_id"] = request.SessionId,
                                    },
                                    error: ex,
                                    ct: CancellationToken.None);
                                await RecordToolMetricAsync(
                                    execTrace,
                                    canonicalCall.Name,
                                    call.Id,
                                    instance.AgentInstanceId,
                                    request.SessionId,
                                    round,
                                    totalToolCalls,
                                    toolStartedAt,
                                    toolSw.ElapsedMilliseconds,
                                    RuntimeActivityStatuses.Failed,
                                    injectedArgsJson,
                                    safeToolArgs,
                                    result: null,
                                    error: ex,
                                    ct: CancellationToken.None);
                                _logger.LogError(ex,
                                    "[AgentExec:ToolAudit] Tool={ToolName} Exception DurationMs={DurationMs} ArgsHash={ArgsHash} Session={SessionId}",
                                    canonicalCall.Name, toolSw.ElapsedMilliseconds, toolArgsHash, request.SessionId);
                                ObserveToolExecutionFacts(
                                    canonicalCall.Name,
                                    success: false,
                                    output: null,
                                    error: ex.Message,
                                    ref toolFailureCount,
                                    ref toolOutputTruncatedCount,
                                    ref toolOutputChars,
                                    ref firstToolFailureSummary);
                                subAgentToolSw.Stop();
                                await TryAppendSubAgentEventAsync(subAgentRunId, "subagent.tool.failed", new
                                {
                                    sub_agent_id = request.SessionId,
                                    round = round + 1,
                                    tool_call_id = call.Id,
                                    tool_name = canonicalCall.Name,
                                    success = false,
                                    duration_ms = subAgentToolSw.ElapsedMilliseconds,
                                    args_hash = subAgentToolArgsHash,
                                    output_length = 0,
                                    error = Truncate(ex.Message, 500),
                                    tool_call_index = totalToolCalls,
                                });
                                if (_subAgentRunStore is not null && subAgentRunId is not null)
                                {
                                    await _subAgentRunStore.AppendToolAuditAsync(
                                        subAgentRunId,
                                        new SubAgentToolAuditEntry
                                        {
                                            ToolCallId = call.Id,
                                            ToolName = canonicalCall.Name,
                                            ArgsHash = subAgentToolArgsHash,
                                            Success = false,
                                            DurationMs = subAgentToolSw.ElapsedMilliseconds,
                                            OutputLength = 0,
                                            ErrorMessage = Truncate(ex.Message, 500),
                                        },
                                        CancellationToken.None);
                                }
                                throw;
                            }
                        }
                        if (!executionBlocked)
                            skillResult = failedToolCallTracker.Observe(repeatKey, skillResult);
                        else
                            await RecordToolMetricAsync(
                                execTrace,
                                canonicalCall.Name,
                                call.Id,
                                instance.AgentInstanceId,
                                request.SessionId,
                                round,
                                totalToolCalls,
                                DateTimeOffset.UtcNow,
                                0,
                                RuntimeActivityStatuses.Failed,
                                injectedArgsJson,
                                safeToolArgs,
                                skillResult,
                                error: null,
                                ct: CancellationToken.None);

                        ObserveToolExecutionFacts(
                            canonicalCall.Name,
                            skillResult.Success,
                            skillResult.Output,
                            skillResult.Error,
                            ref toolFailureCount,
                            ref toolOutputTruncatedCount,
                            ref toolOutputChars,
                            ref firstToolFailureSummary);
                        subAgentToolSw.Stop();
                        var safeSubAgentToolOutput = string.IsNullOrEmpty(skillResult.Output)
                            ? skillResult.Output ?? ""
                            : await _keyVaultService.StripAsync(skillResult.Output, ct);
                        var safeToolError = string.IsNullOrWhiteSpace(skillResult.Error)
                            ? skillResult.Error
                            : await _keyVaultService.StripAsync(skillResult.Error, ct);
                        ReportMeaningfulProgress(
                            request,
                            $"tool.completed:{canonicalCall.Name}",
                            $"{canonicalCall.Name}\u001f{safeToolArgs}\u001f{safeSubAgentToolOutput}\u001f{safeToolError}");

                        await TryAppendSubAgentEventAsync(
                            subAgentRunId,
                            skillResult.Success
                                ? "subagent.tool.completed"
                                : "subagent.tool.failed",
                            new
                            {
                                sub_agent_id = request.SessionId,
                                round = round + 1,
                                tool_call_id = call.Id,
                                tool_name = canonicalCall.Name,
                                success = skillResult.Success,
                                duration_ms = subAgentToolSw.ElapsedMilliseconds,
                                args_hash = subAgentToolArgsHash,
                                output_length = skillResult.Output?.Length ?? 0,
                                output_preview = Truncate(safeSubAgentToolOutput, 2048),
                                output_truncated = safeSubAgentToolOutput.Length > 2048,
                                error = string.IsNullOrWhiteSpace(safeToolError)
                                    ? null
                                    : Truncate(safeToolError, 500),
                                tool_call_index = totalToolCalls,
                            });
                        if (_subAgentRunStore is not null && subAgentRunId is not null)
                        {
                            await _subAgentRunStore.AppendToolAuditAsync(
                                subAgentRunId,
                                new SubAgentToolAuditEntry
                                {
                                    ToolCallId = call.Id,
                                    ToolName = canonicalCall.Name,
                                    ArgsHash = subAgentToolArgsHash,
                                    Success = skillResult.Success,
                                    DurationMs = subAgentToolSw.ElapsedMilliseconds,
                                    OutputLength = skillResult.Output?.Length ?? 0,
                                    ErrorMessage = string.IsNullOrWhiteSpace(safeToolError)
                                        ? null
                                        : Truncate(safeToolError, 500),
                                },
                                CancellationToken.None);
                        }

                        await FireHooksAsync(h => h.OnToolResultAsync(loopCtx, round, canonicalCall.Name, skillResult, ct));

                        var newlyLoadedToolCount = ToolExposurePlanner.RegisterSearchResult(
                            canonicalCall.Name,
                            skillResult.Success,
                            skillResult.Output,
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
                                "[AgentExec:ToolDiscovery] Loaded {AddedCount} and promoted {PromotedCount} tool definition(s) for next LLM round session={Session} visibleToolCount={VisibleToolCount} loadedTools={LoadedTools}",
                                newlyLoadedToolCount,
                                promotedToolCount,
                                request.SessionId,
                                llmTools.Count,
                                SummarizeToolNames(loadedToolIds));
                        }
                        var toolDiscoveryStalled = toolDiscoveryLoopTracker.Observe(canonicalCall.Name);

                        var toolPayloadRaw = skillResult.Success
                            ? $"✅ Tool '{canonicalCall.Name}' succeeded (exit={skillResult.ExitCode}):\n{skillResult.Output}"
                            : BuildToolFailurePayload(canonicalCall.Name, skillResult, request.SessionId, isPermissionError:
                                skillResult.Error?.Contains("permission", StringComparison.OrdinalIgnoreCase) == true ||
                                skillResult.Error?.Contains("not allowed", StringComparison.OrdinalIgnoreCase) == true ||
                                skillResult.Error?.Contains("rejected", StringComparison.OrdinalIgnoreCase) == true);
                        var toolPayload = await ToolResultContextPolicy.MaterializeAsync(
                            toolPayloadRaw,
                            request.WorkingDirectory,
                            request.SessionId,
                            canonicalCall.Name,
                            call.Id,
                            _logger,
                            ct);

                        toolRoundMessages.Add(new ChatMessage(ChatRole.Tool, toolPayload, ToolCallId: call.Id,
                            ContentParts: skillResult.ContentParts));

                        _journal.Record(request.SessionId, new TurnRecord
                        {
                            Round = round,
                            StartedAt = turnStart,
                            CompletedAt = DateTimeOffset.UtcNow,
                            Status = "CONTINUE",
                            MessageSummary = Truncate(rawText, 512),
                            ToolName = canonicalCall.Name,
                            ToolArgs = safeToolArgs,
                            ToolSuccess = skillResult.Success,
                            ToolError = safeToolError,
                        });
                        if (toolDiscoveryStalled)
                        {
                            executionError = BuildToolDiscoveryStalledMessage(
                                toolDiscoveryLoopTracker.ConsecutiveCalls);
                            finalMessage = executionError;
                            stopReason = AgentLoopStopReason.Failed;
                            execState = AgentExecutionState.Failed;
                            subAgentTerminalStatus = "failed";
                            _logger.LogError(
                                "[AgentExec:ToolDiscovery] {Error} session={Session} round={Round}",
                                executionError,
                                request.SessionId,
                                round + 1);
                            await TryAppendSubAgentEventAsync(
                                subAgentRunId,
                                "subagent.tool_discovery.stalled",
                                new
                                {
                                    sub_agent_id = request.SessionId,
                                    round = round + 1,
                                    consecutive_search_tools = toolDiscoveryLoopTracker.ConsecutiveCalls,
                                    visible_tool_count = llmTools.Count,
                                    loaded_tool_count = loadedToolIds.Count,
                                });
                            break;
                        }
                    }

                    if (execState == AgentExecutionState.Failed)
                        break;

                    // History is a provider protocol document. Publish the
                    // assistant tool-call batch and all matching results as one
                    // atomic unit so cancellation/guardrail failures cannot
                    // expose a half-written round to the next execution.
                    history.AddRange(toolRoundMessages);
                    await TryAppendSubAgentEventAsync(subAgentRunId, "subagent.round.completed", new
                    {
                        sub_agent_id = request.SessionId,
                        round = round + 1,
                        status = "continue",
                        tool_calls = totalToolCalls,
                    });
                    continue;
                }

                history.Add(new ChatMessage(ChatRole.Assistant, rawText,
                    ReasoningContent: llmResp.ReasoningContent,
                    ContinuationState: llmResp.ContinuationState));

                // Steering may arrive while the model is producing what would otherwise be
                // the final response. Consume it at this safe boundary and keep the same Turn
                // alive for one more model request instead of leaking it into a later Turn.
                if (round < maxRounds - 1)
                {
                    var lateSteeringCount = await TryInjectSteeringMessageAsync(
                        request,
                        instance.AgentInstanceId,
                        history,
                        round,
                        execTrace,
                        ct);
                    if (lateSteeringCount > 0)
                    {
                        _journal.Record(request.SessionId, new TurnRecord
                        {
                            Round = round,
                            StartedAt = turnStart,
                            CompletedAt = DateTimeOffset.UtcNow,
                            Status = "CONTINUE",
                            MessageSummary = $"Applied {lateSteeringCount} late steering message(s).",
                        });
                        _logger.LogInformation(
                            "[AgentExec:Steering] Continuing after {Count} late steering message(s) session={Session} round={Round}",
                            lateSteeringCount,
                            request.SessionId,
                            round + 1);
                        continue;
                    }
                }

                var loopResp = AgentLoopResponse.Parse(rawText);
                finalMessage = loopResp.Message ?? rawText;
                if (expectedOutputTracker.ShouldAutoComplete(loopResp, finalMessage))
                {
                    loopResp = new AgentLoopResponse
                    {
                        Status = "DONE",
                        Message = finalMessage,
                        Meta = new AgentLoopMeta
                        {
                            Reason = "Runtime accepted an unstructured response that satisfied the expected output contract.",
                            Confidence = 1,
                        },
                    };
                    _logger.LogInformation(
                        "[AgentExec] Promoted contract-complete plain output to DONE session={Session} round={Round}",
                        request.SessionId,
                        round + 1);
                    await TryAppendSubAgentEventAsync(subAgentRunId, "subagent.output_contract.completed", new
                    {
                        sub_agent_id = request.SessionId,
                        round = round + 1,
                        completion_source = "canonical_plain_text",
                    });
                }
                else
                {
                    expectedOutputTracker.Observe(finalMessage);
                }

                await FireHooksAsync(h => h.OnRoundCompleteAsync(loopCtx, round, loopResp, ct));
                await TryAppendSubAgentEventAsync(subAgentRunId, "subagent.round.completed", new
                {
                    sub_agent_id = request.SessionId,
                    round = round + 1,
                    status = loopResp.Status,
                    tool_calls = totalToolCalls,
                });

                // ── CompletionPolicy 裁决 ─────────────────────────────
                var verdict = _completionPolicy.Evaluate(
                    loopCtx, loopResp, _journal.GetTurns(request.SessionId),
                    ct.IsCancellationRequested,
                    _controlRegistry.IsFrozen(request.SessionId));

                if (verdict == CompletionVerdict.Completed)
                {
                    if (expectedOutputTracker.RestoreIfFinalIsIncomplete(ref finalMessage))
                    {
                        _logger.LogWarning(
                            "[AgentExec] Restored prior contract-complete output session={Session} round={Round}",
                            request.SessionId,
                            round + 1);
                    }

                    _journal.Record(request.SessionId, new TurnRecord
                    {
                        Round = round, StartedAt = turnStart, CompletedAt = DateTimeOffset.UtcNow,
                        Status = "DONE", MessageSummary = Truncate(finalMessage, 512),
                    });
                    stopReason = AgentLoopStopReason.Done;
                    execState  = AgentExecutionState.Completed;
                    _logger.LogInformation(
                        "[AgentExec] DONE round={Round} session={Session}", round + 1, request.SessionId);
                    await FireHooksAsync(h => h.OnCompletedAsync(loopCtx, finalMessage, ct));
                    break;
                }

                if (verdict == CompletionVerdict.Waiting)
                {
                    _journal.Record(request.SessionId, new TurnRecord
                    {
                        Round = round, StartedAt = turnStart, CompletedAt = DateTimeOffset.UtcNow,
                        Status = "WAIT", MessageSummary = Truncate(finalMessage, 512),
                    });
                    stopReason = AgentLoopStopReason.Waiting;
                    execState  = AgentExecutionState.WaitingEvent;

                    // 生成 ResumeAnchor，供 Controller 在条件命中后唤醒
                    resumeAnchorId = Guid.NewGuid().ToString("N");
                    _journal.SetAnchor(request.SessionId, new ResumeAnchor
                    {
                        AnchorId  = resumeAnchorId,
                        SessionId = request.SessionId,
                        CreatedAt = DateTimeOffset.UtcNow,
                        WaitType  = nameof(AgentExecutionState.WaitingEvent),
                        WaitReason = loopResp.Meta?.Reason,
                        LastRound = round,
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
                        ActiveTask = request.ActiveTask,
                    });
                    _logger.LogInformation(
                        "[AgentExec] WAIT round={Round} session={Session} reason={Reason} anchorId={AnchorId}",
                        round + 1, request.SessionId, loopResp.Meta?.Reason, resumeAnchorId);
                    _sessionManager.MarkWaitingEvent(request.SessionId);
                    await FireHooksAsync(h => h.OnWaitingAsync(loopCtx, loopResp, ct));
                    break;
                }

                if (verdict == CompletionVerdict.Failed)
                {
                    _journal.Record(request.SessionId, new TurnRecord
                    {
                        Round = round, StartedAt = turnStart, CompletedAt = DateTimeOffset.UtcNow,
                        Status = "FAILED", MessageSummary = Truncate(finalMessage, 512),
                    });
                    stopReason = AgentLoopStopReason.Failed;
                    execState  = AgentExecutionState.Failed;
                    _logger.LogWarning(
                        "[AgentExec] FAILED round={Round} session={Session} reason={Reason}",
                        round + 1, request.SessionId, loopResp.Meta?.Reason);
                    await FireHooksAsync(h => h.OnFailedAsync(
                        loopCtx, loopResp.Meta?.Reason ?? "Agent signaled FAILED", null, ct));
                    break;
                }

                if (verdict == CompletionVerdict.Cancelled)
                {
                    _journal.Record(request.SessionId, new TurnRecord
                    {
                        Round = round, StartedAt = turnStart, CompletedAt = DateTimeOffset.UtcNow,
                        Status = "CANCELLED",
                    });
                    stopReason = AgentLoopStopReason.Cancelled;
                    execState  = AgentExecutionState.Cancelled;
                    await FireHooksAsync(h => h.OnCancelledAsync(loopCtx, ct));
                    break;
                }

                // ── CONTINUE：执行工具调用（可选）────────────────────────
                string? toolName    = loopResp.Tool?.Name;
                string? toolArgs    = null;
                bool?   toolSuccess = null;
                string? toolError   = null;

                if (!string.IsNullOrEmpty(toolName))
                {
                    noProgressCount = 0; // 有工具调用，重置无进展计数
                    // 检查点 C：总工具调用次数上限
                    if (totalToolCalls >= maxToolCallsTotal)
                    {
                        if (subAgentBudget is not null)
                        {
                            // 工具调用硬上限已用尽：与轮次/时间耗尽语义统一，进入收尾宽限窗口
                            // （可续跑 BudgetExhausted），而不是直接 Failed。阻止本次工具执行；
                            // 下一轮循环边界处 SubAgentBudgetLifecycle 会以 cause="tools" 注入收尾提示。
                            _logger.LogWarning(
                                "[AgentExec] MaxToolCallsTotal={Max} reached session={Session} entering cleanup grace",
                                maxToolCallsTotal, request.SessionId);
                            history.Add(new ChatMessage(ChatRole.User,
                                $"[SYSTEM] 工具调用预算已用尽（{maxToolCallsTotal} 次）。" +
                                "系统已进入收尾宽限窗口，后续工具调用将不再执行。" +
                                "请立即停止扩展任务、保存可恢复现场，并输出阶段性报告（SUMMARY、CHANGES、EVIDENCE、RISKS、BLOCKERS）。"));
                            _journal.Record(request.SessionId, new TurnRecord
                            {
                                Round = round, StartedAt = turnStart, CompletedAt = DateTimeOffset.UtcNow,
                                Status = "CONTINUE", MessageSummary = Truncate(finalMessage, 512),
                                ToolName = toolName, ToolArgs = toolArgs,
                                ToolSuccess = false, ToolError = "MaxToolCallsTotal reached (cleanup grace)",
                            });
                            continue;
                        }

                        _logger.LogWarning(
                            "[AgentExec] MaxToolCallsTotal={Max} reached session={Session}",
                            maxToolCallsTotal, request.SessionId);
                        await FireHooksAsync(h => h.OnMaxRoundsReachedAsync(loopCtx, ct));
                        stopReason = AgentLoopStopReason.MaxRoundsReached;
                        execState  = AgentExecutionState.Failed;
                        break;
                    }

                    var argsJson = loopResp.Tool!.Args?.GetRawText() ?? "{}";
                    var injectedArgsJson = await _keyVaultService.InjectAsync(argsJson, ct);
                    var compatibility = HarnessToolCompatibilityAdapter.Normalize(toolName, injectedArgsJson);
                    toolName = compatibility.ToolName;
                    injectedArgsJson = compatibility.ArgumentsJson;
                    toolArgs = await _keyVaultService.StripAsync(injectedArgsJson, ct);

                    // 检查点 D：相同工具相同参数重复次数
                    var repeatKey = $"{toolName}|{injectedArgsJson}";
                    toolRepeatMap.TryGetValue(repeatKey, out var repeatCount);
                    if (repeatCount >= _guardrails.MaxSameToolRepeat)
                    {
                        _logger.LogWarning(
                            "[AgentExec] Tool={Tool} repeated {Count}x session={Session}",
                            toolName, repeatCount, request.SessionId);
                        history.Add(new ChatMessage(ChatRole.User,
                            $"[SYSTEM] Tool '{toolName}' has been called with identical arguments {repeatCount} times. " +
                            "This approach is not progressing. Try a different approach, or output status=FAILED if unable to proceed."));
                        _journal.Record(request.SessionId, new TurnRecord
                        {
                            Round = round, StartedAt = turnStart, CompletedAt = DateTimeOffset.UtcNow,
                            Status = "CONTINUE", MessageSummary = Truncate(finalMessage, 512),
                            ToolName = toolName, ToolArgs = toolArgs,
                            ToolSuccess = false, ToolError = "MaxSameToolRepeat reached",
                        });
                        continue; // 给 LLM 机会换策略，不计入轮次工具调用
                    }
                    toolRepeatMap[repeatKey] = repeatCount + 1;

                    await FireHooksAsync(h => h.OnToolCallAsync(loopCtx, round, toolName, toolArgs, ct));
                    ReportLiveness(request, $"tool.started:{toolName}");
                    totalToolCalls++;

                    // 检查点 E：工具执行前再次检查取消
                    ct.ThrowIfCancellationRequested();

                    _logger.LogInformation(
                        "[AgentExec] ToolCall tool={Tool} round={Round} agent={Agent}",
                        toolName, round + 1, instance.AgentInstanceId);

                    var toolStartedAt2 = DateTimeOffset.UtcNow;
                    var toolSw2 = System.Diagnostics.Stopwatch.StartNew();
                    SkillResult skillResult;
                    try
                    {
                        var executionBlocked = failedToolCallTracker.TryCreateBlockedResult(
                            repeatKey,
                            out skillResult);
                        if (!executionBlocked && _toolInvocationService is not null)
                        {
                            var toolResult = await _toolInvocationService.InvokeAsync(new PuddingCode.Runtime.ToolInvocationRequest
                            {
                                WorkspaceId = request.WorkspaceId,
                                SessionId = request.SessionId,
                                AgentInstanceId = instance.AgentInstanceId,
                                ConfigurationAgentInstanceId =
                                    request.ConfigurationAgentInstanceId ?? instance.AgentInstanceId,
                                WorkingDirectory = request.WorkingDirectory,
                                AgentTemplateId = request.AgentTemplateId,
                                ToolCallId = toolName, // CONTINUE 路径没有独立 toolCallId
                                ToolName = toolName,
                                ArgumentsJson = injectedArgsJson,
                                CapabilityPolicy = effectiveCapability,
                                Trace = execTrace,
                                ExecutionIdentity = request.ExecutionIdentity,
                                ExecutionDeadlineUtc = request.ExecutionDeadlineUtc,
                                DelegationDepth = request.DelegationDepth,
                                MaxDelegationDepth = request.MaxDelegationDepth,
                                AllowSubDelegation = request.AllowSubDelegation,
                                RoleInPlan = request.RoleInPlan,
                                ActiveTask = request.ActiveTask,
                            }, ct);
                            skillResult = new SkillResult
                            {
                                Success = toolResult.Success,
                                Output = toolResult.Output ?? "",
                                Error = toolResult.Error,
                                ExitCode = toolResult.Success ? 0 : 1,
                            };
                        }
                        else if (!executionBlocked)
                        {
                            // ADR-027 legacy fallback for tests only (SkillRuntime)
                            skillResult = await _skillRuntime.InvokeAsync(
                                toolName,
                                new SkillInvokeRequest
                                {
                                    AgentInstanceId = instance.AgentInstanceId,
                                    WorkspaceId     = request.WorkspaceId,
                                    SessionId       = request.SessionId,
                                    Input           = ExtractInputFromJson(injectedArgsJson),
                                    Parameters      = ExtractParametersFromJson(injectedArgsJson),
                                },
                                effectiveCapability, ct);
                        }
                        if (!executionBlocked)
                            skillResult = failedToolCallTracker.Observe(repeatKey, skillResult);
                        toolSw2.Stop();
                        ReportMeaningfulProgress(
                            request,
                            $"tool.completed:{toolName}",
                            $"{toolName}\u001f{toolArgs}\u001f{skillResult.Output}\u001f{skillResult.Error}");
                        var toolArgsHash2 = ComputeSha256Hash(injectedArgsJson ?? "");
                        if (skillResult.Success)
                        {
                            await RecordActivityAsync(
                                execTrace,
                                component: RuntimeActivityComponents.ToolRunner,
                                operation: "execute_tool",
                                status: RuntimeActivityStatuses.Succeeded,
                                toolStartedAt2,
                                endedAt: DateTimeOffset.UtcNow,
                                durationMs: toolSw2.ElapsedMilliseconds,
                                summary: $"Tool '{toolName}' executed successfully.",
                                metadata: new Dictionary<string, string>
                                {
                                    ["tool_name"] = toolName,
                                    ["tool_args_hash"] = toolArgsHash2,
                                    ["tool_args_length"] = (injectedArgsJson?.Length ?? 0).ToString(),
                                    ["tool_duration_ms"] = toolSw2.ElapsedMilliseconds.ToString(),
                                    ["tool_output_length"] = (skillResult.Output?.Length ?? 0).ToString(),
                                    ["session_id"] = request.SessionId,
                                },
                                error: null,
                                ct: CancellationToken.None);
                            await RecordToolMetricAsync(
                                execTrace,
                                toolName,
                                toolName,
                                instance.AgentInstanceId,
                                request.SessionId,
                                round,
                                totalToolCalls,
                                toolStartedAt2,
                                toolSw2.ElapsedMilliseconds,
                                RuntimeActivityStatuses.Succeeded,
                                injectedArgsJson,
                                toolArgs,
                                skillResult,
                                error: null,
                                ct: CancellationToken.None);
                        }
                        else
                        {
                            await RecordActivityAsync(
                                execTrace,
                                component: RuntimeActivityComponents.ToolRunner,
                                operation: "execute_tool",
                                status: RuntimeActivityStatuses.Failed,
                                toolStartedAt2,
                                endedAt: DateTimeOffset.UtcNow,
                                durationMs: toolSw2.ElapsedMilliseconds,
                                summary: $"Tool '{toolName}' execution failed.",
                                metadata: new Dictionary<string, string>
                                {
                                    ["tool_name"] = toolName,
                                    ["tool_args_hash"] = toolArgsHash2,
                                    ["tool_args_length"] = (injectedArgsJson?.Length ?? 0).ToString(),
                                    ["tool_duration_ms"] = toolSw2.ElapsedMilliseconds.ToString(),
                                    ["error_code"] = "tool_failed",
                                    ["error_message"] = Truncate(skillResult.Error ?? "", 500),
                                    ["session_id"] = request.SessionId,
                                },
                                error: new Exception(skillResult.Error),
                                ct: CancellationToken.None);
                            await RecordToolMetricAsync(
                                execTrace,
                                toolName,
                                toolName,
                                instance.AgentInstanceId,
                                request.SessionId,
                                round,
                                totalToolCalls,
                                toolStartedAt2,
                                toolSw2.ElapsedMilliseconds,
                                RuntimeActivityStatuses.Failed,
                                injectedArgsJson,
                                toolArgs,
                                skillResult,
                                error: null,
                                ct: CancellationToken.None);
                        }
                        _logger.LogInformation(
                            "[AgentExec:ToolAudit] Tool={ToolName} Success={Success} DurationMs={DurationMs} ArgsHash={ArgsHash} OutputLen={OutputLen} Session={SessionId}",
                            toolName, skillResult.Success, toolSw2.ElapsedMilliseconds, toolArgsHash2,
                            skillResult.Output?.Length ?? 0, request.SessionId);
                    }
                    catch (Exception ex)
                    {
                        toolSw2.Stop();
                        var toolArgsHash2 = ComputeSha256Hash(injectedArgsJson ?? "");
                        await RecordActivityAsync(
                            execTrace,
                            component: RuntimeActivityComponents.ToolRunner,
                            operation: "execute_tool",
                            status: RuntimeActivityStatuses.Failed,
                            toolStartedAt2,
                            endedAt: DateTimeOffset.UtcNow,
                            durationMs: toolSw2.ElapsedMilliseconds,
                            summary: $"Tool '{toolName}' threw exception.",
                            metadata: new Dictionary<string, string>
                            {
                                ["tool_name"] = toolName,
                                ["tool_args_hash"] = toolArgsHash2,
                                ["tool_args_length"] = (injectedArgsJson?.Length ?? 0).ToString(),
                                ["tool_duration_ms"] = toolSw2.ElapsedMilliseconds.ToString(),
                                ["error_code"] = ex.GetType().Name,
                                ["error_message"] = Truncate(ex.Message, 500),
                                ["session_id"] = request.SessionId,
                            },
                            error: ex,
                            ct: CancellationToken.None);
                        await RecordToolMetricAsync(
                            execTrace,
                            toolName,
                            toolName,
                            instance.AgentInstanceId,
                            request.SessionId,
                            round,
                            totalToolCalls,
                            toolStartedAt2,
                            toolSw2.ElapsedMilliseconds,
                            RuntimeActivityStatuses.Failed,
                            injectedArgsJson,
                            toolArgs,
                            result: null,
                            error: ex,
                            ct: CancellationToken.None);
                        _logger.LogError(ex,
                            "[AgentExec:ToolAudit] Tool={ToolName} Exception DurationMs={DurationMs} ArgsHash={ArgsHash} Session={SessionId}",
                            toolName, toolSw2.ElapsedMilliseconds, toolArgsHash2, request.SessionId);
                        ObserveToolExecutionFacts(
                            toolName,
                            success: false,
                            output: null,
                            error: ex.Message,
                            ref toolFailureCount,
                            ref toolOutputTruncatedCount,
                            ref toolOutputChars,
                            ref firstToolFailureSummary);
                        throw;
                    }

                    ObserveToolExecutionFacts(
                        toolName,
                        skillResult.Success,
                        skillResult.Output,
                        skillResult.Error,
                        ref toolFailureCount,
                        ref toolOutputTruncatedCount,
                        ref toolOutputChars,
                        ref firstToolFailureSummary);

                    toolSuccess = skillResult.Success;
                    toolError = skillResult.Error;

                    await FireHooksAsync(h => h.OnToolResultAsync(loopCtx, round, toolName, skillResult, ct));

                    var newlyLoadedToolCount = ToolExposurePlanner.RegisterSearchResult(
                        toolName,
                        skillResult.Success,
                        skillResult.Output,
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
                            "[AgentExec:ToolDiscovery] Loaded {AddedCount} and promoted {PromotedCount} tool definition(s) for next LLM round session={Session} visibleToolCount={VisibleToolCount} loadedTools={LoadedTools}",
                            newlyLoadedToolCount,
                            promotedToolCount,
                            request.SessionId,
                            llmTools.Count,
                            SummarizeToolNames(loadedToolIds));
                    }
                    var toolDiscoveryStalled = toolDiscoveryLoopTracker.Observe(toolName);

                    var toolMsgRaw = skillResult.Success
                        ? $"✅ Tool '{toolName}' succeeded (exit={skillResult.ExitCode}):\n{skillResult.Output}"
                        : $"❌ Tool '{toolName}' FAILED (exit={skillResult.ExitCode})\n" +
                          $"   Error: {skillResult.Error}\n" +
                          $"   💡 Suggestion: Try an alternative approach or use a different tool if this one has access restrictions.";
                    var toolMsg = await ToolResultContextPolicy.MaterializeAsync(
                        toolMsgRaw,
                        request.WorkingDirectory,
                        request.SessionId,
                        toolName,
                        $"legacy-{round + 1}-{totalToolCalls}",
                        _logger,
                        ct);
                    history.Add(new ChatMessage(ChatRole.User, toolMsg));

                    if (toolDiscoveryStalled)
                    {
                        executionError = BuildToolDiscoveryStalledMessage(
                            toolDiscoveryLoopTracker.ConsecutiveCalls);
                        finalMessage = executionError;
                        stopReason = AgentLoopStopReason.Failed;
                        execState = AgentExecutionState.Failed;
                        subAgentTerminalStatus = "failed";
                        _logger.LogError(
                            "[AgentExec:ToolDiscovery] {Error} session={Session} round={Round}",
                            executionError,
                            request.SessionId,
                            round + 1);
                    }
                }

                // 无工具调用的 CONTINUE —— 计入无进展计数
                if (string.IsNullOrEmpty(toolName))
                {
                    noProgressCount++;
                    if (noProgressCount >= _guardrails.MaxNoProgressRounds)
                    {
                        _logger.LogWarning(
                            "[AgentExec] NoProgress {Count} rounds session={Session}",
                            noProgressCount, request.SessionId);
                        history.Add(new ChatMessage(ChatRole.User,
                            $"[SYSTEM] The last {noProgressCount} rounds produced no tool calls or actionable progress. " +
                            "Either invoke a tool to advance the task, output status=DONE with the delivered result, " +
                            "or output status=FAILED if you are unable to proceed."));
                        noProgressCount = 0;
                    }
                }

                _journal.Record(request.SessionId, new TurnRecord
                {
                    Round          = round,
                    StartedAt      = turnStart,
                    CompletedAt    = DateTimeOffset.UtcNow,
                    Status         = "CONTINUE",
                    MessageSummary = Truncate(finalMessage, 512),
                    ToolName       = toolName,
                    ToolArgs       = toolArgs,
                    ToolSuccess    = toolSuccess,
                    ToolError      = toolError,
                });

                if (execState == AgentExecutionState.Failed)
                    break;

                // 最后一轮 CONTINUE → MaxRoundsReached
                if (subAgentBudget is null && round == maxRounds - 1)
                {
                    _logger.LogWarning(
                        "[AgentExec] MaxRounds={Max} reached session={Session}",
                        _guardrails.MaxRounds, request.SessionId);
                    stopReason = AgentLoopStopReason.MaxRoundsReached;
                    execState  = AgentExecutionState.Failed;
                    await FireHooksAsync(h => h.OnMaxRoundsReachedAsync(loopCtx, ct));
                }
            }
        }
        catch (OperationCanceledException)
        {
            var runtimeControlState = _runtimeControl?.GetStatus(request.SessionId).Session;
            if (runtimeControlState?.State == SessionState.Faulted)
            {
                runtimeFuseFaulted = true;
                stopReason = AgentLoopStopReason.Failed;
                execState = AgentExecutionState.Failed;
                subAgentTerminalStatus = "failed";
                executionError = runtimeControlState.FaultSummary ?? "Session fuse triggered.";
                finalMessage = executionError;
                _logger.LogWarning(
                    "[AgentExec] Runtime fuse stopped buffered execution session={Session}",
                    request.SessionId);
                await FireHooksAsync(h => h.OnFailedAsync(loopCtx, executionError, null, default));
            }
            else
            {
                var deadlineReached =
                    request.ExecutionDeadlineUtc is { } deadline &&
                    DateTimeOffset.UtcNow >= deadline.AddMilliseconds(-250);
                var subAgentDeadlineReached = deadlineReached && subAgentBudget is not null;
                stopReason = subAgentDeadlineReached
                    ? AgentLoopStopReason.BudgetExhausted
                    : deadlineReached
                        ? AgentLoopStopReason.MaxElapsedReached
                        : AgentLoopStopReason.Cancelled;
                execState = subAgentDeadlineReached
                    ? AgentExecutionState.BudgetExhausted
                    : deadlineReached
                        ? AgentExecutionState.Failed
                        : AgentExecutionState.Cancelled;
                subAgentTerminalStatus = subAgentDeadlineReached
                    ? "budget_exhausted"
                    : deadlineReached ? "timed_out" : "cancelled";
                executionError = subAgentDeadlineReached
                    ? "Sub-agent cleanup grace reached the hard execution deadline; the preserved session can be resumed with a fresh system budget."
                    : deadlineReached
                        ? $"Execution timed out at {request.ExecutionDeadlineUtc:O}."
                        : "Cancelled";
                _logger.LogInformation(
                    "[AgentExec] {Termination} session={Session}",
                    deadlineReached ? "Timed out" : "Cancelled",
                    request.SessionId);
                await FireHooksAsync(h => h.OnCancelledAsync(loopCtx, default));
                // Do not write a terminal run here. The common terminal path below computes the
                // real round/tool totals and performs the single first-writer-wins commit.
            }
        }
        catch (Exception ex)
        {
            stopReason = AgentLoopStopReason.Failed;
            execState  = AgentExecutionState.Failed;
            _logger.LogError(ex, "[AgentExec] Error session={Session}", request.SessionId);
            await FireHooksAsync(h => h.OnLoopErrorAsync(loopCtx, ex, default));
            await FireHooksAsync(h => h.OnFailedAsync(loopCtx, ex.Message, ex, default));

            // 完成子代理运行归档（ADR-021）
            await TryCompleteSubAgentRunAsync(
                subAgentRunId, request.SessionId, false,
                finalMessage, ex.Message, roundsStarted, totalToolCalls, totalSw.ElapsedMilliseconds,
                toolFailureCount, toolOutputTruncatedCount, toolOutputChars, firstToolFailureSummary,
                "failed",
                request.ExecutionIdentity,
                CancellationToken.None);

                        return new RuntimeDispatchResult
            {
                SessionId       = request.SessionId,
                AgentInstanceId = instance.AgentInstanceId,
                ReplyText       = finalMessage,
                IsSuccess       = false,
                ExecutionState  = AgentExecutionState.Failed,
                StopReason      = AgentLoopStopReason.Failed.ToString(),
                ErrorMessage    = "Agent 执行失败，请稍后重试。",
                Usage           = usage,
                PrefixSnapshot  = lastPrefixSnapshot,
                TurnSteps       = CollectNewTurnSteps(request.SessionId, journalStartCount),
                ToolFailureCount = toolFailureCount,
                ToolOutputTruncatedCount = toolOutputTruncatedCount,
                ToolOutputChars = toolOutputChars,
                ToolFailureSummary = firstToolFailureSummary,
            };
        }
        finally
        {
            // WAIT 态：保留控制注册条目，等待唤醒后 CreateLinkedToken 时再清理
            if (execState != AgentExecutionState.WaitingEvent)
                _controlRegistry.Remove(request.SessionId);
            if (execState != AgentExecutionState.WaitingEvent)
                _skillPackageRegistry.Remove(instance.AgentInstanceId);
            _contextManager.MarkSessionExecutionCompleted(request.SessionId);
        }

        var terminatedByCancellationOrTimeout =
            runtimeFuseFaulted
            || subAgentTerminalStatus is "cancelled" or "timed_out" or "budget_exhausted";

        // ── 记忆写回 ──────────────────────────────────────────────────
        // A cancelled/timed-out run must not start new post-loop work with an already cancelled token.
        if (!terminatedByCancellationOrTimeout
            && (template.Memory?.EnableSessionMemory == true
             || template.Memory?.EnableWorkspaceMemory == true))
        {
            _memory.WriteBack(
                finalMessage,
                request.SessionId,
                request.WorkspaceId,
                instance.AgentInstanceId,
                instance.AgentInstanceId);
        }

        if (!terminatedByCancellationOrTimeout
            && !request.SuppressContextAutoCompaction)
        {
            await _contextManager.TrimHistoryAsync(
                request.SessionId,
                history,
                effectiveLlmConfig?.MaxContextTokens ?? template.Runtime?.MaxContextTokens ?? 8192,
                preferDbContextWindow: false,
                request.WorkspaceId,
                instance.AgentInstanceId,
                                    ct,
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

        // ExecuteAsync is a synchronous boundary: once it returns, the execution must be terminal.
        // Function-call rounds use `continue`, so the last tool call can otherwise leave state=Running
        // when the for-loop exhausts maxRounds.
        if (execState == AgentExecutionState.Running)
        {
            if (subAgentBudget?.IsInGrace == true)
            {
                execState = AgentExecutionState.BudgetExhausted;
                stopReason = AgentLoopStopReason.BudgetExhausted;
                subAgentTerminalStatus = "budget_exhausted";
                executionError ??=
                    $"Sub-agent exhausted its cleanup grace ({subAgentBudget.GraceRounds} rounds); " +
                    "resume the preserved child session to continue with a fresh system budget.";
            }
            else
            {
                execState = AgentExecutionState.Failed;
                stopReason = AgentLoopStopReason.MaxRoundsReached;
                executionError ??=
                    $"Maximum agent rounds reached ({maxRounds}) before a final response.";
            }
            await FireHooksAsync(h => h.OnMaxRoundsReachedAsync(loopCtx, ct));
        }

        var executeIsSuccess = execState is AgentExecutionState.Completed or AgentExecutionState.WaitingEvent;
        if (AgentExecutionOutcomePolicy.ShouldDowngradeSuccessfulExecution(
                executeIsSuccess,
                toolFailureCount,
                finalMessage,
                request.ExpectedOutputContract))
        {
            // Without a contract-complete delegated report, a failure-only final reply must
            // not be exposed as Completed merely because the loop emitted a nominal DONE.
            executeIsSuccess = false;
            execState = AgentExecutionState.Failed;
            stopReason = AgentLoopStopReason.Failed;
        }
        var finalErrorMessage = executionError
            ?? firstToolFailureSummary
            ?? $"Execution ended with state={execState}";

        await FireHooksAsync(h => h.OnLoopCompleteAsync(
            loopCtx,
            finalMessage,
            stopReason,
            terminatedByCancellationOrTimeout ? default : ct));
        if (!terminatedByCancellationOrTimeout)
            TryEnqueueSubconsciousConsolidationFallback(request, instance.AgentInstanceId, finalMessage);

        _logger.LogInformation(
            "[AgentExec] End session={Session} state={State} reason={Reason} replyLen={Len}",
            request.SessionId, execState, stopReason, finalMessage.Length);

        // ── 记录终端状态 Activity ───────────────────────────────────
        totalSw.Stop();
        var terminalStatus = execState switch
        {
            AgentExecutionState.Completed => RuntimeActivityStatuses.Succeeded,
            AgentExecutionState.WaitingEvent => RuntimeActivityStatuses.Deferred,
            AgentExecutionState.BudgetExhausted => RuntimeActivityStatuses.Deferred,
            AgentExecutionState.Cancelled => RuntimeActivityStatuses.Cancelled,
            _ => RuntimeActivityStatuses.Failed,
        };
        var terminalMetadata = new Dictionary<string, string>
        {
            ["agent_template_id"] = request.AgentTemplateId,
            ["session_id"] = request.SessionId,
            ["total_rounds"] = (_journal.GetTurns(request.SessionId).Count - journalStartCount).ToString(),
            ["total_tool_calls"] = totalToolCalls.ToString(),
            ["total_elapsed_ms"] = totalSw.ElapsedMilliseconds.ToString(),
            ["stop_reason"] = stopReason.ToString(),
        };
        if (usage is not null)
        {
            terminalMetadata["total_tokens"] = usage.TotalTokens?.ToString() ?? "0";
            terminalMetadata["prompt_tokens"] = usage.PromptTokens?.ToString() ?? "0";
            terminalMetadata["completion_tokens"] = usage.CompletionTokens?.ToString() ?? "0";
        }
        await RecordActivityAsync(
            execTrace,
            component: RuntimeActivityComponents.AgentExecution,
            operation: "execute",
            status: terminalStatus,
            execStartedAt,
            endedAt: DateTimeOffset.UtcNow,
            durationMs: totalSw.ElapsedMilliseconds,
            summary: $"Agent execution terminated with state: {execState}",
            metadata: terminalMetadata,
            error: terminalStatus == RuntimeActivityStatuses.Failed
                ? new Exception(executionError ?? $"Execution {execState}: {stopReason}")
                : null,
            ct: CancellationToken.None);

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
                    _logger.LogWarning(ex, "[AgentExec] Session archive failed");
                }
            });
        }

        // 完成子代理运行归档（ADR-021）
        await TryCompleteSubAgentRunAsync(
            subAgentRunId, request.SessionId, executeIsSuccess,
            finalMessage, executeIsSuccess ? null : finalErrorMessage,
            roundsStarted, totalToolCalls, totalSw.ElapsedMilliseconds,
            toolFailureCount, toolOutputTruncatedCount, toolOutputChars, firstToolFailureSummary,
            subAgentTerminalStatus,
            request.ExecutionIdentity,
            CancellationToken.None);

        var isSuccess = executeIsSuccess;
        return new RuntimeDispatchResult
        {
            SessionId       = request.SessionId,
            AgentInstanceId = instance.AgentInstanceId,
            ReplyText       = finalMessage,
            IsSuccess       = isSuccess,
            ExecutionState  = execState,
            StopReason      = stopReason.ToString(),
            ResumeAnchorId  = resumeAnchorId,
            ErrorMessage    = isSuccess ? null : finalErrorMessage,
            Usage           = usage,
            PrefixSnapshot  = lastPrefixSnapshot,
            TurnSteps       = CollectNewTurnSteps(request.SessionId, journalStartCount),
            ToolFailureCount = toolFailureCount,
            ToolOutputTruncatedCount = toolOutputTruncatedCount,
            ToolOutputChars = toolOutputChars,
            ToolFailureSummary = firstToolFailureSummary,
        };
    }
}
