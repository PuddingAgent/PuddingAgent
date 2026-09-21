using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PuddingCode.Abstractions;
using PuddingCode.Classification;
using PuddingCode.Operators;
using PuddingCode.Tools;
using PuddingRuntime.Classification;
using PuddingRuntime.Services.Tools;
using PuddingRuntime.Thresholds;

namespace PuddingRuntimeTests.Operators;

/// <summary>
/// S2a 判据解析端口测试：三个默认值的<b>实际解析值</b>等价性、既有取值来源仍然生效、
/// 未注册 id fail-closed，以及三处消费点「端口未注入 ⇒ 退回既有常量」的<b>真实</b>退回路径
/// （用真实消费点跑边界值，而不是只测注入后的路径）。
/// <para>
/// 退回路径为什么必须真测：默认路径才是生产路径。只测注入后的路径，等于对「不配置 = 行为逐位不变」
/// 这句承诺没有证据。
/// </para>
/// </summary>
[TestClass]
public sealed class AcceptanceThresholdPolicyProviderTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 21, 8, 0, 0, TimeSpan.Zero);

    // ---------- ① 默认值等价性：不配置时三个 id 解析出的数值 ----------

    [TestMethod]
    public void DefaultProvider_WithoutAnyConfiguration_ResolvesNumbersEqualToExistingConstants()
    {
        var provider = new DefaultAcceptanceThresholdPolicyProvider();

        var permanent = provider.Resolve(AcceptanceThresholdPolicyIds.PermanentConfidence);
        var expiry = provider.Resolve(AcceptanceThresholdPolicyIds.SuggestedExpiryConfidence);
        var allowlist = provider.Resolve(AcceptanceThresholdPolicyIds.AllowlistProbability);

        // 逐个列出实际解析值（验收要的是原始数值，不是「看起来相等」）。
        Assert.AreEqual(
            0.90d, permanent.RequiredConfidence, 1e-12,
            $"permanent_confidence 实际解析值={permanent.RequiredConfidence:R}");
        Assert.AreEqual(
            0.95d, expiry.RequiredConfidence, 1e-12,
            $"suggested_expiry_confidence 实际解析值={expiry.RequiredConfidence:R}");
        Assert.AreEqual(
            0.90d, allowlist.RequiredConfidence, 1e-12,
            $"allowlist_probability 实际解析值={allowlist.RequiredConfidence:R}");

        // 等价性对齐「既有常量 / 既有默认」本身，而不是对齐字面量（防止将来常量变了这里悄悄漂移）。
        Assert.AreEqual(
            ToolCallClassifierPipelineOptions.DefaultPermanentConfidenceThreshold,
            permanent.RequiredConfidence,
            1e-12,
            "永久类门槛默认必须等于既有常量 DefaultPermanentConfidenceThreshold。");
        Assert.AreEqual(
            ClassificationRuleCurator.SuggestedExpiryConfidenceThreshold,
            expiry.RequiredConfidence,
            1e-12,
            "规则沉淀门槛默认必须等于既有常量 SuggestedExpiryConfidenceThreshold。");
        Assert.AreEqual(
            new ToolApprovalJevOptions().AllowlistProbabilityThreshold,
            allowlist.RequiredConfidence,
            1e-12,
            "白名单概率门槛默认必须等于既有 options 默认值。");

        // 判据标识与版本必须随解析结果可得（否则结果不可复现、不可审计）。
        Assert.AreEqual("tool_approval.permanent_confidence", permanent.PolicyId);
        Assert.AreEqual("classification.rule.suggested_expiry_confidence", expiry.PolicyId);
        Assert.AreEqual("tool_approval.jev.allowlist_probability", allowlist.PolicyId);
        Assert.AreEqual(AcceptanceThresholdPolicyIds.BuiltInVersion, permanent.Version);
        Assert.AreEqual(AcceptanceThresholdPolicyIds.BuiltInVersion, expiry.Version);
        Assert.AreEqual(AcceptanceThresholdPolicyIds.BuiltInVersion, allowlist.Version);

        // 永久类门槛自描述它约束哪两个标签；概率门槛不适用逐标签语义 ⇒ 空集。
        Assert.AreEqual(2, permanent.AppliesToLabels.Count);
        Assert.IsTrue(permanent.AppliesToLabels.Contains("allow_permanent"));
        Assert.IsTrue(permanent.AppliesToLabels.Contains("deny_permanent"));
        Assert.AreEqual(0, allowlist.AppliesToLabels.Count);
    }

    // ---------- ② 既有取值来源仍然生效（改的是取值来源，不是语义） ----------

    [TestMethod]
    public void DefaultProvider_ReadsExistingPipelineOptionsValue()
    {
        var options = new ToolCallClassifierPipelineOptions { PermanentConfidenceThreshold = 0.8d };
        var provider = new DefaultAcceptanceThresholdPolicyProvider(pipelineOptions: options);

        var permanent = provider.Resolve(AcceptanceThresholdPolicyIds.PermanentConfidence);

        Assert.AreEqual(0.8d, permanent.RequiredConfidence, 1e-12, "既有 options 实例是永久类门槛的单一事实源。");
    }

    [TestMethod]
    public void DefaultProvider_ReadsExistingJevConfigurationKey()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{ToolApprovalJevOptions.SectionName}:AllowlistProbabilityThreshold"] = "0.75",
            })
            .Build();
        var provider = new DefaultAcceptanceThresholdPolicyProvider(configuration);

        var allowlist = provider.Resolve(AcceptanceThresholdPolicyIds.AllowlistProbability);

        Assert.AreEqual(0.75d, allowlist.RequiredConfidence, 1e-12, "既有的白名单概率配置键必须继续生效。");
    }

    // ---------- ③ 未注册 id ⇒ fail-closed（不猜测默认值） ----------

    [TestMethod]
    public void Resolve_UnknownPolicyId_ThrowsKeyNotFound()
    {
        var provider = new DefaultAcceptanceThresholdPolicyProvider();

        Assert.ThrowsExactly<KeyNotFoundException>(
            () => provider.Resolve("tool_approval.unknown_policy"),
            "解析不到的判据必须抛错；静默兜底会让「门槛没生效」变成不可发现的事故。");
        Assert.ThrowsExactly<ArgumentException>(() => provider.Resolve("   "));
    }

    // ---------- ④ 管线：端口未注入 ⇒ 退回既有常量（真实边界行为） ----------

    [TestMethod]
    public async Task Pipeline_WithoutProvider_FallsBackToPermanentConfidenceConstant()
    {
        // 0.90 == 既有常量 ⇒ 含等达标、采纳为永久类（与既有 08 号用例同一判定）。
        var atThreshold = new ToolCallClassifierPipeline(
            [],
            new StubArbiter(0.90d),
            new InMemoryToolApprovalAuditStore());
        var adopted = await atThreshold.ClassifyAsync(ClassifierContext());
        Assert.AreEqual(
            ClassificationOutcome.AllowPermanent,
            adopted.Outcome,
            "端口未注入时门槛必须等于既有常量 0.90（含等达标）。");
        Assert.AreEqual(ToolCallClassifierPipeline.ReasonArbiterFinal, adopted.ReasonCode);

        // 低于常量一位 ⇒ 降级为单次类（降级动作与原因码不变）。
        var below = new ToolCallClassifierPipeline(
            [],
            new StubArbiter(0.8999d),
            new InMemoryToolApprovalAuditStore());
        var downgraded = await below.ClassifyAsync(ClassifierContext());
        Assert.AreEqual(ClassificationOutcome.AllowOnce, downgraded.Outcome);
        Assert.AreEqual(ToolCallClassifierPipeline.ReasonPermanentDowngraded, downgraded.ReasonCode);
        StringAssert.Contains(downgraded.Reason, "0.9", "降级理由里的门槛文本仍取既有常量值。");
    }

    [TestMethod]
    public async Task Pipeline_WithProvider_UsesResolvedThreshold()
    {
        var provider = new StubThresholdPolicyProvider(0.99d);
        var pipeline = new ToolCallClassifierPipeline(
            [],
            new StubArbiter(0.95d),
            new InMemoryToolApprovalAuditStore(),
            thresholdPolicyProvider: provider);

        var verdict = await pipeline.ClassifyAsync(ClassifierContext());

        Assert.AreEqual(1, provider.ResolveCount, "门槛应在构造期解析一次（与 options 同为不可变配置）。");
        Assert.AreEqual(
            ClassificationOutcome.AllowOnce,
            verdict.Outcome,
            "门槛被移动到 0.99 后 0.95 不再达标 ⇒ 证明注入生效、取值来源确实被替换。");
    }

    // ---------- ⑤ 策展器：端口未注入 ⇒ 退回既有常量 0.95（含缺失保守） ----------

    [TestMethod]
    public async Task Curator_WithoutProvider_FallsBackToSuggestedExpiryConstant()
    {
        var atThreshold = await CurateAsync(CreateCurator(null), 0.95d);
        Assert.IsNull(atThreshold.ExpiresAtUtc, "0.95 达既有门槛（含等）⇒ 不建议有效期。");

        var below = await CurateAsync(CreateCurator(null), 0.9499d);
        Assert.AreEqual(T0.AddDays(30), below.ExpiresAtUtc, "低于既有门槛 ⇒ 建议 30 天有效期。");

        var missing = await CurateAsync(CreateCurator(null), null);
        Assert.AreEqual(T0.AddDays(30), missing.ExpiresAtUtc, "缺失置信度 ⇒ 保守按低于门槛处理。");
    }

    [TestMethod]
    public async Task Curator_WithProvider_UsesResolvedThreshold()
    {
        var provider = new StubThresholdPolicyProvider(0.5d);
        var rule = await CurateAsync(CreateCurator(provider), 0.6d);

        Assert.AreEqual(1, provider.ResolveCount);
        Assert.IsNull(rule.ExpiresAtUtc, "门槛被移动到 0.5 后 0.6 达标 ⇒ 不建议有效期（注入生效）。");
    }

    // ---------- ⑥ 评审器：端口未注入 ⇒ 退回既有常量 0.90 ----------

    [TestMethod]
    public async Task JevReviewer_WithoutProvider_FallsBackToAllowlistProbabilityConstant()
    {
        var atThreshold = await ReviewAsync(new JevToolApprovalReviewer(new StubJevDecisionService(0.90d)));
        Assert.AreEqual(ToolApprovalDecision.Approved, atThreshold.Decision);
        Assert.IsFalse(atThreshold.RequiresHumanAuthorization, "0.90 == 既有常量（含等）⇒ 命中低风险包络。");

        var below = await ReviewAsync(new JevToolApprovalReviewer(new StubJevDecisionService(0.8999d)));
        Assert.IsTrue(below.RequiresHumanAuthorization, "低于既有常量 ⇒ 退出低风险包络，需人工授权。");

        var missing = await ReviewAsync(new JevToolApprovalReviewer(new StubJevDecisionService(null)));
        Assert.IsTrue(missing.RequiresHumanAuthorization, "缺失校准概率 ⇒ 保守不命中。");
        Assert.AreEqual(0, missing.AllowlistProposals.Count, "缺失概率 ⇒ 不产出提案（既有保守动作不变）。");
    }

    [TestMethod]
    public async Task JevReviewer_WithProvider_UsesResolvedThreshold()
    {
        var provider = new StubThresholdPolicyProvider(0.99d);
        var reviewer = new JevToolApprovalReviewer(
            new StubJevDecisionService(0.95d),
            thresholdPolicyProvider: provider);

        var result = await ReviewAsync(reviewer);

        Assert.AreEqual(1, provider.ResolveCount);
        Assert.IsTrue(
            result.RequiresHumanAuthorization,
            "门槛被移动到 0.99 后 0.95 不再命中包络 ⇒ 证明注入生效。");
    }

    // ---------- ⑦ 可观测面：判据 id + 版本随读数输出 ----------

    [TestMethod]
    public async Task StatusTool_WithoutProvider_ReportsBuiltInCriterion()
    {
        using var provider = BuildStatusHost();
        var thresholds = await ReadThresholdsAsync(provider);

        Assert.AreEqual(
            0.90d,
            thresholds.GetProperty("permanentConfidenceThreshold").GetDouble(),
            0.0001,
            "原数字字段保留且仍为既有 options 取值（未注入端口）。");
        Assert.AreEqual(
            AcceptanceThresholdPolicyIds.PermanentConfidence,
            thresholds.GetProperty("permanentConfidencePolicyId").GetString());
        Assert.AreEqual(
            AcceptanceThresholdPolicyIds.BuiltInVersion,
            thresholds.GetProperty("permanentConfidencePolicyVersion").GetInt32());
    }

    [TestMethod]
    public async Task StatusTool_WithProvider_ReportsResolvedCriterion()
    {
        var stub = new StubThresholdPolicyProvider(0.5d, version: 7);
        using var provider = BuildStatusHost(stub);

        var thresholds = await ReadThresholdsAsync(provider);

        Assert.AreEqual(
            0.5d,
            thresholds.GetProperty("permanentConfidenceThreshold").GetDouble(),
            0.0001,
            "注入端口后三个读数同源 ⇒ 报告值必须与生效值一致。");
        Assert.AreEqual(
            AcceptanceThresholdPolicyIds.PermanentConfidence,
            thresholds.GetProperty("permanentConfidencePolicyId").GetString());
        Assert.AreEqual(7, thresholds.GetProperty("permanentConfidencePolicyVersion").GetInt32());
    }

    // ---------- 测试辅助 ----------

    private static (ClassificationRuleCurator Curator, InMemoryToolApprovalAllowlistStore Store) CreateCurator(
        IAcceptanceThresholdPolicyProvider? provider)
    {
        var store = new InMemoryToolApprovalAllowlistStore();
        return (new ClassificationRuleCurator(store, new InMemoryToolApprovalAuditStore(), new FixedClock(), provider), store);
    }

    private static async Task<ToolApprovalAllowlistRule> CurateAsync(
        (ClassificationRuleCurator Curator, InMemoryToolApprovalAllowlistStore Store) target,
        double? confidence)
    {
        var verdict = new ClassificationVerdict
        {
            Outcome = ClassificationOutcome.AllowPermanent,
            Reason = "测试裁决",
            ClassifierId = "curator-under-test",
            PerOutcomeConfidence = confidence is null
                ? null
                : new Dictionary<string, double> { ["allow_permanent"] = confidence.Value },
        };

        var outcome = await target.Curator.CurateAsync(verdict, ClassifierContext());
        Assert.IsTrue(
            outcome.Applied,
            $"策展应落规则（实际 Degraded={outcome.Degraded}，原因={outcome.DegradeReason}）。");

        var stored = await target.Store.ListAsync();
        var rule = stored.SingleOrDefault(r => r.RuleId == outcome.RuleId);
        Assert.IsNotNull(rule, "落库结果必须可回查（幂等读改写的可验证性）。");
        return rule;
    }

    private static Task<ToolApprovalReviewResult> ReviewAsync(JevToolApprovalReviewer reviewer)
        => reviewer.ReviewAsync(
            new ToolApprovalTicketRequest
            {
                ToolId = "shell",
                Purpose = "unit test purpose",
                RequestedArgumentsJson = "{\"command\":\"git status\"}",
            },
            new ToolApprovalIdentity
            {
                WorkspaceId = "ws-test",
                SessionId = "s-1",
                AgentInstanceId = "agent-1",
                UserId = "user-1",
            },
            new ToolDescriptor { ToolId = "shell", Name = "shell", Description = "test descriptor" });

    private static ServiceProvider BuildStatusHost(IAcceptanceThresholdPolicyProvider? thresholdPolicyProvider = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IToolApprovalAllowlistStore>(new InMemoryToolApprovalAllowlistStore());
        services.AddSingleton<IToolApprovalAuditStore>(new InMemoryToolApprovalAuditStore());
        if (thresholdPolicyProvider is not null)
        {
            services.AddSingleton(thresholdPolicyProvider);
        }

        // 与生产同形态：故意不注册 Jev 决策端口 ⇒ 仲裁位为 fail-closed 占位。
        services.AddPuddingToolRegistry();
        return services.BuildServiceProvider();
    }

    private static async Task<JsonElement> ReadThresholdsAsync(ServiceProvider provider)
    {
        var tool = provider.GetServices<IPuddingTool>().OfType<ClassifierStatusTool>().Single();
        var result = await tool.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "call-classifier-status",
            ArgumentsJson = string.Empty,
            Context = new ToolExecutionContext
            {
                SessionId = "s-1",
                WorkspaceId = "ws-test",
                AgentInstanceId = "agent-1",
            },
        });

        Assert.IsTrue(result.Success, $"执行应成功：{result.Error}");
        using var doc = JsonDocument.Parse(result.Output);
        return doc.RootElement.GetProperty("thresholds").Clone();
    }

    private static ToolCallClassificationContext ClassifierContext()
        => new()
        {
            ToolId = "terminal_execute",
            CommandName = "git status",
            ArgumentsJson = "{\"k\":1}",
            WorkingDirectory = "E:/repo",
            Shell = "pwsh",
            WorkspaceId = "ws-test",
            SessionId = "sess-1",
            AgentInstanceId = "agent-1",
            UserId = "user-1",
            TargetResources = [],
        };

    /// <summary>判据端口假件：可指定解析出的门槛与版本，并统计解析次数。</summary>
    private sealed class StubThresholdPolicyProvider : IAcceptanceThresholdPolicyProvider
    {
        private readonly double _requiredConfidence;
        private readonly int _version;

        public StubThresholdPolicyProvider(
            double requiredConfidence,
            int version = AcceptanceThresholdPolicyIds.BuiltInVersion)
        {
            _requiredConfidence = requiredConfidence;
            _version = version;
        }

        public int ResolveCount { get; private set; }

        public AcceptanceThresholdPolicy Resolve(string policyId)
        {
            ResolveCount++;
            return AcceptanceThresholdPolicy.Create(policyId, _version, _requiredConfidence);
        }
    }

    /// <summary>仲裁分类器假件：固定返回永久类放行 + 指定逐分类可信度。</summary>
    private sealed class StubArbiter : IToolCallClassifier
    {
        private readonly double _allowPermanentConfidence;

        public StubArbiter(double allowPermanentConfidence) => _allowPermanentConfidence = allowPermanentConfidence;

        public string ClassifierId => "arbiter-stub";

        public Task<ClassificationVerdict> ClassifyAsync(
            ToolCallClassificationContext context,
            CancellationToken ct = default)
            => Task.FromResult(new ClassificationVerdict
            {
                Outcome = ClassificationOutcome.AllowPermanent,
                Reason = "stub：建议永久放行",
                ClassifierId = ClassifierId,
                PerOutcomeConfidence = new Dictionary<string, double>
                {
                    ["allow_permanent"] = _allowPermanentConfidence,
                },
            });
    }

    /// <summary>决策端口假件：approve + 低风险 + session 范围 + 可指定校准概率（null = 缺答案）。</summary>
    private sealed class StubJevDecisionService : IJevDecisionService
    {
        private readonly double? _allowlist;

        public StubJevDecisionService(double? allowlist) => _allowlist = allowlist;

        public Task<JevDecisionResult> DecideAsync(JevDecisionRequest request, CancellationToken ct = default)
        {
            var answers = new Dictionary<string, JevAnswer>
            {
                ["decision"] = new JevAnswer { Name = "decision", Type = "choice", Choice = "approve" },
                ["risk"] = new JevAnswer { Name = "risk", Type = "score", Score = 1d },
                ["scope"] = new JevAnswer { Name = "scope", Type = "choice", Choice = "session" },
            };
            if (_allowlist is { } value)
            {
                answers["allowlist"] = new JevAnswer { Name = "allowlist", Type = "noul", Noul = value };
            }

            return Task.FromResult(new JevDecisionResult { Model = "stub-model", Answers = answers });
        }
    }

    /// <summary>固定时钟：时间只经注入 TimeProvider 获取（与既有策展器测试同一形状）。</summary>
    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => T0;
    }
}
