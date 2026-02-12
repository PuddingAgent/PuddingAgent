using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Abstractions;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services;

[TestClass]
public sealed class AgentContextCompactionSummaryGeneratorTests
{
    [TestMethod]
    public async Task GenerateSummaryAsync_DispatchesCompactionPromptToCurrentAgent()
    {
        var dispatcher = new RecordingDispatcher([
            ServerSentEventFrame.Json(SseEventTypes.Done, new
            {
                reply = "<compact_summary>\n## 用户目标\n继续修复上下文压缩。\n</compact_summary>"
            })
        ]);
        var services = new ServiceCollection()
            .AddSingleton<IRuntimeAgentDispatcher>(dispatcher)
            .BuildServiceProvider();
        var generator = new AgentContextCompactionSummaryGenerator(
            services,
            new ContextCompactionOptions { SummaryGenerator = "agent", AgentSummaryTimeoutSeconds = 5 },
            NullLogger<AgentContextCompactionSummaryGenerator>.Instance);

        var summary = await generator.GenerateSummaryAsync(new ContextCompactionSummaryRequest(
            WorkspaceId: "workspace-1",
            SessionId: "session-1",
            AgentId: "agent-1",
            AgentTemplateId: "global:assistant-1",
            Messages:
            [
                new ContextCompactionMessage("m1", 1, "user", "请修复压缩摘要"),
                new ContextCompactionMessage("m2", 2, "agent", "我正在处理 ContextCompactionService.cs")
            ],
            Reason: "user command /compact"));

        Assert.AreEqual("<compact_summary>\n## 用户目标\n继续修复上下文压缩。\n</compact_summary>", summary);
        Assert.IsNotNull(dispatcher.LastRequest);
        Assert.AreEqual("session-1", dispatcher.LastRequest.SessionId);
        Assert.AreEqual("workspace-1", dispatcher.LastRequest.WorkspaceId);
        Assert.AreEqual("global:assistant-1", dispatcher.LastRequest.AgentTemplateId);
        Assert.AreEqual("agent-1", dispatcher.LastRequest.AgentInstanceId);
        Assert.AreEqual(1, dispatcher.LastRequest.MaxRounds);
        Assert.IsTrue(dispatcher.LastRequest.SuppressContextAutoCompaction);
        StringAssert.Contains(dispatcher.LastRequest.MessageText, "请修复压缩摘要");
        StringAssert.Contains(dispatcher.LastRequest.MessageText, "ContextCompactionService.cs");
    }

    [TestMethod]
    public async Task GenerateSummaryAsync_ThrowsTimeout_WhenAgentDoesNotFinish()
    {
        var dispatcher = new RecordingDispatcher(delayUntilCancelled: true);
        var services = new ServiceCollection()
            .AddSingleton<IRuntimeAgentDispatcher>(dispatcher)
            .BuildServiceProvider();
        var generator = new AgentContextCompactionSummaryGenerator(
            services,
            new ContextCompactionOptions { SummaryGenerator = "agent", AgentSummaryTimeoutSeconds = 1 },
            NullLogger<AgentContextCompactionSummaryGenerator>.Instance);

        await Assert.ThrowsExactlyAsync<TimeoutException>(() =>
            generator.GenerateSummaryAsync(new ContextCompactionSummaryRequest(
                WorkspaceId: "workspace-1",
                SessionId: "session-timeout",
                AgentId: "agent-1",
                AgentTemplateId: "agent-template-1",
                Messages:
                [
                    new ContextCompactionMessage("m1", 1, "user", "slow summary")
                ],
                Reason: "manual compact")));
    }

    [TestMethod]
    public async Task GenerateSummaryAsync_Throws_WhenAgentStreamReturnsError()
    {
        var dispatcher = new RecordingDispatcher([
            ServerSentEventFrame.Json(SseEventTypes.Error, new
            {
                message = "Agent LLM config is null."
            })
        ]);
        var services = new ServiceCollection()
            .AddSingleton<IRuntimeAgentDispatcher>(dispatcher)
            .BuildServiceProvider();
        var generator = new AgentContextCompactionSummaryGenerator(
            services,
            new ContextCompactionOptions { SummaryGenerator = "agent", AgentSummaryTimeoutSeconds = 5 },
            NullLogger<AgentContextCompactionSummaryGenerator>.Instance);

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            generator.GenerateSummaryAsync(new ContextCompactionSummaryRequest(
                WorkspaceId: "workspace-1",
                SessionId: "session-error",
                AgentId: "agent-1",
                AgentTemplateId: "agent-template-1",
                Messages:
                [
                    new ContextCompactionMessage("m1", 1, "user", "summarize")
                ],
                Reason: "manual compact")));

        StringAssert.Contains(ex.Message, "Agent LLM config is null");
    }

    private sealed class RecordingDispatcher : IRuntimeAgentDispatcher
    {
        private readonly IReadOnlyList<ServerSentEventFrame> _frames;
        private readonly bool _delayUntilCancelled;

        public RecordingDispatcher(
            IReadOnlyList<ServerSentEventFrame>? frames = null,
            bool delayUntilCancelled = false)
        {
            _frames = frames ?? [];
            _delayUntilCancelled = delayUntilCancelled;
        }

        public RuntimeDispatchRequest? LastRequest { get; private set; }

        public Task<RuntimeDispatchResult> DispatchAsync(RuntimeDispatchRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();

        public async IAsyncEnumerable<ServerSentEventFrame> DispatchStreamAsync(
            RuntimeDispatchRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            LastRequest = request;
            if (_delayUntilCancelled)
            {
                await Task.Delay(TimeSpan.FromMinutes(5), ct);
                yield break;
            }

            foreach (var frame in _frames)
                yield return frame;
        }
    }
}
