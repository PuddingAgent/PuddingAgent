using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PuddingChat;
using PuddingCode.Platform;
using PuddingDesktop.Foundation;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingNativeChat.IntegrationTests;

public partial class NativeChatIntegrationTests
{
    private static async Task VerifyActivityReplayAsync(InProcessKernel kernel, IChatClient client,
        RoleKey role, string session, CancellationToken ct)
    {
        var turn = Guid.NewGuid().ToString("N"); var run = "run-" + turn;
        async Task AppendAsync(bool initial, string type = "", string payload = "{}")
        {
            await kernel.RunSettingsAsync("test-activity-replay", async (scope, token) =>
            {
                var db = scope.Services.GetRequiredService<PlatformDbContext>();
                var sequence = await db.ConversationEvents.Where(e => e.ConversationId == session)
                    .MaxAsync(e => (long?)e.Sequence, token) ?? 0;
                void Add(string eventType, string content, string? eventRun = null)
                    => db.ConversationEvents.Add(new ConversationEventEntity { EventId = Guid.NewGuid().ToString("N"),
                        ConversationId = session, WorkspaceId = role.WorkspaceId, TurnId = turn, RunId = eventRun ?? run,
                        MessageId = "reply-" + turn, Type = eventType, Payload = content, Sequence = ++sequence,
                        OccurredAt = DateTimeOffset.UtcNow.ToString("O"), CommittedAt = DateTimeOffset.UtcNow.ToString("O") });
                if (initial)
                {
                    Add(ConversationEventTypes.TurnStarted, "{}");
                    for (var i = 0; i < 600; i++)
                        Add(ConversationEventTypes.MessageThinkingSummaryAppended, """{"delta":"reasoning"}""");
                    Add(ConversationEventTypes.MessageContentAppended, """{"delta":"initial"}""");
                    Add(ConversationEventTypes.MessageContentAppended, """{"delta":"CHILD MUST NOT LEAK"}""", "child");
                    Add(ConversationEventTypes.SubAgentRunCompleted, """{"run_id":"child","task_summary":"review"}""", "child");
                }
                else Add(type, payload);
                await db.SaveChangesAsync(token); return true;
            }, ct);
        }
        await AppendAsync(true);
        var activity = Assert.IsAssignableFrom<IConversationActivity>(client);
        var snapshot = (await client.GetConversationAsync(role, null, ct))!;
        Assert.Equal(run, snapshot.ActiveRun!.RunId);
        Assert.True(snapshot.ActiveRun.OutputSnapshot.Window!.HasMoreBefore);
        var state = snapshot with { EventCursor = 0, ActiveRun = snapshot.ActiveRun with {
            OutputSnapshot = new("", [], snapshot.ActiveRun.OutputSnapshot.Window) } };
        var pages = 0;
        while (true)
        {
            var page = await activity.ReadActivityAsync(role, new(session, run, turn, state.EventCursor, snapshot.EventCursor, true), ct);
            Assert.InRange(page.Items.Length, 0, 256); Assert.False(page.RequiresSnapshot);
            state = ConversationActivity.Apply(state, page)!; Assert.NotNull(state);
            pages++;
            if (pages == 1) await AppendAsync(false, ConversationEventTypes.MessageContentAppended, """{"delta":" + live"}""");
            if (!page.HasMore) break;
            Assert.True(pages < 5, "Replay must finish at its original ceiling.");
        }
        Assert.Equal(3, pages);
        Assert.Equal(600, state.ActiveRun!.OutputSnapshot.ProcessItems.Count(p => p.Kind == "thinking"));
        Assert.Equal("initial", state.ActiveRun.OutputSnapshot.Markdown);
        Assert.Contains(state.ActiveRun.OutputSnapshot.ProcessItems, p => p.DelegationExecutionId == "child");
        Assert.Equal(snapshot.EventCursor, state.EventCursor);
        var next = await activity.ReadActivityAsync(role, new(session, run, turn, state.EventCursor), ct);
        Assert.False(next.RequiresSnapshot); Assert.Single(next.Items);
        state = ConversationActivity.Apply(state, next)!;
        Assert.Equal("initial + live", state.ActiveRun!.OutputSnapshot.Markdown);
        Assert.False(state.ActiveRun.OutputSnapshot.Window!.HasMoreBefore);
        var read = new ActivityRead(session, run, turn, state.EventCursor);
        await Assert.ThrowsAsync<InvalidOperationException>(() => activity.ReadActivityAsync(role with { AgentId = "foreign" }, read, ct));
        Assert.True((await activity.ReadActivityAsync(role, read with { RunId = "child" }, ct)).RequiresSnapshot);
        await AppendAsync(false, ConversationEventTypes.TurnCancelled);
        Assert.True((await activity.ReadActivityAsync(role, read, ct)).RequiresSnapshot);
        Assert.Null((await client.GetConversationAsync(role, null, ct))!.ActiveRun);
    }
}
