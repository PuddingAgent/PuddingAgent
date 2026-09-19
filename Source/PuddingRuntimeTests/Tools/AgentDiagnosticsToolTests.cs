using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PuddingCode.Runtime;
using PuddingCode.Tools;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

[TestClass]
public sealed class AgentDiagnosticsToolTests
{
    [TestMethod]
    public async Task ContextHealth_ReturnsHealthSnapshotAlignedWithContract()
    {
        var resolver = new FakeContextCapacityResolver
        {
            Capacity = new ResolvedContextCapacity(200_000, 8_192, 128_000),
        };
        var compaction = new FakeContextCompactionService
        {
            Health = new ContextHealthSnapshot(
                "session-1", 50_000, 200_000, 180_000, 130_000, 0.277,
                ContextHealthState.Healthy, false, false, false),
        };
        var tool = CreateTool(resolver, compaction, out var provider);
        using (provider)
        {
            var result = await ExecuteAsync(
                tool,
                """{"action":"context_health"}""",
                sessionId: "session-1",
                workspaceId: "default",
                agentInstanceId: "agent-a");

            Assert.IsTrue(result.Success, result.Error);
            Assert.AreEqual("default", resolver.LastWorkspaceId);
            Assert.AreEqual("agent-a", resolver.LastAgentId);
            Assert.AreEqual("session-1", compaction.LastSessionId);
            Assert.AreEqual(200_000, compaction.LastContextWindowTokens);
            Assert.AreEqual(8_192, compaction.LastMaxOutputTokens);
            Assert.AreEqual(128_000, compaction.LastMaxInputTokens);

            using var doc = JsonDocument.Parse(result.Output);
            var root = doc.RootElement;
            Assert.AreEqual("session-1", root.GetProperty("sessionId").GetString());
            Assert.AreEqual("Healthy", root.GetProperty("state").GetString());
            Assert.AreEqual(50_000, root.GetProperty("usedTokens").GetInt32());
            Assert.AreEqual(200_000, root.GetProperty("contextWindowTokens").GetInt32());
            Assert.AreEqual(180_000, root.GetProperty("effectiveWindowTokens").GetInt32());
            Assert.AreEqual(130_000, root.GetProperty("remainingTokens").GetInt32());
            Assert.IsTrue(root.GetProperty("usageRatio").GetDouble() > 0.2);
        }
    }

    [TestMethod]
    public async Task ContextHealth_MissingAgentIdentity_ReturnsError()
    {
        var resolver = new FakeContextCapacityResolver();
        var compaction = new FakeContextCompactionService();
        var tool = CreateTool(resolver, compaction, out var provider);
        using (provider)
        {
            var result = await ExecuteAsync(
                tool,
                """{"action":"context_health"}""",
                sessionId: "session-1",
                workspaceId: null,
                agentInstanceId: null);

            using var doc = JsonDocument.Parse(result.Output);
            StringAssert.Contains(
                doc.RootElement.GetProperty("error").GetString()!,
                "session_id, workspace_id, and agent_instance_id are required");
            Assert.IsNull(resolver.LastWorkspaceId);
        }
    }

    [TestMethod]
    public async Task ContextHealth_UnresolvableCapacity_ReturnsError()
    {
        var resolver = new FakeContextCapacityResolver
        {
            Capacity = null,
        };
        var compaction = new FakeContextCompactionService();
        var tool = CreateTool(resolver, compaction, out var provider);
        using (provider)
        {
            var result = await ExecuteAsync(
                tool,
                """{"action":"context_health"}""",
                sessionId: "session-1",
                workspaceId: "default",
                agentInstanceId: "agent-a");

            using var doc = JsonDocument.Parse(result.Output);
            StringAssert.Contains(
                doc.RootElement.GetProperty("error").GetString()!,
                "Unable to resolve the context window capacity");
        }
    }

    [TestMethod]
    public async Task ContextHealth_MissingServices_ReturnsGracefulError()
    {
        var services = new ServiceCollection();
        using var provider = services.BuildServiceProvider();
        var tool = new AgentDiagnosticsTool(
            activitySink: null,
            scopeFactory: provider.GetRequiredService<IServiceScopeFactory>());

        var result = await ExecuteAsync(
            tool,
            """{"action":"context_health"}""",
            sessionId: "session-1",
            workspaceId: "default",
            agentInstanceId: "agent-a");

        using var doc = JsonDocument.Parse(result.Output);
        StringAssert.Contains(
            doc.RootElement.GetProperty("error").GetString()!,
            "services are not available");
    }

    [TestMethod]
    public async Task Diagnose_PartialDataSources_ReportsUnknownWithCoverageAndSkipReasons()
    {
        // 只有上下文维度可解析：活动流、缓存、子代理全都不可用。
        // 契约：不得因此声称健康，且必须把「覆盖不完整」与「跳过了哪些检查」如实报出来。
        var resolver = new FakeContextCapacityResolver
        {
            Capacity = new ResolvedContextCapacity(200_000, 8_192, 128_000),
        };
        var compaction = new FakeContextCompactionService
        {
            Health = new ContextHealthSnapshot(
                "session-1", 50_000, 200_000, 180_000, 130_000, 0.277,
                ContextHealthState.Healthy, false, false, false),
        };
        var tool = CreateTool(resolver, compaction, out var provider);
        using (provider)
        {
            var result = await ExecuteAsync(tool, """{"action":"diagnose"}""");

            Assert.IsTrue(result.Success, result.Error);

            using var doc = JsonDocument.Parse(result.Output);
            var root = doc.RootElement;

            Assert.AreEqual("unknown", root.GetProperty("verdict").GetString());

            var coverage = root.GetProperty("coverage");
            Assert.AreEqual("partial", coverage.GetProperty("scope").GetString());
            Assert.AreEqual("available", coverage.GetProperty("context").GetString());
            Assert.AreEqual("unavailable", coverage.GetProperty("cache").GetString());
            Assert.AreEqual("unavailable", coverage.GetProperty("activity").GetString());
            Assert.AreEqual("unavailable", coverage.GetProperty("subagent").GetString());

            var codes = root.GetProperty("findings").EnumerateArray()
                .Select(f => f.GetProperty("code").GetString())
                .ToList();
            CollectionAssert.Contains(codes, "source.unavailable");
            CollectionAssert.Contains(codes, "coverage.partial");

            Assert.IsTrue(
                root.GetProperty("checks_skipped").GetArrayLength() > 0,
                "跳过的检查必须带原因，不能静默省略");

            // 观测窗口必须如实回报 unknown，而不是编造一个时间范围。
            Assert.AreEqual(
                "unknown",
                root.GetProperty("observation_window").GetProperty("window_start_utc").GetString());
        }
    }

    [TestMethod]
    public async Task Diagnose_UnknownActionMessageListsDiagnose()
    {
        var resolver = new FakeContextCapacityResolver();
        var compaction = new FakeContextCompactionService();
        var tool = CreateTool(resolver, compaction, out var provider);
        using (provider)
        {
            var result = await ExecuteAsync(tool, """{"action":"nope"}""");

            using var doc = JsonDocument.Parse(result.Output);
            StringAssert.Contains(
                doc.RootElement.GetProperty("error").GetString()!,
                "diagnose");
        }
    }

    private static AgentDiagnosticsTool CreateTool(
        IContextCapacityResolver resolver,
        IContextCompactionService compaction,
        out ServiceProvider provider)
    {
        var services = new ServiceCollection();
        services.AddSingleton(resolver);
        services.AddSingleton(compaction);
        provider = services.BuildServiceProvider();
        return new AgentDiagnosticsTool(
            activitySink: null,
            scopeFactory: provider.GetRequiredService<IServiceScopeFactory>());
    }

    private static Task<ToolExecutionResult> ExecuteAsync(
        AgentDiagnosticsTool tool,
        string argumentsJson,
        string? sessionId = "session-1",
        string? workspaceId = "default",
        string? agentInstanceId = "agent-a")
        => tool.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "call-agent-diagnostics",
            ArgumentsJson = argumentsJson,
            Context = new ToolExecutionContext
            {
                SessionId = sessionId!,
                WorkspaceId = workspaceId!,
                AgentInstanceId = agentInstanceId!,
            },
        });

    private sealed class FakeContextCapacityResolver : IContextCapacityResolver
    {
        public ResolvedContextCapacity? Capacity { get; init; }
        public string? LastWorkspaceId { get; private set; }
        public string? LastAgentId { get; private set; }

        public Task<ResolvedContextCapacity?> ResolveAsync(
            string workspaceId,
            string agentId,
            CancellationToken ct = default)
        {
            LastWorkspaceId = workspaceId;
            LastAgentId = agentId;
            return Task.FromResult(Capacity);
        }
    }

    private sealed class FakeContextCompactionService : IContextCompactionService
    {
        public ContextHealthSnapshot? Health { get; init; }
        public string? LastSessionId { get; private set; }
        public int? LastContextWindowTokens { get; private set; }
        public int? LastMaxOutputTokens { get; private set; }
        public int? LastMaxInputTokens { get; private set; }

        public Task<ContextHealthSnapshot> GetHealthAsync(
            string sessionId,
            CancellationToken ct = default,
            int? contextWindowTokens = null,
            int? maxOutputTokens = null,
            int? maxInputTokens = null,
            int toolCount = 0)
        {
            LastSessionId = sessionId;
            LastContextWindowTokens = contextWindowTokens;
            LastMaxOutputTokens = maxOutputTokens;
            LastMaxInputTokens = maxInputTokens;
            return Task.FromResult(Health
                ?? new ContextHealthSnapshot(
                    sessionId, 0, contextWindowTokens ?? 200_000, 180_000, 180_000,
                    0, ContextHealthState.Healthy, false, false, false));
        }

        public Task<ContextCompactionResult> CompactAsync(
            ContextCompactionRequest request,
            CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
