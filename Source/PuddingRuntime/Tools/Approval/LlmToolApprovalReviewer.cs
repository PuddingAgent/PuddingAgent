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

    /// <summary>
    /// Reviewer implementation. Empty or "fake" keeps the construction-stage fake reviewer;
    /// "llm" explicitly enables the isolated approval LLM reviewer.
    /// </summary>
    public string? Reviewer { get; set; } = FakeReviewer;

    /// <summary>
    /// When enabled, automatic approval must use the workspace audit agent path even if Reviewer is left as "fake".
    /// </summary>
    public bool RequireAuditAgent { get; set; }
}

/// <summary>
/// Strict option-backed resolver. Missing provider/profile/model means no approval LLM;
/// it deliberately does not fall back to conscious, subconscious, or platform defaults.
/// </summary>
public sealed class StrictConfiguredToolApprovalLlmProfileResolver : IToolApprovalLlmProfileResolver
{
    private readonly IOptions<ToolApprovalLlmOptions> _options;
    private readonly ILlmConfigService? _llmConfigService;
    private readonly IWorkspaceAuditAgentProvider? _workspaceAuditAgentProvider;

    public StrictConfiguredToolApprovalLlmProfileResolver(
        IOptions<ToolApprovalLlmOptions> options,
        ILlmConfigService? llmConfigService = null,
        IWorkspaceAuditAgentProvider? workspaceAuditAgentProvider = null)
    {
        _options = options;
        _llmConfigService = llmConfigService;
        _workspaceAuditAgentProvider = workspaceAuditAgentProvider;
    }

    public async Task<ToolApprovalLlmProfile?> ResolveAsync(
        ToolApprovalTicketRequest request,
        ToolApprovalIdentity identity,
        ToolDescriptor descriptor,
        CancellationToken ct = default)
    {
        if (_workspaceAuditAgentProvider is not null)
        {
            var auditAgent = await _workspaceAuditAgentProvider.FindFirstEnabledAuditAgentAsync(identity.WorkspaceId, ct);
            if (auditAgent is null)
                throw new ToolApprovalLlmProfileResolutionException("当前工作空间不具有审计类型的agent");

            var providerId = auditAgent.ProviderId?.Trim() ?? "";
            var modelId = auditAgent.ModelId?.Trim() ?? "";
            var explicitProfileId = auditAgent.ProfileId?.Trim();
            if (string.IsNullOrWhiteSpace(explicitProfileId)
                && (string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(modelId)))
            {
                throw new ToolApprovalLlmProfileResolutionException("当前工作空间的审计类型agent缺少审批模型配置");
            }

            return new ToolApprovalLlmProfile
            {
                ProviderId = providerId,
                ProfileId = string.IsNullOrWhiteSpace(explicitProfileId)
                    ? $"workspace-audit:{auditAgent.AgentInstanceId}"
                    : explicitProfileId,
                ModelId = modelId,
                AgentInstanceId = auditAgent.AgentInstanceId,
                AgentTemplateId = auditAgent.AgentTemplateId,
            };
        }

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
                    return null;
                }

                if (!string.IsNullOrWhiteSpace(options.ModelId)
                    && !string.Equals(options.ModelId.Trim(), resolved.ModelId, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                return new ToolApprovalLlmProfile
                {
                    ProviderId = resolved.ProviderId,
                    ProfileId = resolved.ProfileId,
                    ModelId = resolved.ModelId,
                    AgentTemplateId = string.IsNullOrWhiteSpace(options.AgentTemplateId)
                        ? null
                        : options.AgentTemplateId.Trim(),
                };
            }

            if (_llmConfigService is not null
                || string.IsNullOrWhiteSpace(options.ProviderId)
                || string.IsNullOrWhiteSpace(options.ModelId))
            {
                return null;
            }
        }

        if (string.IsNullOrWhiteSpace(options.ProviderId)
            || string.IsNullOrWhiteSpace(options.ProfileId)
            || string.IsNullOrWhiteSpace(options.ModelId))
        {
            return null;
        }

        if (_llmConfigService is not null
            && _llmConfigService.Resolve(options.ProviderId.Trim(), options.ModelId.Trim()) is null)
        {
            return null;
        }

        return new ToolApprovalLlmProfile
        {
            ProviderId = options.ProviderId.Trim(),
            ProfileId = options.ProfileId.Trim(),
            ModelId = options.ModelId.Trim(),
            AgentTemplateId = string.IsNullOrWhiteSpace(options.AgentTemplateId)
                ? null
                : options.AgentTemplateId.Trim(),
        };
    }
}

/// <summary>Calls the isolated approval LLM through the runtime invocation facade.</summary>
public sealed class InvocationToolApprovalLlmClient : IToolApprovalLlmClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ILlmInvocationService _invocationService;
    private readonly IToolApprovalLlmProfileResolver _profileResolver;
    private readonly ILogger<InvocationToolApprovalLlmClient> _logger;

    public InvocationToolApprovalLlmClient(
        ILlmInvocationService invocationService,
        IToolApprovalLlmProfileResolver profileResolver,
        ILogger<InvocationToolApprovalLlmClient> logger)
    {
        _invocationService = invocationService;
        _profileResolver = profileResolver;
        _logger = logger;
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
            _logger.LogWarning(
                "[ToolApproval] approval LLM profile resolution failed workspace={WorkspaceId} agent={AgentInstanceId} tool={ToolId} reason={Reason}",
                identity.WorkspaceId, identity.AgentInstanceId, descriptor.ToolId, ex.Message);
            return NeedHumanJson(ex.Message);
        }

        if (profile is null)
        {
            _logger.LogWarning(
                "[ToolApproval] approval LLM profile is not configured workspace={WorkspaceId} agent={AgentInstanceId} tool={ToolId}",
                identity.WorkspaceId, identity.AgentInstanceId, descriptor.ToolId);
            return NeedHumanJson("approval LLM profile is not configured.");
        }

        var startedAt = DateTimeOffset.UtcNow;
        _logger.LogInformation(
            "[ToolApproval] approval LLM call started provider={ProviderId} profile={ProfileId} model={ModelId} workspace={WorkspaceId} session={SessionId} agent={AgentInstanceId} auditAgent={AuditAgentInstanceId} tool={ToolId}",
            profile.ProviderId,
            profile.ProfileId,
            profile.ModelId,
            identity.WorkspaceId,
            identity.SessionId,
            identity.AgentInstanceId,
            profile.AgentInstanceId,
            descriptor.ToolId);

        var result = await _invocationService.InvokeAsync(new LlmInvocationRequest
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
            Messages =
            [
                new ChatMessage(ChatRole.System, prompt.SystemPrompt),
                new ChatMessage(ChatRole.User, prompt.UserPrompt),
            ],
        }, ct);

        if (!result.Success)
        {
            _logger.LogWarning(
                "[ToolApproval] approval LLM call failed provider={ProviderId} profile={ProfileId} model={ModelId} workspace={WorkspaceId} session={SessionId} tool={ToolId} durationMs={DurationMs} error={Error}",
                profile.ProviderId,
                profile.ProfileId,
                profile.ModelId,
                identity.WorkspaceId,
                identity.SessionId,
                descriptor.ToolId,
                DurationMs(startedAt),
                result.Error);
            return NeedHumanJson("approval LLM call failed: " + (result.Error ?? "unknown error"));
        }

        if (string.IsNullOrWhiteSpace(result.ReplyText))
        {
            _logger.LogWarning(
                "[ToolApproval] approval LLM returned empty response provider={ProviderId} profile={ProfileId} model={ModelId} workspace={WorkspaceId} session={SessionId} tool={ToolId} durationMs={DurationMs}",
                profile.ProviderId,
                profile.ProfileId,
                profile.ModelId,
                identity.WorkspaceId,
                identity.SessionId,
                descriptor.ToolId,
                DurationMs(startedAt));
            return NeedHumanJson("approval LLM returned an empty response.");
        }

        _logger.LogInformation(
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

    private static long DurationMs(DateTimeOffset startedAt)
        => Math.Max(0, (long)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds);

    private static string NeedHumanJson(string reason)
        => JsonSerializer.Serialize(new
        {
            decision = "need_human",
            reason,
            requiresHumanAuthorization = true,
            missingRequirements = new[] { "explicit approval LLM review" },
            recommendedFix = "Configure an approval LLM profile and retry request_tool_approval. Use /authorize only as a manual human fallback.",
        }, JsonOptions);
}
