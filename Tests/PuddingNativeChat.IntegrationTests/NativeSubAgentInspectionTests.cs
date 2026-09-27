using Microsoft.Extensions.DependencyInjection;
using PuddingChat;
using PuddingCode.Abstractions;
using PuddingCode.SubAgents;
using PuddingDesktop.Foundation;

namespace PuddingNativeChat.IntegrationTests;

public partial class NativeChatIntegrationTests
{
    private static async Task VerifySubAgentInspectionAsync(InProcessKernel kernel, IChatClient client,
        RoleKey role, string session, CancellationToken ct)
    {
        var runIds = await kernel.RunSettingsAsync("test-subagent-inspection", async (scope, token) =>
        {
            var store = scope.Services.GetRequiredService<ISubAgentRunStore>();
            async Task<string> Create(string parent)
            {
                var handle = await store.CreateRunAsync(new SubAgentRunCreateRequest
                {
                    WorkspaceId = role.WorkspaceId, AgentInstanceId = role.AgentId,
                    ParentSessionId = parent, SubSessionId = "inspection-child", TemplateId = "test", Task = "检查真实归档",
                }, token);
                await store.AppendEventAsync(handle.RunId, "subagent.llm.completed", new { reasoning_preview = "归档思考", reasoning_truncated = true, message_preview = "归档正文" }, token);
                await store.AppendEventAsync(handle.RunId, "subagent.tool.started", new { tool_call_id = "one", tool_name = "read", arguments_preview = "{}" }, token);
                await store.AppendEventAsync(handle.RunId, "subagent.tool.completed", new { tool_call_id = "one", tool_name = "read", output_preview = "read result" }, token);
                await store.AppendEventAsync(handle.RunId, "subagent.tool.failed", new { tool_call_id = "two", tool_name = "read", output_preview = "partial", error = "failed read" }, token);
                await store.AppendEventAsync(handle.RunId, "subagent.future.event", new { detail = "unknown retained" }, token);
                await store.CompleteRunAsync(handle.RunId, new SubAgentRunCompletion { Status = "completed", Output = new string('x', 1000), TotalRounds = 1, TotalToolCalls = 2 }, token);
                return handle.RunId;
            }
            return new[] { await Create(session), await Create("unrelated-parent") };
        }, ct);
        var inspector = Assert.IsAssignableFrom<ISubAgentInspectionClient>(client);
        var key = new SubAgentInspectionKey(role, session, runIds[0]);
        var view = await inspector.ReadAsync(key, ct);
        Assert.Equal(key, view.Key); Assert.Equal("completed", view.Status); Assert.Equal(1000, view.Output!.Length);
        Assert.Contains(view.Activities, p => p.Kind == "thinking" && p.Text.Contains("归档思考") && p.Text.Contains("截断预览"));
        Assert.Contains(view.Activities, p => p.Kind == "tool_call" && p.ToolCallId == "one" && p.Arguments == "{}");
        Assert.Contains(view.Activities, p => p.Kind == "tool_result" && p.ToolCallId == "one" && p.Output == "read result");
        Assert.Contains(view.Activities, p => p.Kind == "tool_result" && p.ToolCallId == "two" && p.Status == "failed" && p.Output!.Contains("partial") && p.Output.Contains("failed read"));
        Assert.Contains(view.Activities, p => p.Name == "subagent.future.event" && p.Text.Contains("unknown retained"));
        Assert.Equal(view.Activities.Length, view.Activities.Select(p => p.Sequence).Distinct().Count());
        await Assert.ThrowsAsync<InvalidOperationException>(() => inspector.ReadAsync(key with { Role = role with { AgentId = "wrong" } }, ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => inspector.ReadAsync(key with { ParentSessionId = "unrelated-parent" }, ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => inspector.ReadAsync(key with { RunId = runIds[1] }, ct));
        await Assert.ThrowsAsync<FileNotFoundException>(() => inspector.ReadAsync(key with { RunId = "missing-run" }, ct));
        await Assert.ThrowsAsync<ArgumentException>(() => inspector.ReadAsync(key with { RunId = "../escape" }, ct));
    }
}
