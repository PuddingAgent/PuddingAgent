using Microsoft.Extensions.DependencyInjection;
using PuddingChat;
using PuddingCode.Models;
using PuddingCode.Services;
using PuddingDesktop.Foundation;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingNativeChat.IntegrationTests;

public partial class NativeChatIntegrationTests
{
    private static async Task VerifyHistoryPagingAsync(InProcessKernel kernel, IChatClient client,
        RoleKey role, string session, CancellationToken ct)
    {
        var prefix = "history-" + Guid.NewGuid().ToString("N") + "-";
        var timestamp = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeMilliseconds();
        var canonical = prefix + "canonical";
        var envelope = AgentContextEnvelopeRenderer.RenderForAgent(new AgentContextEnvelope {
            MessageId = canonical, MessageType = "user_message", ContentType = "text", CreatedAt = timestamp,
            WorkspaceId = role.WorkspaceId, From = new("user", "single-user", "用户"), To = [], Constraints = [],
            Context = new("text", "Original message") });
        async Task InsertAsync(bool old)
        {
            await kernel.RunSettingsAsync("test-history-page", async (scope, token) =>
            {
                var db = scope.Services.GetRequiredService<PlatformDbContext>();
                for (var i = 0; i < (old ? 65 : 1); i++)
                    db.ChatMessages.Add(new ChatMessageEntity { SessionId = session, WorkspaceId = role.WorkspaceId,
                        AgentInstanceId = role.AgentId, MessageId = old ? prefix + i : prefix + "new",
                        Role = "user", Content = old && (i == 0 || i == 64) ? envelope : "history",
                        CreatedAt = old ? timestamp : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
                await db.SaveChangesAsync(token); return true;
            }, ct);
        }
        await InsertAsync(true);
        var history = Assert.IsAssignableFrom<IConversationHistory>(client);
        var initial = (await client.GetConversationAsync(role, null, ct))!;
        Assert.Equal(20, initial.Messages.Length); Assert.NotNull(initial.OlderCursor);
        var cursor = initial.OlderCursor; var all = initial.Messages.ToList(); var pages = 0;
        var selection = new ChatSelection(); selection.Select(role); selection.Apply(selection.Generation, initial);
        await InsertAsync(false); // New input must not shift any of the older pages.
        while (cursor is not null)
        {
            var page = await history.ReadHistoryAsync(role, session, cursor, ct);
            Assert.InRange(page.Messages.Length, 1, 20);
            Assert.True(page.Next is null || page.Next.CompareTo(cursor) < 0);
            Assert.DoesNotContain(page.Messages, m => m.MessageId == prefix + "new");
            Assert.True(selection.PrependHistory(selection.Generation, page));
            all.InsertRange(0, page.Messages); cursor = page.Next;
            Assert.True(++pages < 10, "History cursor must make progress.");
        }
        var loaded = all.Where(m => m.MessageId.StartsWith(prefix, StringComparison.Ordinal)).Select(m => m.MessageId).ToArray();
        Assert.Equal(Enumerable.Range(0, 65).Select(i => prefix + i), loaded);
        Assert.Equal(all.Count, all.Select(m => m.MessageId).Distinct().Count());
        Assert.Equal(2, all.Count(m => m.CanonicalMessageId == canonical));
        Assert.Single(selection.Conversation!.Messages, m => m.CanonicalMessageId == canonical);
        var repeat = await history.ReadHistoryAsync(role, session, initial.OlderCursor!, ct);
        Assert.Equal(20, repeat.Messages.Length);
        await Assert.ThrowsAsync<InvalidOperationException>(() => history.ReadHistoryAsync(role, "wrong-session", initial.OlderCursor!, ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => history.ReadHistoryAsync(role with { AgentId = "foreign" }, session, initial.OlderCursor!, ct));
        var empty = await history.ReadHistoryAsync(role, session, new(0, 1), ct);
        Assert.Empty(empty.Messages); Assert.Null(empty.Next);
    }
}
