using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Tools;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Services.Tools;

[TestClass]
public sealed class SubconsciousTriggerToolTests
{
    [TestMethod]
    public async Task Trigger_SkillCurate_ShouldReturnCurationReport()
    {
        var orchestrator = new StubSubconsciousOrchestrator();
        var tool = CreateTool(orchestrator);

        var result = await tool.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "call-skill-curate-1",
            ArgumentsJson = """{"action":"skill_curate","workspaceId":"workspace-evolution"}""",
            Context = CreateContext(),
        });

        Assert.IsTrue(result.Success);
        Assert.AreEqual(1, orchestrator.SkillCurateCallCount);
        using var json = JsonDocument.Parse(result.Output);
        var root = json.RootElement;
        Assert.AreEqual("skill_curate", root.GetProperty("action").GetString());
        Assert.AreEqual(6, root.GetProperty("n_before").GetInt32());
        Assert.AreEqual(6, root.GetProperty("n_after").GetInt32());
        Assert.IsTrue(
            root.TryGetProperty("not_reduced_reason", out var reason)
            && !string.IsNullOrWhiteSpace(reason.GetString()));
        Assert.AreEqual(2, root.GetProperty("candidate_count").GetInt32());
        Assert.AreEqual(1, root.GetProperty("retire_suggestion_count").GetInt32());
        Assert.IsFalse(root.TryGetProperty("error", out _));
    }

    [TestMethod]
    public async Task Trigger_UnknownAction_ShouldListSkillCurateAsValid()
    {
        var tool = CreateTool(new StubSubconsciousOrchestrator());

        var result = await tool.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "call-unknown-1",
            ArgumentsJson = """{"action":"no_such_pipeline"}""",
            Context = CreateContext(),
        });

        Assert.IsTrue(result.Success);
        using var json = JsonDocument.Parse(result.Output);
        var error = json.RootElement.GetProperty("error").GetString();
        Assert.IsNotNull(error);
        Assert.IsTrue(error.Contains("skill_curate", StringComparison.Ordinal));
    }

    private static SubconsciousTriggerTool CreateTool(StubSubconsciousOrchestrator orchestrator)
        => new(
            orchestrator,
            new StubLlmConfigResolver(),
            NullLogger<SubconsciousTriggerTool>.Instance);

    private static ToolExecutionContext CreateContext() => new()
    {
        WorkspaceId = "workspace-evolution",
        SessionId = "session-evolution",
        AgentInstanceId = "agent-evolution",
    };

    private sealed class StubSubconsciousOrchestrator : ISubconsciousOrchestrator
    {
        public int SkillCurateCallCount { get; private set; }

        public Task ConsolidateAsync(
            ConsolidationJob job, string mode,
            MemoryLlmConfig? memoryLlmConfig = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<SessionSummary> SummarizeSessionAsync(
            string sessionId, string workspaceId, string agentId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<string?> RecallAugmentedAsync(
            string userMessage, string workspaceId, string agentId,
            string? sessionId = null, int maxTokens = 2000,
            MemoryLlmConfig? memoryLlmConfig = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<MemoryDashboard> GetMemoryDashboardAsync(
            string workspaceId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<MemorySearchResult> SearchMemoriesAsync(
            MemorySearchRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<AutoDreamReport> AutoDreamAsync(
            string workspaceId, MemoryLlmConfig? memoryLlmConfig = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<PatternExtractionReport> ExtractPatternsAsync(
            string workspaceId, string agentInstanceId,
            MemoryLlmConfig? memoryLlmConfig = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<SkillImprovementReport> ImproveSkillsAsync(
            string workspaceId, string agentInstanceId,
            MemoryLlmConfig? memoryLlmConfig = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<SkillCurationReport> SkillCurateAsync(
            string workspaceId, string agentInstanceId,
            MemoryLlmConfig? memoryLlmConfig = null, CancellationToken ct = default)
        {
            SkillCurateCallCount++;
            return Task.FromResult(new SkillCurationReport
            {
                DurationMs = 45,
                NBefore = 6,
                NAfter = 6,
                NotReducedReason = "G6 is report-only: identified 1 duplicate-name retire suggestion(s), but curation has no disable authority until G7 gates land.",
                CandidateCount = 2,
                RetireSuggestionCount = 1,
                Summary = "n_before=6, n_after=6, refine candidates=2, retire suggestions=1; report-only, no skills modified",
                Timestamp = new DateTime(2026, 7, 30, 4, 3, 0, DateTimeKind.Utc),
            });
        }
    }

    private sealed class StubLlmConfigResolver : ILLMConfigResolver
    {
        public Task<AgentRoleLlmRoutingConfig> ResolveRoleAsync(
            string workspaceId, string configurationAgentInstanceId, string roleId,
            CancellationToken ct = default)
            => Task.FromResult(new AgentRoleLlmRoutingConfig
            {
                RoleId = roleId,
                ConfigurationAgentInstanceId = configurationAgentInstanceId,
                ProviderId = "stub-provider",
                ProfileId = "stub-profile",
                ModelId = "stub-model",
                Config = new LlmConfig { Endpoint = "http://stub.local/v1", ModelId = "stub-model" },
            });

        public Task<LlmRoutingConfig?> ResolveAsync(
            AgentLlmBinding binding, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<MemoryLlmRoutingConfig?> ResolveMemoryAsync(
            AgentLlmBinding binding, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
