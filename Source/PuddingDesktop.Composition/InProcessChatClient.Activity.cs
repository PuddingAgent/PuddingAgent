using Microsoft.Extensions.DependencyInjection;
using PuddingChat;
using PuddingPlatform.Services.AgentChat;

namespace PuddingDesktop.Composition;

internal sealed partial class InProcessChatClient : IConversationActivity
{
    public Task<ActivityPage> ReadActivityAsync(RoleKey role, ActivityRead read, CancellationToken ct)
        => ExecuteAsync(async (services, token) =>
        {
            var page = await services.GetRequiredService<IAgentConversationProjectionService>().ReadActivityAsync(
                role.WorkspaceId, LocalUserId, role.AgentId,
                new(read.MainSessionId, read.RunId, read.TurnId, read.AfterSequence, read.ThroughSequence, read.Replay), token);
            return new ActivityPage(read, page.ThroughSequence, page.HasMore, page.RequiresSnapshot, page.Items.Select(Map).ToArray());
        }, ct);
}
