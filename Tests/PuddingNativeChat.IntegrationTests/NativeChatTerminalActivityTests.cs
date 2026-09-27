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
    private static async Task VerifyTerminalActivityAsync(InProcessKernel kernel, IChatClient client,
        RoleKey role, string session, CancellationToken ct)
    {
        // Seed canonical facts only in this test's isolated Core, with no model call.
        foreach (var terminalType in new[] { ConversationEventTypes.TurnFailed,
                     ConversationEventTypes.TurnCancelled, ConversationEventTypes.RunLeaseLost, ConversationEventTypes.TurnCompleted })
        {
            var turn = Guid.NewGuid().ToString("N");
            var input = "input-" + turn;
            var run = "run-" + turn;
            await kernel.RunSettingsAsync("test-terminal-activity", async (scope, token) =>
            {
                var db = scope.Services.GetRequiredService<PlatformDbContext>();
                var sequence = await db.ConversationEvents.Where(e => e.ConversationId == session)
                    .MaxAsync(e => (long?)e.Sequence, token) ?? 0;
                var now = DateTimeOffset.UtcNow;
                db.ChatMessages.Add(new ChatMessageEntity { MessageId = input, SessionId = session,
                    WorkspaceId = role.WorkspaceId, AgentInstanceId = role.AgentId, Role = "user",
                    Content = "Keep my input separate from execution output", TurnId = turn, CreatedAt = now.ToUnixTimeMilliseconds() });
                if (terminalType == ConversationEventTypes.TurnCompleted)
                    db.ChatMessages.Add(new ChatMessageEntity { MessageId = "reply-" + turn, SessionId = session,
                        WorkspaceId = role.WorkspaceId, AgentInstanceId = role.AgentId, Role = "agent",
                        Content = "Completed", TurnId = turn, CreatedAt = now.ToUnixTimeMilliseconds() + 1 });
                void Add(string type, string payload, string? eventRun = null, string? eventTurn = null)
                    => db.ConversationEvents.Add(new ConversationEventEntity { EventId = Guid.NewGuid().ToString("N"),
                        ConversationId = session, WorkspaceId = role.WorkspaceId, TurnId = eventTurn ?? turn,
                        RunId = eventRun ?? run, MessageId = "reply-" + turn, Type = type, Payload = payload,
                        Sequence = ++sequence, OccurredAt = now.ToString("O"), CommittedAt = now.ToString("O") });
                Add(ConversationEventTypes.TurnStarted, "{}");
                for (var i = 0; i < 80; i++)
                    Add(ConversationEventTypes.MessageThinkingSummaryAppended, "{\"delta\":\"reasoning\"}");
                Add(ConversationEventTypes.ToolCallRequested, """{"toolCallId":"call","name":"terminal","arguments":"build"}""");
                Add(ConversationEventTypes.ToolCallFailed, """{"toolCallId":"call","name":"terminal","output":"compile error","exitCode":1}""");
                Add(ConversationEventTypes.SubAgentRunCompleted, """{"run_id":"child","sub_agent_id":"pooled","task_summary":"review"}""", "child");
                Add(ConversationEventTypes.MessageContentAppended, """{"delta":"CHILD OUTPUT MUST NOT LEAK"}""", "child");
                Add(ConversationEventTypes.SubAgentRunCompleted, """{"run_id":"unrelated"}""", "unrelated", "other-turn");
                Add(terminalType, "{}"); // Cancellation need not carry an error message.
                await db.SaveChangesAsync(token);
                return true;
            }, ct);

            var succeeded = terminalType == ConversationEventTypes.TurnCompleted;
            var details = await client.GetProcessAsync(role, succeeded ? "reply-" + turn : input, ct);
            Assert.Equal(83, details.ProcessItems.Length);
            Assert.Equal(80, details.ProcessItems.Count(p => p.Kind == "thinking"));
            Assert.Contains(details.ProcessItems, p => p.Kind == "tool_result" && p.ExitCode == 1);
            Assert.Contains(details.ProcessItems, p => p.DelegationExecutionId == "child");
            Assert.DoesNotContain(details.ProcessItems, p => p.Text.Contains("MUST NOT LEAK") || p.DelegationExecutionId == "unrelated");
            Assert.Equal(turn, details.Window!.TurnId);
            Assert.False(details.Window.HasMoreBefore);
            var snapshot = await client.GetConversationAsync(role, null, ct);
            var message = Assert.Single(snapshot!.Messages, m => m.MessageId == input);
            Assert.Equal(succeeded ? "succeeded" : terminalType == ConversationEventTypes.TurnCancelled ? "cancelled" : "failed", message.TurnOutcome!.Status);
            if (succeeded)
                Assert.Empty((await client.GetProcessAsync(role, input, ct)).ProcessItems);
            else
                Assert.DoesNotContain(snapshot.Messages, m => m.MessageId == "reply-" + turn); // Never fabricate assistant text.
        }
    }
}
