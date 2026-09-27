using Microsoft.Extensions.DependencyInjection;
using PuddingChat;
using PuddingPlatform.Services.AgentChat;

namespace PuddingDesktop.Composition;

internal sealed partial class InProcessChatClient : IConversationHistory
{
    public Task<HistoryPage> ReadHistoryAsync(RoleKey role, string sessionId, HistoryCursor before, CancellationToken ct)
        => ExecuteAsync(async (services, token) =>
        {
            var page = await services.GetRequiredService<IAgentConversationProjectionService>().GetHistoryAsync(
                role.WorkspaceId, LocalUserId, role.AgentId, sessionId, new(before.CreatedAt, before.RowId), token);
            return new HistoryPage(page.MainSessionId, before, Map(page.OlderCursor), page.Messages.Select(MapMessage).ToArray());
        }, ct);
}
