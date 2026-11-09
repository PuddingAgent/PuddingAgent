using PuddingCode.Core;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using PuddingCode.Models;
using PuddingCode.Observability;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingExecutionResources.Contracts;
using PuddingRuntime.Services;

namespace PuddingRuntime.Services;

/// <summary>
/// LLM 调用 Facade，包装 IRuntimeLlmClient 对外暴露稳定契约。
/// PA-01 P2：请求携带预算身份时，provider 调用前经 IModelAttemptBudgetPort 门禁；成功按实际 usage 结算，
/// 用量缺失或 provider 异常标记 Unknown。流式门禁尚未接线（有预算身份时 fail-closed）。
/// </summary>
public sealed class LlmInvocationService : ILlmInvocationService
{
    private readonly IRuntimeLlmClient _llmClient;
    private readonly ILlmProfileResolver? _profileResolver;
    private readonly ILogger<LlmInvocationService> _logger;
    private readonly LlmInvocationPurposeAccessor? _purposeAccessor;
    private readonly IModelAttemptBudgetPort? _budgetPort;

    public LlmInvocationService(
        IRuntimeLlmClient llmClient,
        ILogger<LlmInvocationService> logger,
        ILlmProfileResolver? profileResolver = null,
        LlmInvocationPurposeAccessor? purposeAccessor = null,
        IModelAttemptBudgetPort? budgetPort = null)
    {
        _llmClient = llmClient;
        _logger = logger;
        _profileResolver = profileResolver;
        _purposeAccessor = purposeAccessor;
        _budgetPort = budgetPort;
    }

    public async Task<LlmInvocationResult> InvokeAsync(LlmInvocationRequest request, CancellationToken ct = default)
    {
        using var purposeScope = _purposeAccessor?.Push(request.Purpose);
        using var captureScope = LlmRequestCapture.Enabled
            ? new LlmRequestCapture(request.InvocationId, request.LocalEstimatedTokens, _logger)
            : null;
        var messages = NormalizeMessages(request);
        _logger.LogDebug(
            "[LlmInvocation] Invoke session={SessionId} provider={ProviderId} profile={ProfileId} model={ModelId} msgCount={MsgCount} toolCount={ToolCount} prefix={PrefixHash}",
            request.SessionId, request.Profile.ProviderId, request.Profile.ProfileId, request.Profile.ModelId,
            messages.Count, request.Tools.Count, request.PrefixSnapshot?.PrefixHash ?? "(none)");

        ModelAttemptBudgetGrant? grant = null;
        LlmAttemptBudget? perSendBudget = null;
        var settled = false;
        ResolvedLlmInvocationProfile resolved;
        try
        {
            resolved = await ResolveAsync(request, ct).ConfigureAwait(false);
            if (request.BudgetAttempt is { } budget)
            {
                if (_budgetPort is null)
                    throw new ModelAttemptBudgetException(BudgetErrorCodes.DispatchInvalid,
                        "A budget-bound invocation requires the model attempt budget port; refusing to dispatch unmanaged.");
                EnsureBudgetIdentity(budget, request);
                if (_llmClient is IBufferedSendAttemptBudgetClient)
                {
                    // The transport owns one attempt per real send (CORE-I1-06); the facade only binds the scope.
                    perSendBudget = budget;
                }
                else
                {
                    grant = await _budgetPort.BeginAsync(ToClaim(budget, request, resolved.ModelId), ct)
                        .ConfigureAwait(false);
                }
            }

            LlmResponse response;
            if (perSendBudget is not null)
            {
                using var sendScope = LlmSendAttemptScope.Push(
                    new BudgetedSendAttemptObserver(_budgetPort!, perSendBudget));
                response = await _llmClient.ChatAsync(
                    request.WorkspaceId,
                    request.SessionId,
                    request.AgentTemplateId,
                    messages,
                    request.Tools.Count > 0 ? request.Tools : null,
                    resolved.Config,
                    ct);
            }
            else
            {
                response = await _llmClient.ChatAsync(
                    request.WorkspaceId,
                    request.SessionId,
                    request.AgentTemplateId,
                    messages,
                    request.Tools.Count > 0 ? request.Tools : null,
                    resolved.Config,
                    ct);
            }

            if (grant is not null)
            {
                await SettleOrUnknownAsync(grant, response.Usage, resolved, ct).ConfigureAwait(false);
                settled = true;
            }

            return new LlmInvocationResult
            {
                Success = true,
                ReplyText = response.Content,
                ToolCalls = response.ToolCalls ?? Array.Empty<ToolCall>(),
                ReasoningContent = response.ReasoningContent,
                Usage = response.Usage,
                ContinuationState = response.ContinuationState,
                FinishReason = response.FinishReason,
                ProviderId = resolved.ProviderId,
                ModelId = resolved.ModelId,
                PrefixSnapshot = request.PrefixSnapshot,
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await TryMarkUnknownAsync(grant, "llm.cancelled").ConfigureAwait(false);
            // Cancellation is execution control, not an ordinary provider failure.
            // The Agent loop owns the timeout-vs-user-cancel classification.
            throw;
        }
        catch (ModelAttemptBudgetException ex)
        {
            if (grant is not null && !settled)
                await TryMarkUnknownAsync(grant, "llm.settlement_failed").ConfigureAwait(false);
            _logger.LogWarning("[LlmInvocation] Budget gate rejected the attempt code={Code} session={SessionId}",
                ex.Code, request.SessionId);
            return new LlmInvocationResult { Success = false, Error = ex.Message, ErrorCode = ex.Code };
        }
        catch (Exception ex)
        {
            await TryMarkUnknownAsync(grant, "llm.provider_failed").ConfigureAwait(false);
            _logger.LogError(ex, "[LlmInvocation] LLM API error session={SessionId}", request.SessionId);
            return new LlmInvocationResult
            {
                Success = false,
                Error = ex is VisionPipelineException vision ? vision.UserMessage : ex.Message,
                ErrorCode = (ex as VisionPipelineException)?.Code,
                IsVisionError = ex is VisionPipelineException,
            };
        }
    }

    public async IAsyncEnumerable<StreamDelta> InvokeStreamAsync(
        LlmInvocationRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var purposeScope = _purposeAccessor?.Push(request.Purpose);
        using var captureScope = LlmRequestCapture.Enabled
            ? new LlmRequestCapture(request.InvocationId, request.LocalEstimatedTokens, _logger)
            : null;
        var messages = NormalizeMessages(request);
        _logger.LogDebug(
            "[LlmInvocation] Stream session={SessionId} provider={ProviderId} profile={ProfileId} model={ModelId} msgCount={MsgCount} toolCount={ToolCount} prefix={PrefixHash}",
            request.SessionId, request.Profile.ProviderId, request.Profile.ProfileId, request.Profile.ModelId,
            messages.Count, request.Tools.Count, request.PrefixSnapshot?.PrefixHash ?? "(none)");

        var resolved = await ResolveAsync(request, ct).ConfigureAwait(false);
        ModelAttemptBudgetGrant? grant = null;
        LlmAttemptBudget? perSendBudget = null;
        if (request.BudgetAttempt is { } budget)
        {
            if (_budgetPort is null)
                throw new ModelAttemptBudgetException(BudgetErrorCodes.DispatchInvalid,
                    "A budget-bound streaming invocation requires the model attempt budget port.");
            EnsureBudgetIdentity(budget, request);
            if (_llmClient is IStreamingSendAttemptBudgetClient)
            {
                perSendBudget = budget;
            }
            else
            {
                grant = await _budgetPort.BeginAsync(ToClaim(budget, request, resolved.ModelId), ct)
                    .ConfigureAwait(false);
            }
        }

        // The transport owns one attempt per real stream send (CORE-I1-06); the facade only binds the scope for its lifetime.
        using var streamSendScope = perSendBudget is null
            ? null
            : LlmSendAttemptScope.Push(new BudgetedSendAttemptObserver(_budgetPort!, perSendBudget));

        var settled = false;
        string? failureReason = null;
        TokenUsageDto? usage = null;
        IAsyncEnumerator<StreamDelta> enumerator;
        try
        {
            enumerator = _llmClient.ChatStreamAsync(
                request.WorkspaceId,
                request.SessionId,
                request.AgentTemplateId,
                messages,
                request.Tools.Count > 0 ? request.Tools : null,
                resolved.Config,
                ct).GetAsyncEnumerator(ct);
        }
        catch
        {
            await TryMarkUnknownAsync(grant, "llm.stream_create_failed").ConfigureAwait(false);
            throw;
        }

        await using var enumeratorScope = enumerator;
        try
        {
            while (true)
            {
                bool moved;
                try
                {
                    moved = await enumerator.MoveNextAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    failureReason = "llm.cancelled";
                    throw;
                }
                catch (Exception)
                {
                    failureReason = "llm.provider_failed";
                    throw;
                }
                if (!moved) break;
                var delta = enumerator.Current;
                if (delta.Usage is { } reported) usage = reported;
                yield return delta;
            }
            if (grant is not null)
            {
                await SettleOrUnknownAsync(grant, usage, resolved, ct).ConfigureAwait(false);
                settled = true;
            }
        }
        finally
        {
            // A consumer that abandons the stream before completion has an unknown provider usage.
            if (grant is not null && !settled)
                await TryMarkUnknownAsync(grant, failureReason ?? "llm.stream_abandoned").ConfigureAwait(false);
        }
    }

    private async ValueTask<ResolvedLlmInvocationProfile> ResolveAsync(LlmInvocationRequest request,
        CancellationToken ct)
    {
        if (_profileResolver is not null)
        {
            var resolved = await _profileResolver.ResolveAsync(
                request.WorkspaceId, request.AgentInstanceId, request.Profile, ct).ConfigureAwait(false);
            return ApplyConfigOverride(resolved, request.ConfigOverride);
        }
        var direct = new ResolvedLlmInvocationProfile
        {
            ProviderId = request.Profile.ProviderId,
            ProfileId = request.Profile.ProfileId,
            ModelId = request.Profile.ModelId,
            Role = request.Profile.Role,
            Config = new LlmConfig { ModelId = request.Profile.ModelId },
        };
        return ApplyConfigOverride(direct, request.ConfigOverride);
    }

    private async ValueTask SettleOrUnknownAsync(ModelAttemptBudgetGrant grant, TokenUsageDto? usage,
        ResolvedLlmInvocationProfile resolved, CancellationToken ct)
    {
        if (usage?.PromptTokens is not { } input || usage.CompletionTokens is not { } output)
        {
            await TryMarkUnknownAsync(grant, "llm.usage_unknown").ConfigureAwait(false);
            return;
        }
        var modelUsage = new ModelUsage(input, output, Math.Clamp(usage.PromptCacheHitTokens ?? 0, 0, input));
        await _budgetPort!.SettleAsync(grant, modelUsage, $"{resolved.ProviderId}:{resolved.ModelId}", ct)
            .ConfigureAwait(false);
    }

    private async ValueTask TryMarkUnknownAsync(ModelAttemptBudgetGrant? grant, string reasonCode)
    {
        if (grant is null || _budgetPort is null) return;
        try
        {
            await _budgetPort.MarkUnknownAsync(grant, reasonCode, CancellationToken.None).ConfigureAwait(false);
        }
        catch (ModelAttemptBudgetException ex)
        {
            _logger.LogError("[LlmInvocation] Failed to mark the budget attempt unknown code={Code} attempt={AttemptId}",
                ex.Code, grant.AttemptId);
        }
    }

    private static void EnsureBudgetIdentity(LlmAttemptBudget budget, LlmInvocationRequest request)
    {
        if (!string.Equals(budget.WorkspaceId, request.WorkspaceId, StringComparison.Ordinal)
            || !string.Equals(budget.AgentId, request.AgentInstanceId, StringComparison.Ordinal)
            || !string.Equals(budget.SessionId, request.SessionId, StringComparison.Ordinal))
            throw new ModelAttemptBudgetException(BudgetErrorCodes.RequestInvalid,
                "The budget identity does not match the invocation workspace/agent/session.");
    }

    private static ModelAttemptBudgetClaim ToClaim(LlmAttemptBudget budget, LlmInvocationRequest request,
        string resolvedModelId)
    {
        if (!budget.IsValid || !Enum.TryParse<BudgetAccountKind>(budget.RootKind, ignoreCase: false, out var rootKind))
            throw new ModelAttemptBudgetException(BudgetErrorCodes.RequestInvalid,
                "A bounded budget attempt identity is required.");
        var identity = new BudgetIdentity(rootKind, budget.RootId, budget.WorkspaceId, budget.AgentId, budget.SessionId);
        if (!identity.IsValid)
            throw new ModelAttemptBudgetException(BudgetErrorCodes.RequestInvalid, "The budget identity is invalid.");
        return new(budget.AccountId, [.. budget.Ancestors], identity, budget.AttemptId, resolvedModelId,
            budget.ModelRevision, budget.PriceVersion, budget.MaxInputTokens, budget.MaxOutputTokens, budget.CostUnit,
            DateTimeOffset.UtcNow);
    }

    private static ResolvedLlmInvocationProfile ApplyConfigOverride(
        ResolvedLlmInvocationProfile resolved,
        LlmConfig? configOverride)
    {
        if (configOverride is null)
            return resolved;

        var merged = resolved.Config with
        {
            Endpoint = FirstNonBlank(configOverride.Endpoint, resolved.Config.Endpoint),
#pragma warning disable CS0618
            ApiKey = FirstNonBlank(configOverride.ApiKey, resolved.Config.ApiKey),
#pragma warning restore CS0618
            KeyVaultId = FirstNonBlank(configOverride.KeyVaultId, resolved.Config.KeyVaultId),
            ModelId = FirstNonBlank(configOverride.ModelId, resolved.Config.ModelId),
            MaxContextTokens = configOverride.MaxContextTokens ?? resolved.Config.MaxContextTokens,
            MaxInputTokens = configOverride.MaxInputTokens ?? resolved.Config.MaxInputTokens,
            MaxOutputTokens = configOverride.MaxOutputTokens ?? resolved.Config.MaxOutputTokens,
            ReasoningEffort = FirstNonBlank(configOverride.ReasoningEffort, resolved.Config.ReasoningEffort),
            // Protocol is owned by the selected model in llm.providers.json.
            // Request/profile overrides must not reroute the model to another wire protocol.
            Protocol = resolved.Config.Protocol,
        };

        return resolved with
        {
            ModelId = merged.ModelId ?? resolved.ModelId,
            Config = merged,
        };
    }

    private static string? FirstNonBlank(string? first, string? fallback)
        => string.IsNullOrWhiteSpace(first) ? fallback : first;

    private IReadOnlyList<ChatMessage> NormalizeMessages(LlmInvocationRequest request)
    {
        var normalized = LlmMessageSequenceNormalizer.Normalize(request.Messages);
        if (normalized.Changed)
        {
            _logger.LogWarning(
                "[LlmInvocation] Repaired invalid tool-call history before provider invocation session={SessionId} incompleteToolRounds={IncompleteToolRounds} orphanToolMessages={OrphanToolMessages} downgradedAssistants={DowngradedAssistants}",
                request.SessionId,
                normalized.RepairedIncompleteToolRounds,
                normalized.DroppedOrphanToolMessages,
                normalized.DowngradedAssistantMessages);
        }

        return normalized.Messages;
    }
}
