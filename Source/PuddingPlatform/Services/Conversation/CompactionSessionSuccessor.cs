using PuddingCode.Platform;

namespace PuddingPlatform.Services.Conversation;

/// <summary>
/// Owns the storage-side transition from a compacted conversation to its
/// successor. No controller or runtime service may update these three stores
/// independently.
/// </summary>
public sealed class CompactionSessionSuccessor(
    PlatformApiClient platformApi,
    WorkspaceAgentFileService workspaceAgents,
    SessionRedirectStore redirects,
    ILogger<CompactionSessionSuccessor> logger) : ICompactionSessionSuccessor
{
    public async Task<CompactionSuccessor> CreateAsync(
        CreateCompactionSuccessorCommand command,
        CancellationToken ct)
    {
        var previous = await platformApi.GetSessionAsync(
            command.PreviousConversationId,
            ct)
            ?? throw new InvalidOperationException(
                $"Conversation '{command.PreviousConversationId}' does not exist.");

        var templateId = !string.IsNullOrWhiteSpace(command.SourceTemplateId)
            ? command.SourceTemplateId
            : previous.AgentTemplateId;
        if (string.IsNullOrWhiteSpace(templateId))
        {
            throw new InvalidOperationException(
                $"Agent '{command.AgentId}' has no source template identity for successor creation.");
        }

        var title = string.IsNullOrWhiteSpace(previous.Title)
            ? "压缩后的新会话"
            : $"压缩 - {previous.Title}";
        var created = await platformApi.CreateSessionAsync(
            command.WorkspaceId,
            templateId,
            title,
            ct: ct)
            ?? throw new InvalidOperationException(
                $"Failed to create successor for conversation '{command.PreviousConversationId}'.");

        var rebound = await platformApi.RebindMainSessionAsync(
            new RebindMainSessionRequest
            {
                WorkspaceId = command.WorkspaceId,
                PrincipalKind = "agent",
                PrincipalId = command.AgentId,
                SuccessorSessionId = created.SessionId,
            },
            ct)
            ?? throw new InvalidOperationException(
                $"Failed to transfer Agent '{command.AgentId}' main-session ownership to '{created.SessionId}'.");

        // The Controller repository owns canonical Main identity. The Agent
        // manifest mirrors that identity for file-based runtime discovery.
        await workspaceAgents.SetAgentMainSessionAsync(
            command.WorkspaceId,
            command.AgentId,
            rebound.SessionId,
            ct);
        redirects.Register(
            command.WorkspaceId,
            command.AgentId,
            command.PreviousConversationId,
            rebound.SessionId);

        logger.LogInformation(
            "[CompactSuccessor] rebound agent={AgentId} old={OldConversationId} new={NewConversationId}",
            command.AgentId,
            command.PreviousConversationId,
            rebound.SessionId);

        return new CompactionSuccessor(rebound.SessionId, rebound.Title);
    }
}
