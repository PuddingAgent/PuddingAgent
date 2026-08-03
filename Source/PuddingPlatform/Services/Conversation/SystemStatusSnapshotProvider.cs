using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;
using PuddingCode.Platform;
using PuddingCode.Runtime;

namespace PuddingPlatform.Services.Conversation;

/// <summary>
/// Reads the same runtime/profile/context authorities used by execution and the
/// chat context-health UI, then exposes a transport-neutral command snapshot.
/// </summary>
public sealed class SystemStatusSnapshotProvider(
    IAgentRuntimeProfileResolver agentRuntimeProfileResolver,
    ILlmConfigService llmConfigService,
    IContextCompactionService contextCompactionService,
    ISessionStateManager sessionStateManager,
    IRuntimeControlService runtimeControl,
    ILogger<SystemStatusSnapshotProvider> logger) : ISystemStatusSnapshotProvider
{
    public async Task<SystemStatusSnapshot> GetAsync(
        SystemStatusSnapshotRequest request,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ConversationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.AgentId);

        var warnings = new List<string>();
        var runtime = runtimeControl.GetStatus(request.ConversationId);

        AgentRuntimeProfile? profile = null;
        try
        {
            profile = await agentRuntimeProfileResolver.ResolveAsync(
                request.WorkspaceId,
                request.AgentId,
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            warnings.Add("Agent profile is unavailable.");
            logger.LogWarning(
                ex,
                "[SystemStatus] profile unavailable workspace={WorkspaceId} conversation={ConversationId} agent={AgentId}",
                request.WorkspaceId,
                request.ConversationId,
                request.AgentId);
        }

        var sessionState = runtime.Session?.State ?? SessionState.Created;
        try
        {
            sessionState = await sessionStateManager.GetSessionStateAsync(
                request.ConversationId,
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            warnings.Add("Canonical session state is unavailable; runtime state is shown.");
            logger.LogWarning(
                ex,
                "[SystemStatus] session state unavailable conversation={ConversationId}",
                request.ConversationId);
        }

        var runningSubAgents = 0;
        try
        {
            runningSubAgents = await sessionStateManager.GetRunningSubAgentCountAsync(
                request.ConversationId,
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            warnings.Add("Running sub-agent count is unavailable.");
            logger.LogWarning(
                ex,
                "[SystemStatus] sub-agent count unavailable conversation={ConversationId}",
                request.ConversationId);
        }

        var providerId = profile?.PreferredProviderId;
        var modelId = profile?.PreferredModelId ?? profile?.LlmConfig?.ModelId;
        var capacity = ResolveContextCapacity(profile, providerId, modelId);
        ContextHealthSnapshot? contextHealth = null;
        if (capacity is null)
        {
            warnings.Add("Context window is not configured for the selected model.");
        }
        else
        {
            try
            {
                contextHealth = await contextCompactionService.GetHealthAsync(
                    request.ConversationId,
                    ct,
                    contextWindowTokens: capacity.Value.ContextWindowTokens,
                    maxOutputTokens: capacity.Value.MaxOutputTokens,
                    maxInputTokens: capacity.Value.MaxInputTokens,
                    toolCount: profile?.ToolDefinitions?.Count ?? 0);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                warnings.Add("Context usage is temporarily unavailable.");
                logger.LogWarning(
                    ex,
                    "[SystemStatus] context health unavailable workspace={WorkspaceId} conversation={ConversationId} agent={AgentId}",
                    request.WorkspaceId,
                    request.ConversationId,
                    request.AgentId);
            }
        }

        return new SystemStatusSnapshot(
            request.WorkspaceId,
            request.ConversationId,
            request.AgentId,
            profile?.DisplayName ?? request.AgentId,
            profile?.SourceTemplateId,
            sessionState,
            runningSubAgents,
            runtime.Mode,
            runtime.ActiveSessions,
            runtime.Session?.WindowErrorCount ?? 0,
            runtime.Session?.FaultSummary,
            providerId,
            modelId,
            profile?.CapabilityCount ?? 0,
            contextHealth,
            warnings);
    }

    private (int ContextWindowTokens, int? MaxOutputTokens, int? MaxInputTokens)? ResolveContextCapacity(
        AgentRuntimeProfile? profile,
        string? providerId,
        string? modelId)
    {
        if (profile?.LlmConfig?.MaxContextTokens is > 0)
        {
            return (
                profile.LlmConfig.MaxContextTokens.Value,
                profile.LlmConfig.MaxOutputTokens is > 0
                    ? profile.LlmConfig.MaxOutputTokens
                    : null,
                profile.LlmConfig.MaxInputTokens is > 0
                    ? profile.LlmConfig.MaxInputTokens
                    : null);
        }

        if (string.IsNullOrWhiteSpace(providerId)
            || string.IsNullOrWhiteSpace(modelId))
        {
            return null;
        }

        var model = llmConfigService.GetAllModels().FirstOrDefault(item =>
            string.Equals(item.ProviderId, providerId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.ModelId, modelId, StringComparison.OrdinalIgnoreCase));
        return model?.MaxContextTokens > 0
            ? (
                model.MaxContextTokens,
                model.MaxOutputTokens > 0 ? model.MaxOutputTokens : null,
                model.MaxInputTokens is > 0 ? model.MaxInputTokens : null)
            : null;
    }
}
