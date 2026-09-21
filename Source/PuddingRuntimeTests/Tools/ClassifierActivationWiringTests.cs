using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PuddingCode.Abstractions;
using PuddingCode.Classification;
using PuddingCode.Tools;
using PuddingRuntime.Classification;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

/// <summary>
/// S3c-1（分类器审批链路「激活准备」）的离线接线与契约测试（零网络）。
/// <para>
/// 逐条锁定交付契约：
/// W1 DI 能解析 <see cref="IToolCallClassifier"/> 且为管线形态（规则命中 ⇒ 零仲裁调用）；
/// W2 <c>Reviewer=classifier</c> 时选中 <see cref="ClassifierToolApprovalReviewer"/>；
/// W3 默认 <see cref="ToolApprovalRuntimeOptions.Reviewer"/> **已翻转**（= <c>classifier</c>，原 llm；
///   回退方式：配置 <c>ToolApproval:Reviewer=llm</c>，无需重新构建）；
/// W4 <c>llm</c>/<c>jev</c> 显式选择行为不变；
/// W5 未知 Reviewer 取值仍 fail-closed；
/// W6 提案 <see cref="ToolApprovalAllowlistProposal.Effect"/> 结构化承载（AllowPermanent⇒Allow / DenyPermanent⇒Deny）；
/// W7 契约可空性（既有构造 <see cref="ToolApprovalAllowlistProposal"/> 不赋值 ⇒ <c>Effect</c> 为 null）；
/// W8 Jev 未注册/未启用时接线优雅（不抛，分类器返回 Unknown）。
/// </para>
/// </summary>
[TestClass]
public sealed class ClassifierActivationWiringTests
{
    private const string TestWorkspace = "ws-wiring";
    private const string DefaultShellArgs = """{"command":"dotnet test"}""";

    // ---------- W1 ① DI 解析 IToolCallClassifier（管线形态；规则命中 ⇒ 零仲裁调用） ----------

    [TestMethod]
    public async Task W1_DiResolvesPipelineClassifier_RuleHitBypassesArbiter()
    {
        var jev = new FakeJevDecisionService();
        var services = new ServiceCollection();
        services.AddSingleton<IJevDecisionService>(jev);
        services.AddPuddingToolRegistry();

        using var provider = services.BuildServiceProvider();
        var classifier = provider.GetRequiredService<IToolCallClassifier>();
        Assert.IsInstanceOfType<ToolCallClassifierPipeline>(classifier, "DI 解析出的必须是管线形态。");

        var allowlist = provider.GetRequiredService<IToolApprovalAllowlistStore>();
        await allowlist.SaveAsync(Rule("rule-allow-w1", ToolApprovalRuleEffect.Allow));

        var verdict = await classifier.ClassifyAsync(Context());

        Assert.AreEqual(ClassificationOutcome.AllowOnce, verdict.Outcome);
        Assert.AreEqual(
            ToolCallClassifierPipeline.ReasonAllowFastPath,
            verdict.ReasonCode,
            "allow 规则命中必须走管线快路径。");
        Assert.AreEqual(0, jev.CallCount, "规则命中 ⇒ 终局放行，必须零仲裁调用（管线形态的间接证明）。");
    }

    // ---------- W2 ② Reviewer=classifier ⇒ ClassifierToolApprovalReviewer ----------

    [TestMethod]
    public void W2_ClassifierReviewerSelected_WhenExplicitlyConfigured()
    {
        var services = new ServiceCollection();
        services.Configure<ToolApprovalRuntimeOptions>(options =>
            options.Reviewer = ToolApprovalRuntimeOptions.ClassifierReviewer);
        services.AddPuddingToolRegistry();

        using var provider = services.BuildServiceProvider();
        var reviewer = provider.GetRequiredService<IToolApprovalReviewer>();

        Assert.IsInstanceOfType<ClassifierToolApprovalReviewer>(reviewer);
    }

    // ---------- W3 ③ 默认 Reviewer 已翻转（S3c 激活切片，父级独立提交） ----------

    [TestMethod]
    public void W3_DefaultReviewerOption_IsClassifier_AfterActivation()
    {
        // 2026-09-21 父级独立提交完成翻转：默认值 = classifier（原 llm）。
        // 回退方式是配置 ToolApproval:Reviewer=llm，无需重新构建。
        Assert.AreEqual("classifier", new ToolApprovalRuntimeOptions().Reviewer);

        var services = new ServiceCollection();
        services.AddSingleton<IJevDecisionService>(new FakeJevDecisionService());
        services.AddPuddingToolRegistry();

        using var provider = services.BuildServiceProvider();

        Assert.IsInstanceOfType<ClassifierToolApprovalReviewer>(provider.GetRequiredService<IToolApprovalReviewer>());
    }

    // ---------- W4 ④ llm / jev 显式选择行为不变 ----------

    [TestMethod]
    public void W4a_ExplicitLlmReviewer_StillResolvesLlmReviewer()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IJevDecisionService>(new FakeJevDecisionService());
        services.Configure<ToolApprovalRuntimeOptions>(options => options.Reviewer = "llm");
        services.AddPuddingToolRegistry();

        using var provider = services.BuildServiceProvider();

        Assert.IsInstanceOfType<LlmToolApprovalReviewer>(provider.GetRequiredService<IToolApprovalReviewer>());
    }

    [TestMethod]
    public void W4b_ExplicitJevReviewer_StillResolvesJevReviewer()
    {
        var services = new ServiceCollection();
        services.Configure<ToolApprovalRuntimeOptions>(options => options.Reviewer = "jev");
        services.AddPuddingToolRegistry();

        using var provider = services.BuildServiceProvider();

        Assert.IsInstanceOfType<JevToolApprovalReviewer>(provider.GetRequiredService<IToolApprovalReviewer>());
    }

    // ---------- W5 ⑤ 未知 Reviewer 取值仍 fail-closed ----------

    [TestMethod]
    public void W5_UnknownReviewerValue_StillFailsClosed()
    {
        var services = new ServiceCollection();
        services.Configure<ToolApprovalRuntimeOptions>(options => options.Reviewer = "bogus");
        services.AddPuddingToolRegistry();

        using var provider = services.BuildServiceProvider();

        var ex = Assert.ThrowsExactly<InvalidOperationException>(
            () => provider.GetRequiredService<IToolApprovalReviewer>());
        StringAssert.Contains(ex.Message, "not supported");
    }

    // ---------- W6 ⑥ 提案 Effect 结构化承载 ----------

    [TestMethod]
    public async Task W6a_AllowPermanentProposal_CarriesStructuredEffectAllow()
    {
        var reviewer = new ClassifierToolApprovalReviewer(
            new FakeClassifier(Verdict(
                ClassificationOutcome.AllowPermanent,
                confidence: Confidence(("allow_permanent", 0.97)))));

        var result = await reviewer.ReviewAsync(TicketRequest(), Identity(), Descriptor());

        var proposal = result.AllowlistProposals.Single();
        Assert.AreEqual(
            ToolApprovalRuleEffect.Allow,
            proposal.Effect,
            "AllowPermanent ⇒ 提案 Effect 必须为 Allow（结构化承载）。");
    }

    [TestMethod]
    public async Task W6b_DenyPermanentProposal_CarriesStructuredEffectDeny()
    {
        var reviewer = new ClassifierToolApprovalReviewer(
            new FakeClassifier(Verdict(
                ClassificationOutcome.DenyPermanent,
                confidence: Confidence(("deny_permanent", 0.99)))));

        var result = await reviewer.ReviewAsync(TicketRequest(), Identity(), Descriptor());

        var proposal = result.AllowlistProposals.Single();
        Assert.AreEqual(
            ToolApprovalRuleEffect.Deny,
            proposal.Effect,
            "DenyPermanent ⇒ 提案 Effect 必须为 Deny（结构化承载）。");
    }

    // ---------- W7 ⑦ 契约可空性：既有构造调用不破 ----------

    [TestMethod]
    public void W7_LegacyProposalConstruction_EffectRemainsNull()
    {
        // append-only 契约：既有调用点不赋值 ⇒ 可编译且 Effect 为 null（null 表示「未声明」，策展器不得猜测为 allow）。
        var legacy = new ToolApprovalAllowlistProposal
        {
            ToolId = "shell",
            Command = "dotnet",
            ArgumentsJson = DefaultShellArgs,
            Reason = "legacy construction without Effect",
        };

        Assert.IsNull(legacy.Effect);
        Assert.AreEqual("legacy construction without Effect", legacy.Reason);
    }

    // ---------- W8 ⑧ Jev 未注册 / 未启用：接线优雅，分类器返回 Unknown ----------

    [TestMethod]
    public async Task W8a_MissingJevRegistration_PipelineStillResolvesAndReturnsUnknown()
    {
        // 不注册 IJevDecisionService：DI 解析不得抛异常（仲裁位退化为 fail-closed 占位）。
        var services = new ServiceCollection();
        services.AddPuddingToolRegistry();

        using var provider = services.BuildServiceProvider();
        var classifier = provider.GetRequiredService<IToolCallClassifier>();

        // 空规则库 ⇒ 规则分类器 Unknown ⇒ 仲裁（占位） ⇒ 管线转 Unknown（arbiter_unavailable）。
        var verdict = await classifier.ClassifyAsync(Context(toolId: "file_read", command: "no-such-command"));

        Assert.AreEqual(ClassificationOutcome.Unknown, verdict.Outcome, "仲裁占位不得放行/拒绝。");
        Assert.AreEqual(
            ToolCallClassifierPipeline.ReasonArbiterUnavailable,
            verdict.ReasonCode,
            "仲裁不可用必须收敛到管线的 arbiter_unavailable 原因码。");
    }

    [TestMethod]
    public async Task W8b_JevRegisteredButDisabled_PipelineGracefulUnknown()
    {
        var jev = new FakeJevDecisionService();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ToolApproval:Jev:Enabled"] = "false" })
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IJevDecisionService>(jev);
        services.AddPuddingToolRegistry(configuration);

        using var provider = services.BuildServiceProvider();
        var classifier = provider.GetRequiredService<IToolCallClassifier>();

        var verdict = await classifier.ClassifyAsync(Context(toolId: "file_read", command: "no-such-command"));

        Assert.AreEqual(ClassificationOutcome.Unknown, verdict.Outcome, "Jev 禁用时分类器必须返回 Unknown（既有契约）。");
        Assert.AreEqual(0, jev.CallCount, "禁用状态下不得发起 Jev 调用。");
    }

    // ---------- 测试基建 ----------

    private static ToolApprovalAllowlistRule Rule(string ruleId, ToolApprovalRuleEffect effect) => new()
    {
        RuleId = ruleId,
        WorkspaceId = TestWorkspace,
        ToolId = "terminal_execute",
        Command = "git status",
        ArgumentsJson = null,
        Source = ToolApprovalAllowlistRuleSource.Human,
        Status = ToolApprovalAllowlistRuleStatus.Enabled,
        Effect = effect,
        CreatedAtUtc = DateTimeOffset.Parse("2026-09-21T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
        WorkingDirectory = "E:/repo",
        Shell = "pwsh",
    };

    private static ToolCallClassificationContext Context(
        string command = "git status",
        string toolId = "terminal_execute")
        => new()
        {
            ToolId = toolId,
            CommandName = command,
            ArgumentsJson = null,
            WorkingDirectory = "E:/repo",
            Shell = "pwsh",
            WorkspaceId = TestWorkspace,
            SessionId = "sess-w1",
            AgentInstanceId = "agent-w1",
            UserId = "user-w1",
        };

    private static ClassificationVerdict Verdict(
        ClassificationOutcome outcome,
        string reason = "unit verdict",
        IReadOnlyDictionary<string, double>? confidence = null,
        string classifierId = "fake-classifier",
        string? model = "fake-model-1",
        string? reasonCode = null)
        => new()
        {
            Outcome = outcome,
            Reason = reason,
            PerOutcomeConfidence = confidence,
            ClassifierId = classifierId,
            ClassifierModel = model,
            ReasonCode = reasonCode,
        };

    private static IReadOnlyDictionary<string, double> Confidence(params (string Key, double Value)[] entries)
        => entries.ToDictionary(entry => entry.Key, entry => entry.Value);

    private static ToolApprovalTicketRequest TicketRequest() => new()
    {
        ToolId = "shell",
        CommandName = "dotnet",
        Purpose = "unit test purpose",
        RequestedArgumentsJson = DefaultShellArgs,
    };

    private static ToolApprovalIdentity Identity() => new()
    {
        WorkspaceId = "ws-wiring",
        SessionId = "sess-w1",
        AgentInstanceId = "agent-w1",
        UserId = "user-w1",
    };

    private static ToolDescriptor Descriptor() => new()
    {
        ToolId = "shell",
        Name = "shell",
        Description = "test descriptor",
    };

    /// <summary>最小 Jev 决策端口 fake：仅计数，不产出有效答案（本文件不依赖其结论）。</summary>
    private sealed class FakeJevDecisionService : IJevDecisionService
    {
        public int CallCount { get; private set; }

        public Task<JevDecisionResult> DecideAsync(JevDecisionRequest request, CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(new JevDecisionResult
            {
                Model = "fake-jev",
                Answers = new Dictionary<string, JevAnswer>(),
            });
        }
    }

    /// <summary>单结论分类器 stub（零网络）。</summary>
    private sealed class FakeClassifier(ClassificationVerdict verdict) : IToolCallClassifier
    {
        public string ClassifierId => "fake-classifier";

        public Task<ClassificationVerdict> ClassifyAsync(
            ToolCallClassificationContext context,
            CancellationToken ct = default)
            => Task.FromResult(verdict);
    }
}
