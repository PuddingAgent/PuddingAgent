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

        var execTrace = RuntimeTraceContext.CreateNew(
            sessionId: request.SessionId,
            workspaceId: request.WorkspaceId,
            userId: request.UserId)
            .WithAgent(request.AgentInstanceId, request.AgentTemplateId);
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
                ReplyText = AgentExecutionConstants.DuplicateMessagePlaceholder,
                IsSuccess = true,
                ExecutionState = AgentExecutionState.Completed,
                StopReason = "DuplicateMessage",
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
                    }, ct);
                    systemPromptText = facadeResult.Messages.FirstOrDefault(m => m.Role == ChatRole.System)?.Content ?? string.Empty;
                }
                else
                {
                    var pipelineResult = await _contextPipeline.AssembleAsync(new ContextRequest
                    {
                        Template = template,
                        WorkspaceId = request.WorkspaceId ?? string.Empty,
                        SessionId = request.SessionId,
                        AgentTemplateId = request.AgentTemplateId,
                        UserMessage = request.MessageText,
                        Capability = effectiveCapability,
                        AgentInstanceId = instance.AgentInstanceId,
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
                await _contextManager.TryHydrateStreamHistoryFromDbAsync(
                    request.SessionId,
                    persistedHistory,
                    request.LlmConfig?.MaxInputTokens
                        ?? template.Runtime?.MaxContextTokens
                        ?? 8192,
                    ct);
                if (persistedHistory.Count > 0)
                {
                    history.AddRange(persistedHistory.Where(message => message.Role != ChatRole.System));
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
                        AgentTemplateId = request.AgentTemplateId,
                        UserMessage = request.MessageText,
                        Capability = effectiveCapability,
                        AgentInstanceId = instance.AgentInstanceId,
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
                history[0] = new ChatMessage(ChatRole.System, systemPrompt.SystemPrompt);
            }
        }
                history.Add(new ChatMessage(
                    ChatRole.User,
                    BuildUserMessageForLlm(request),
                    VisualArtifactIds: request.VisualArtifactIds,
                    AudioArtifactIds: request.AudioArtifactIds));

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
        TokenUsageDto?     usage          = null;
        PromptPrefixSnapshot? lastPrefixSnapshot = null;
        var expectedOutputTracker =
            new ExpectedOutputCandidateTracker(request.ExpectedOutputContract);

        // 记录本次 dispatch 前已有的 journal 条数，用于在结束时截取本次新增的 turns
        var journalStartCount = _journal.GetTurns(request.SessionId).Count;

        // 护栏状态
        var  totalSw          = System.Diagnostics.Stopwatch.StartNew();
        int  totalToolCalls   = 0;
        int  roundsStarted    = 0;
        int  noProgressCount  = 0;   // 连续无工具调用进展的轮次计数
        var  toolRepeatMap    = new Dictionary<string, int>(StringComparer.Ordinal);
        int  toolFailureCount = 0;
        int  toolOutputTruncatedCount = 0;
        long toolOutputChars = 0;
        string? firstToolFailureSummary = null;
        var loadedToolIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var providerInputRecoveryAttempted = false;

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
                    var budgetDecision = subAgentBudget.EvaluateBeforeRound(round, totalSw.Elapsed);
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
                var runtimeTools = BuildRuntimeToolDefinitions(effectiveCapability, template, request);
                var availableToolNames = runtimeTools
                    .Select(t => t.Name)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var allLlmTools = request.ToolDefinitions is { Count: > 0 }
                    ? request.ToolDefinitions
                        .Where(t => availableToolNames.Contains(t.Name))
                        .ToList()
                    : runtimeTools.ToList();

                // 合并运行时中 DB 未覆盖的工具（如 spawn_sub_agent）
                var dbToolNames = allLlmTools.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var runtimeMergedTools = new List<string>();
                foreach (var rt in runtimeTools)
                {
                    if (!dbToolNames.Contains(rt.Name))
                    {
                        allLlmTools.Add(rt);
                        runtimeMergedTools.Add(rt.Name);
                        _logger.LogDebug("[AgentExec] Merged runtime tool: {Tool}", rt.Name);
                    }
                }
                var exposurePlan = ToolExposurePlanner.CreatePlan(allLlmTools, loadedToolIds);
                var llmTools = exposurePlan.VisibleTools.ToList();
                _logger.LogDebug(
                    "[AgentExec:Tools] Prepared LLM tools session={Session} agent={Agent} template={Template} round={Round} requestToolCount={RequestToolCount} runtimeToolCount={RuntimeToolCount} filteredRequestToolCount={FilteredRequestToolCount} runtimeMergedToolCount={RuntimeMergedToolCount} availableToolCount={AvailableToolCount} finalToolCount={FinalToolCount} deferredLoading={DeferredLoading} deferredToolCount={DeferredToolCount} requestTools={RequestTools} runtimeTools={RuntimeTools} mergedTools={MergedTools} finalTools={FinalTools}",
                    request.SessionId,
                    instance.AgentInstanceId,
                    request.AgentTemplateId,
                    round + 1,
                    request.ToolDefinitions?.Count ?? 0,
                    runtimeTools.Count,
                    request.ToolDefinitions is { Count: > 0 } ? allLlmTools.Count - runtimeMergedTools.Count : 0,
                    runtimeMergedTools.Count,
                    exposurePlan.AvailableToolCount,
                    llmTools.Count,
                    exposurePlan.DeferredLoadingEnabled,
                    exposurePlan.DeferredToolCount,
                    SummarizeToolDefinitions(request.ToolDefinitions),
                    SummarizeToolDefinitions(runtimeTools),
                    SummarizeToolNames(runtimeMergedTools),
                    SummarizeToolDefinitions(llmTools));

                await TryInjectSteeringMessageAsync(
                    request,
                    instance.AgentInstanceId,
                    history,
                    round,
                    execTrace,
                    ct);
                var injectedHistory = await BuildInjectedHistoryAsync(history, ct);
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
                        injectedHistory = await BuildInjectedHistoryAsync(history, ct);
                    }
                }
                ContextUsageSnapshot? contextUsageSnapshot = null;
                if (_contextUsageSnapshotStore is not null)
                {
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
                var prefixSnapshot = PrefixCacheSnapshotBuilder.Build(injectedHistory, llmTools);
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
                    estimated_context_tokens = contextUsageSnapshot?.UsedTokens,
                });
                LlmResponse llmResp;
                try
                {
                    if (_llmInvocationService is not null)
                    {
                        var facadeResult = await _llmInvocationService.InvokeAsync(new PuddingCode.Runtime.LlmInvocationRequest
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
                        }, ct);

                        if (!facadeResult.Success)
                        {
                            if (!providerInputRecoveryAttempted
                                && LlmRequestBudgetGuard.TryGetProviderMaxInputTokens(
                                    facadeResult.Error,
                                    out var providerMaxInputTokens))
                            {
                                providerInputRecoveryAttempted = true;
                                _contextUsageSnapshotStore?.RecordProviderInputLimitFailure(
                                    request.SessionId,
                                    providerMaxInputTokens);
                                _logger.LogWarning(
                                    "[AgentExec:ContextBudget] Provider rejected input length; recalibrating and retrying once session={Session} round={Round} providerMaxInput={ProviderMaxInput}",
                                    request.SessionId,
                                    round + 1,
                                    providerMaxInputTokens);
                                round--;
                                continue;
                            }

                            _logger.LogError(
                                "[AgentExec] LLM facade error round={Round} session={Session} error={Error}",
                                round + 1, request.SessionId, facadeResult.Error);
                            executionError = $"LLM API call failed: {facadeResult.Error}";
                            finalMessage = executionError;
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
                                error = Truncate(facadeResult.Error ?? "LLM invocation failed.", 500),
                            });
                            break;
                        }

                        llmResp = new LlmResponse(
                            facadeResult.ReplyText,
                            facadeResult.ToolCalls,
                            facadeResult.ReasoningContent,
                            facadeResult.Usage,
                            facadeResult.ContinuationState);
                    }
                    else
                    {
                        // ADR-027 legacy fallback for tests only (LLM client)
                        llmResp = await _llmClient.ChatAsync(
                            request.WorkspaceId, request.SessionId,
                            request.AgentTemplateId, injectedHistory, llmTools, effectiveLlmConfig, ct);
                    }
                }
                catch (Exception ex)
                {
                    if (!providerInputRecoveryAttempted
                        && LlmRequestBudgetGuard.TryGetProviderMaxInputTokens(ex, out var providerMaxInputTokens))
                    {
                        providerInputRecoveryAttempted = true;
                        _contextUsageSnapshotStore?.RecordProviderInputLimitFailure(
                            request.SessionId,
                            providerMaxInputTokens);
                        _logger.LogWarning(
                            ex,
                            "[AgentExec:ContextBudget] Provider rejected input length; recalibrating and retrying once session={Session} round={Round} providerMaxInput={ProviderMaxInput}",
                            request.SessionId,
                            round + 1,
                            providerMaxInputTokens);
                        round--;
                        continue;
                    }

                    _logger.LogError(ex, "[AgentExec] LLM API error round={Round} session={Session}", round + 1, request.SessionId);
                    executionError = $"LLM API call failed: {ex.Message}";
                    finalMessage = executionError;
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
                    await FireHooksAsync(h => h.OnFailedAsync(loopCtx, executionError, ex, ct));
                    await TryAppendSubAgentEventAsync(subAgentRunId, "subagent.llm.failed", new
                    {
                        sub_agent_id = request.SessionId,
                        round = round + 1,
                        duration_ms = llmSw.ElapsedMilliseconds,
                        error = Truncate(ex.Message, 500),
                    });
                    break;
                }
                if (llmResp.Usage is not null)
                {
                    usage = ApplyResolvedModelCapacity(llmResp.Usage, effectiveLlmConfig);
                    RecordProviderContextUsageSnapshot(request.SessionId, usage);
                }
                llmSw.Stop();
                var rawText = await _keyVaultService.StripAsync(llmResp.Content ?? "{}", ct);
                ReportMeaningfulProgress(
                    request,
                    "llm.completed",
                    rawText + "\u001f" + string.Join(
                        "\u001e",
                        llmResp.ToolCalls?.Select(call => $"{call.Name}:{call.ArgumentsJson}") ?? []));
                const int subAgentMessagePreviewLimit = 2048;
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
                    reasoning_available = !string.IsNullOrWhiteSpace(llmResp.ReasoningContent),
                    reasoning_chars = llmResp.ReasoningContent?.Length ?? 0,
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
                            _logger.LogWarning(
                                "[AgentExec] MaxToolCallsTotal={Max} reached session={Session}",
                                maxToolCallsTotal, request.SessionId);
                            stopReason = AgentLoopStopReason.MaxRoundsReached;
                            execState = AgentExecutionState.Failed;
                            break;
                        }

                        var injectedArgsJson = await _keyVaultService.InjectAsync(call.ArgumentsJson ?? "{}", ct);
                        var safeToolArgs = await _keyVaultService.StripAsync(injectedArgsJson, ct);

                        var repeatKey = $"{call.Name}|{injectedArgsJson}";
                        toolRepeatMap.TryGetValue(repeatKey, out var repeatCount);
                        if (repeatCount >= _guardrails.MaxSameToolRepeat)
                        {
                            toolRoundMessages.Add(new ChatMessage(ChatRole.Tool,
                                $"Tool '{call.Name}' blocked: repeated identical arguments {repeatCount} times.",
                                ToolCallId: call.Id));
                            continue;
                        }
                        toolRepeatMap[repeatKey] = repeatCount + 1;

                        totalToolCalls++;
                        await FireHooksAsync(h => h.OnToolCallAsync(loopCtx, round, call.Name, safeToolArgs, ct));
                        ReportLiveness(request, $"tool.started:{call.Name}");
                        var subAgentToolSw = System.Diagnostics.Stopwatch.StartNew();
                        var subAgentToolArgsHash = ComputeSha256Hash(injectedArgsJson ?? "");
                        await TryAppendSubAgentEventAsync(subAgentRunId, "subagent.tool.started", new
                        {
                            sub_agent_id = request.SessionId,
                            round = round + 1,
                            tool_call_id = call.Id,
                            tool_name = call.Name,
                            args_hash = subAgentToolArgsHash,
                            arguments_preview = Truncate(safeToolArgs, 1024),
                            arguments_truncated = safeToolArgs.Length > 1024,
                            tool_call_index = totalToolCalls,
                        });

                        // 统一 Tool 执行服务已经按 CapabilityPolicy 做模板授权门控。
                        // 仅 legacy fallback 保留旧的用户确认占位逻辑，避免非流式路径绕过新工具注册表。
                        var skill = _skillRuntime.TryGetSkill(call.Name);
                        SkillResult skillResult;
                        if (_toolInvocationService is null
                            && skill is not null
                            && !await CheckToolPermissionAsync(skill, request.SessionId, ct))
                        {
                            skillResult = new SkillResult
                            {
                                Success = false,
                                Output = "",
                                Error = $"Tool '{call.Name}' requires user confirmation (High permission). Execution denied.",
                                ExitCode = 1,
                            };
                        }
                        else
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
                                        ToolName = call.Name,
                                        ArgumentsJson = injectedArgsJson,
                                        CapabilityPolicy = effectiveCapability,
                                        Trace = execTrace,
                                        ExecutionIdentity = request.ExecutionIdentity,
                                        ExecutionDeadlineUtc = request.ExecutionDeadlineUtc,
                                        DelegationDepth = request.DelegationDepth,
                                        MaxDelegationDepth = request.MaxDelegationDepth,
                                        AllowSubDelegation = request.AllowSubDelegation,
                                        RoleInPlan = request.RoleInPlan,
                                    }, ct);
                                    skillResult = new SkillResult
                                    {
                                        Success = toolResult.Success,
                                        Output = toolResult.Output ?? "",
                                        Error = toolResult.Error,
                                        ExitCode = toolResult.Success ? 0 : 1,
                                    };
                                }
                                else
                                {
                                    // ADR-027 legacy fallback for tests only (SkillRuntime)
                                    skillResult = await _skillRuntime.InvokeAsync(
                                        call.Name,
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
                                        summary: $"Tool '{call.Name}' executed successfully.",
                                        metadata: new Dictionary<string, string>
                                        {
                                            ["tool_name"] = call.Name,
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
                                        call.Name,
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
                                        summary: $"Tool '{call.Name}' execution failed.",
                                        metadata: new Dictionary<string, string>
                                        {
                                            ["tool_name"] = call.Name,
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
                                        call.Name,
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
                                    call.Name, skillResult.Success, toolSw.ElapsedMilliseconds, toolArgsHash,
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
                                    summary: $"Tool '{call.Name}' threw exception.",
                                    metadata: new Dictionary<string, string>
                                    {
                                        ["tool_name"] = call.Name,
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
                                    call.Name,
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
                                    call.Name, toolSw.ElapsedMilliseconds, toolArgsHash, request.SessionId);
                                ObserveToolExecutionFacts(
                                    call.Name,
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
                                    tool_name = call.Name,
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
                                            ToolName = call.Name,
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

                        ObserveToolExecutionFacts(
                            call.Name,
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
                            $"tool.completed:{call.Name}",
                            $"{call.Name}\u001f{safeToolArgs}\u001f{safeSubAgentToolOutput}\u001f{safeToolError}");

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
                                tool_name = call.Name,
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
                                    ToolName = call.Name,
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

                        await FireHooksAsync(h => h.OnToolResultAsync(loopCtx, round, call.Name, skillResult, ct));

                        var newlyLoadedToolCount = ToolExposurePlanner.RegisterSearchResult(
                            call.Name,
                            skillResult.Success,
                            skillResult.Output,
                            loadedToolIds,
                            allLlmTools);
                        if (newlyLoadedToolCount > 0)
                        {
                            _logger.LogInformation(
                                "[AgentExec:ToolDiscovery] Loaded {AddedCount} tool definition(s) for next round session={Session} loadedTools={LoadedTools}",
                                newlyLoadedToolCount,
                                request.SessionId,
                                SummarizeToolNames(loadedToolIds));
                        }

                        var toolPayloadRaw = skillResult.Success
                            ? $"✅ Tool '{call.Name}' succeeded (exit={skillResult.ExitCode}):\n{skillResult.Output}"
                            : BuildToolFailurePayload(call.Name, skillResult, request.SessionId, isPermissionError:
                                skillResult.Error?.Contains("permission", StringComparison.OrdinalIgnoreCase) == true ||
                                skillResult.Error?.Contains("not allowed", StringComparison.OrdinalIgnoreCase) == true ||
                                skillResult.Error?.Contains("rejected", StringComparison.OrdinalIgnoreCase) == true);
                        var toolPayload = await _keyVaultService.StripAsync(toolPayloadRaw, ct);

                        toolRoundMessages.Add(new ChatMessage(ChatRole.Tool, toolPayload, ToolCallId: call.Id));

                        _journal.Record(request.SessionId, new TurnRecord
                        {
                            Round = round,
                            StartedAt = turnStart,
                            CompletedAt = DateTimeOffset.UtcNow,
                            Status = "CONTINUE",
                            MessageSummary = Truncate(rawText, 512),
                            ToolName = call.Name,
                            ToolArgs = safeToolArgs,
                            ToolSuccess = skillResult.Success,
                            ToolError = safeToolError,
                        });
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

                var loopResp = AgentLoopResponse.Parse(rawText);
                finalMessage = loopResp.Message ?? rawText;
                expectedOutputTracker.Observe(finalMessage);

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
                            }, ct);
                            skillResult = new SkillResult
                            {
                                Success = toolResult.Success,
                                Output = toolResult.Output ?? "",
                                Error = toolResult.Error,
                                ExitCode = toolResult.Success ? 0 : 1,
                            };
                        }
                        else
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
                    toolError   = string.IsNullOrWhiteSpace(skillResult.Error)
                        ? skillResult.Error
                        : await _keyVaultService.StripAsync(skillResult.Error, ct);

                    await FireHooksAsync(h => h.OnToolResultAsync(loopCtx, round, toolName, skillResult, ct));

                    var newlyLoadedToolCount = ToolExposurePlanner.RegisterSearchResult(
                        toolName,
                        skillResult.Success,
                        skillResult.Output,
                        loadedToolIds,
                        allLlmTools);
                    if (newlyLoadedToolCount > 0)
                    {
                        _logger.LogInformation(
                            "[AgentExec:ToolDiscovery] Loaded {AddedCount} tool definition(s) for next round session={Session} loadedTools={LoadedTools}",
                            newlyLoadedToolCount,
                            request.SessionId,
                            SummarizeToolNames(loadedToolIds));
                    }

                    var toolMsgRaw = skillResult.Success
                        ? $"✅ Tool '{toolName}' succeeded (exit={skillResult.ExitCode}):\n{skillResult.Output}"
                        : $"❌ Tool '{toolName}' FAILED (exit={skillResult.ExitCode})\n" +
                          $"   Error: {skillResult.Error}\n" +
                          $"   💡 Suggestion: Try an alternative approach or use a different tool if this one has access restrictions.";
                    var toolMsg = await _keyVaultService.StripAsync(toolMsgRaw, ct);
                    history.Add(new ChatMessage(ChatRole.User, toolMsg));
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
                ErrorMessage    = ex.Message,
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
            subAgentTerminalStatus is "cancelled" or "timed_out" or "budget_exhausted";

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
                agentTemplateId: request.AgentTemplateId);
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
