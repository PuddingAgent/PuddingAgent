using System.Runtime.CompilerServices;
using System.Threading.Channels;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Services;
using PuddingCode.SubAgents;
using PuddingMemoryEngine.Data;
using PuddingRuntime.Services.AgentLoop;
using PuddingRuntime.Services.Background;
using PuddingRuntime.Services.Skills;
using PuddingCode.Observability;
using PuddingCode.Runtime;
using Serilog.Context;

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
    private readonly ILlmInvocationService? _llmInvocationService;
    private readonly SkillRuntime _skillRuntime;
    private readonly AgentExecutionGuardrails _guardrails;
    private readonly ExecutionControlRegistry _controlRegistry;
    private readonly ExecutionJournal _journal;
    private readonly CompletionPolicy _completionPolicy;
    private readonly AgentSkillPackageRegistry _skillPackageRegistry;
    private readonly SkillPackageDownloadService _skillPackageDownloader;
    private readonly IReadOnlyList<IAgentLoopHook> _hooks;
    private readonly ContextPipeline _contextPipeline;
    private readonly IContextAssemblyService? _contextAssemblyService;
    private readonly ContextWindowManager _contextManager;
    private readonly IKeyVaultService _keyVaultService;
    private readonly PuddingMemoryEngine.Data.JsonlSessionWriter? _jsonlSessionWriter;
    private readonly ITerminalProcessManager _terminalManager;
    private readonly IMemoryLibraryConvenience? _libraryConvenience;
    private readonly Channel<ConsolidationJob>? _subconsciousJobChannel;
    private readonly bool _hasSubconsciousHook;
    private readonly IStreamingEventBus? _eventBus;
    private readonly SessionArchiver? _sessionArchiver;
    private readonly ILogger<AgentExecutionService> _logger;
    private readonly ILlmResolver? _llmResolver; // 可选：为子代理等无 LlmConfig 场景兜底
    private readonly ISessionStateManager? _ssm;  // ADR-016：会话状态层
    private readonly IRuntimeActivitySink? _activitySink;
    private readonly ISubAgentRunStore? _subAgentRunStore; // ADR-021：子代理运行归档
    private readonly ISubAgentManager? _subAgentManager;   // ADR-021：避免 run 双创建
    private readonly IToolInvocationService? _toolInvocationService;       // ADR-026：工具调用 facade
    private readonly ISubAgentInvocationService? _subAgentInvocationService; // ADR-026：子代理调用 facade
    private readonly ISessionOutputWriter? _sessionOutputWriter;           // ADR-026：会话输出 facade

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
        ContextPipeline contextPipeline,
        ContextWindowManager contextManager,
        ILogger<AgentExecutionService> logger,
        IContextAssemblyService? contextAssemblyService = null,
        ILlmInvocationService? llmInvocationService = null,
        IKeyVaultService? keyVaultService = null,
        PuddingMemoryEngine.Data.JsonlSessionWriter? jsonlSessionWriter = null,
        ITerminalProcessManager? terminalManager = null,
        IMemoryLibraryConvenience? libraryConvenience = null,
        Channel<ConsolidationJob>? subconsciousJobChannel = null,
        IStreamingEventBus? eventBus = null,
        SessionArchiver? sessionArchiver = null,
        ILlmResolver? llmResolver = null,
        ISessionStateManager? ssm = null,
        IRuntimeActivitySink? activitySink = null,
        ISubAgentRunStore? subAgentRunStore = null,
        ISubAgentManager? subAgentManager = null,
        IToolInvocationService? toolInvocationService = null,
        ISubAgentInvocationService? subAgentInvocationService = null,
        ISessionOutputWriter? sessionOutputWriter = null)
    {
        _sessionManager      = sessionManager;
        _runtimeSessionStore = runtimeSessionStore;
        _memory              = memory;
        _sandbox             = sandbox;
        _llmClient           = llmClient;
        _llmInvocationService = llmInvocationService;
        _skillRuntime        = skillRuntime;
        _guardrails          = guardrails;
        _controlRegistry     = controlRegistry;
        _journal             = journal;
        _completionPolicy    = completionPolicy;
        _skillPackageRegistry    = skillPackageRegistry;
        _skillPackageDownloader  = skillPackageDownloader;
        _hooks               = hooks.ToArray();
        _contextPipeline     = contextPipeline;
        _contextAssemblyService = contextAssemblyService;
        _contextManager      = contextManager;
        _keyVaultService     = keyVaultService ?? NoOpKeyVaultService.Instance;
        _jsonlSessionWriter  = jsonlSessionWriter;
        _terminalManager     = terminalManager ?? NoOpTerminalProcessManager.Instance;
        _libraryConvenience  = libraryConvenience;
        _subconsciousJobChannel = subconsciousJobChannel;
        _hasSubconsciousHook = _hooks.Any(h => h is SubconsciousConsolidationHook);
        _eventBus            = eventBus;
        _sessionArchiver     = sessionArchiver;
        _logger              = logger;
        _llmResolver         = llmResolver;
        _ssm                 = ssm;  // ADR-016
        _activitySink        = activitySink;
        _subAgentRunStore    = subAgentRunStore;
        _subAgentManager     = subAgentManager; // ADR-021
        _toolInvocationService     = toolInvocationService;       // ADR-026
        _subAgentInvocationService = subAgentInvocationService; // ADR-026
        _sessionOutputWriter       = sessionOutputWriter;           // ADR-026

        if (_ssm is null)
            _logger.LogWarning("[AgentExec] SSM is NULL — SSE frames will NOT be forwarded through SessionStateManager");
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

        using var logScope = LogContext.PushProperty("SessionId", request.SessionId);

        // 前端给全局模板附加了 "global:" 前缀（用于在 UI 中区分工作区模板），
        // Runtime 侧查内置模板时需剥除前缀后再匹配。
        const string globalPrefix = "global:";
        var canonicalTemplateId = request.AgentTemplateId.StartsWith(globalPrefix, StringComparison.OrdinalIgnoreCase)
            ? request.AgentTemplateId[globalPrefix.Length..]
            : request.AgentTemplateId;

        var template = BuiltInAgentTemplates.FindById(canonicalTemplateId)
                       ?? BuiltInAgentTemplates.WorkspaceServiceAgent;
        var effectiveCapability = MergeCapability(request.CapabilityPolicy, template.Capability);
        var sessionTimeout = ResolveSessionTimeout(template);
        // TODO(platform-template-guardrails): 从 AgentTemplate（Global/Workspace）读取 MaxRounds/MaxElapsedSeconds/MaxToolCallsTotal，
        // 并覆盖注入 AgentExecutionGuardrails；当前阶段仅支持在模板侧存储配置，不参与 Runtime 执行。

        var execTrace = RuntimeTraceContext.CreateNew(
            sessionId: request.SessionId,
            workspaceId: request.WorkspaceId);
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
        var instance = _sessionManager.GetOrCreate(request.SessionId, request.AgentTemplateId, sessionTimeout);
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
                await TryEmitContextAssembledAsync(subAgentRunId, request.SessionId, ct);
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
                await TryEmitContextAssembledAsync(subAgentRunId, request.SessionId, ct);
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
        history.Add(new ChatMessage(ChatRole.User, request.MessageText));

        // ── 初始化 Loop 上下文 ────────────────────────────────────────
        var maxRounds = request.MaxRounds > 0
            ? Math.Min(request.MaxRounds, _guardrails.MaxRounds)
            : _guardrails.MaxRounds;

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

            for (int round = 0; round < maxRounds; round++)
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
                var availableSkillNames = _skillRuntime.GetAvailableSkills(effectiveCapability, template)
                    .Select(s => s.SkillId)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var llmTools = request.ToolDefinitions is { Count: > 0 }
                    ? request.ToolDefinitions
                        .Where(t => availableSkillNames.Contains(t.Name))
                        .ToList()
                    : _skillRuntime.BuildLlmTools(effectiveCapability).ToList();

                // 合并 SkillRuntime 中 DB 未覆盖的工具（如 spawn_sub_agent）
                var dbToolNames = llmTools.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var runtimeTools = _skillRuntime.BuildLlmTools(effectiveCapability);
                foreach (var rt in runtimeTools)
                {
                    if (!dbToolNames.Contains(rt.Name))
                    {
                        llmTools.Add(rt);
                        _logger.LogDebug("[AgentExec] Merged runtime tool: {Tool}", rt.Name);
                    }
                }

                var injectedHistory = await BuildInjectedHistoryAsync(history, ct);
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
                            Profile = new PuddingCode.Runtime.LlmInvocationProfile
                            {
                                ProviderId = "legacy.direct",
                                ProfileId = "legacy.default",
                                ModelId = effectiveLlmConfig?.ModelId ?? "default",
                            },
                            Messages = injectedHistory,
                            Tools = llmTools,
                        }, ct);

                        if (!facadeResult.Success)
                        {
                            _logger.LogError(
                                "[AgentExec] LLM facade error round={Round} session={Session} error={Error}",
                                round + 1, request.SessionId, facadeResult.Error);
                            history.Add(new ChatMessage(ChatRole.Tool,
                                $"⚠️ LLM API call failed: {facadeResult.Error}\n" +
                                "The previous tool result may contain content the model cannot process. " +
                                "Please summarize the tool output in your own words and try a different approach.",
                                ToolCallId: "llm-error"));
                            continue;
                        }

                        llmResp = new LlmResponse(facadeResult.ReplyText, facadeResult.ToolCalls, Usage: facadeResult.Usage);
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
                    _logger.LogError(ex, "[AgentExec] LLM API error round={Round} session={Session}", round + 1, request.SessionId);
                    history.Add(new ChatMessage(ChatRole.Tool,
                        $"⚠️ LLM API call failed: {ex.Message}\n" +
                        "The previous tool result may contain content the model cannot process. " +
                        "Please summarize the tool output in your own words and try a different approach.",
                        ToolCallId: "llm-error"));
                    continue;
                }
                if (llmResp.Usage is not null)
                    usage = llmResp.Usage with { ContextWindowTokens = template.Runtime?.MaxContextTokens ?? 0 };
                llmSw.Stop();

                var rawText = await _keyVaultService.StripAsync(llmResp.Content ?? "{}", ct);
                _logger.LogInformation(
                    "[AgentExec] LLM round={Round}/{Max} session={Session} elapsed={Ms}ms",
                    round + 1, maxRounds, request.SessionId, llmSw.ElapsedMilliseconds);

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

                        // 权限检查：High 权限工具需要用户确认
                        var skill = _skillRuntime.TryGetSkill(call.Name);
                        SkillResult skillResult;
                        if (skill is not null && !await CheckToolPermissionAsync(skill, request.SessionId, ct))
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
                                        ToolCallId = call.Id,
                                        ToolName = call.Name,
                                        ArgumentsJson = injectedArgsJson,
                                        Trace = execTrace,
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
                                _logger.LogError(ex,
                                    "[AgentExec:ToolAudit] Tool={ToolName} Exception DurationMs={DurationMs} ArgsHash={ArgsHash} Session={SessionId}",
                                    call.Name, toolSw.ElapsedMilliseconds, toolArgsHash, request.SessionId);
                                throw;
                            }
                        }

                        await FireHooksAsync(h => h.OnToolResultAsync(loopCtx, round, call.Name, skillResult, ct));

                        var toolPayloadRaw = skillResult.Success
                            ? $"✅ Tool '{call.Name}' succeeded (exit={skillResult.ExitCode}):\n{skillResult.Output}"
                            : $"❌ Tool '{call.Name}' FAILED (exit={skillResult.ExitCode})\n" +
                              $"   Error: {skillResult.Error}\n" +
                              $"   💡 Suggestion: Check the tool's parameter constraints (paths must be within workspace, commands must use valid syntax, etc.). " +
                              $"If the tool has access restrictions, try an alternative approach or use a different tool.";
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

                history.Add(new ChatMessage(ChatRole.Assistant, rawText,
                    ReasoningContent: llmResp.ReasoningContent));

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
                                ToolCallId = toolName, // CONTINUE 路径没有独立 toolCallId
                                ToolName = toolName,
                                ArgumentsJson = injectedArgsJson,
                                Trace = execTrace,
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
                        _logger.LogError(ex,
                            "[AgentExec:ToolAudit] Tool={ToolName} Exception DurationMs={DurationMs} ArgsHash={ArgsHash} Session={SessionId}",
                            toolName, toolSw2.ElapsedMilliseconds, toolArgsHash2, request.SessionId);
                        throw;
                    }

                    toolSuccess = skillResult.Success;
                    toolError   = string.IsNullOrWhiteSpace(skillResult.Error)
                        ? skillResult.Error
                        : await _keyVaultService.StripAsync(skillResult.Error, ct);

                    await FireHooksAsync(h => h.OnToolResultAsync(loopCtx, round, toolName, skillResult, ct));

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
                if (round == maxRounds - 1)
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

            // 完成子代理运行归档（ADR-021）
            await TryCompleteSubAgentRunAsync(
                subAgentRunId, request.SessionId, false,
                finalMessage, "Cancelled", 0, totalToolCalls, totalSw.ElapsedMilliseconds, CancellationToken.None);
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
                finalMessage, ex.Message, 0, totalToolCalls, totalSw.ElapsedMilliseconds, CancellationToken.None);

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
                ? new Exception($"Execution {execState}: {stopReason}")
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
        var newTurnCount = _journal.GetTurns(request.SessionId).Count - journalStartCount;
        var executeIsSuccess = execState is AgentExecutionState.Completed or AgentExecutionState.WaitingEvent;
        await TryCompleteSubAgentRunAsync(
            subAgentRunId, request.SessionId, executeIsSuccess,
            finalMessage, executeIsSuccess ? null : $"Execution ended with state={execState}",
            newTurnCount, totalToolCalls, totalSw.ElapsedMilliseconds, CancellationToken.None);

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

        using var logScope = LogContext.PushProperty("SessionId", request.SessionId);

        const string globalPrefix = "global:";
        var canonicalTemplateId = request.AgentTemplateId.StartsWith(globalPrefix, StringComparison.OrdinalIgnoreCase)
            ? request.AgentTemplateId[globalPrefix.Length..]
            : request.AgentTemplateId;

        var template = BuiltInAgentTemplates.FindById(canonicalTemplateId)
                       ?? BuiltInAgentTemplates.WorkspaceServiceAgent;
        var effectiveCapability = MergeCapability(request.CapabilityPolicy, template.Capability);
        var sessionTimeout = ResolveSessionTimeout(template);

        _contextManager.CleanupExpiredSessions(request.SessionId);

        var instance = _sessionManager.GetOrCreate(request.SessionId, request.AgentTemplateId, sessionTimeout);
        _sessionManager.MarkRunning(request.SessionId);
        _contextManager.TouchHistoryAccess(request.SessionId, sessionTimeout);

        _runtimeSessionStore.GetOrCreate(
            request.SessionId, instance.AgentInstanceId,
            request.WorkspaceId, request.AgentTemplateId);

        // ── Streaming trace context ─────────────────────────────────
        var streamTrace = RuntimeTraceContext.CreateNew(
            sessionId: request.SessionId,
            workspaceId: request.WorkspaceId);

        // ── 子代理运行归档（ADR-021）───────────────────────────────
        var streamSubAgentRunId = await TryCreateSubAgentRunAndEmitStartedAsync(
            request, instance.AgentInstanceId, CancellationToken.None);

        var skillPackages = request.SkillPackages ?? [];
        _skillPackageRegistry.Register(instance.AgentInstanceId, skillPackages);
        if (skillPackages.Count > 0)
            await _skillPackageDownloader.EnsureDownloadedAsync(skillPackages);

        var ct = _controlRegistry.CreateLinkedToken(request.SessionId, external);

        // ── 全管道性能诊断 ──
        var perfTotalSw = System.Diagnostics.Stopwatch.StartNew();
        var perfHistorySw = System.Diagnostics.Stopwatch.StartNew();
        var history = _contextManager.GetOrCreateHistory(request.SessionId);
        await _contextManager.TryHydrateStreamHistoryFromDbAsync(
            request.SessionId,
            history,
            template.Runtime?.MaxContextTokens ?? 8000,
            ct);
        perfHistorySw.Stop();
        _logger.LogInformation(
            "[AgentExec:Perf] History loaded session={Session} elapsed={Ms}ms count={Count}",
            request.SessionId, perfHistorySw.ElapsedMilliseconds, history.Count);

        var perfContextSw = System.Diagnostics.Stopwatch.StartNew();
        var streamingSystemPrompt = await _contextPipeline.AssembleAsync(new ContextRequest
        {
            Template = template,
            WorkspaceId = request.WorkspaceId ?? string.Empty,
            SessionId = request.SessionId,
            AgentTemplateId = request.AgentTemplateId,
            UserMessage = request.MessageText,
            Capability = effectiveCapability,
            AgentInstanceId = instance.AgentInstanceId,
            ForStreaming = true,
            IsFirstMessage = history.Count == 0,
            SessionHistory = history.Where(m => m.Role != ChatRole.System).ToList(),
        }, ct);
        perfContextSw.Stop();
        _logger.LogInformation(
            "[AgentExec:Perf] Context assembled session={Session} elapsed={Ms}ms promptLen={Len}",
            request.SessionId, perfContextSw.ElapsedMilliseconds, streamingSystemPrompt.SystemPrompt.Length);

        // 子代理上下文装配完毕事件（ADR-021）
        await TryEmitContextAssembledAsync(streamSubAgentRunId, request.SessionId, ct);

        if (history.Count == 0 || history[0].Role != ChatRole.System)
        {
            history.Insert(0, new ChatMessage(ChatRole.System, streamingSystemPrompt.SystemPrompt));
        }
        else
        {
            history[0] = new ChatMessage(ChatRole.System, streamingSystemPrompt.SystemPrompt);
        }

        history.Add(new ChatMessage(ChatRole.User, request.MessageText));

        var loopCtx = new AgentLoopContext
        {
            SessionId       = request.SessionId,
            AgentInstanceId = instance.AgentInstanceId,
            WorkspaceId     = request.WorkspaceId,
            AgentTemplateId = request.AgentTemplateId,
            UserMessage     = request.MessageText,
            MaxRounds       = request.MaxRounds > 0 ? request.MaxRounds : 5,
        };

        var effectiveLlmConfig = await ResolveLlmConfigAsync(request.LlmConfig, ct);
        // 若上游 LlmConfig 未设置 ReasoningEffort，从模板定义继承
        if (effectiveLlmConfig?.ReasoningEffort is null && template.ReasoningEffort is not null)
            effectiveLlmConfig = (effectiveLlmConfig ?? new LlmConfig()) with { ReasoningEffort = template.ReasoningEffort };

        // 构建工具定义：优先用上游下发的 ToolDefinitions，否则从 SkillRuntime 构建
        var availableSkillNames = _skillRuntime.GetAvailableSkills(effectiveCapability, template)
            .Select(s => s.SkillId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var llmTools = request.ToolDefinitions is { Count: > 0 }
            ? request.ToolDefinitions
                .Where(t => availableSkillNames.Contains(t.Name))
                .ToList()
            : _skillRuntime.BuildLlmTools(effectiveCapability).ToList();

        // 合并 SkillRuntime 中 DB 未覆盖的工具
        var dbToolNames2 = llmTools.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var runtimeTools2 = _skillRuntime.BuildLlmTools(effectiveCapability);
        foreach (var rt in runtimeTools2)
        {
            if (!dbToolNames2.Contains(rt.Name))
            {
                llmTools.Add(rt);
                _logger.LogDebug("[AgentExec] Stream merged runtime tool: {Tool}", rt.Name);
            }
        }

        try
        {
            _contextManager.MarkSessionExecuting(request.SessionId);
            await FireHooksAsync(h => h.OnLoopStartAsync(loopCtx, ct));

            // ADR-016：将每一帧推送到 SessionStateManager（持久化 + 实时 Channel）
            // fire-and-forget，不阻塞流式管道；AppendAsync 内部 TryWrite Channel 非阻塞
            async void Append(ServerSentEventFrame frame)
            {
                try
                {
                    if (_sessionOutputWriter is not null)
                    {
                        await _sessionOutputWriter.WriteFrameAsync(
                            request.SessionId, request.WorkspaceId ?? "", frame, trace: null, CancellationToken.None);
                    }
                    else if (_ssm is not null)
                    {
                        // ADR-027 legacy fallback for tests only (SSM)
                        _logger.LogWarning("[AgentExec:Append] SessionOutputWriter not wired, falling back to SSM direct — session={Session}", request.SessionId);
                        await _ssm.AppendAsync(request.SessionId, request.WorkspaceId ?? "", frame, CancellationToken.None);
                    }
                    else
                    {
                        _logger.LogWarning("[AgentExec:Append] No output writer available, cannot push frame type={Type} session={Session}", frame.Event, request.SessionId);
                    }
                }
                catch (Exception ex) { _logger.LogWarning(ex, "[AgentExec:Append] AppendAsync failed session={Session}", request.SessionId); }
            }

            // ── 流式 Agent Loop（与同步路径共享护栏参数）──────
            var maxRounds = request.MaxRounds > 0
                ? Math.Min(request.MaxRounds, _guardrails.MaxRounds)
                : _guardrails.MaxRounds;
            var reply = "(no response)";
            TokenUsageDto? usage = null;
            var hasExecutedAnyTool = false;
            var lastToolResult = "(未执行任何工具)";
            var consecutiveShortReplies = 0;
            var totalToolCalls = 0;

            for (int round = 0; round < maxRounds; round++)
            {
                var roundSw = System.Diagnostics.Stopwatch.StartNew();
                int roundDeltaFrames = 0;
                int roundThinkingFrames = 0;

                // ── 检查点：取消 / 最大耗时 / 最大工具调用 ──
                if (ct.IsCancellationRequested)
                {
                    _logger.LogInformation("[AgentExec:Stream] Cancelled session={Session}", request.SessionId);
                    var cancelledFrame = ServerSentEventFrame.Json(SseEventTypes.Cancelled, new { message = "已取消" });
                    Append(cancelledFrame);
                    yield return cancelledFrame;
                    break;
                }
                if (perfTotalSw.Elapsed > _guardrails.MaxElapsed)
                {
                    _logger.LogWarning("[AgentExec:Stream] MaxElapsed={Max} exceeded", _guardrails.MaxElapsed);
                    var timeoutFrame = ServerSentEventFrame.Json(SseEventTypes.Error, new { message = $"执行超时 ({_guardrails.MaxElapsed.TotalSeconds}s)" });
                    Append(timeoutFrame);
                    yield return timeoutFrame;
                    break;
                }
                if (totalToolCalls >= _guardrails.MaxToolCallsTotal)
                {
                    _logger.LogWarning("[AgentExec:Stream] MaxToolCallsTotal={Max} reached", _guardrails.MaxToolCallsTotal);
                    var maxToolFrame = ServerSentEventFrame.Json(SseEventTypes.Error, new { message = $"工具调用次数已达上限 ({_guardrails.MaxToolCallsTotal})" });
                    Append(maxToolFrame);
                    yield return maxToolFrame;
                    break;
                }

                // 发送 context 帧（仅第1轮）
                if (round == 0)
                {
                    perfTotalSw.Stop();
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
                var replyBuf = new StringBuilder();
                var reasoningBuf = new StringBuilder();

                var injectedHistory = await BuildInjectedHistoryAsync(history, ct);
                IAsyncEnumerator<StreamDelta> llmEnumerator;
                if (_llmInvocationService is not null)
                {
                    llmEnumerator = _llmInvocationService.InvokeStreamAsync(new PuddingCode.Runtime.LlmInvocationRequest
                    {
                        WorkspaceId = request.WorkspaceId,
                        SessionId = request.SessionId,
                        AgentInstanceId = instance.AgentInstanceId,
                        AgentTemplateId = request.AgentTemplateId,
                        Profile = new PuddingCode.Runtime.LlmInvocationProfile
                        {
                            ProviderId = "legacy.direct",
                            ProfileId = "legacy.default",
                            ModelId = effectiveLlmConfig?.ModelId ?? "default",
                        },
                        Messages = injectedHistory,
                        Tools = llmTools,
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
                            var thinkingFrame = ServerSentEventFrame.Json(SseEventTypes.Thinking,
                                new { delta = delta.ReasoningDelta });
                            Append(thinkingFrame);
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
                            var safeDelta = await _keyVaultService.StripAsync(delta.ContentDelta, ct);
                            replyBuf.Append(safeDelta);
                            var deltaFrame = ServerSentEventFrame.Json(SseEventTypes.Delta,
                                new { delta = safeDelta });
                            Append(deltaFrame);
                            yield return deltaFrame;
                            _ = _eventBus?.EmitAsync(new StreamingEvent
                            {
                                Type = StreamingEventTypes.AgentDelta,
                                Data = new { delta = safeDelta }
                            }, ct);
                        }

                        // 工具调用增量 → 累积
                        if (delta.ToolCallIndex != null)
                        {
                            AccumulateToolCall(accumulatedToolCalls, delta);
                            hasToolCalls = true;
                        }

                        if (delta.Usage is not null)
                            usage = delta.Usage;
                    }
                }
                finally
                {
                    await llmEnumerator.DisposeAsync();
                }

                // LLM API 出错 → 发送 error 事件后继续下一轮
                if (llmException != null)
                {
                    _logger.LogError(llmException, "[AgentExec] LLM API error in streaming loop, round={Round}", round);
                    var errFrame = ServerSentEventFrame.Json(SseEventTypes.Error, new { message = $"LLM 调用失败: {llmException.Message}" });
                    Append(errFrame);
                    yield return errFrame;
                    continue;
                }

                // 发送 usage
                if (usage is not null)
                {
                    var usageFrame = ServerSentEventFrame.Json(SseEventTypes.Usage, usage);
                    Append(usageFrame);
                    yield return usageFrame;
                }

                // 无工具调用 → 终止循环，replyBuf 即为最终回复
                if (!hasToolCalls)
                {
                    reply = replyBuf.Length > 0
                        ? await _keyVaultService.StripAsync(replyBuf.ToString(), ct)
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
                        ReasoningContent: assistantReasoningContent));
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
                var assistantToolCalls = accumulatedToolCalls
                    .Select(tc => new ToolCall(tc.Id, tc.Name, tc.Arguments))
                    .ToList();
                var assistantContent = replyBuf.Length > 0
                    ? await _keyVaultService.StripAsync(replyBuf.ToString(), ct)
                    : null;
                history.Add(new ChatMessage(ChatRole.Assistant, assistantContent,
                    ToolCalls: assistantToolCalls,
                    ReasoningContent: reasoningBuf.Length > 0 ? reasoningBuf.ToString() : null));

                // 逐个工具调用：发送 tool_call → 执行 → 发送 tool_result
                foreach (var tc in accumulatedToolCalls)
                {
                    var toolCallFrame = ServerSentEventFrame.Json(SseEventTypes.ToolCall,
                        new { name = tc.Name, arguments = tc.Arguments });
                    Append(toolCallFrame);
                    yield return toolCallFrame;

                    _ = _eventBus?.EmitAsync(new StreamingEvent
                    {
                        Type = StreamingEventTypes.AgentToolCall,
                        Data = new { name = tc.Name, arguments = tc.Arguments }
                    }, ct);

                    var injectedArgsJson = await _keyVaultService.InjectAsync(tc.Arguments, ct);
                    SkillResult result;
                    if (_toolInvocationService is not null)
                    {
                        var toolResult = await _toolInvocationService.InvokeAsync(new PuddingCode.Runtime.ToolInvocationRequest
                        {
                            WorkspaceId = request.WorkspaceId ?? string.Empty,
                            SessionId = request.SessionId,
                            AgentInstanceId = instance.AgentInstanceId,
                            ToolCallId = tc.Id,
                            ToolName = tc.Name,
                            ArgumentsJson = injectedArgsJson,
                            Trace = null, // Streaming local function scope
                        }, ct);
                        result = new SkillResult
                        {
                            Success = toolResult.Success,
                            Output = toolResult.Output ?? "",
                            Error = toolResult.Error,
                            ExitCode = toolResult.Success ? 0 : 1,
                        };
                    }
                    else
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

                    hasExecutedAnyTool = true;
                    totalToolCalls++;
                    lastToolResult = result.Success
                        ? $"已完成: {(result.Output?.Length > 0 ? Truncate(result.Output!, 200) : "(空输出)")}"
                        : $"执行失败: {(result.Error?.Length > 0 ? Truncate(result.Error!, 200) : "(未知错误)")}";

                    var toolResultFrame = ServerSentEventFrame.Json(SseEventTypes.ToolResult, new
                    {
                        name = tc.Name,
                        exitCode = result.ExitCode,
                        output = result.Output,
                        error = result.Error,
                    });
                    Append(toolResultFrame);
                    yield return toolResultFrame;

                    _ = _eventBus?.EmitAsync(new StreamingEvent
                    {
                        Type = StreamingEventTypes.AgentToolResult,
                        Data = new { name = tc.Name, exitCode = result.ExitCode, output = result.Output, error = result.Error }
                    }, ct);

                    // ── terminal_execute 特殊处理：订阅进程输出流 ──
                    if (tc.Name is "terminal_execute" && result.Success)
                    {
                        var pid = result.Output.Trim();
                        var terminalOutput = new StringBuilder();
                        await foreach (var line in _terminalManager.SubscribeAsync(pid, ct))
                        {
                            terminalOutput.AppendLine(line);
                            yield return ServerSentEventFrame.Json(SseEventTypes.Terminal,
                                new { pid, stream = "stdout", data = line });
                        }
                        var finalInfo = _terminalManager.ListProcesses(request.SessionId)
                            .FirstOrDefault(p => p.ProcessId == pid);
                        yield return ServerSentEventFrame.Json(SseEventTypes.Terminal,
                            new { pid, type = "exit", exitCode = finalInfo?.ExitCode });

                        // 工具结果追加到历史（携带完整输出摘要）
                        var terminalPayload = $"Tool 'terminal_execute' exited with code={finalInfo?.ExitCode}. Output:\n{Truncate(terminalOutput.ToString(), 2000)}";
                        var safeTerminalPayload = await _keyVaultService.StripAsync(terminalPayload, ct);
                        history.Add(new ChatMessage(ChatRole.Tool, safeTerminalPayload, ToolCallId: tc.Id));
                        continue;
                    }

                    // 工具结果追加到历史（非 terminal 工具）
                    var toolPayloadRaw = result.Success
                        ? $"✅ Tool '{tc.Name}' succeeded (exit={result.ExitCode}):\n{result.Output}"
                        : $"❌ Tool '{tc.Name}' FAILED (exit={result.ExitCode})\n" +
                          $"   Error: {result.Error}\n" +
                          $"   💡 Suggestion: Check the command syntax, permissions, and path constraints. Try an alternative approach.";
                    var toolPayload = await _keyVaultService.StripAsync(toolPayloadRaw, ct);
                    history.Add(new ChatMessage(ChatRole.Tool, toolPayload, ToolCallId: tc.Id));
                }
                // 下一轮 LLM 调用，模型可根据工具结果继续生成
            }

            // ── 后处理：记忆写入 + JSONL + 历史裁剪 ─────────────────
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

            // 终端执行记录持久化到记忆图书馆（fire-and-forget）
            if (_libraryConvenience is not null)
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
            TryEnqueueSubconsciousConsolidationFallback(request, instance.AgentInstanceId, reply);

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

            var contextWindowTokens = template.Runtime?.MaxContextTokens ?? 0;
            var finalUsage = usage is not null
                ? usage with { ContextWindowTokens = contextWindowTokens }
                : new TokenUsageDto { ContextWindowTokens = contextWindowTokens };
            // 如果 LLM 未产生有效回复，但工具已执行，用最后工具结果作为回复
            if (reply == "(no response)" && hasExecutedAnyTool)
            {
                reply = lastToolResult;
            }
            _logger.LogDebug("[Diag] Stream done session={Session} replyLen={Len} totalToolCalls={Ttc}",
                request.SessionId, reply.Length, totalToolCalls);

            // 完成子代理运行归档（ADR-021）
            var streamSuccess = reply != "(no response)" || hasExecutedAnyTool;
            await TryCompleteSubAgentRunAsync(
                streamSubAgentRunId, request.SessionId, streamSuccess,
                reply, streamSuccess ? null : "No response generated",
                0, totalToolCalls, perfTotalSw.ElapsedMilliseconds, CancellationToken.None);

            var doneFrame = ServerSentEventFrame.Json(SseEventTypes.Done, new
            {
                reply,
                usage = finalUsage,
                traceId = streamTrace.TraceId,
                sessionId = request.SessionId,
            });
            Append(doneFrame);
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

            _jsonlSessionWriter.Enqueue(request.SessionId, new PuddingMemoryEngine.Data.JsonlEntry
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
                _jsonlSessionWriter.Enqueue(request.SessionId, new PuddingMemoryEngine.Data.JsonlEntry
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

    /// <summary>
    /// 将 RuntimeActivity 记录到可观测性管道。当 _activitySink 为 null 时静默跳过。
    /// 异常会被吞掉（不阻断主执行链），但会记录警告日志。
    /// </summary>
    private async Task RecordActivityAsync(
        RuntimeTraceContext? trace,
        string component,
        string operation,
        string status,
        DateTimeOffset startedAt,
        DateTimeOffset? endedAt,
        long? durationMs,
        string? summary,
        IReadOnlyDictionary<string, string>? metadata,
        Exception? error,
        CancellationToken ct)
    {
        if (_activitySink is null) return;

        try
        {
            await _activitySink.RecordAsync(new RuntimeActivity
            {
                Trace = trace ?? RuntimeTraceContext.CreateNew(),
                Component = component,
                Operation = operation,
                Status = status,
                StartedAtUtc = startedAt,
                EndedAtUtc = endedAt,
                DurationMs = durationMs,
                Severity = error is null ? "info" : "error",
                Summary = summary,
                Metadata = metadata ?? new Dictionary<string, string>(),
                ErrorCode = error?.GetType().Name,
                ErrorMessage = error?.Message,
            }, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AgentExec:Activity] Record failed component={Comp} op={Op}", component, operation);
        }
    }

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

    /// <summary>
    /// 将流式 LLM 返回的 StreamDelta 工具调用片段累积为完整的 AccumulatedToolCall。
    /// 按 ToolCallIndex 分组，Name/Id 取自首次出现的 chunk，Arguments 逐步拼接。
    /// </summary>
    private static void AccumulateToolCall(List<AccumulatedToolCall> list, StreamDelta delta)
    {
        var idx = delta.ToolCallIndex!.Value;
        // 扩容到所需索引
        while (list.Count <= idx)
            list.Add(new AccumulatedToolCall { Index = list.Count });

        var tc = list[idx];
        if (delta.ToolCallId is not null)
            tc.Id = delta.ToolCallId;
        if (delta.ToolCallNameDelta is not null)
            tc.Name += delta.ToolCallNameDelta;
        if (delta.ToolCallArgsDelta is not null)
            tc.Arguments += delta.ToolCallArgsDelta;
    }

    /// <summary>
    /// 构建流式 context 帧——向客户端报告当前上下文层占比和系统提示摘要。
    /// </summary>
    private object BuildStreamContextFrame(IReadOnlyList<ChatMessage> history,
        AgentTemplateDefinition? template, CapabilityPolicy? capability)
    {
        var systemMsg = history.FirstOrDefault(m => m.Role == ChatRole.System)?.Content ?? "";
        return new
        {
            messageCount = history.Count,
            systemPromptLength = systemMsg.Length,
            templateId = template?.TemplateId ?? "",
            capability = capability?.ToString() ?? "",
        };
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
        {
            // 兜底：通过 ILlmResolver 从 DB 注册表或 .env 获取默认配置
            if (_llmResolver is not null)
                return await _llmResolver.ResolveDefaultAsync(ct);

            _logger.LogWarning("[AgentExec] No LlmConfig and no ILlmResolver available.");
            return null;
        }

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

    /// <summary>
    /// 合并 DB 策略与代码模板策略。DB 为主（覆盖布尔标志），
    /// 但 DefaultToolNames / RequiresGrantToolNames 合并两源，不丢失代码内置的默认工具。
    /// 若无 DB 策略，直接使用模板策略。
    /// </summary>
    private static CapabilityPolicy MergeCapability(CapabilityPolicy? db, CapabilityPolicy? template)
    {
        if (db is null) return template ?? new CapabilityPolicy();
        if (template is null) return db;

        var defaultTools = new HashSet<string>(db.DefaultToolNames, StringComparer.OrdinalIgnoreCase);
        foreach (var t in template.DefaultToolNames) defaultTools.Add(t);
        var grantTools = new HashSet<string>(db.RequiresGrantToolNames, StringComparer.OrdinalIgnoreCase);
        foreach (var t in template.RequiresGrantToolNames) grantTools.Add(t);

        return new CapabilityPolicy
        {
            AllowShellExecution = db.AllowShellExecution || template.AllowShellExecution,
            AllowFileWrite = db.AllowFileWrite || template.AllowFileWrite,
            AllowNetworkAccess = db.AllowNetworkAccess || template.AllowNetworkAccess,
            AllowedToolNames = db.AllowedToolNames.Count > 0 ? db.AllowedToolNames : template.AllowedToolNames,
            DefaultToolNames = defaultTools.ToList(),
            RequiresGrantToolNames = grantTools.ToList(),
        };
    }

    private static string Truncate(string s, int maxLen) =>
        s.Length <= maxLen ? s : s[..maxLen] + "…";

    /// <summary>计算字符串的 SHA256 哈希（小写十六进制），用于审计日志中脱敏参数。</summary>
    private static string ComputeSha256Hash(string input)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// 检查工具权限。High 权限需要用户确认。
    /// 返回 true 表示可以继续执行，false 表示被阻止。
    /// V1: High 权限直接拒绝并记录日志；V2: 等待用户通过前端确认。
    /// </summary>
    private async Task<bool> CheckToolPermissionAsync(IAgentSkill skill, string sessionId, CancellationToken ct)
    {
        if (skill.PermissionLevel != ToolPermissionLevel.High)
            return true;

        _logger.LogWarning("[Permission] High-risk tool '{SkillId}' needs user confirmation for session {SessionId}",
            skill.SkillId, sessionId);

        // 流式路径：发送权限请求事件到前端
        if (_eventBus is not null)
        {
            await _eventBus.EmitAsync(new StreamingEvent
            {
                Type = "agent.permission_required",
                Data = new
                {
                    tool = skill.SkillId,
                    permission = "high",
                    message = $"Agent 请求执行高危操作: {skill.Name}。是否允许？",
                },
            }, ct);
        }

        // 记录审批请求 activity
        var permStartedAt = DateTimeOffset.UtcNow;
        try
        {
            await RecordActivityAsync(
                trace: null,
                component: RuntimeActivityComponents.ToolRunner,
                operation: "approve_tool",
                status: RuntimeActivityStatuses.Started,
                startedAt: permStartedAt,
                endedAt: null,
                durationMs: null,
                summary: $"Tool permission check for '{skill.SkillId}' (High).",
                metadata: new Dictionary<string, string>
                {
                    ["tool_name"] = skill.SkillId,
                    ["permission_level"] = "High",
                    ["session_id"] = sessionId,
                },
                error: null,
                ct: CancellationToken.None);

            // V1: 简化处理 — 记录日志，返回 false 阻止执行
            // V2: 等待用户通过前端确认（实现许可 token 机制）
            await RecordActivityAsync(
                trace: null,
                component: RuntimeActivityComponents.ToolRunner,
                operation: "approve_tool",
                status: RuntimeActivityStatuses.Failed,
                startedAt: permStartedAt,
                endedAt: DateTimeOffset.UtcNow,
                durationMs: (long)(DateTimeOffset.UtcNow - permStartedAt).TotalMilliseconds,
                summary: $"Tool '{skill.SkillId}' denied — High permission requires user confirmation.",
                metadata: new Dictionary<string, string>
                {
                    ["tool_name"] = skill.SkillId,
                    ["permission_level"] = "High",
                    ["approval_result"] = "denied",
                    ["session_id"] = sessionId,
                },
                error: null,
                ct: CancellationToken.None);
            return false;
        }
        catch
        {
            // Activity 记录失败不阻断权限判定
            return false;
        }
    }

    /// <summary>
    /// 潜意识任务兜底触发：当 Hook 管线未注册时，直接在执行服务末尾投递后台任务。
    /// 该路径为 fire-and-forget，不阻塞 SSE/主循环。
    /// </summary>
    private void TryEnqueueSubconsciousConsolidationFallback(
        RuntimeDispatchRequest request,
        string agentInstanceId,
        string reply)
    {
        if (_hasSubconsciousHook || _subconsciousJobChannel is null)
            return;

        _ = Task.Run(() =>
        {
            try
            {
                var job = new ConsolidationJob
                {
                    SessionId = request.SessionId,
                    WorkspaceId = request.WorkspaceId,
                    AgentId = agentInstanceId,
                    AgentTemplateId = request.AgentTemplateId,
                    LastUserMessage = request.MessageText,
                    LastAssistantReply = reply,
                };

                _subconsciousJobChannel.Writer.TryWrite(job);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    ex,
                    "[Subconscious] Fallback enqueue ignored session={Session}",
                    request.SessionId);
            }
        });
    }

    // ════════════════════════════════════════════════════════
    // 子代理运行归档辅助方法（ADR-021）
    // ════════════════════════════════════════════════════════

    /// <summary>判断会话 ID 是否属于子代理（包含 "-sub-" 前缀）。</summary>
    private static bool IsSubAgentSession(string sessionId) =>
        sessionId.Contains("-sub-", StringComparison.Ordinal);

    /// <summary>从子代理会话 ID 提取父会话 ID。</summary>
    private static string? ExtractParentSessionId(string subSessionId)
    {
        var idx = subSessionId.IndexOf("-sub-", StringComparison.Ordinal);
        return idx > 0 ? subSessionId[..idx] : null;
    }

    /// <summary>
    /// 为子代理会话创建运行归档并发出 subagent.run.started 事件。
    /// 纯 fire-and-forget，不阻断主执行路径。
    /// </summary>
    private async Task<string?> TryCreateSubAgentRunAndEmitStartedAsync(
        RuntimeDispatchRequest request,
        string agentInstanceId,
        CancellationToken ct)
    {
        if (_subAgentRunStore is null || !IsSubAgentSession(request.SessionId))
            return null;

        var parentSessionId = ExtractParentSessionId(request.SessionId) ?? request.SessionId;

        // 异步子代理路径：SubAgentManager.SpawnAsync 已创建 run，跳过重复创建
        var existingRunId = _subAgentManager?.TryGetRunId(request.SessionId);
        if (existingRunId != null)
        {
            // run 已存在，只补发 subagent.run.started 事件
            await _subAgentRunStore.AppendEventAsync(existingRunId, "subagent.run.started", new
            {
                parent_session_id = parentSessionId,
                sub_agent_id = request.SessionId,
            }, ct);

            _logger.LogInformation(
                "[AgentExec:SubAgent] Run already exists (async spawn) runId={RunId} sub={Sub}",
                existingRunId, request.SessionId);

            return existingRunId;
        }

        // 同步子代理路径：此处首次创建 run
        var runHandle = await _subAgentRunStore.CreateRunAsync(new SubAgentRunCreateRequest
        {
            ParentSessionId = parentSessionId,
            SubSessionId = request.SessionId,
            WorkspaceId = request.WorkspaceId ?? string.Empty,
            AgentInstanceId = agentInstanceId,
            TemplateId = request.AgentTemplateId,
            Task = request.MessageText,
        }, ct);

        // 发出 subagent.run.started
        await _subAgentRunStore.AppendEventAsync(runHandle.RunId, "subagent.run.started", new
        {
            parent_session_id = parentSessionId,
            sub_agent_id = request.SessionId,
        }, ct);

        _logger.LogInformation(
            "[AgentExec:SubAgent] Run created + started runId={RunId} sub={Sub} parent={Parent}",
            runHandle.RunId, request.SessionId, parentSessionId);

        return runHandle.RunId;
    }

    /// <summary>
    /// 发出 subagent.run.context_assembled 事件（如果存在 runId）。
    /// </summary>
    private async Task TryEmitContextAssembledAsync(string? runId, string subSessionId, CancellationToken ct)
    {
        if (_subAgentRunStore is null || runId is null)
            return;

        await _subAgentRunStore.AppendEventAsync(runId, "subagent.run.context_assembled", new
        {
            parent_session_id = ExtractParentSessionId(subSessionId) ?? subSessionId,
            sub_agent_id = subSessionId,
        }, ct);
    }

    /// <summary>
    /// 完成子代理运行归档并发出 subagent.run.completed / subagent.run.failed 事件。
    /// AgentExecutionService 是 terminal 状态的唯一写入者。
    /// </summary>
    private async Task TryCompleteSubAgentRunAsync(
        string? runId,
        string subSessionId,
        bool success,
        string? output,
        string? errorMessage,
        int totalRounds,
        int totalToolCalls,
        long totalDurationMs,
        CancellationToken ct)
    {
        if (_subAgentRunStore is null || runId is null)
            return;

        var status = success ? "completed" : "failed";
        var result = await _subAgentRunStore.CompleteRunAsync(runId, new SubAgentRunCompletion
        {
            Status = status,
            Output = output,
            ErrorMessage = errorMessage,
            TotalRounds = totalRounds,
            TotalToolCalls = totalToolCalls,
            TotalDurationMs = totalDurationMs,
        }, ct);

        if (result != SubAgentRunTerminalWriteResult.Applied)
        {
            _logger.LogWarning(
                "[AgentExec:SubAgent] CompleteRunAsync returned {Result} for runId={RunId} sub={Sub} — skipping event emission",
                result, runId, subSessionId);
            return;
        }

        // 发出最终事件（仅在 Applied 时）
        var eventType = success ? "subagent.run.completed" : "subagent.run.failed";
        await _subAgentRunStore.AppendEventAsync(runId, eventType, new
        {
            parent_session_id = ExtractParentSessionId(subSessionId) ?? subSessionId,
            sub_agent_id = subSessionId,
            success,
            reply = output,
            error = errorMessage,
        }, ct);

        _logger.LogInformation(
            "[AgentExec:SubAgent] Run completed runId={RunId} sub={Sub} status={Status} rounds={Rounds} tools={Tools}",
            runId, subSessionId, status, totalRounds, totalToolCalls);
    }
}
