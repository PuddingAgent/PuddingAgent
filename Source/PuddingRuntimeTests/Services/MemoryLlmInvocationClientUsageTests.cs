using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services;

[TestClass]
public sealed class MemoryLlmInvocationClientUsageTests
{
    [TestMethod]
    public async Task ChatAsync_AwaitsRequiredUsageFactWithInvocationIdentity()
    {
        var invocation = new RecordingInvocationService();
        var recorder = new RecordingTokenUsageRecorder();
        var client = new MemoryLlmInvocationClient(
            invocation,
            NullLogger<MemoryLlmInvocationClient>.Instance,
            llmConfigService: null,
            tokenUsageRecorder: recorder);

        var reply = await client.ChatAsync("system", "user");

        Assert.AreEqual("ok", reply);
        Assert.IsNotNull(invocation.Request);
        Assert.AreEqual(0, recorder.BestEffortCalls);
        Assert.AreEqual(1, recorder.RequiredCalls);
        Assert.AreEqual($"llm:{invocation.Request!.InvocationId}", recorder.SourceId);
        Assert.AreEqual("memory", recorder.WorkspaceId);
        Assert.AreEqual("subconscious-memory", recorder.SessionId);
        Assert.AreEqual("deepseek", recorder.ProviderId);
        Assert.AreEqual("deepseek-v4-flash", recorder.ModelId);
    }

    [TestMethod]
    public async Task ChatAsync_WhenRequiredUsageFactFails_DoesNotReportSuccess()
    {
        var client = new MemoryLlmInvocationClient(
            new RecordingInvocationService(),
            NullLogger<MemoryLlmInvocationClient>.Instance,
            llmConfigService: null,
            tokenUsageRecorder: new RecordingTokenUsageRecorder
            {
                RequiredFailure = new InvalidOperationException("ledger unavailable"),
            });

        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => client.ChatAsync("system", "user"));

        Assert.AreEqual("ledger unavailable", error.Message);
    }

    [TestMethod]
    public async Task ChatWithConfigAsync_AttributesUsageToExplicitAgentScopeAndStage()
    {
        var invocation = new RecordingInvocationService();
        var recorder = new RecordingTokenUsageRecorder();
        var client = new MemoryLlmInvocationClient(
            invocation,
            NullLogger<MemoryLlmInvocationClient>.Instance,
            llmConfigService: null,
            tokenUsageRecorder: recorder);

        await client.ChatWithConfigAsync("system", "user", new MemoryLlmConfig(null, null, "deepseek-v4-flash")
        {
            ProviderId = "deepseek",
            ProfileId = "agent:agent-1:subconscious",
            WorkspaceId = "workspace-1",
            SessionId = "daily-summary:2026-07-31",
            AgentInstanceId = "agent-1",
            Stage = "daily-summary",
        });

        Assert.AreEqual("workspace-1", invocation.Request!.WorkspaceId);
        Assert.AreEqual("daily-summary:2026-07-31", invocation.Request.SessionId);
        Assert.AreEqual("agent-1", invocation.Request.AgentInstanceId);
        Assert.AreEqual("workspace-1", recorder.WorkspaceId);
        Assert.AreEqual("daily-summary:2026-07-31", recorder.SessionId);
        StringAssert.StartsWith(recorder.SourceId!, "llm:daily-summary:");
    }

    private sealed class RecordingInvocationService : ILlmInvocationService
    {
        public LlmInvocationRequest? Request { get; private set; }

        public Task<LlmInvocationResult> InvokeAsync(
            LlmInvocationRequest request,
            CancellationToken ct = default)
        {
            Request = request;
            return Task.FromResult(new LlmInvocationResult
            {
                Success = true,
                ReplyText = "ok",
                ProviderId = "deepseek",
                ModelId = "deepseek-v4-flash",
                Usage = new TokenUsageDto
                {
                    PromptTokens = 100,
                    CompletionTokens = 20,
                    TotalTokens = 120,
                    PromptCacheHitTokens = 80,
                    PromptCacheMissTokens = 20,
                },
            });
        }

        public async IAsyncEnumerable<StreamDelta> InvokeStreamAsync(
            LlmInvocationRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class RecordingTokenUsageRecorder : ITokenUsageRecorder
    {
        public int BestEffortCalls { get; private set; }
        public int RequiredCalls { get; private set; }
        public string? SourceId { get; private set; }
        public string? WorkspaceId { get; private set; }
        public string? SessionId { get; private set; }
        public string? ProviderId { get; private set; }
        public string? ModelId { get; private set; }
        public Exception? RequiredFailure { get; init; }

        public Task RecordAsync(
            TokenUsageDto usage,
            string sourceType,
            string sourceId,
            string? workspaceId,
            string? sessionId,
            string? providerId,
            string? modelId,
            PromptPrefixSnapshot? prefixSnapshot = null,
            DateTimeOffset? occurredAtUtc = null,
            string? parentSessionId = null)
        {
            BestEffortCalls++;
            return Task.CompletedTask;
        }

        public Task RecordRequiredAsync(
            TokenUsageDto usage,
            string sourceType,
            string sourceId,
            string? workspaceId,
            string? sessionId,
            string? providerId,
            string? modelId,
            PromptPrefixSnapshot? prefixSnapshot = null,
            DateTimeOffset? occurredAtUtc = null,
            string? parentSessionId = null)
        {
            RequiredCalls++;
            SourceId = sourceId;
            WorkspaceId = workspaceId;
            SessionId = sessionId;
            ProviderId = providerId;
            ModelId = modelId;

            return RequiredFailure is null
                ? Task.CompletedTask
                : Task.FromException(RequiredFailure);
        }
    }
}
