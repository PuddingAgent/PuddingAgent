using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services;

[TestClass]
public sealed class LlmInvocationServiceCancellationTests
{
    [TestMethod]
    public async Task InvokeAsync_PropagatesCallerCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var service = new LlmInvocationService(
            new ThrowingRuntimeLlmClient(new OperationCanceledException(cts.Token)),
            NullLogger<LlmInvocationService>.Instance);

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => service.InvokeAsync(CreateRequest(), cts.Token));
    }

    [TestMethod]
    public async Task InvokeAsync_ConvertsProviderFailureToFailedResult()
    {
        var service = new LlmInvocationService(
            new ThrowingRuntimeLlmClient(new InvalidOperationException("provider unavailable")),
            NullLogger<LlmInvocationService>.Instance);

        var result = await service.InvokeAsync(CreateRequest());

        Assert.IsFalse(result.Success);
        Assert.AreEqual("provider unavailable", result.Error);
    }

    private static LlmInvocationRequest CreateRequest() => new()
    {
        WorkspaceId = "workspace",
        SessionId = "session",
        AgentInstanceId = "agent",
        AgentTemplateId = "template",
        Profile = new LlmInvocationProfile
        {
            ProviderId = "provider",
            ProfileId = "profile",
            ModelId = "model",
        },
        Messages = [new ChatMessage(ChatRole.User, "hello")],
    };

    private sealed class ThrowingRuntimeLlmClient(Exception error) : IRuntimeLlmClient
    {
        public Task<LlmResponse> ChatAsync(
            string workspaceId,
            string sessionId,
            string agentTemplateId,
            IReadOnlyList<ChatMessage> messages,
            IReadOnlyList<LlmToolDefinition>? tools = null,
            LlmConfig? llmConfig = null,
            CancellationToken ct = default)
            => Task.FromException<LlmResponse>(error);

        public async IAsyncEnumerable<StreamDelta> ChatStreamAsync(
            string workspaceId,
            string sessionId,
            string agentTemplateId,
            IReadOnlyList<ChatMessage> messages,
            IReadOnlyList<LlmToolDefinition>? tools = null,
            LlmConfig? llmConfig = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
