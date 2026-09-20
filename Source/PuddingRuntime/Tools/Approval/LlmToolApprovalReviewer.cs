using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Runtime;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.Tools;

/// <summary>Approval reviewer backed by a clean, single-shot LLM call.</summary>
public sealed class LlmToolApprovalReviewer : IToolApprovalReviewer
{
    private readonly IToolApprovalLlmClient _client;

    public LlmToolApprovalReviewer(IToolApprovalLlmClient client)
    {
        _client = client;
    }

    public async Task<ToolApprovalReviewResult> ReviewAsync(
        ToolApprovalTicketRequest request,
        ToolApprovalIdentity identity,
        ToolDescriptor descriptor,
        CancellationToken ct = default)
    {
        var prompt = ToolApprovalPromptBuilder.Build(request, identity, descriptor);
        var raw = await _client.ReviewAsync(request, identity, descriptor, prompt, ct);
        return ToolApprovalReviewParser.Parse(raw);
    }
}

/// <summary>Narrow client boundary for the approval reviewer LLM call.</summary>
public interface IToolApprovalLlmClient
{
    Task<string> ReviewAsync(
        ToolApprovalTicketRequest request,
        ToolApprovalIdentity identity,
        ToolDescriptor descriptor,
        ToolApprovalPrompt prompt,
        CancellationToken ct = default);
}

/// <summary>Explicit LLM profile used only by the automatic approval reviewer.</summary>
public sealed record ToolApprovalLlmProfile
{
    public required string ProviderId { get; init; }

    /// <summary>
    /// 审查档案标识：配置显式提供 ProfileId 时为该值；仅配置 ProviderId+ModelId 时
    /// 由解析器确定性合成为 "{providerId}/{modelId}"。仅作日志/追踪标识，
    /// 不代表 llm.providers.json 的 profiles 中存在同名条目。
    /// </summary>
    public required string ProfileId { get; init; }
    public required string ModelId { get; init; }
    public string? AgentInstanceId { get; init; }
    public string? AgentTemplateId { get; init; }
}

/// <summary>Resolves the explicitly configured approval LLM profile without fallback.</summary>
public interface IToolApprovalLlmProfileResolver
{
    Task<ToolApprovalLlmProfile?> ResolveAsync(
        ToolApprovalTicketRequest request,
        ToolApprovalIdentity identity,
        ToolDescriptor descriptor,
        CancellationToken ct = default);
}

/// <summary>Raised when approval profile resolution fails with a user-actionable safety reason.</summary>
public sealed class ToolApprovalLlmProfileResolutionException : Exception
{
    public ToolApprovalLlmProfileResolutionException(string message)
        : base(message)
    {
    }
}

/// <summary>Configuration section for the explicit approval LLM profile.</summary>
public sealed class ToolApprovalLlmOptions
{
    public string? ProviderId { get; set; }
    public string? ProfileId { get; set; }
    public string? ModelId { get; set; }
    public string? AgentTemplateId { get; set; }
}

/// <summary>Runtime switch for selecting the automatic approval reviewer implementation.</summary>
public sealed class ToolApprovalRuntimeOptions
{
    public const string SectionName = "ToolApproval";
    public const string FakeReviewer = "fake";
    public const string LlmReviewer = "llm";

    /// <summary>Jev 决策模型评审器（Jev自动审批与白名单自学习改造方案 §3.2）。</summary>
    public const string JevReviewer = "jev";

    /// <summary>
    /// 安全分类器驱动的评审器（方案 v2 §14.13，切片 S3b/S3c-1）：经 <c>IToolCallClassifier</c> 管线裁决，
    /// 仅显式配置 <c>Reviewer=classifier</c> 时选中；默认值不受影响。
    /// </summary>
    public const string ClassifierReviewer = "classifier";

    /// <summary>
    /// Reviewer implementation. Unset (null/empty) auto-selects at composition time:
    /// "jev" when an <c>IJevDecisionService</c> is registered in the container, otherwise
    /// "llm" (the isolated approval LLM reviewer). Explicit values: "llm" | "jev" | "classifier".
    /// "fake" is test-only; production must never silently auto-approve (ADR-091 §5).
    /// </summary>
    /// <remarks>
    /// **2026-09-21 默认值已翻转为 <c>classifier</c>**（原为 <c>llm</c>）：v2 形态已落地——
    /// 四选一 + 逐分类可信度（§14.13.3 门槛 0.90）、健康面契约、<c>ClassificationRuleCurator</c> 规则策展、
    /// 审批门户（<c>classify</c>/<c>rules_*</c>/<c>full_access_*</c>）齐备，且翻转前的三道前置均已**实证**：
    /// ① 生产实现清单盘点（<c>JevToolCallClassifier</c> 充当管线仲裁位）；
    /// ② Jev 生产可用性（<c>JevDecisionLiveTests</c> 真实联网 1/1 通过）；
    /// ③ **真链路解析一致性**（用真实 Jev 驱动分类器 ⇒ <c>Outcome≠Unknown</c>、<c>ReasonCode≠…unparsed</c>，
    ///    且真实返回的 choice 字面量与 <c>confidence.*</c> 键与解析器期望一致）。
    /// <para>
    /// **回退方式（无需重新构建）**：把配置 <c>ToolApproval:Reviewer</c> 显式设为 <c>llm</c>（或 <c>jev</c>）即可；
    /// <c>LlmToolApprovalReviewer</c> 按 §14.13.7 保留不删，作为回滚路径。
    /// 分类器不可用时按 §14.7 产生**依赖等待（deferred）**，不会静默放行也不会静默拒绝。
    /// </para>
    /// </remarks>
    public string? Reviewer { get; set; } = ClassifierReviewer;

    /// <summary>
    /// ADR-091 §4.4/F06：隔离审查的自身 deadline（秒）。到期产生
    /// approval_review_timeout 依赖等待；调用者取消不在此列，继续向上抛。
    /// </summary>
    public int ReviewTimeoutSeconds { get; set; } = 30;
}

/// <summary>
/// Strict option-backed resolver. Missing provider or model means no approval LLM;
/// ProfileId is optional and synthesized as "provider/model" when absent (B1).
/// It deliberately does not fall back to conscious, subconscious, or platform defaults.
/// </summary>
public sealed class StrictConfiguredToolApprovalLlmProfileResolver : IToolApprovalLlmProfileResolver
{
    private readonly IOptions<ToolApprovalLlmOptions> _options;
    private readonly ILlmConfigService? _llmConfigService;

    /// <summary>
    /// ADR-091 决策 5：审查模型路由与工作空间审计 Agent 实例解耦。
    /// 只依据独立的 ToolApproval:Llm 配置解析；缺配置返回 null，由调用方转成依赖等待，
    /// 不再因「工作空间没有审计类型 agent」抛异常或要求人工授权。
    /// </summary>
    public StrictConfiguredToolApprovalLlmProfileResolver(
        IOptions<ToolApprovalLlmOptions> options,
        ILlmConfigService? llmConfigService = null)
    {
        _options = options;
        _llmConfigService = llmConfigService;
    }

    public Task<ToolApprovalLlmProfile?> ResolveAsync(
        ToolApprovalTicketRequest request,
        ToolApprovalIdentity identity,
        ToolDescriptor descriptor,
        CancellationToken ct = default)
    {
        var options = _options.Value;
        if (!string.IsNullOrWhiteSpace(options.ProfileId))
        {
            var profileId = options.ProfileId.Trim();
            var resolved = _llmConfigService?.ResolveProfile(profileId);
            if (resolved is not null)
            {
                if (!string.IsNullOrWhiteSpace(options.ProviderId)
                    && !string.Equals(options.ProviderId.Trim(), resolved.ProviderId, StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult<ToolApprovalLlmProfile?>(null);
                }

                if (!string.IsNullOrWhiteSpace(options.ModelId)
                    && !string.Equals(options.ModelId.Trim(), resolved.ModelId, StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult<ToolApprovalLlmProfile?>(null);
                }

                return Task.FromResult<ToolApprovalLlmProfile?>(new ToolApprovalLlmProfile
                {
                    ProviderId = resolved.ProviderId,
                    ProfileId = resolved.ProfileId,
                    ModelId = resolved.ModelId,
                    AgentTemplateId = string.IsNullOrWhiteSpace(options.AgentTemplateId)
                        ? null
                        : options.AgentTemplateId.Trim(),
                });
            }

            if (_llmConfigService is not null
                || string.IsNullOrWhiteSpace(options.ProviderId)
                || string.IsNullOrWhiteSpace(options.ModelId))
            {
                return Task.FromResult<ToolApprovalLlmProfile?>(null);
            }
        }

        // B1：ProviderId+ModelId 齐备即可解析，ProfileId 不再是硬性前置
        // （生产 profiles 为空时本路径曾永不可达）。语义仍 fail-closed：
        // 服务存在但路由未注册 → null（依赖等待），绝不回退平台默认模型。
        if (string.IsNullOrWhiteSpace(options.ProviderId)
            || string.IsNullOrWhiteSpace(options.ModelId))
        {
            return Task.FromResult<ToolApprovalLlmProfile?>(null);
        }

        if (_llmConfigService is not null
            && _llmConfigService.Resolve(options.ProviderId.Trim(), options.ModelId.Trim()) is null)
        {
            return Task.FromResult<ToolApprovalLlmProfile?>(null);
        }

        var providerId = options.ProviderId.Trim();
        var modelId = options.ModelId.Trim();
        return Task.FromResult<ToolApprovalLlmProfile?>(new ToolApprovalLlmProfile
        {
            ProviderId = providerId,
            ProfileId = string.IsNullOrWhiteSpace(options.ProfileId)
                ? $"{providerId}/{modelId}"
                : options.ProfileId.Trim(),
            ModelId = modelId,
            AgentTemplateId = string.IsNullOrWhiteSpace(options.AgentTemplateId)
                ? null
                : options.AgentTemplateId.Trim(),
        });
    }
}

/// <summary>Calls the isolated approval LLM through the runtime invocation facade.</summary>
public sealed class InvocationToolApprovalLlmClient : IToolApprovalLlmClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ILlmInvocationService? _invocationService;
    private readonly IToolApprovalLlmProfileResolver _profileResolver;
    private readonly ILogger<InvocationToolApprovalLlmClient>? _logger;
    private readonly TimeSpan _reviewTimeout;

    /// <summary>
    /// ADR-091 §4.4/F01/F06：审查依赖（调用服务、日志）允许缺席——缺席必须产生 typed
    /// 依赖等待，而不是让 DI 直接抛异常；审查另有自己的 deadline，与调用者取消分开。
    /// </summary>
    public InvocationToolApprovalLlmClient(
        ILlmInvocationService? invocationService,
        IToolApprovalLlmProfileResolver profileResolver,
        ILogger<InvocationToolApprovalLlmClient>? logger = null,
        TimeSpan? reviewTimeout = null)
    {
        _invocationService = invocationService;
        _profileResolver = profileResolver;
        _logger = logger;
        _reviewTimeout = reviewTimeout is { TotalMilliseconds: > 0 }
            ? reviewTimeout.Value
            : TimeSpan.FromSeconds(30);
    }

    public async Task<string> ReviewAsync(
        ToolApprovalTicketRequest request,
        ToolApprovalIdentity identity,
        ToolDescriptor descriptor,
        ToolApprovalPrompt prompt,
        CancellationToken ct = default)
    {
        ToolApprovalLlmProfile? profile;
        try
        {
            profile = await _profileResolver.ResolveAsync(request, identity, descriptor, ct);
        }
        catch (ToolApprovalLlmProfileResolutionException ex)
        {
            _logger?.LogWarning(
                "[ToolApproval] approval LLM profile resolution failed workspace={WorkspaceId} agent={AgentInstanceId} tool={ToolId} reason={Reason}",
                identity.WorkspaceId, identity.AgentInstanceId, descriptor.ToolId, ex.Message);
            return DeferredDependencyJson("approval_review_profile_resolution_failed", ex.Message);
        }

        if (profile is null)
        {
            _logger?.LogWarning(
                "[ToolApproval] approval LLM profile is not configured workspace={WorkspaceId} agent={AgentInstanceId} tool={ToolId}",
                identity.WorkspaceId, identity.AgentInstanceId, descriptor.ToolId);
            return DeferredDependencyJson(
                "approval_review_profile_not_configured",
                "No approval review model profile is configured (ToolApproval:Llm).");
        }

        var startedAt = DateTimeOffset.UtcNow;
        _logger?.LogInformation(
            "[ToolApproval] approval LLM call started provider={ProviderId} profile={ProfileId} model={ModelId} workspace={WorkspaceId} session={SessionId} agent={AgentInstanceId} auditAgent={AuditAgentInstanceId} tool={ToolId}",
            profile.ProviderId,
            profile.ProfileId,
            profile.ModelId,
            identity.WorkspaceId,
            identity.SessionId,
            identity.AgentInstanceId,
            profile.AgentInstanceId,
            descriptor.ToolId);

        var result = await InvokeWithDeadlineAsync(profile, prompt, identity, descriptor, ct);

        if (!result.Success)
        {
            _logger?.LogWarning(
                "[ToolApproval] approval LLM call failed provider={ProviderId} profile={ProfileId} model={ModelId} workspace={WorkspaceId} session={SessionId} tool={ToolId} durationMs={DurationMs} error={Error}",
                profile.ProviderId,
                profile.ProfileId,
                profile.ModelId,
                identity.WorkspaceId,
                identity.SessionId,
                descriptor.ToolId,
                DurationMs(startedAt),
                result.Error);
            return DeferredDependencyJson(
                result.Error ?? ToolApprovalWire.CodeCallFailed,
                IsDependencyCode(result.Error)
                    ? ReasonForDependencyCode(result.Error!)
                    : "Approval review model call failed: " + (result.Error ?? "unknown error"));
        }

        if (string.IsNullOrWhiteSpace(result.ReplyText))
        {
            _logger?.LogWarning(
                "[ToolApproval] approval LLM returned empty response provider={ProviderId} profile={ProfileId} model={ModelId} workspace={WorkspaceId} session={SessionId} tool={ToolId} durationMs={DurationMs}",
                profile.ProviderId,
                profile.ProfileId,
                profile.ModelId,
                identity.WorkspaceId,
                identity.SessionId,
                descriptor.ToolId,
                DurationMs(startedAt));
            return DeferredDependencyJson(
                "approval_review_empty_response",
                "Approval review model returned an empty response.");
        }

        _logger?.LogInformation(
            "[ToolApproval] approval LLM call succeeded provider={ProviderId} profile={ProfileId} model={ModelId} workspace={WorkspaceId} session={SessionId} tool={ToolId} durationMs={DurationMs}",
            profile.ProviderId,
            profile.ProfileId,
            profile.ModelId,
            identity.WorkspaceId,
            identity.SessionId,
            descriptor.ToolId,
            DurationMs(startedAt));

        return result.ReplyText;
    }

    /// <summary>
    /// F06：审查专属 deadline。自身到期 → typed 依赖等待（approval_review_timeout）；
    /// 调用者取消继续向上抛（用户停止不是待恢复的依赖动作）；调用服务缺席 → service_unavailable。
    /// </summary>
    private async Task<LlmInvocationResult> InvokeWithDeadlineAsync(
        ToolApprovalLlmProfile profile,
        ToolApprovalPrompt prompt,
        ToolApprovalIdentity identity,
        ToolDescriptor descriptor,
        CancellationToken ct)
    {
        if (_invocationService is null)
        {
            _logger?.LogWarning(
                "[ToolApproval] approval LLM invocation service is unavailable workspace={WorkspaceId} session={SessionId} tool={ToolId}",
                identity.WorkspaceId,
                identity.SessionId,
                descriptor.ToolId);
            return MissingDependencyResult(ToolApprovalWire.CodeServiceUnavailable);
        }

        using var reviewCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        reviewCts.CancelAfter(_reviewTimeout);

        try
        {
            return await _invocationService.InvokeAsync(new LlmInvocationRequest
            {
                WorkspaceId = identity.WorkspaceId,
                SessionId = identity.SessionId,
                AgentInstanceId = profile.AgentInstanceId ?? identity.AgentInstanceId,
                AgentTemplateId = profile.AgentTemplateId ?? identity.AgentTemplateId ?? "approval-auditor",
                Profile = new LlmInvocationProfile
                {
                    ProviderId = profile.ProviderId,
                    ProfileId = profile.ProfileId,
                    ModelId = profile.ModelId,
                    Role = "approval",
                },
                Purpose = "approval",
                Messages =
                [
                    new ChatMessage(ChatRole.System, prompt.SystemPrompt),
                    new ChatMessage(ChatRole.User, prompt.UserPrompt),
                ],
            }, reviewCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger?.LogWarning(
                "[ToolApproval] approval LLM review deadline exceeded timeoutMs={TimeoutMs} workspace={WorkspaceId} session={SessionId} tool={ToolId}",
                (long)_reviewTimeout.TotalMilliseconds,
                identity.WorkspaceId,
                identity.SessionId,
                descriptor.ToolId);
            return MissingDependencyResult(ToolApprovalWire.CodeTimeout);
        }
    }

    private static bool IsDependencyCode(string? code)
        => string.Equals(code, ToolApprovalWire.CodeServiceUnavailable, StringComparison.Ordinal)
           || string.Equals(code, ToolApprovalWire.CodeTimeout, StringComparison.Ordinal);

    private static string ReasonForDependencyCode(string code)
        => code switch
        {
            ToolApprovalWire.CodeTimeout => "The isolated approval review exceeded its own deadline.",
            _ => "The isolated approval review invocation service is unavailable.",
        };

    private static LlmInvocationResult MissingDependencyResult(string reasonCode)
        => new()
        {
            Success = false,
            Error = reasonCode,
        };

    private static long DurationMs(DateTimeOffset startedAt)
        => Math.Max(0, (long)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds);

    /// <summary>
    /// ADR-091 §4.4：审查依赖不可用是**依赖等待**，不是人工决定。
    /// 输出 typed reasonCode + requiresHumanAuthorization=false，避免把基础设施故障
    /// 折叠成无期限人工票据，也避免诱导业务 Agent 反复调用 request_tool_approval / 索要授权。
    /// </summary>
    private static string DeferredDependencyJson(string reasonCode, string reason)
        => JsonSerializer.Serialize(new
        {
            decision = "deferred_dependency",
            reasonCode,
            reason,
            requiresHumanAuthorization = false,
            missingRequirements = new[] { "available isolated approval review model" },
            recommendedFix = "Restore the approval review dependency (configure ToolApproval:Llm provider/profile/model or fix model availability), then retry the same invocation. Do not ask the user to authorize a dependency failure.",
        }, JsonOptions);
}
