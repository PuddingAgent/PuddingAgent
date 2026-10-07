using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PuddingCode.Observability;
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

    /// <summary>
    /// 2026-09-22 超限事故可见性回归：context_health 必须同时输出门禁比率（分母 = 有效输入窗口）
    /// 与门禁阈值常量，否则「usageRatio=0.609 看似宽松、gateRatio=1.0041 实际已超限」这类
    /// 观测盲点无法从诊断输出里看出。
    /// </summary>
    [TestMethod]
    public async Task ContextHealth_OutputCarriesGateRatioAndGateThresholds()
    {
        var resolver = new FakeContextCapacityResolver
        {
            Capacity = new ResolvedContextCapacity(200_000, 8_192, 128_000),
        };
        var compaction = new FakeContextCompactionService
        {
            Health = new ContextHealthSnapshot(
                "session-1", 609_305, 1_000_000, 606_784, 0, 0.609305,
                ContextHealthState.Blocking, true, true, true, GateRatio: 1.00415),
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
            using var doc = JsonDocument.Parse(result.Output);
            var root = doc.RootElement;

            Assert.AreEqual(0.609305, root.GetProperty("usageRatio").GetDouble(), 1e-6);
            Assert.AreEqual(1.00415, root.GetProperty("gateRatio").GetDouble(), 1e-6);
            Assert.IsGreaterThan(
                root.GetProperty("usageRatio").GetDouble(),
                root.GetProperty("gateRatio").GetDouble());

            var thresholds = root.GetProperty("gateThresholds");
            Assert.AreEqual(0.60, thresholds.GetProperty("warning").GetDouble(), 1e-9);
            Assert.AreEqual(0.75, thresholds.GetProperty("unhealthy").GetDouble(), 1e-9);
            Assert.AreEqual(
                ContextCompactionDefaults.TriggerRatio,
                thresholds.GetProperty("trigger").GetDouble(),
                1e-9);
            Assert.AreEqual(0.92, thresholds.GetProperty("blocking").GetDouble(), 1e-9);
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

    // ── runtime_identity：部署核实（P2）────────────────────────────

    [TestMethod]
    public async Task RuntimeIdentity_ReportsLoadedAssembliesWithHashVerifiableOnDisk()
    {
        var services = new ServiceCollection();
        using var provider = services.BuildServiceProvider();
        var tool = new AgentDiagnosticsTool(
            activitySink: null,
            scopeFactory: provider.GetRequiredService<IServiceScopeFactory>());

        var result = await ExecuteAsync(tool, """{"action":"runtime_identity"}""");

        Assert.IsTrue(result.Success, result.Error);

        using var doc = JsonDocument.Parse(result.Output);
        var root = doc.RootElement;

        Assert.AreEqual(Environment.ProcessId, root.GetProperty("process").GetProperty("pid").GetInt32());
        Assert.IsFalse(
            string.IsNullOrWhiteSpace(root.GetProperty("runtime").GetProperty("framework").GetString()));

        var runtimeAssembly = typeof(AgentDiagnosticsTool).Assembly;
        var runtimeEntry = root.GetProperty("assemblies").EnumerateArray()
            .Single(a => a.GetProperty("role").GetString() == "runtime");

        Assert.AreEqual(runtimeAssembly.GetName().Name, runtimeEntry.GetProperty("name").GetString());

        var location = runtimeEntry.GetProperty("location").GetString();
        Assert.AreEqual(runtimeAssembly.Location, location);

        // 哈希必须与磁盘字节一一对应，否则「核实部署到底加载了哪份产物」这句话没有意义。
        using var stream = File.OpenRead(location!);
        var expectedHash = Convert.ToHexString(SHA256.HashData(stream));
        var file = runtimeEntry.GetProperty("file");
        Assert.AreEqual(expectedHash, file.GetProperty("sha256").GetString());
        Assert.AreEqual(new FileInfo(location!).Length, file.GetProperty("bytes").GetInt64());

        // MVID / InformationalVersion 必须报出来，且 MVID 形状可判别（Guid "D" 格式）。
        var mvid = runtimeEntry.GetProperty("mvid").GetString();
        Assert.AreEqual(runtimeAssembly.ManifestModule.ModuleVersionId.ToString("D"), mvid);
        Assert.AreEqual(36, mvid!.Length);
        Assert.IsFalse(
            string.IsNullOrWhiteSpace(runtimeEntry.GetProperty("informationalVersion").GetString()));
    }

    [TestMethod]
    public async Task RuntimeIdentity_TargetFile_ReportsHashMatchingDiskBytes()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pd-runtime-identity-{Guid.NewGuid():N}.bin");
        var payload = new byte[4096];
        for (var i = 0; i < payload.Length; i++)
            payload[i] = (byte)(i % 251);
        await File.WriteAllBytesAsync(path, payload);

        try
        {
            var services = new ServiceCollection();
            using var provider = services.BuildServiceProvider();
            var tool = new AgentDiagnosticsTool(
                activitySink: null,
                scopeFactory: provider.GetRequiredService<IServiceScopeFactory>());

            var result = await ExecuteAsync(
                tool,
                $$"""{"action":"runtime_identity","assembly_path":{{JsonSerializer.Serialize(path)}}}""");

            Assert.IsTrue(result.Success, result.Error);

            using var doc = JsonDocument.Parse(result.Output);
            var target = doc.RootElement.GetProperty("target");
            Assert.IsNull(
                target.GetProperty("error").GetString(),
                "an existing target file must not be reported as an error");

            var file = target.GetProperty("file");
            Assert.AreEqual(
                Convert.ToHexString(SHA256.HashData(payload)),
                file.GetProperty("sha256").GetString());
            Assert.AreEqual(payload.Length, file.GetProperty("bytes").GetInt64());
            Assert.IsTrue(
                string.Equals(
                    Path.GetFullPath(path),
                    file.GetProperty("path").GetString(),
                    StringComparison.OrdinalIgnoreCase),
                "the target must be reported with its on-disk full path");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task RuntimeIdentity_MissingTargetPath_ReportsErrorWithoutInventingIdentity()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pd-missing-{Guid.NewGuid():N}.bin");
        var services = new ServiceCollection();
        using var provider = services.BuildServiceProvider();
        var tool = new AgentDiagnosticsTool(
            activitySink: null,
            scopeFactory: provider.GetRequiredService<IServiceScopeFactory>());

        var result = await ExecuteAsync(
            tool,
            $$"""{"action":"runtime_identity","assembly_path":{{JsonSerializer.Serialize(path)}}}""");

        Assert.IsTrue(result.Success, result.Error);

        using var doc = JsonDocument.Parse(result.Output);
        var target = doc.RootElement.GetProperty("target");
        Assert.AreEqual("file not found or unreadable", target.GetProperty("error").GetString());
        Assert.AreEqual(
            JsonValueKind.Null,
            target.GetProperty("file").ValueKind,
            "a missing file must not be described with invented size or hash");
    }

    [TestMethod]
    public async Task RuntimeIdentity_DoesNotEchoSecretsOrEnvironmentVariables()
    {
        var services = new ServiceCollection();
        using var provider = services.BuildServiceProvider();
        var tool = new AgentDiagnosticsTool(
            activitySink: null,
            scopeFactory: provider.GetRequiredService<IServiceScopeFactory>());

        var result = await ExecuteAsync(tool, """{"action":"runtime_identity"}""");

        Assert.IsTrue(result.Success, result.Error);

        var lowered = result.Output.ToLowerInvariant();
        foreach (var forbidden in new[] { "token", "secret", "password", "apikey", "api_key", "connectionstring" })
        {
            Assert.IsFalse(
                lowered.Contains(forbidden, StringComparison.Ordinal),
                $"runtime_identity must not echo '{forbidden}'");
        }
    }

    [TestMethod]
    public async Task RuntimeIdentity_UnknownActionMessageListsRuntimeIdentity()
    {
        var resolver = new FakeContextCapacityResolver();
        var compaction = new FakeContextCompactionService();
        var tool = CreateTool(resolver, compaction, out var provider);
        using (provider)
        {
            var result = await ExecuteAsync(tool, """{"action":"nope"}""");

            StringAssert.Contains(result.Output, "runtime_identity");
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

    // ── tool_stats：统计口径不得被 limit 截断（2026-10-08 缺陷修复）────────────────

    [TestMethod]
    public async Task ToolStats_StatisticsCoverEveryMatchedActivity_NotJustTheFirstLimit()
    {
        var sink = new FakeActivitySink(
        [
            Activity("file_patch", RuntimeActivityStatuses.Succeeded, 10),
            Activity("file_patch", RuntimeActivityStatuses.Succeeded, 20),
            Activity("file_patch", RuntimeActivityStatuses.Failed, 30, "boom"),
            Activity("file_patch", RuntimeActivityStatuses.Failed, 40, "boom"),
            Activity("file_patch", RuntimeActivityStatuses.Succeeded, 50),
            Activity("file_patch", RuntimeActivityStatuses.Succeeded, 60),
        ]);

        var services = new ServiceCollection();
        using var provider = services.BuildServiceProvider();
        var tool = new AgentDiagnosticsTool(
            activitySink: sink,
            scopeFactory: provider.GetRequiredService<IServiceScopeFactory>());

        // limit=2：修复前这里只统计「最近 2 条」——total_calls 会报成 2，成功率/耗时同样被截断。
        var result = await ExecuteAsync(tool, """{"action":"tool_stats","tool_name":"file_patch","limit":2}""");

        Assert.IsTrue(result.Success, result.Error);
        using var doc = JsonDocument.Parse(result.Output);
        var root = doc.RootElement;

        Assert.AreEqual(6, root.GetProperty("total_calls").GetInt32(),
            "统计必须覆盖样本内全部匹配活动；limit 只约束采样量，不得截断统计口径");
        Assert.AreEqual(4, root.GetProperty("success_count").GetInt32());
        Assert.AreEqual(2, root.GetProperty("failure_count").GetInt32());
        Assert.AreEqual(0.667, root.GetProperty("success_rate").GetDouble(), 0.001);
        Assert.AreEqual(1, root.GetProperty("common_errors").GetArrayLength(), "两条 boom 应聚成一条");
        Assert.AreEqual(2, root.GetProperty("common_errors")[0].GetProperty("count").GetInt32());

        var sample = root.GetProperty("sample");
        Assert.AreEqual(6, sample.GetProperty("activities_queried").GetInt32());
        Assert.AreEqual(6, sample.GetProperty("tool_activities_matched").GetInt32());
        Assert.IsFalse(sample.GetProperty("sample_truncated_by_sink_limit").GetBoolean(),
            "6 条活动远低于 sink 上限，不得报「已截断」");
    }

    [TestMethod]
    public async Task ToolStats_SampleBlockReportsSinkLimitAndStableErrorOrdering()
    {
        var activities = new List<RuntimeActivity>();
        for (var i = 0; i < 500; i++)
            activities.Add(Activity("search_grep", RuntimeActivityStatuses.Succeeded, 5));

        // 两条同计数的错误：稳定次序须按 message 字典序（alpha 在前），否则同一份数据每次查询顺序都可能漂移。
        activities.Add(Activity("search_grep", RuntimeActivityStatuses.Failed, 7, "zeta failure"));
        activities.Add(Activity("search_grep", RuntimeActivityStatuses.Failed, 9, "alpha failure"));
        var sink = new FakeActivitySink(activities);

        var services = new ServiceCollection();
        using var provider = services.BuildServiceProvider();
        var tool = new AgentDiagnosticsTool(
            activitySink: sink,
            scopeFactory: provider.GetRequiredService<IServiceScopeFactory>());

        var result = await ExecuteAsync(tool, """{"action":"tool_stats","tool_name":"search_grep","limit":200}""");

        Assert.IsTrue(result.Success, result.Error);
        using var doc = JsonDocument.Parse(result.Output);
        var root = doc.RootElement;

        Assert.AreEqual(502, root.GetProperty("total_calls").GetInt32());

        var sample = root.GetProperty("sample");
        Assert.AreEqual(502, sample.GetProperty("activities_queried").GetInt32());
        Assert.AreEqual(500, sample.GetProperty("sample_limit_effective").GetInt32(),
            "sink 实现在查询层把 Limit 夹到 500，必须如实上报该上限");
        Assert.IsTrue(sample.GetProperty("sample_truncated_by_sink_limit").GetBoolean(),
            "样本数达到有效上限时必须标注已截断，否则统计看起来像全量");

        var errors = root.GetProperty("common_errors");
        Assert.AreEqual(2, errors.GetArrayLength());
        Assert.AreEqual("alpha failure", errors[0].GetProperty("message").GetString());
        Assert.AreEqual("zeta failure", errors[1].GetProperty("message").GetString());
    }

    // ── tool_stats 总览/排名模式（2026-10-08）────────────────────────────────

    [TestMethod]
    public async Task ToolStats_OverviewRanksByFailuresThenCallsAndSkipsUntaggedActivities()
    {
        var sink = new FakeActivitySink(
        [
            // code_outline：调用数最多但零失败
            Activity("code_outline", RuntimeActivityStatuses.Succeeded, 5),
            Activity("code_outline", RuntimeActivityStatuses.Succeeded, 6),
            Activity("code_outline", RuntimeActivityStatuses.Succeeded, 7),
            Activity("code_outline", RuntimeActivityStatuses.Succeeded, 8),
            Activity("code_outline", RuntimeActivityStatuses.Succeeded, 9),
            Activity("code_outline", RuntimeActivityStatuses.Succeeded, 10),
            // file_patch：调用数少但有失败
            Activity("file_patch", RuntimeActivityStatuses.Succeeded, 10),
            Activity("file_patch", RuntimeActivityStatuses.Succeeded, 20),
            Activity("file_patch", RuntimeActivityStatuses.Succeeded, 30),
            Activity("file_patch", RuntimeActivityStatuses.Failed, 40, "boom"),
            Activity("file_patch", RuntimeActivityStatuses.Failed, 50, "boom"),
            // search_grep：偶发一次
            Activity("search_grep", RuntimeActivityStatuses.Succeeded, 1),
            // 无 tool_name 元数据：不入排名，但必须如实计数
            UntaggedActivity(RuntimeActivityStatuses.Succeeded, 2),
        ]);

        var services = new ServiceCollection();
        using var provider = services.BuildServiceProvider();
        var tool = new AgentDiagnosticsTool(
            activitySink: sink,
            scopeFactory: provider.GetRequiredService<IServiceScopeFactory>());

        // 不传 tool_name ⇒ 总览模式
        var result = await ExecuteAsync(tool, """{"action":"tool_stats"}""");

        Assert.IsTrue(result.Success, result.Error);
        using var doc = JsonDocument.Parse(result.Output);
        var root = doc.RootElement;

        Assert.AreEqual("overview", root.GetProperty("mode").GetString());

        var tools = root.GetProperty("tools");
        Assert.AreEqual(3, tools.GetArrayLength());
        Assert.AreEqual(3, root.GetProperty("tools_in_sample").GetInt32());

        // 第一排序键是失败数：失败 2 次的 file_patch 排在调用数最多（6）但零失败的 code_outline 之前
        Assert.AreEqual("file_patch", tools[0].GetProperty("tool_name").GetString());
        Assert.AreEqual(5, tools[0].GetProperty("total_calls").GetInt32());
        Assert.AreEqual(2, tools[0].GetProperty("failure_count").GetInt32());
        Assert.AreEqual(0.6, tools[0].GetProperty("success_rate").GetDouble(), 0.001);

        // 第二排序键是调用数（同为 0 失败时）
        Assert.AreEqual("code_outline", tools[1].GetProperty("tool_name").GetString());
        Assert.AreEqual(6, tools[1].GetProperty("total_calls").GetInt32());
        Assert.AreEqual("search_grep", tools[2].GetProperty("tool_name").GetString());

        Assert.AreEqual(1, root.GetProperty("activities_without_tool_name").GetInt32(),
            "无 tool_name 元数据的活动不得隐式归入某个工具，也不得被静默丢弃");
        Assert.AreEqual(13, root.GetProperty("sample").GetProperty("activities_queried").GetInt32());
    }

    [TestMethod]
    public async Task ToolStats_OverviewAcceptsAllAliasAndHonoursLimit()
    {
        var sink = new FakeActivitySink(
        [
            Activity("file_patch", RuntimeActivityStatuses.Failed, 10, "boom"),
            Activity("search_grep", RuntimeActivityStatuses.Succeeded, 10),
            Activity("code_outline", RuntimeActivityStatuses.Succeeded, 10),
        ]);

        var services = new ServiceCollection();
        using var provider = services.BuildServiceProvider();
        var tool = new AgentDiagnosticsTool(
            activitySink: sink,
            scopeFactory: provider.GetRequiredService<IServiceScopeFactory>());

        var result = await ExecuteAsync(tool, """{"action":"tool_stats","tool_name":"all","limit":2}""");

        Assert.IsTrue(result.Success, result.Error);
        using var doc = JsonDocument.Parse(result.Output);
        var root = doc.RootElement;

        Assert.AreEqual("overview", root.GetProperty("mode").GetString());
        Assert.AreEqual(2, root.GetProperty("tool_count_returned").GetInt32(),
            "limit 只约束返回的工具条数");
        Assert.AreEqual(3, root.GetProperty("tools_in_sample").GetInt32(),
            "limit 不得改变聚合口径：样本里仍是 3 个工具");
        Assert.AreEqual("file_patch", root.GetProperty("tools")[0].GetProperty("tool_name").GetString());
    }

    private static RuntimeActivity Activity(
        string toolName, string status, long durationMs, string? error = null)
        => new()
        {
            Trace = RuntimeTraceContext.CreateNew(sessionId: "session-1", workspaceId: "default"),
            Component = RuntimeActivityComponents.ToolRunner,
            Operation = $"tool.{toolName}",
            Status = status,
            DurationMs = durationMs,
            ErrorMessage = error,
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["tool_name"] = toolName },
        };

    private static RuntimeActivity UntaggedActivity(string status, long durationMs)
        => new()
        {
            Trace = RuntimeTraceContext.CreateNew(sessionId: "session-1", workspaceId: "default"),
            Component = RuntimeActivityComponents.ToolRunner,
            Operation = "tool.unknown",
            Status = status,
            DurationMs = durationMs,
            Metadata = null,
        };

    private sealed class FakeActivitySink : IRuntimeActivitySink
    {
        private readonly IReadOnlyList<RuntimeActivity> _activities;

        public FakeActivitySink(IReadOnlyList<RuntimeActivity> activities) => _activities = activities;

        public RuntimeActivityQuery? LastQuery { get; private set; }

        public Task RecordAsync(RuntimeActivity activity, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<RuntimeActivity>> QueryAsync(
            RuntimeActivityQuery query, CancellationToken ct = default)
        {
            LastQuery = query;
            return Task.FromResult(_activities);
        }
    }
}
