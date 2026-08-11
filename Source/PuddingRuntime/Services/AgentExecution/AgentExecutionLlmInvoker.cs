using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Runtime;

namespace PuddingRuntime.Services;

/// <summary>
/// LLM 调用封装：统一 facade / legacy 路径，处理 provider input recovery、异常转换与 usage 记录。
/// 从 AgentExecutionService 提取，消除上帝类（审计 P0 #1）。
/// </summary>
internal sealed class AgentExecutionLlmInvoker
{
    private readonly IRuntimeLlmClient _llmClient;
    private readonly ILlmInvocationService? _llmInvocationService;
    private readonly IKeyVaultService _keyVaultService;
    private readonly ILogger _logger;
    private readonly ContextUsageSnapshotStore? _contextUsageSnapshotStore;

    public AgentExecutionLlmInvoker(
        IRuntimeLlmClient llmClient,
        ILlmInvocationService? llmInvocationService,
        IKeyVaultService keyVaultService,
        ILogger logger,
        ContextUsageSnapshotStore? contextUsageSnapshotStore = null)
    {
        _llmClient = llmClient;
        _llmInvocationService = llmInvocationService;
        _keyVaultService = keyVaultService;
        _logger = logger;
        _contextUsageSnapshotStore = contextUsageSnapshotStore;
    }

    /// <summary>
    /// 执行单次 LLM 调用，内置 provider input recovery 重试逻辑。
    /// 返回的结构化结果由调用方根据 ShouldRetryRound / Success 分支处理。
    /// </summary>
    public async Task<AgentExecutionLlmInvocationResult> InvokeAsync(
        RuntimeDispatchRequest request,
        string agentInstanceId,
        IReadOnlyList<ChatMessage> injectedHistory,
        IReadOnlyList<LlmToolDefinition> llmTools,
        LlmConfig? effectiveLlmConfig,
        PromptPrefixSnapshot? prefixSnapshot,
        bool providerInputRecoveryAlreadyAttempted,
        int round,
        CancellationToken ct)
    {
        LlmResponse llmResp;

        try
        {
            if (_llmInvocationService is not null)
            {
                var facadeResult = await _llmInvocationService.InvokeAsync(new LlmInvocationRequest
                {
                    WorkspaceId = request.WorkspaceId,
                    SessionId = request.SessionId,
                    AgentInstanceId = agentInstanceId,
                    AgentTemplateId = request.AgentTemplateId,
                    Profile = RequireInvocationProfile(request),
                    Messages = injectedHistory,
                    Tools = llmTools,
                    PrefixSnapshot = prefixSnapshot,
                    ConfigOverride = effectiveLlmConfig,
                }, ct);

                if (!facadeResult.Success)
                {
                    if (!providerInputRecoveryAlreadyAttempted
                        && LlmRequestBudgetGuard.TryGetProviderMaxInputTokens(
                            facadeResult.Error,
                            out var providerMaxInputTokens))
                    {
                        _contextUsageSnapshotStore?.RecordProviderInputLimitFailure(
                            request.SessionId,
                            providerMaxInputTokens);
                        _logger.LogWarning(
                            "[AgentExec:ContextBudget] Provider rejected input length; recalibrating and retrying once session={Session} round={Round} providerMaxInput={ProviderMaxInput}",
                            request.SessionId,
                            round + 1,
                            providerMaxInputTokens);
                        return AgentExecutionLlmInvocationResult.CreateRetryRound();
                    }

                    _logger.LogError(
                        "[AgentExec] LLM facade error round={Round} session={Session} error={Error}",
                        round + 1, request.SessionId, facadeResult.Error);
                    return AgentExecutionLlmInvocationResult.CreateFatal(
                        $"LLM API call failed: {facadeResult.Error}");
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
                llmResp = await _llmClient.ChatAsync(
                    request.WorkspaceId, request.SessionId,
                    request.AgentTemplateId, injectedHistory, llmTools, effectiveLlmConfig, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (!providerInputRecoveryAlreadyAttempted
                && LlmRequestBudgetGuard.TryGetProviderMaxInputTokens(ex, out var providerMaxInputTokens))
            {
                _contextUsageSnapshotStore?.RecordProviderInputLimitFailure(
                    request.SessionId,
                    providerMaxInputTokens);
                _logger.LogWarning(
                    ex,
                    "[AgentExec:ContextBudget] Provider rejected input length; recalibrating and retrying once session={Session} round={Round} providerMaxInput={ProviderMaxInput}",
                    request.SessionId,
                    round + 1,
                    providerMaxInputTokens);
                return AgentExecutionLlmInvocationResult.CreateRetryRound();
            }

            _logger.LogError(ex, "[AgentExec] LLM API error round={Round} session={Session}", round + 1, request.SessionId);
            return AgentExecutionLlmInvocationResult.CreateFatal(
                $"LLM API call failed: {ex.Message}");
        }

        TokenUsageDto? usage = null;
        if (llmResp.Usage is not null)
        {
            usage = llmResp.Usage with
            {
                ContextWindowTokens = effectiveLlmConfig?.MaxContextTokens ?? 0,
            };
            _contextUsageSnapshotStore?.RecordProviderUsage(request.SessionId, usage);
        }

        return AgentExecutionLlmInvocationResult.CreateSuccess(llmResp, usage);
    }

    private static LlmInvocationProfile RequireInvocationProfile(RuntimeDispatchRequest request)
        => request.LlmProfile
            ?? throw new InvalidOperationException(
                $"Runtime dispatch for agent '{request.AgentInstanceId ?? "(unknown)"}' is missing LlmProfile. " +
                "The invocation boundary must resolve provider/profile/model before execution.");
}

/// <summary>
/// LLM 调用结果：成功返回响应与 usage；失败时区分重试（provider input recovery）与致命错误。
/// </summary>
internal sealed class AgentExecutionLlmInvocationResult
{
    public bool Success { get; private init; }
    public LlmResponse? Response { get; private init; }
    public bool ShouldRetryRound { get; private init; }
    public string? ExecutionError { get; private init; }
    public string? FinalMessage { get; private init; }
    public TokenUsageDto? Usage { get; private init; }

    public static AgentExecutionLlmInvocationResult CreateSuccess(LlmResponse response, TokenUsageDto? usage)
        => new() { Success = true, Response = response, Usage = usage };

    public static AgentExecutionLlmInvocationResult CreateRetryRound()
        => new() { ShouldRetryRound = true };

    public static AgentExecutionLlmInvocationResult CreateFatal(string executionError)
        => new() { ExecutionError = executionError, FinalMessage = executionError };
}
