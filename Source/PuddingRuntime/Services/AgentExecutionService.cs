using System.Collections.Concurrent;
using System.Text.Json;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingMemoryEngine;
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
    private readonly AgentSessionManager _sessionManager;
    private readonly InMemoryRuntimeSessionStore _runtimeSessionStore;
    private readonly MemoryEngine _memory;
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
    private readonly ILogger<AgentExecutionService> _logger;

    private readonly ConcurrentDictionary<string, List<ChatMessage>> _histories = new();

    public AgentExecutionService(
        AgentSessionManager sessionManager,
        InMemoryRuntimeSessionStore runtimeSessionStore,
        MemoryEngine memory,
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
        ILogger<AgentExecutionService> logger)
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

        // ── 获取/创建 Agent 实例 ──────────────────────────────────────
        var instance = _sessionManager.GetOrCreate(request.SessionId, request.AgentTemplateId);
        _runtimeSessionStore.GetOrCreate(
            request.SessionId, instance.AgentInstanceId,
            request.WorkspaceId, request.AgentTemplateId);

        // ── 注册并预下载 Skill 包────────────────────────────────────
        var skillPackages = request.SkillPackages ?? [];
        _skillPackageRegistry.Register(instance.AgentInstanceId, skillPackages);
        if (skillPackages.Count > 0)
            await _skillPackageDownloader.EnsureDownloadedAsync(skillPackages);

        // 前端给全局模板附加了 "global:" 前缀（用于在 UI 中区分工作区模板），
        // Runtime 侧查内置模板时需剥除前缀后再匹配。
        const string globalPrefix = "global:";
        var canonicalTemplateId = request.AgentTemplateId.StartsWith(globalPrefix, StringComparison.OrdinalIgnoreCase)
            ? request.AgentTemplateId[globalPrefix.Length..]
            : request.AgentTemplateId;

        var template = BuiltInAgentTemplates.FindById(canonicalTemplateId)
                       ?? BuiltInAgentTemplates.WorkspaceServiceAgent;
        var effectiveCapability = request.CapabilityPolicy ?? template.Capability;

        // ── 构建对话历史 ─────────────────────────────────────────────
        var history = _histories.GetOrAdd(request.SessionId, _ => []);
        if (history.Count == 0)
        {
            history.Add(new ChatMessage(ChatRole.System,
                BuildSystemPrompt(template, request.SessionId, request.WorkspaceId, effectiveCapability, instance.AgentInstanceId)));
        }
        else if (template.Memory?.EnableSessionMemory == true
              || template.Memory?.EnableWorkspaceMemory == true)
        {
            if (history[0].Role == ChatRole.System)
                history[0] = new ChatMessage(ChatRole.System,
                    BuildSystemPrompt(template, request.SessionId, request.WorkspaceId, effectiveCapability, instance.AgentInstanceId));
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

        // ── 创建与外部令牌联结的执行控制令牌 ─────────────────────────
        var ct = _controlRegistry.CreateLinkedToken(request.SessionId, external);

        string             finalMessage   = "(no response)";
        var                stopReason     = AgentLoopStopReason.MaxRoundsReached;
        var                execState      = AgentExecutionState.Running;
        string?            resumeAnchorId = null;

        // 记录本次 dispatch 前已有的 journal 条数，用于在结束时截取本次新增的 turns
        var journalStartCount = _journal.GetTurns(request.SessionId).Count;

        // 护栏状态
        var  totalSw          = System.Diagnostics.Stopwatch.StartNew();
        int  totalToolCalls   = 0;
        int  noProgressCount  = 0;   // 连续无工具调用进展的轮次计数
        var  toolRepeatMap    = new Dictionary<string, int>(StringComparer.Ordinal);

        await FireHooksAsync(h => h.OnLoopStartAsync(loopCtx, ct));

        try
        {
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
                var llmResp = await _llmClient.ChatAsync(
                    request.WorkspaceId, request.SessionId,
                    request.AgentTemplateId, history, llmTools, request.LlmConfig, ct);
                llmSw.Stop();

                var rawText = llmResp.Content ?? "{}";
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

                        var repeatKey = $"{call.Name}|{call.ArgumentsJson}";
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
                        await FireHooksAsync(h => h.OnToolCallAsync(loopCtx, round, call.Name, call.ArgumentsJson, ct));

                        var skillResult = await _skillRuntime.InvokeAsync(
                            call.Name,
                            new SkillInvokeRequest
                            {
                                AgentInstanceId = instance.AgentInstanceId,
                                WorkspaceId = request.WorkspaceId,
                                SessionId = request.SessionId,
                                Input = ExtractInputFromJson(call.ArgumentsJson),
                                Parameters = ExtractParametersFromJson(call.ArgumentsJson),
                            },
                            effectiveCapability,
                            ct);

                        await FireHooksAsync(h => h.OnToolResultAsync(loopCtx, round, call.Name, skillResult, ct));

                        var toolPayload = skillResult.Success
                            ? $"Tool '{call.Name}' succeeded (exit={skillResult.ExitCode}):\n{skillResult.Output}"
                            : $"Tool '{call.Name}' failed (exit={skillResult.ExitCode}):\n{skillResult.Error}";

                        history.Add(new ChatMessage(ChatRole.Tool, toolPayload, ToolCallId: call.Id));

                        _journal.Record(request.SessionId, new TurnRecord
                        {
                            Round = round,
                            StartedAt = turnStart,
                            CompletedAt = DateTimeOffset.UtcNow,
                            Status = "CONTINUE",
                            MessageSummary = Truncate(rawText, 512),
                            ToolName = call.Name,
                            ToolArgs = call.ArgumentsJson,
                            ToolSuccess = skillResult.Success,
                            ToolError = skillResult.Error,
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
                    toolArgs = argsJson;

                    // 检查点 D：相同工具相同参数重复次数
                    var repeatKey = $"{toolName}|{argsJson}";
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
                            ToolName = toolName, ToolArgs = argsJson,
                            ToolSuccess = false, ToolError = "MaxSameToolRepeat reached",
                        });
                        continue; // 给 LLM 机会换策略，不计入轮次工具调用
                    }
                    toolRepeatMap[repeatKey] = repeatCount + 1;

                    await FireHooksAsync(h => h.OnToolCallAsync(loopCtx, round, toolName, argsJson, ct));
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
                            Input           = ExtractInput(loopResp.Tool.Args),
                            Parameters      = ExtractParameters(loopResp.Tool.Args),
                        },
                        effectiveCapability, ct);

                    toolSuccess = skillResult.Success;
                    toolError   = skillResult.Error;

                    await FireHooksAsync(h => h.OnToolResultAsync(loopCtx, round, toolName, skillResult, ct));

                    var toolMsg = skillResult.Success
                        ? $"Tool '{toolName}' succeeded (exit={skillResult.ExitCode}):\n{skillResult.Output}"
                        : $"Tool '{toolName}' failed (exit={skillResult.ExitCode}):\n{skillResult.Error}";
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
            _logger.LogWarning("[AgentExec] Cancelled session={Session}", request.SessionId);
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
        }

        // ── 记忆写回 ──────────────────────────────────────────────────
        if (template.Memory?.EnableSessionMemory == true
         || template.Memory?.EnableWorkspaceMemory == true)
        {
            _memory.WriteBack(finalMessage, request.SessionId, request.WorkspaceId, instance.AgentInstanceId);
        }

        TrimHistory(history, template.Runtime?.MaxContextTokens ?? 8192);
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
            TurnSteps       = CollectNewTurnSteps(request.SessionId, journalStartCount),
        };
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

    private string BuildSystemPrompt(
        AgentTemplateDefinition template,
        string sessionId,
        string? workspaceId,
        CapabilityPolicy? capability,
        string agentInstanceId)
    {
        var sb = new System.Text.StringBuilder(
            template.SystemPrompt ?? "You are a helpful assistant.");

        if (template.Memory?.EnableSessionMemory == true
         || template.Memory?.EnableWorkspaceMemory == true)
        {
            var memCtx = _memory.BuildMemoryContext(sessionId, workspaceId);
            if (memCtx is not null)
                sb.Append("\n\n---\n").Append(memCtx);
        }

        // 注入已挂载的 Skill 包信息（名称+用途，不披露下载 URL）
        var pkgs = _skillPackageRegistry.Get(agentInstanceId);
        if (pkgs.Count > 0)
        {
            sb.Append("\n\n---\n## Available Skill Packages\n");
            sb.Append("The following skill packages are pre-installed at /skills/<package-id>/ in your container:\n");
            foreach (var pkg in pkgs)
            {
                sb.Append($"- **{pkg.Name}** (`/skills/{pkg.SkillPackageId}/`)");
                if (!string.IsNullOrWhiteSpace(pkg.Description))
                    sb.Append($": {pkg.Description}");
                sb.AppendLine();
            }
            sb.Append("Invoke scripts or binaries in these directories directly from your shell commands.\n");
        }

        sb.Append(_skillRuntime.BuildLoopInstructions(capability));
        return sb.ToString();
    }

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

    private static void TrimHistory(List<ChatMessage> history, int maxTokenBudget)
    {
        const int maxMessages = 40;
        if (history.Count <= maxMessages + 1) return;

        var system = history.FirstOrDefault(m => m.Role == ChatRole.System);
        var recent = history.TakeLast(maxMessages).ToList();
        history.Clear();
        if (system is not null) history.Add(system);
        history.AddRange(recent);
    }

    private static string Truncate(string s, int maxLen) =>
        s.Length <= maxLen ? s : s[..maxLen] + "…";
}
