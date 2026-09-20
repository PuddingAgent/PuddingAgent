using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PuddingCode.Abstractions;
using PuddingCode.Tools;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

/// <summary>
/// <see cref="JevToolApprovalReviewer"/> 的离线单元测试（不触网，Jev 端口全部用 fake）。
/// <para>
/// 逐条锁定方案安全不变量 I1–I6（Docs/Features/Jev自动审批与白名单自学习改造方案.md §2）、
/// §3.1 决策映射表、以及组合根开关（§3.2）。既有审批测试不在此重复。
/// </para>
/// </summary>
[TestClass]
public sealed class JevToolApprovalReviewerTests
{
    private const string DefaultShellArgs = """{"command":"dotnet test"}""";

    // ---------- I1：确定性 deny 优先，绝不调用 Jev ----------

    [TestMethod]
    public async Task I1_DenyRule_DeniesImmediately_WithoutCallingJev()
    {
        var jev = new FakeJevDecisionService(JevResult());
        var reviewer = new JevToolApprovalReviewer(jev);

        var result = await reviewer.ReviewAsync(
            TicketRequest("""{"command":"rm -rf temp/cache"}"""),
            Identity(),
            Descriptor());

        Assert.AreEqual(ToolApprovalDecision.Denied, result.Decision);
        Assert.AreEqual(JevToolApprovalReviewer.ReasonCodeSkippedDenyRule, result.ReasonCode);
        Assert.AreEqual(0, jev.CallCount, "确定性 deny 命中时绝不允许触发 Jev 调用。");
    }

    // ---------- I2：Jev 不可用/超时/坏答案 ⇒ DeferredDependency（fail-closed） ----------

    [TestMethod]
    public async Task I2_JevPortNotRegistered_ReturnsDeferredDependency()
    {
        var result = await new JevToolApprovalReviewer().ReviewAsync(TicketRequest(), Identity(), Descriptor());

        Assert.AreEqual(ToolApprovalDecision.DeferredDependency, result.Decision);
        Assert.AreEqual(JevToolApprovalReviewer.ReasonCodeUnavailable, result.ReasonCode);
        Assert.IsTrue(result.RequiresHumanAuthorization);
    }

    [TestMethod]
    public async Task I2_JevThrows_ReturnsDeferredDependency()
    {
        var jev = new FakeJevDecisionService(
            exceptionToThrow: new JevDecisionException(JevDecisionCodes.NotConfigured, "endpoint not configured"));
        var reviewer = new JevToolApprovalReviewer(jev);

        var result = await reviewer.ReviewAsync(TicketRequest(), Identity(), Descriptor());

        Assert.AreEqual(ToolApprovalDecision.DeferredDependency, result.Decision);
        Assert.AreEqual(JevToolApprovalReviewer.ReasonCodeUnavailable, result.ReasonCode);
        Assert.AreNotEqual(ToolApprovalDecision.Approved, result.Decision, "依赖失败不得折叠为 Approved。");
        Assert.AreNotEqual(ToolApprovalDecision.NeedHuman, result.Decision, "依赖失败不得折叠为 NeedHuman。");
    }

    [TestMethod]
    public async Task I2_MissingDecisionAnswer_ReturnsDeferredDependency()
    {
        var jev = new FakeJevDecisionService(JevResult(decision: null));
        var reviewer = new JevToolApprovalReviewer(jev);

        var result = await reviewer.ReviewAsync(TicketRequest(), Identity(), Descriptor());

        Assert.AreEqual(ToolApprovalDecision.DeferredDependency, result.Decision);
        Assert.AreEqual(JevToolApprovalReviewer.ReasonCodeInvalidResponse, result.ReasonCode);
    }

    [TestMethod]
    public async Task I2_UnrecognizedDecisionChoice_ReturnsDeferredDependency()
    {
        var jev = new FakeJevDecisionService(JevResult(decision: "maybe"));
        var reviewer = new JevToolApprovalReviewer(jev);

        var result = await reviewer.ReviewAsync(TicketRequest(), Identity(), Descriptor());

        Assert.AreEqual(ToolApprovalDecision.DeferredDependency, result.Decision);
        Assert.AreEqual(JevToolApprovalReviewer.ReasonCodeInvalidResponse, result.ReasonCode);
    }

    [TestMethod]
    public async Task I2_JevDeadline_ReturnsDeferredDependency()
    {
        var jev = new FakeJevDecisionService(handler: async (_, innerCt) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, innerCt);
            return null!;
        });
        var reviewer = new JevToolApprovalReviewer(
            jev,
            configuration: Config(("ToolApproval:Jev:ReviewTimeoutSeconds", "1")));

        // 调用者令牌不取消：超时必须由评审器自身 deadline 触发（与 LLM 评审器语义一致）。
        var result = await reviewer.ReviewAsync(TicketRequest(), Identity(), Descriptor(), CancellationToken.None);

        Assert.AreEqual(ToolApprovalDecision.DeferredDependency, result.Decision);
        Assert.AreEqual(JevToolApprovalReviewer.ReasonCodeUnavailable, result.ReasonCode);
    }

    [TestMethod]
    public async Task I2_DisabledByConfiguration_ReturnsDeferredDependency_WithoutCallingJev()
    {
        var jev = new FakeJevDecisionService(JevResult());
        var reviewer = new JevToolApprovalReviewer(
            jev,
            configuration: Config(("ToolApproval:Jev:Enabled", "false")));

        var result = await reviewer.ReviewAsync(TicketRequest(), Identity(), Descriptor());

        Assert.AreEqual(ToolApprovalDecision.DeferredDependency, result.Decision);
        Assert.AreEqual(JevToolApprovalReviewer.ReasonCodeUnavailable, result.ReasonCode);
        Assert.AreEqual(0, jev.CallCount);
    }

    // ---------- I3：白名单提案只允许精确匹配 ----------

    [DataTestMethod]
    [DataRow("dotnet build & dir bin")]
    [DataRow("dotnet build | tee-object log.txt")]
    [DataRow("dotnet build ; dir bin")]
    [DataRow("dotnet build > out.txt")]
    [DataRow("dotnet build < in.txt")]
    [DataRow("echo $HOME")]
    [DataRow("echo $IFS expanded")]
    [DataRow("echo `cmd`")]
    [DataRow("echo (sub)")]
    [DataRow("echo {brace}")]
    [DataRow("dir *.cs")]
    [DataRow("dir a?b")]
    [DataRow("dir ~")]
    [DataRow("dir a\\b")]
    [DataRow("dotnet build\necho done")]
    public async Task I3_MetacharacterCommand_ProducesNoAllowlistProposal(string command)
    {
        var jev = new FakeJevDecisionService(JevResult());
        var reviewer = new JevToolApprovalReviewer(jev);
        var argumentsJson = JsonSerializer.Serialize(new { command });

        var result = await reviewer.ReviewAsync(TicketRequest(argumentsJson), Identity(), Descriptor());

        Assert.AreEqual(ToolApprovalDecision.Approved, result.Decision, "用例前提：Jev 已 approve，否则测不到提案门槛。");
        Assert.AreEqual(1, jev.CallCount, "用例前提：命令未命中确定性 deny，Jev 必须被调用过。");
        Assert.AreEqual(0, result.AllowlistProposals.Count, $"含元字符的命令不得产出白名单提案：{command}");
    }

    // ---------- I4：低置信/非 approve 不自动放行白名单 ----------

    [TestMethod]
    public async Task I4_LowAllowlistProbability_ProducesNoProposal()
    {
        var jev = new FakeJevDecisionService(JevResult(allowlist: 0.89d));
        var reviewer = new JevToolApprovalReviewer(jev);

        var result = await reviewer.ReviewAsync(TicketRequest(), Identity(), Descriptor());

        Assert.AreEqual(ToolApprovalDecision.Approved, result.Decision);
        Assert.AreEqual(0, result.AllowlistProposals.Count, "低于阈值的校准概率不得产出提案。");
    }

    [TestMethod]
    public async Task I4_AtThreshold_ProducesSingleExactProposal()
    {
        var jev = new FakeJevDecisionService(JevResult(allowlist: 0.90d));
        var reviewer = new JevToolApprovalReviewer(jev);

        var result = await reviewer.ReviewAsync(TicketRequest(), Identity(), Descriptor());

        var proposal = result.AllowlistProposals.Single();
        Assert.AreEqual("shell", proposal.ToolId);
        Assert.AreEqual("dotnet test", proposal.Command);
        StringAssert.Contains(proposal.ArgumentsJson, "dotnet test");
    }

    [TestMethod]
    public async Task I4_NonApproveDecision_NeverProducesProposal()
    {
        var denied = await new JevToolApprovalReviewer(new FakeJevDecisionService(JevResult(
                decision: "deny",
                allowlist: 0.99d)))
            .ReviewAsync(TicketRequest(), Identity(), Descriptor());
        Assert.AreEqual(ToolApprovalDecision.Denied, denied.Decision);
        Assert.AreEqual(0, denied.AllowlistProposals.Count, "deny 判定不得产出提案，即使概率再高。");

        var needHuman = await new JevToolApprovalReviewer(new FakeJevDecisionService(JevResult(
                decision: "need_human",
                allowlist: 0.99d)))
            .ReviewAsync(TicketRequest(), Identity(), Descriptor());
        Assert.AreEqual(ToolApprovalDecision.NeedHuman, needHuman.Decision);
        Assert.AreEqual(0, needHuman.AllowlistProposals.Count);
    }

    // ---------- I5：提案必须带 provenance 且落库可撤销 ----------

    [TestMethod]
    public async Task I5_AllowlistProposal_CarriesProvenance_AndRulePersistsIt()
    {
        var jev = new FakeJevDecisionService(JevResult(model: "jev-9.9.9-test", risk: 1d, allowlist: 0.97d));
        var reviewer = new JevToolApprovalReviewer(jev);

        var review = await reviewer.ReviewAsync(TicketRequest(), Identity(), Descriptor());

        Assert.AreEqual("jev-9.9.9-test", review.ReviewerModel);
        var proposal = review.AllowlistProposals.Single();
        StringAssert.Contains(proposal.Reason, "jev-9.9.9-test", "提案 Reason 必须携带 Jev 模型。");
        StringAssert.Contains(proposal.Reason, "0.970", "提案 Reason 必须携带校准概率。");
        StringAssert.Contains(proposal.Reason, "risk=1.0", "提案 Reason 必须携带风险等级。");

        // 落库链路：approved ticket ⇒ allowlist rule 带 provenance（Status 可禁用 ⇒ 可撤销）。
        var allowlistStore = new InMemoryToolApprovalAllowlistStore();
        var service = new InMemoryToolApprovalService(
            reviewer,
            new InMemoryToolApprovalTicketStore(),
            allowlistStore,
            new InMemoryToolApprovalAuditStore());

        var submit = await service.SubmitAsync(TicketRequest(), Identity(), Descriptor());
        Assert.AreEqual(ToolApprovalDecision.Approved, submit.Decision);

        // 注：InMemoryToolApprovalAllowlistStore 构造时预置内置规则，ListAsync 会一并返回，需按来源过滤。
        var rule = (await allowlistStore.ListAsync())
            .Single(static r => r.Source == ToolApprovalAllowlistRuleSource.AuditAgent);
        Assert.AreEqual(ToolApprovalAllowlistRuleSource.AuditAgent, rule.Source);
        Assert.AreEqual(submit.TicketId, rule.ApprovalTicketId);
        Assert.AreEqual("agent-1", rule.ApprovedByAgentInstanceId);
        Assert.AreEqual("user-1", rule.ApprovedByUserId);
        Assert.AreEqual(ToolApprovalAllowlistRuleStatus.Enabled, rule.Status);
        Assert.AreNotEqual(default, rule.CreatedAtUtc);
        StringAssert.Contains(rule.Reason, "jev-9.9.9-test");
    }

    // ---------- I6：白名单命中不再调用 Jev ----------

    [TestMethod]
    public async Task I6_AllowlistHit_ReturnsApprovedBeforeReviewerIsCalled()
    {
        var jev = new FakeJevDecisionService(JevResult());
        var service = new InMemoryToolApprovalService(
            new JevToolApprovalReviewer(jev),
            new InMemoryToolApprovalTicketStore(),
            new InMemoryToolApprovalAllowlistStore(),
            new InMemoryToolApprovalAuditStore());

        var submit = await service.SubmitAsync(TicketRequest(), Identity(), Descriptor());
        Assert.AreEqual(ToolApprovalDecision.Approved, submit.Decision);
        Assert.AreEqual(1, jev.CallCount, "前提：提交工单时恰好调用了一次 Jev。");

        var check = await service.CheckAsync(
            new ToolApprovalExecutionRequest
            {
                WorkspaceId = "ws-test",
                SessionId = "s-1",
                AgentInstanceId = "agent-1",
                UserId = "user-1",
                ToolId = "shell",
                ActualArgumentsJson = DefaultShellArgs,
            },
            Descriptor());

        Assert.IsTrue(check.IsApproved);
        Assert.IsNotNull(check.AllowlistRuleId);
        Assert.AreEqual("AuditAgent", check.ApprovalSource);
        Assert.AreEqual(1, jev.CallCount, "白名单命中时不得再调用 Jev（快速通道必须先于评审器）。");
    }

    // ---------- §3.1 决策映射表 ----------

    [TestMethod]
    public async Task Map_Approve_InLowRiskEnvelope_GrantsWithoutHuman()
    {
        var result = await new JevToolApprovalReviewer(new FakeJevDecisionService(JevResult()))
            .ReviewAsync(TicketRequest(), Identity(), Descriptor());

        Assert.AreEqual(ToolApprovalDecision.Approved, result.Decision);
        Assert.IsFalse(result.RequiresHumanAuthorization);
        Assert.AreEqual(ToolApprovalScope.Session, result.AllowedScope);
        Assert.AreEqual(JevToolApprovalReviewer.ReasonCodeApproved, result.ReasonCode);
        Assert.AreEqual("jev-test-1", result.ReviewerModel);
    }

    [TestMethod]
    public async Task Map_Approve_OutsideLowRiskEnvelope_RequiresHuman()
    {
        // risk=3：超出低风险包络 ⇒ 仍 Approved，但必须人工确认。
        var highRisk = await new JevToolApprovalReviewer(new FakeJevDecisionService(JevResult(risk: 3d)))
            .ReviewAsync(TicketRequest(), Identity(), Descriptor());
        Assert.AreEqual(ToolApprovalDecision.Approved, highRisk.Decision);
        Assert.IsTrue(highRisk.RequiresHumanAuthorization);

        // scope=once：同样不满足无人工包络。
        var onceScope = await new JevToolApprovalReviewer(new FakeJevDecisionService(JevResult(scope: "once")))
            .ReviewAsync(TicketRequest(), Identity(), Descriptor());
        Assert.AreEqual(ToolApprovalDecision.Approved, onceScope.Decision);
        Assert.IsTrue(onceScope.RequiresHumanAuthorization);
    }

    [TestMethod]
    public async Task Map_NeedHuman_And_Deny_PreserveTypedDecision()
    {
        var needHuman = await new JevToolApprovalReviewer(new FakeJevDecisionService(JevResult(decision: "need_human")))
            .ReviewAsync(TicketRequest(), Identity(), Descriptor());
        Assert.AreEqual(ToolApprovalDecision.NeedHuman, needHuman.Decision);
        Assert.IsTrue(needHuman.RequiresHumanAuthorization);
        Assert.AreEqual(JevToolApprovalReviewer.ReasonCodeNeedHuman, needHuman.ReasonCode);

        var denied = await new JevToolApprovalReviewer(new FakeJevDecisionService(JevResult(decision: "deny")))
            .ReviewAsync(TicketRequest(), Identity(), Descriptor());
        Assert.AreEqual(ToolApprovalDecision.Denied, denied.Decision);
        Assert.IsFalse(denied.RequiresHumanAuthorization);
        Assert.AreEqual(JevToolApprovalReviewer.ReasonCodeDenied, denied.ReasonCode);
    }

    // ---------- State 组装 ----------

    [TestMethod]
    public async Task State_ArgumentsJson_TruncatedToConfiguredBudget()
    {
        var longCommand = "dotnet build " + new string('x', 200);
        var jev = new FakeJevDecisionService(JevResult());
        var reviewer = new JevToolApprovalReviewer(
            jev,
            configuration: Config(("ToolApproval:Jev:StateTruncateBytes", "64")));

        var result = await reviewer.ReviewAsync(
            TicketRequest(JsonSerializer.Serialize(new { command = longCommand })),
            Identity(),
            Descriptor());

        var state = Assert.IsInstanceOfType<JsonObject>(jev.LastRequest!.State);
        var stateArguments = state["argumentsJson"]!.GetValue<string>();
        Assert.IsTrue(
            Encoding.UTF8.GetByteCount(stateArguments) <= 64,
            $"state.argumentsJson 必须按配置截断：实际 {Encoding.UTF8.GetByteCount(stateArguments)} 字节。");

        // 提案命令必须来自完整原文，而不是截断摘要。
        var proposal = result.AllowlistProposals.Single();
        Assert.AreEqual(longCommand, proposal.Command);
    }

    [TestMethod]
    public async Task Questions_Protocol_FixedFourQuestions()
    {
        var jev = new FakeJevDecisionService(JevResult());
        await new JevToolApprovalReviewer(jev).ReviewAsync(TicketRequest(), Identity(), Descriptor());

        var questions = jev.LastRequest!.Questions;
        Assert.AreEqual(4, questions.Count);
        CollectionAssert.AreEquivalent(
            new[] { "decision", "risk", "scope", "allowlist" },
            questions.Select(static question => question.Name).ToArray());
        Assert.AreEqual(JevQuestionType.Choice, questions[0].Type);
        Assert.AreEqual(JevQuestionType.Score, questions[1].Type);
        Assert.AreEqual(JevQuestionType.Choice, questions[2].Type);
        Assert.AreEqual(JevQuestionType.Noul, questions[3].Type);
    }

    // ---------- §3.2 组合根开关 ----------

    /// <summary>
    /// 组合根「未显式配置」时的自动选择分支：Jev 端口已注册 ⇒ jev。
    /// 注意：<b>必须显式把 <see cref="ToolApprovalRuntimeOptions.Reviewer"/> 置空</b>才会进入该分支——
    /// 选项类当前默认值是 "llm"，见下一个测试。
    /// </summary>
    [TestMethod]
    public void ProductionRegistry_AutoSelectsJevReviewer_WhenJevPortIsRegisteredAndReviewerUnset()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IJevDecisionService>(new FakeJevDecisionService(JevResult()));
        services.Configure<ToolApprovalRuntimeOptions>(options => options.Reviewer = null);
        services.AddPuddingToolRegistry();

        using var provider = services.BuildServiceProvider();
        var reviewer = provider.GetRequiredService<IToolApprovalReviewer>();

        Assert.IsInstanceOfType<JevToolApprovalReviewer>(reviewer);
    }

    /// <summary>
    /// 钉住**已翻转**的默认值：未配置时走分类器审阅器（<c>classifier</c>）。
    /// <para>
    /// 保留 llm 的原始理由（v1 形态 Jev 评审器缺「四选一 + 逐分类可信度 + 健康面」）已消除：
    /// v2 分类器管线 + 审批门户 + 规则策展已落地（方案 v2 §14.14），且翻转前的三道前置均已实证
    /// （生产实现清单盘点 / Jev 真实联网可用 / **真链路解析一致性**）。
    /// 2026-09-21 父级在**独立提交**中翻转默认值（原 llm → classifier），本测试同步改写。
    /// 本测试的作用依旧是让「再改默认值」必须是有意识的改动：改的人会看到这条注释。
    /// </para>
    /// <para>
    /// 回退方式（无需重新构建）：配置 <c>ToolApproval:Reviewer=llm</c>（或 <c>jev</c>）。
    /// </para>
    /// </summary>
    [TestMethod]
    public void ProductionRegistry_DefaultReviewerOption_IsClassifier_AfterActivation()
    {
        Assert.AreEqual("classifier", new ToolApprovalRuntimeOptions().Reviewer);

        var services = new ServiceCollection();
        services.AddSingleton<IJevDecisionService>(new FakeJevDecisionService(JevResult()));
        services.AddPuddingToolRegistry();

        using var provider = services.BuildServiceProvider();

        Assert.IsInstanceOfType<ClassifierToolApprovalReviewer>(provider.GetRequiredService<IToolApprovalReviewer>());
    }

    [TestMethod]
    public async Task ProductionRegistry_ExplicitJevReviewer_ConstructsEvenWithoutJevPort()
    {
        var services = new ServiceCollection();
        services.Configure<ToolApprovalRuntimeOptions>(options => options.Reviewer = "jev");
        services.AddPuddingToolRegistry();

        using var provider = services.BuildServiceProvider();
        var reviewer = provider.GetRequiredService<IToolApprovalReviewer>();
        Assert.IsInstanceOfType<JevToolApprovalReviewer>(reviewer);

        // fail-closed：Jev 端口缺失 ⇒ DeferredDependency，而不是崩溃或跨模型回退。
        var result = await reviewer.ReviewAsync(TicketRequest(), Identity(), Descriptor());
        Assert.AreEqual(ToolApprovalDecision.DeferredDependency, result.Decision);
        Assert.AreEqual(JevToolApprovalReviewer.ReasonCodeUnavailable, result.ReasonCode);
    }

    [TestMethod]
    public void ProductionRegistry_ExplicitLlmReviewer_StillResolvesLlmReviewer()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IJevDecisionService>(new FakeJevDecisionService(JevResult()));
        services.Configure<ToolApprovalRuntimeOptions>(options => options.Reviewer = "llm");
        services.AddPuddingToolRegistry();

        using var provider = services.BuildServiceProvider();
        var reviewer = provider.GetRequiredService<IToolApprovalReviewer>();

        Assert.IsInstanceOfType<LlmToolApprovalReviewer>(reviewer);
    }

    // ---------- 测试基建 ----------

    private sealed class FakeJevDecisionService : IJevDecisionService
    {
        private readonly Func<JevDecisionRequest, CancellationToken, Task<JevDecisionResult>>? _handler;

        public FakeJevDecisionService(
            JevDecisionResult? result = null,
            Exception? exceptionToThrow = null,
            Func<JevDecisionRequest, CancellationToken, Task<JevDecisionResult>>? handler = null)
        {
            Result = result;
            ExceptionToThrow = exceptionToThrow;
            _handler = handler;
        }

        public JevDecisionResult? Result { get; }

        public Exception? ExceptionToThrow { get; }

        public int CallCount { get; private set; }

        public JevDecisionRequest? LastRequest { get; private set; }

        public Task<JevDecisionResult> DecideAsync(JevDecisionRequest request, CancellationToken ct = default)
        {
            CallCount++;
            LastRequest = request;
            if (ExceptionToThrow is not null)
                throw ExceptionToThrow;
            if (_handler is not null)
                return _handler(request, ct);
            return Task.FromResult(Result ?? throw new InvalidOperationException("fake has no result configured"));
        }
    }

    private static ToolApprovalTicketRequest TicketRequest(string argumentsJson = DefaultShellArgs, string toolId = "shell")
        => new()
        {
            ToolId = toolId,
            Purpose = "unit test purpose",
            RequestedArgumentsJson = argumentsJson,
        };

    private static ToolApprovalIdentity Identity()
        => new()
        {
            WorkspaceId = "ws-test",
            SessionId = "s-1",
            AgentInstanceId = "agent-1",
            UserId = "user-1",
        };

    private static ToolDescriptor Descriptor(string toolId = "shell")
        => new()
        {
            ToolId = toolId,
            Name = toolId,
            Description = "test descriptor",
        };

    private static JevDecisionResult JevResult(
        string? decision = "approve",
        double? risk = 1d,
        string? scope = "session",
        double? allowlist = 0.97d,
        string model = "jev-test-1")
    {
        var answers = new Dictionary<string, JevAnswer>();
        if (decision is not null)
            answers["decision"] = new JevAnswer { Name = "decision", Type = "choice", Choice = decision };
        if (risk is not null)
            answers["risk"] = new JevAnswer { Name = "risk", Type = "score", Score = risk };
        if (scope is not null)
            answers["scope"] = new JevAnswer { Name = "scope", Type = "choice", Choice = scope };
        if (allowlist is not null)
            answers["allowlist"] = new JevAnswer { Name = "allowlist", Type = "noul", Noul = allowlist };
        return new JevDecisionResult { Model = model, Answers = answers };
    }

    private static IConfiguration Config((string Key, string Value) setting)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [setting.Key] = setting.Value })
            .Build();
}
