using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Services;
using PuddingMemoryEngine.Data;
using PuddingRuntime.Services.AgentLoop;
using PuddingRuntime.Services.Skills;

namespace PuddingRuntime.Services;

/// <summary>
/// Agent 执行服务——接收 RuntimeDispatchRequest，驱动多轮 Agent Loop。
///
/// Loop 设计原则：
///   · Runtime 控制循环——LLM 只负责单轮结构化决策，不接管执行控制权。
///   · 四类停止机制：status=DONE/WAIT/FAILED 信号 + MaxRounds护栏。
///   · 五类护栏：最大轮次、最大总耗时、最大工具调用次数、相同工具重复次数。
///   · CompletionPolicy 对 Agent DONE 信号进行二次裁决。
///   · ExecutionControlRegistry 支持 Controller 下发 Cancel/Freeze 控制信号。
///   · ExecutionJournal 记录每轮摘要，供可观测性审计和 ResumeAnchor 使用。
///   · IAgentLoopHook 提供 12 个生命周期扩展点，Hook 故障不中断主执行链。
/// </summary>
public sealed class AgentExecutionService
{
    private static readonly TimeSpan DefaultSessionTimeout = TimeSpan.FromHours(1);

    private readonly AgentSessionManager _sessionManager;
    private readonly InMemoryRuntimeSessionStore _runtimeSessionStore;
    private readonly IMemoryEngine _memory;
    private readonly SandboxExecutor _sandbox;
    private readonly IRuntimeLlmClient _llmClient;
    private readonly SkillRuntime _skillRuntime;
    private readonly AgentExecutionGuardrails _guardrails;
    private readonly ExecutionControlRegistry _controlRegistry;
    private readonly ExecutionJournal _journal;
    private readonly CompletionPolicy _completionPolicy;
    private readonly AgentSkillPackageRegistry _skillPackageRegistry;
    private readonly SkillPackageDownloadService _skillPackageDownloader;
    private readonly IReadOnlyList<IAgentLoopHook> _hooks;
    private readonly SystemPromptBuilder _promptBuilder;
    private readonly ContextWindowManager _contextManager;
    private readonly IKeyVaultService _keyVaultService;
    private readonly JsonlSessionWriter? _jsonlSessionWriter;
    private readonly ILogger<AgentExecutionService> _logger;

    public AgentExecutionService(
        AgentSessionManager sessionManager,
        InMemoryRuntimeSessionStore runtimeSessionStore,
        IMemoryEngine memory,
        SandboxExecutor sandbox,
        IRuntimeLlmClient llmClient,
        SkillRuntime skillRuntime,
        AgentExecutionGuardrails guardrails,
        ExecutionControlRegistry controlRegistry,
        ExecutionJournal journal,
        CompletionPolicy completionPolicy,
        AgentSkillPackageRegistry skillPackageRegistry,
        SkillPackageDownloadService skillPackageDownloader,
        IEnumerable<IAgentLoopHook> hooks,
        SystemPromptBuilder promptBuilder,
        ContextWindowManager contextManager,
        ILogger<AgentExecutionService> logger,
        IKeyVaultService? keyVaultService = null,
        JsonlSessionWriter? jsonlSessionWriter = null)
    {
        _sessionManager      = sessionManager;
        _runtimeSessionStore = runtimeSessionStore;
        _memory              = memory;
        _sandbox             = sandbox;
        _llmClient           = llmClient;
        _skillRuntime        = skillRuntime;
        _guardrails          = guardrails;
        _controlRegistry     = controlRegistry;
        _journal             = journal;
        _completionPolicy    = completionPolicy;
        _skillPackageRegistry    = skillPackageRegistry;
        _skillPackageDownloader  = skillPackageDownloader;
        _hooks               = hooks.ToArray();
        _promptBuilder       = promptBuilder;
        _contextManager      = contextManager;
        _keyVaultService     = keyVaultService ?? NoOpKeyVaultService.Instance;
        _jsonlSessionWriter  = jsonlSessionWriter;
        _logger              = logger;
    }

    /// <summary>
    /// 执行 Agent Loop：
    ///   User Message → LLM → [CompletionPolicy → 工具调用 → LLM] × N → 终止
    /// </summary>
    public async Task<RuntimeDispatchResult> ExecuteAsync(
        RuntimeDispatchRequest request,
        CancellationToken external = default)
    {
        _logger.LogInformation(
            "[AgentExec] session={Session} template={Template} msgLen={Len} hasLlmConfig={HasCfg}",
            request.SessionId, request.AgentTemplateId,
            request.MessageText.Length, request.LlmConfig is not null);

        // 前端给全局模板附加了 "global:" 前缀（用于在 UI 中区分工作区模板），
        // Runtime 侧查内置模板时需剥除前缀后再匹配。
        const string globalPrefix = "global:";
        var canonicalTemplateId = request.AgentTemplateId.StartsWith(globalPrefix, StringComparison.OrdinalIgnoreCase)
            ? request.AgentTemplateId[globalPrefix.Length..]
            : request.AgentTemplateId;

        var template = BuiltInAgentTemplates.FindById(canonicalTemplateId)
                       ?? BuiltInAgentTemplates.WorkspaceServiceAgent;
        var effectiveCapability = request.CapabilityPolicy ?? template.Capability;
        var sessionTimeout = ResolveSessionTimeout(template);

        _contextManager.CleanupExpiredSessions(request.SessionId);

        // ── 获取/创建 Agent 实例 ──────────────────────────────────────
        var instance = _sessionManager.GetOrCreate(request.SessionId, request.AgentTemplateId, sessionTimeout);
        _sessionManager.MarkRunning(request.SessionId);
        _contextManager.TouchHistoryAccess(request.SessionId, sessionTimeout);

        _runtimeSessionStore.GetOrCreate(
            request.SessionId, instance.AgentInstanceId,
            request.WorkspaceId, request.AgentTemplateId);

        // ── 注册并预下载 Skill 包────────────────────────────────────
        var skillPackages = request.SkillPackages ?? [];
        _skillPackageRegistry.Register(instance.AgentInstanceId, skillPackages);
        if (skillPackages.Count > 0)
            await _skillPackageDownloader.EnsureDownloadedAsync(skillPackages);

        // ── 创建与外部令牌联结的执行控制令牌 ─────────────────────────
        var ct = _controlRegistry.CreateLinkedToken(request.SessionId, external);

        // ── 构建对话历史 ─────────────────────────────────────────────
        var history = _contextManager.GetOrCreateHistory(request.SessionId);
        if (history.Count == 0)
        {
            var layeredSystemPrompt = await _promptBuilder.BuildLayeredSystemPromptAsync(
                template,
                request.WorkspaceId,
                request.SessionId,
                request.AgentTemplateId,
                request.MessageText,
                effectiveCapability,
                instance.AgentInstanceId,
                forStreaming: false,
                ct);
            history.Add(new ChatMessage(ChatRole.System,
                layeredSystemPrompt));
        }
        else if (template.Memory?.EnableSessionMemory == true
              || template.Memory?.EnableWorkspaceMemory == true)
        {
            if (history[0].Role == ChatRole.System)
            {
                var layeredSystemPrompt = await _promptBuilder.BuildLayeredSystemPromptAsync(
                    template,
                    request.WorkspaceId,
                    request.SessionId,
                    request.AgentTemplateId,
                    request.MessageText,
                    effectiveCapability,
                    instance.AgentInstanceId,
                    forStreaming: false,
                    ct);
                history[0] = new ChatMessage(ChatRole.System,
                    layeredSystemPrompt);
            }
        }
        history.Add(new ChatMessage(ChatRole.User, request.MessageText));

        // ── 初始化 Loop 上下文 ────────────────────────────────────────
        var loopCtx = new AgentLoopContext
        {
            SessionId       = request.SessionId,
            AgentInstanceId = instance.AgentInstanceId,
            WorkspaceId     = request.WorkspaceId,
            AgentTemplateId = request.AgentTemplateId,
            UserMessage     = request.MessageText,
            MaxRounds       = _guardrails.MaxRounds,
        };

        var effectiveLlmConfig = await ResolveLlmConfigAsync(request.LlmConfig, ct);

        string             finalMessage   = "(no response)";
        var                stopReason     = AgentLoopStopReason.MaxRoundsReached;
        var                execState      = AgentExecutionState.Running;
        string?            resumeAnchorId = null;
        TokenUsageDto?     usage          = null;

        // 记录本次 dispatch 前已有的 journal 条数，用于在结束时截取本次新增的 turns
        var journalStartCount = _journal.GetTurns(request.SessionId).Count;

        // 护栏状态
        var  totalSw          = System.Diagnostics.Stopwatch.StartNew();
        int  totalToolCalls   = 0;
        int  noProgressCount  = 0;   // 连续无工具调用进展的轮次计数
        var  toolRepeatMap    = new Dictionary<string, int>(StringComparer.Ordinal);

        try
        {
            _contextManager.MarkSessionExecuting(request.SessionId);
            await FireHooksAsync(h => h.OnLoopStartAsync(loopCtx, ct));

            for (int round = 0; round < _guardrails.MaxRounds; round++)
            {
                // ── 检查点 A：取消 / 冻结 ─────────────────────────────
                if (ct.IsCancellationRequested || _controlRegistry.IsFrozen(request.SessionId))
                {
                    stopReason = AgentLoopStopReason.Cancelled;
                    execState  = AgentExecutionState.Cancelled;
                    await FireHooksAsync(h => h.OnCancelledAsync(loopCtx, ct));
                    break;
                }

                // ── 检查点 B：最大总耗时 ──────────────────────────────
                if (totalSw.Elapsed > _guardrails.MaxElapsed)
                {
                    _logger.LogWarning(
                        "[AgentExec] MaxElapsed={Max} exceeded session={Session}",
                        _guardrails.MaxElapsed, request.SessionId);
                    stopReason = AgentLoopStopReason.MaxElapsedReached;
                    execState  = AgentExecutionState.Failed;
                    await FireHooksAsync(h => h.OnMaxRoundsReachedAsync(loopCtx, ct));
                    break;
                }

                await FireHooksAsync(h => h.OnRoundStartAsync(loopCtx, round, ct));
                var turnStart = DateTimeOffset.UtcNow;

                // ── LLM 调用 ──────────────────────────────────────────
                var llmSw = System.Diagnostics.Stopwatch.StartNew();
                var availableSkillNames = _skillRuntime.GetAvailableSkills(effectiveCapability)
                    .Select(s => s.SkillId)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var llmTools = request.ToolDefinitions is { Count: > 0 }
                    ? request.ToolDefinitions
                        .Where(t => availableSkillNames.Contains(t.Name))
                        .ToList()
                    : _skillRuntime.BuildLlmTools(effectiveCapability);

                var injectedHistory = await BuildInjectedHistoryAsync(history, ct);
                var llmResp = await _llmClient.ChatAsync(
                    request.WorkspaceId, request.SessionId,
                    request.AgentTemplateId, injectedHistory, llmTools, effectiveLlmConfig, ct);
                if (llmResp.Usage is not null)
                    usage = llmResp.Usage;
                llmSw.Stop();

                var rawText = await _keyVaultService.StripAsync(llmResp.Content ?? "{}", ct);
                _logger.LogInformation(
                    "[AgentExec] LLM round={Round}/{Max} session={Session} elapsed={Ms}ms",
                    round + 1, _guardrails.MaxRounds, request.SessionId, llmSw.ElapsedMilliseconds);

                // 优先走 function-call 闭环：Assistant(tool_calls) -> Tool(result) -> 下一轮
                if (llmResp.ToolCalls is { Count: > 0 })
                {
                    history.Add(new ChatMessage(
                        ChatRole.Assistant,
                        rawText,
                        ToolCalls: llmResp.ToolCalls,
                        ReasoningContent: llmResp.ReasoningContent));

                    noProgressCount = 0;
                    foreach (var call in llmResp.ToolCalls)
                    {
                        if (totalToolCalls >= _guardrails.MaxToolCallsTotal)
                        {
                            _logger.LogWarning(
                                "[AgentExec] MaxToolCallsTotal={Max} reached session={Session}",
                                _guardrails.MaxToolCallsTotal, request.SessionId);
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
                            history.Add(new ChatMessage(ChatRole.Tool,
                                $"Tool '{call.Name}' blocked: repeated identical arguments {repeatCount} times.",
                                ToolCallId: call.Id));
                            continue;
                        }
                        toolRepeatMap[repeatKey] = repeatCount + 1;

                        totalToolCalls++;
                        await FireHooksAsync(h => h.OnToolCallAsync(loopCtx, round, call.Name, safeToolArgs, ct));

                        var skillResult = await _skillRuntime.InvokeAsync(
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

                        await FireHooksAsync(h => h.OnToolResultAsync(loopCtx, round, call.Name, skillResult, ct));

                        var toolPayloadRaw = skillResult.Success
                            ? $"Tool '{call.Name}' succeeded (exit={skillResult.ExitCode}):\n{skillResult.Output}"
                            : $"Tool '{call.Name}' failed (exit={skillResult.ExitCode}):\n{skillResult.Error}";
                        var toolPayload = await _keyVaultService.StripAsync(toolPayloadRaw, ct);

                        var safeToolError = string.IsNullOrWhiteSpace(skillResult.Error)
                            ? skillResult.Error
                            : await _keyVaultService.StripAsync(skillResult.Error, ct);

                        history.Add(new ChatMessage(ChatRole.Tool, toolPayload, ToolCallId: call.Id));

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

                    continue;
                }

                history.Add(new ChatMessage(ChatRole.Assistant, rawText));

                var loopResp = AgentLoopResponse.Parse(rawText);
                finalMessage = loopResp.Message ?? rawText;

                await FireHooksAsync(h => h.OnRoundCompleteAsync(loopCtx, round, loopResp, ct));

                // ── CompletionPolicy 裁决 ─────────────────────────────
                var verdict = _completionPolicy.Evaluate(
                    loopCtx, loopResp, _journal.GetTurns(request.SessionId),
                    ct.IsCancellationRequested,
                    _controlRegistry.IsFrozen(request.SessionId));

                if (verdict == CompletionVerdict.Completed)
                {
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
                    if (totalToolCalls >= _guardrails.MaxToolCallsTotal)
                    {
                        _logger.LogWarning(
                            "[AgentExec] MaxToolCallsTotal={Max} reached session={Session}",
                            _guardrails.MaxToolCallsTotal, request.SessionId);
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
                    totalToolCalls++;

                    // 检查点 E：工具执行前再次检查取消
                    ct.ThrowIfCancellationRequested();

                    _logger.LogInformation(
                        "[AgentExec] ToolCall tool={Tool} round={Round} agent={Agent}",
                        toolName, round + 1, instance.AgentInstanceId);

                    var skillResult = await _skillRuntime.InvokeAsync(
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

                    toolSuccess = skillResult.Success;
                    toolError   = string.IsNullOrWhiteSpace(skillResult.Error)
                        ? skillResult.Error
                        : await _keyVaultService.StripAsync(skillResult.Error, ct);

                    await FireHooksAsync(h => h.OnToolResultAsync(loopCtx, round, toolName, skillResult, ct));

                    var toolMsgRaw = skillResult.Success
                        ? $"Tool '{toolName}' succeeded (exit={skillResult.ExitCode}):\n{skillResult.Output}"
                        : $"Tool '{toolName}' failed (exit={skillResult.ExitCode}):\n{skillResult.Error}";
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
                if (round == _guardrails.MaxRounds - 1)
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
            stopReason = AgentLoopStopReason.Cancelled;
            execState  = AgentExecutionState.Cancelled;
            _logger.LogInformation("[AgentExec] Cancelled session={Session}", request.SessionId);
            await FireHooksAsync(h => h.OnCancelledAsync(loopCtx, default));
        }
        catch (Exception ex)
        {
            stopReason = AgentLoopStopReason.Failed;
            execState  = AgentExecutionState.Failed;
            _logger.LogError(ex, "[AgentExec] Error session={Session}", request.SessionId);
            await FireHooksAsync(h => h.OnLoopErrorAsync(loopCtx, ex, default));
            await FireHooksAsync(h => h.OnFailedAsync(loopCtx, ex.Message, ex, default));

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
                TurnSteps       = CollectNewTurnSteps(request.SessionId, journalStartCount),
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

        // ── 记忆写回 ──────────────────────────────────────────────────
        if (template.Memory?.EnableSessionMemory == true
         || template.Memory?.EnableWorkspaceMemory == true)
        {
            _memory.WriteBack(
                finalMessage,
                request.SessionId,
                request.WorkspaceId,
                instance.AgentInstanceId,
                instance.AgentInstanceId);
        }

        await _contextManager.TrimHistoryAsync(
            request.SessionId,
            history,
            template.Runtime?.MaxContextTokens ?? 8192,
            preferDbContextWindow: false,
            ct);
        _contextManager.TouchHistoryAccess(request.SessionId, sessionTimeout);
        _sessionManager.Touch(request.SessionId);
        _runtimeSessionStore.Touch(request.SessionId);

        await FireHooksAsync(h => h.OnLoopCompleteAsync(loopCtx, finalMessage, stopReason, ct));

        _logger.LogInformation(
            "[AgentExec] End session={Session} state={State} reason={Reason} replyLen={Len}",
            request.SessionId, execState, stopReason, finalMessage.Length);

        var isSuccess = execState is AgentExecutionState.Completed or AgentExecutionState.WaitingEvent;
        return new RuntimeDispatchResult
        {
            SessionId       = request.SessionId,
            AgentInstanceId = instance.AgentInstanceId,
            ReplyText       = finalMessage,
            IsSuccess       = isSuccess,
            ExecutionState  = execState,
            StopReason      = stopReason.ToString(),
            ResumeAnchorId  = resumeAnchorId,
            ErrorMessage    = isSuccess ? null : $"Execution ended with state={execState}",
            Usage           = usage,
            TurnSteps       = CollectNewTurnSteps(request.SessionId, journalStartCount),
        };
    }

    /// <summary>
    /// 面向 Chat UI 的流式执行路径。
    /// 它沿用 Session/Memory/LLM 配置链路，但刻意使用直接 Markdown 回复提示，
    /// 避免把结构化 Agent Loop JSON（status/tool/meta）逐 token 暴露给用户界面。
    /// </summary>
    public async IAsyncEnumerable<ServerSentEventFrame> ExecuteStreamAsync(
        RuntimeDispatchRequest request,
        [EnumeratorCancellation] CancellationToken external = default)
    {
        _logger.LogInformation(
            "[AgentExec] STREAM session={Session} template={Template} msgLen={Len} hasLlmConfig={HasCfg}",
            request.SessionId, request.AgentTemplateId,
            request.MessageText.Length, request.LlmConfig is not null);

        const string globalPrefix = "global:";
        var canonicalTemplateId = request.AgentTemplateId.StartsWith(globalPrefix, StringComparison.OrdinalIgnoreCase)
            ? request.AgentTemplateId[globalPrefix.Length..]
            : request.AgentTemplateId;

        var template = BuiltInAgentTemplates.FindById(canonicalTemplateId)
                       ?? BuiltInAgentTemplates.WorkspaceServiceAgent;
        var effectiveCapability = request.CapabilityPolicy ?? template.Capability;
        var sessionTimeout = ResolveSessionTimeout(template);

        _contextManager.CleanupExpiredSessions(request.SessionId);

        var instance = _sessionManager.GetOrCreate(request.SessionId, request.AgentTemplateId, sessionTimeout);
        _sessionManager.MarkRunning(request.SessionId);
        _contextManager.TouchHistoryAccess(request.SessionId, sessionTimeout);

        _runtimeSessionStore.GetOrCreate(
            request.SessionId, instance.AgentInstanceId,
            request.WorkspaceId, request.AgentTemplateId);

        var skillPackages = request.SkillPackages ?? [];
        _skillPackageRegistry.Register(instance.AgentInstanceId, skillPackages);
        if (skillPackages.Count > 0)
            await _skillPackageDownloader.EnsureDownloadedAsync(skillPackages);

        var ct = _controlRegistry.CreateLinkedToken(request.SessionId, external);

        var history = _contextManager.GetOrCreateHistory(request.SessionId);
        await _contextManager.TryHydrateStreamHistoryFromDbAsync(
            request.SessionId,
            history,
            template.Runtime?.MaxContextTokens ?? 8000,
            ct);

        var streamingSystemPrompt = await _promptBuilder.BuildLayeredSystemPromptAsync(
            template,
            request.WorkspaceId,
            request.SessionId,
            request.AgentTemplateId,
            request.MessageText,
            effectiveCapability,
            instance.AgentInstanceId,
            forStreaming: true,
            ct);

        if (history.Count == 0 || history[0].Role != ChatRole.System)
        {
            history.Insert(0, new ChatMessage(ChatRole.System, streamingSystemPrompt));
        }
        else
        {
            history[0] = new ChatMessage(ChatRole.System, streamingSystemPrompt);
        }

        history.Add(new ChatMessage(ChatRole.User, request.MessageText));

        var loopCtx = new AgentLoopContext
        {
            SessionId       = request.SessionId,
            AgentInstanceId = instance.AgentInstanceId,
            WorkspaceId     = request.WorkspaceId,
            AgentTemplateId = request.AgentTemplateId,
            UserMessage     = request.MessageText,
            MaxRounds       = 1,
        };

        var effectiveLlmConfig = await ResolveLlmConfigAsync(request.LlmConfig, ct);
        var finalMessage = new StringBuilder();
        TokenUsageDto? usage = null;

        try
        {
            _contextManager.MarkSessionExecuting(request.SessionId);
            await FireHooksAsync(h => h.OnLoopStartAsync(loopCtx, ct));

            // Streaming chat intentionally does not expose function-call deltas to the UI.
            // Tool execution remains available on the synchronous structured loop path.
            var injectedHistory = await BuildInjectedHistoryAsync(history, ct);
            await foreach (var delta in _llmClient.ChatStreamAsync(
                request.WorkspaceId,
                request.SessionId,
                request.AgentTemplateId,
                injectedHistory,
                tools: null,
                llmConfig: effectiveLlmConfig,
                ct: ct))
            {
                if (delta.Usage is not null)
                {
                    usage = delta.Usage;
                    yield return ServerSentEventFrame.Json("usage", usage);
                }

                if (!string.IsNullOrEmpty(delta.ContentDelta))
                {
                    var safeDelta = await _keyVaultService.StripAsync(delta.ContentDelta, ct);
                    finalMessage.Append(safeDelta);
                    yield return ServerSentEventFrame.Json("delta", new { delta = safeDelta });
                }

                if (delta.ToolCallIndex is not null)
                {
                    yield return ServerSentEventFrame.Json("step", new
                    {
                        status = "TOOL_CALL_DELTA",
                        message = "模型请求工具调用；当前流式 UI 路径仅展示自然语言回复。",
                        toolCallIndex = delta.ToolCallIndex,
                    });
                }
            }

            var replyRaw = finalMessage.Length > 0 ? finalMessage.ToString() : "（Agent 未返回可展示文本）";
            var reply = await _keyVaultService.StripAsync(replyRaw, ct);
            history.Add(new ChatMessage(ChatRole.Assistant, reply));
            TryEnqueueStreamJsonl(request, instance.AgentInstanceId, reply, usage);

            if (template.Memory?.EnableSessionMemory == true
             || template.Memory?.EnableWorkspaceMemory == true)
            {
                _memory.WriteBack(
                    reply,
                    request.SessionId,
                    request.WorkspaceId,
                    instance.AgentInstanceId,
                    instance.AgentInstanceId);
            }

            await _contextManager.TrimHistoryAsync(
                request.SessionId,
                history,
                template.Runtime?.MaxContextTokens ?? 8192,
                preferDbContextWindow: true,
                ct);
            _contextManager.TouchHistoryAccess(request.SessionId, sessionTimeout);
            _sessionManager.Touch(request.SessionId);
            _runtimeSessionStore.Touch(request.SessionId);

            await FireHooksAsync(h => h.OnCompletedAsync(loopCtx, reply, ct));
            await FireHooksAsync(h => h.OnLoopCompleteAsync(loopCtx, reply, AgentLoopStopReason.Done, ct));

            _logger.LogInformation(
                "[AgentExec] STREAM end session={Session} replyLen={Len} usage={Usage}",
                request.SessionId, reply.Length, usage?.TotalTokens);

            yield return ServerSentEventFrame.Json("done", new { reply, usage });
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
        }
    }

    private void TryEnqueueStreamJsonl(
        RuntimeDispatchRequest request,
        string agentInstanceId,
        string assistantReply,
        TokenUsageDto? usage)
    {
        if (_jsonlSessionWriter is null)
            return;

        try
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var timestampPrefix = now.ToString("x");
            var userMessageId = $"{timestampPrefix}-{Guid.NewGuid().ToString("N")[..8]}";

            _jsonlSessionWriter.Enqueue(request.SessionId, new JsonlEntry
            {
                Type = "user",
                MessageId = userMessageId,
                SessionId = request.SessionId,
                ParentId = null,
                Role = "user",
                ContentType = "text",
                Content = request.MessageText,
                AgentId = agentInstanceId,
                BranchType = "MAIN",
                CreatedAt = now - 1,
            });

            if (!string.IsNullOrWhiteSpace(assistantReply))
            {
                _jsonlSessionWriter.Enqueue(request.SessionId, new JsonlEntry
                {
                    Type = "assistant",
                    MessageId = $"{timestampPrefix}-{Guid.NewGuid().ToString("N")[..8]}",
                    SessionId = request.SessionId,
                    ParentId = userMessageId,
                    Role = "assistant",
                    ContentType = "text",
                    Content = assistantReply,
                    UsageJson = usage is not null ? JsonSerializer.Serialize(usage) : null,
                    AgentId = agentInstanceId,
                    BranchType = "MAIN",
                    CreatedAt = now,
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "[AgentExec] JSONL enqueue failed session={Session}",
                request.SessionId);
        }
    }

    /// <summary>央取本次 dispatch 新增的 journal turns 并转换为 DTO。</summary>
    private IReadOnlyList<TurnStepDto> CollectNewTurnSteps(string sessionId, int startCount)
    {
        var all = _journal.GetTurns(sessionId);
        if (all.Count <= startCount) return Array.Empty<TurnStepDto>();
        return all.Skip(startCount).Select(t => new TurnStepDto
        {
            Round          = t.Round,
            Status         = t.Status,
            MessageSummary = t.MessageSummary,
            ToolName       = t.ToolName,
            ToolArgs       = t.ToolArgs,
            ToolSuccess    = t.ToolSuccess,
            ToolError      = t.ToolError,
            DurationMs     = t.CompletedAt.HasValue
                ? (long)(t.CompletedAt.Value - t.StartedAt).TotalMilliseconds
                : null,
        }).ToList();
    }

    /// <summary>
    /// 唤醒 WAIT 态会话——事件命中时由 Controller 通过 DispatchWakeupRequest 调用。
    /// 清理 ResumeAnchor，将事件内容注入历史，然后继续执行 Loop。
    /// </summary>
    public async Task<RuntimeDispatchResult> ExecuteWakeupAsync(
        DispatchWakeupRequest request,
        CancellationToken external = default)
    {
        var anchor = _journal.GetAnchor(request.SessionId);
        if (anchor is null)
        {
            _logger.LogWarning(
                "[AgentExec] Wakeup: no ResumeAnchor for session={Session}", request.SessionId);
            return new RuntimeDispatchResult
            {
                SessionId       = request.SessionId,
                AgentInstanceId = "(unknown)",
                IsSuccess       = false,
                ErrorMessage    = "No ResumeAnchor found; session may not be in WAIT state.",
                ExecutionState  = AgentExecutionState.Failed,
            };
        }

        _journal.ClearAnchor(request.SessionId);

        // 构造事件唤醒上下文消息，注入 LLM 历史
        var eventMsg = string.IsNullOrWhiteSpace(request.EventData)
            ? $"[SYSTEM WAKEUP] Event received: {request.EventType ?? "unknown"}. Please resume execution."
            : $"[SYSTEM WAKEUP] Event: {request.EventType ?? "unknown"}\n\n{request.EventData}\n\nPlease resume execution based on this event.";

        _logger.LogInformation(
            "[AgentExec] WakeupAsync session={Session} eventType={EventType} anchorRound={Round}",
            request.SessionId, request.EventType, anchor.LastRound);

        // 转换为标准 DispatchRequest；已有历史不会被清空（GetOrAdd 复用），只追加唤醒消息
        var dispatchRequest = new RuntimeDispatchRequest
        {
            SessionId       = request.SessionId,
            AgentTemplateId = request.AgentTemplateId,
            WorkspaceId     = request.WorkspaceId,
            MessageText     = eventMsg,
            LlmConfig       = request.LlmConfig,
            CapabilityPolicy = request.CapabilityPolicy,
            ToolDefinitions = request.ToolDefinitions,
        };

        return await ExecuteAsync(dispatchRequest, external);
    }

    // ── 私有辅助 ──────────────────────────────────────────────────────────

    private static TimeSpan ResolveSessionTimeout(AgentTemplateDefinition template)
    {
        var configured = template.Runtime?.SessionTimeout ?? TimeSpan.Zero;
        return NormalizeSessionTimeout(configured);
    }

    private static TimeSpan NormalizeSessionTimeout(TimeSpan timeout) =>
        timeout > TimeSpan.Zero ? timeout : DefaultSessionTimeout;

    private static string ExtractInput(JsonElement? args)
    {
        if (args is null) return string.Empty;
        var el = args.Value;
        if (el.ValueKind != JsonValueKind.Object) return el.GetRawText();

        foreach (var key in new[] { "command", "input", "query", "content", "text", "code", "url", "path" })
        {
            if (el.TryGetProperty(key, out var prop)
                && prop.ValueKind == JsonValueKind.String)
                return prop.GetString() ?? string.Empty;
        }
        return el.GetRawText();
    }

    private static IReadOnlyDictionary<string, string> ExtractParameters(JsonElement? args)
    {
        if (args is null || args.Value.ValueKind != JsonValueKind.Object)
            return new Dictionary<string, string>();

        return args.Value.EnumerateObject()
            .Where(p => p.Value.ValueKind == JsonValueKind.String)
            .ToDictionary(p => p.Name, p => p.Value.GetString() ?? string.Empty,
                StringComparer.OrdinalIgnoreCase);
    }

    private static string ExtractInputFromJson(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson)) return string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
                return ExtractInput(root);

            return root.ValueKind == JsonValueKind.String
                ? root.GetString() ?? string.Empty
                : root.GetRawText();
        }
        catch
        {
            return argumentsJson;
        }
    }

    private static IReadOnlyDictionary<string, string> ExtractParametersFromJson(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson)) return new Dictionary<string, string>();
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return new Dictionary<string, string>();

            return doc.RootElement.EnumerateObject()
                .Where(p => p.Value.ValueKind == JsonValueKind.String)
                .ToDictionary(p => p.Name, p => p.Value.GetString() ?? string.Empty,
                    StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, string>();
        }
    }

    /// <summary>
    /// 仅对发送给 LLM 的 System/User 文本执行 KeyVault 占位符注入，
    /// 避免把密钥明文持久化到会话历史中。
    /// </summary>
    private async Task<IReadOnlyList<ChatMessage>> BuildInjectedHistoryAsync(
        IReadOnlyList<ChatMessage> source,
        CancellationToken ct)
    {
        if (source.Count == 0) return source;

        var result = new List<ChatMessage>(source.Count);
        foreach (var message in source)
        {
            if (message.Role is ChatRole.System or ChatRole.User)
            {
                var content = message.Content ?? string.Empty;
                var injected = await _keyVaultService.InjectAsync(content, ct);
                result.Add(string.Equals(injected, content, StringComparison.Ordinal)
                    ? message
                    : message with { Content = injected });
            }
            else
            {
                result.Add(message);
            }
        }

        return result;
    }

    /// <summary>
    /// 解析 Runtime 有效 LLM 配置：
    /// - 优先使用上游已提供的 ApiKey（兼容旧链路）；
    /// - 当仅有 KeyVaultId 时，优先按 KeyVaultId 读取，再回退 {{vault:...}} 注入；
    /// - 当 ApiKey 是 {{vault:...}} 占位符时，调用 InjectAsync 解析。
    /// </summary>
    private async Task<LlmConfig?> ResolveLlmConfigAsync(LlmConfig? config, CancellationToken ct)
    {
        if (config is null)
            return null;

        var apiKey = config.ApiKey;

        if (!string.IsNullOrWhiteSpace(config.KeyVaultId))
        {
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                try
                {
                    var byId = await _keyVaultService.GetSecretAsync(config.KeyVaultId, includePlainText: true, ct);
                    if (!string.IsNullOrWhiteSpace(byId?.Value))
                        apiKey = byId.Value;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "[AgentExec] Resolve key by KeyVaultId failed keyVaultId={KeyVaultId}",
                        config.KeyVaultId);
                }

                if (string.IsNullOrWhiteSpace(apiKey))
                {
                    var placeholder = $"{{{{vault:{config.KeyVaultId}}}}}";
                    var injected = await _keyVaultService.InjectAsync(placeholder, ct);
                    if (!string.Equals(injected, placeholder, StringComparison.Ordinal))
                        apiKey = injected;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(apiKey)
            && apiKey.Contains("{{vault:", StringComparison.OrdinalIgnoreCase))
        {
            apiKey = await _keyVaultService.InjectAsync(apiKey, ct);
        }

        if (string.Equals(apiKey, config.ApiKey, StringComparison.Ordinal))
            return config;

        return config with { ApiKey = apiKey };
    }

    private sealed class NoOpKeyVaultService : IKeyVaultService
    {
        public static NoOpKeyVaultService Instance { get; } = new();

        public Task<string> EncryptAsync(string plainText, CancellationToken ct = default)
            => Task.FromResult(plainText);

        public Task<string> DecryptAsync(string encryptedValue, CancellationToken ct = default)
            => Task.FromResult(encryptedValue);

        public Task<KeyVaultSecretSummary> CreateSecretAsync(CreateKeyVaultSecretCommand request, CancellationToken ct = default)
            => Task.FromResult(new KeyVaultSecretSummary());

        public Task<KeyVaultSecretSummary?> UpdateSecretAsync(string keyVaultId, UpdateKeyVaultSecretCommand request, CancellationToken ct = default)
            => Task.FromResult<KeyVaultSecretSummary?>(null);

        public Task<KeyVaultSecretDetail?> GetSecretAsync(string keyVaultId, bool includePlainText = false, CancellationToken ct = default)
            => Task.FromResult<KeyVaultSecretDetail?>(null);

        public Task<IReadOnlyList<KeyVaultSecretSummary>> ListSecretsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<KeyVaultSecretSummary>>([]);

        public Task<bool> DeleteSecretAsync(string keyVaultId, CancellationToken ct = default)
            => Task.FromResult(false);

        public Task<string> InjectAsync(string text, CancellationToken ct = default)
            => Task.FromResult(text);

        public Task<string> StripAsync(string text, CancellationToken ct = default)
            => Task.FromResult(text);
    }

    private async Task FireHooksAsync(Func<IAgentLoopHook, Task> action)
    {
        foreach (var hook in _hooks)
        {
            try   { await action(hook); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[AgentExec] Hook {Hook} threw, continuing.",
                    hook.GetType().Name);
            }
        }
    }

    private static string Truncate(string s, int maxLen) =>
        s.Length <= maxLen ? s : s[..maxLen] + "…";
}
