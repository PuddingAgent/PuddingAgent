using PuddingCode.Classification;
using PuddingCode.Tools;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

/// <summary>
/// <see cref="ClassifierToolApprovalReviewer"/> 的离线单元测试（零网络，分类器端口用 fake stub）。
/// <para>
/// 逐条锁定 S3b 适配器契约（Docs/Features/安全分类器与工具调用准入方案-v2.md §14.5/§14.7/§14.13）：
/// 四选一映射表、Unknown ⇒ DeferredDependency（绝不折叠）、异常降级、外层取消传播、
/// 审计 Reason / ReviewerModel 溯源、单次调用纪律与幂等性。
/// </para>
/// </summary>
[TestClass]
public sealed class ClassifierToolApprovalReviewerTests
{
    private const string DefaultShellArgs = """{"command":"dotnet test"}""";

    // ---------- ① AllowOnce ⇒ Approved + Once + 无提案 ----------

    [TestMethod]
    public async Task M1_AllowOnce_ReturnsApprovedOnceScope_WithoutProposal()
    {
        var classifier = new FakeClassifier(Verdict(ClassificationOutcome.AllowOnce));
        var reviewer = new ClassifierToolApprovalReviewer(classifier);

        var result = await reviewer.ReviewAsync(TicketRequest(), Identity(), Descriptor());

        Assert.AreEqual(ToolApprovalDecision.Approved, result.Decision);
        Assert.AreEqual(ToolApprovalScope.Once, result.AllowedScope);
        Assert.AreEqual(0, result.AllowlistProposals.Count, "AllowOnce 不得产出任何提案。");
        Assert.IsFalse(result.RequiresHumanAuthorization, "RequiresHumanAuthorization 一律 false。");
    }

    // ---------- ② AllowPermanent ⇒ Approved + allow 提案 ----------

    [TestMethod]
    public async Task M2_AllowPermanent_ReturnsApprovedOnceScope_WithAllowProposal()
    {
        var classifier = new FakeClassifier(Verdict(
            ClassificationOutcome.AllowPermanent,
            confidence: Confidence(("allow_permanent", 0.97))));
        var reviewer = new ClassifierToolApprovalReviewer(classifier);
        var request = TicketRequest(commandName: "dotnet");

        var result = await reviewer.ReviewAsync(request, Identity(), Descriptor());

        Assert.AreEqual(ToolApprovalDecision.Approved, result.Decision);
        Assert.AreEqual(ToolApprovalScope.Once, result.AllowedScope, "永久类结论的票据仍是单次作用域。");

        var proposal = result.AllowlistProposals.Single();
        Assert.AreEqual(request.ToolId, proposal.ToolId);
        Assert.AreEqual("dotnet", proposal.Command);
        Assert.AreEqual(DefaultShellArgs, proposal.ArgumentsJson);
        Assert.IsTrue(proposal.Reason!.Contains("effect=allow", StringComparison.Ordinal), "提案必须携带 effect=allow 供上层策展器落规则。");
        Assert.IsTrue(proposal.Reason.Contains("outcome=AllowPermanent", StringComparison.Ordinal));
    }

    // ---------- ③ DenyOnce ⇒ Denied + 无提案 ----------

    [TestMethod]
    public async Task M3_DenyOnce_ReturnsDenied_WithoutProposal()
    {
        var classifier = new FakeClassifier(Verdict(ClassificationOutcome.DenyOnce));
        var reviewer = new ClassifierToolApprovalReviewer(classifier);

        var result = await reviewer.ReviewAsync(TicketRequest(), Identity(), Descriptor());

        Assert.AreEqual(ToolApprovalDecision.Denied, result.Decision);
        Assert.IsNull(result.AllowedScope);
        Assert.AreEqual(0, result.AllowlistProposals.Count, "DenyOnce 不得产出提案。");
        Assert.IsFalse(result.RequiresHumanAuthorization);
    }

    // ---------- ④ DenyPermanent ⇒ Denied + deny 提案 ----------

    [TestMethod]
    public async Task M4_DenyPermanent_ReturnsDenied_WithDenyProposal()
    {
        var classifier = new FakeClassifier(Verdict(
            ClassificationOutcome.DenyPermanent,
            confidence: Confidence(("deny_permanent", 0.99))));
        var reviewer = new ClassifierToolApprovalReviewer(classifier);
        var request = TicketRequest(commandName: "rm");

        var result = await reviewer.ReviewAsync(request, Identity(), Descriptor());

        Assert.AreEqual(ToolApprovalDecision.Denied, result.Decision);
        Assert.IsNull(result.AllowedScope);

        var proposal = result.AllowlistProposals.Single();
        Assert.AreEqual(request.ToolId, proposal.ToolId);
        Assert.IsTrue(proposal.Reason!.Contains("effect=deny", StringComparison.Ordinal), "提案必须携带 effect=deny 供上层策展器落规则。");
        Assert.IsTrue(proposal.Reason.Contains("outcome=DenyPermanent", StringComparison.Ordinal));
    }

    // ---------- ⑤ Unknown ⇒ DeferredDependency（绝不折叠） ----------

    [TestMethod]
    public async Task M5_Unknown_ReturnsDeferredDependency_NotApprovedNotDeniedNotNeedHuman()
    {
        var classifier = new FakeClassifier(Verdict(
            ClassificationOutcome.Unknown,
            reason: "arbiter unavailable",
            reasonCode: "classifier.pipeline.arbiter_unavailable"));
        var reviewer = new ClassifierToolApprovalReviewer(classifier);

        var result = await reviewer.ReviewAsync(TicketRequest(), Identity(), Descriptor());

        Assert.AreEqual(ToolApprovalDecision.DeferredDependency, result.Decision);
        Assert.AreNotEqual(ToolApprovalDecision.Approved, result.Decision, "Unknown 不得折叠为 Approved（ADR-091 §4.4）。");
        Assert.AreNotEqual(ToolApprovalDecision.Denied, result.Decision, "Unknown 不得折叠为 Denied。");
        Assert.AreNotEqual(ToolApprovalDecision.NeedHuman, result.Decision, "Unknown 不得折叠为 NeedHuman。");
        Assert.AreEqual(ToolApprovalWire.CodeClassifierUnknown, result.ReasonCode);
        Assert.IsFalse(result.RequiresHumanAuthorization, "依赖等待不是人工授权请求。");
        Assert.AreEqual(0, result.AllowlistProposals.Count);
    }

    // ---------- ⑥ 分类器抛异常 ⇒ DeferredDependency + service_unavailable，且不抛 ----------

    [TestMethod]
    public async Task M6_ClassifierThrows_ReturnsDeferredDependency_WithServiceUnavailableCode()
    {
        var classifier = new FakeClassifier(exception: new InvalidOperationException("boom"));
        var reviewer = new ClassifierToolApprovalReviewer(classifier);

        var result = await reviewer.ReviewAsync(TicketRequest(), Identity(), Descriptor());

        Assert.AreEqual(ToolApprovalDecision.DeferredDependency, result.Decision);
        Assert.AreEqual(ToolApprovalWire.CodeServiceUnavailable, result.ReasonCode);
        Assert.AreNotEqual(ToolApprovalDecision.Approved, result.Decision, "依赖失败不得折叠为 Approved。");
        Assert.AreNotEqual(ToolApprovalDecision.NeedHuman, result.Decision, "依赖失败不得折叠为 NeedHuman。");
    }

    // ---------- ⑦ 外层取消 ⇒ OperationCanceledException 传播 ----------

    [TestMethod]
    public async Task M7_OuterCancellation_PropagatesOperationCanceledException()
    {
        var classifier = new FakeClassifier(handler: async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return null!;
        });
        var reviewer = new ClassifierToolApprovalReviewer(classifier);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            reviewer.ReviewAsync(TicketRequest(), Identity(), Descriptor(), new CancellationToken(canceled: true)));
    }

    // ---------- ⑧ Reason 非空含结论；ReviewerModel 取自 verdict ----------

    [TestMethod]
    public async Task M8_ReasonCarriesOutcomeAndConfidence_ReviewerModelComesFromVerdict()
    {
        var classifier = new FakeClassifier(Verdict(
            ClassificationOutcome.AllowPermanent,
            reason: "safe deterministic build command",
            confidence: Confidence(
                ("allow_once", 0.02),
                ("allow_permanent", 0.97),
                ("deny_once", 0.005),
                ("deny_permanent", 0.005)),
            model: "fake-model-9"));
        var reviewer = new ClassifierToolApprovalReviewer(classifier);

        var result = await reviewer.ReviewAsync(TicketRequest(), Identity(), Descriptor());

        Assert.IsFalse(string.IsNullOrWhiteSpace(result.DecisionReason), "Reason 必须非空。");
        Assert.IsTrue(result.DecisionReason.Contains("outcome=AllowPermanent", StringComparison.Ordinal), "Reason 必须包含分类器结论。");
        Assert.IsTrue(result.DecisionReason.Contains("allow_permanent=0.97", StringComparison.Ordinal), "Reason 必须包含逐分类可信度摘要。");
        Assert.IsTrue(result.DecisionReason.Contains("safe deterministic build command", StringComparison.Ordinal), "Reason 必须保留分类器原始理由。");
        Assert.AreEqual("fake-model-9", result.ReviewerModel, "ReviewerModel 必须取自 verdict.ClassifierModel，不得伪造。");
    }

    // ---------- ⑨ 分类器只被调用一次 ----------

    [TestMethod]
    public async Task M9_ClassifierInvokedExactlyOnce_PerReview()
    {
        var classifier = new FakeClassifier(Verdict(ClassificationOutcome.AllowOnce));
        var reviewer = new ClassifierToolApprovalReviewer(classifier);

        await reviewer.ReviewAsync(TicketRequest(), Identity(), Descriptor());

        Assert.AreEqual(1, classifier.CallCount, "§14.9.2：每次评审只请求分类器一次，不做重试风暴。");
    }

    // ---------- ⑩ 多轮调用幂等：同输入同输出，不累积状态 ----------

    [TestMethod]
    public async Task M10_RepeatedSameInput_ProducesSameOutput_WithoutStateAccumulation()
    {
        var classifier = new FakeClassifier(Verdict(
            ClassificationOutcome.DenyPermanent,
            confidence: Confidence(("deny_permanent", 0.98))));
        var reviewer = new ClassifierToolApprovalReviewer(classifier);
        var request = TicketRequest();

        var first = await reviewer.ReviewAsync(request, Identity(), Descriptor());
        var second = await reviewer.ReviewAsync(request, Identity(), Descriptor());

        Assert.AreEqual(first.Decision, second.Decision);
        Assert.AreEqual(first.DecisionReason, second.DecisionReason);
        Assert.AreEqual(first.AllowedScope, second.AllowedScope);
        Assert.AreEqual(first.AllowlistProposals.Count, second.AllowlistProposals.Count);
        Assert.AreEqual(first.AllowlistProposals.Single().Reason, second.AllowlistProposals.Single().Reason);
        Assert.AreEqual(2, classifier.CallCount);
    }

    // ---------- 补充：上下文映射与有界轨迹 ----------

    [TestMethod]
    public async Task C1_ContextCarriesTicketFacts_IdentityQuad_AndBoundedTrajectory()
    {
        var classifier = new FakeClassifier(Verdict(ClassificationOutcome.AllowOnce));
        var reviewer = new ClassifierToolApprovalReviewer(classifier);
        var identity = Identity();
        var request = new ToolApprovalTicketRequest
        {
            ToolId = "shell",
            CommandName = "dotnet",
            Purpose = "run unit tests",
            Necessity = "verify slice S3b",
            FactBasis = ["repo at HEAD 2ab235b"],
            RequestedArgumentsJson = DefaultShellArgs,
            TargetResources = ["bin/"],
            IsIrreversibleOperation = false,
            MayDamageOrDeleteData = false,
            OperationContext = "context snippet",
            RiskNotes = new string('r', 2000),
            OperationSteps =
            [
                new ToolApprovalOperationStep
                {
                    StepNumber = 1,
                    Command = "dotnet test",
                    TargetObject = "repo",
                    Purpose = "verify",
                    ExpectedEffect = "tests pass",
                    Reasonableness = "standard",
                    StopCondition = "all green",
                    WorkingDirectory = @"E:\tmp\worktree",
                },
            ],
        };

        await reviewer.ReviewAsync(request, identity, Descriptor());

        var context = classifier.LastContext!;
        Assert.AreEqual("shell", context.ToolId);
        Assert.AreEqual("dotnet", context.CommandName);
        Assert.AreEqual(DefaultShellArgs, context.ArgumentsJson);
        Assert.AreEqual(@"E:\tmp\worktree", context.WorkingDirectory, "工作目录取自出题单步骤。");
        Assert.IsNull(context.Shell, "出题单不携带 shell 信息，不得伪造。");
        Assert.AreEqual("run unit tests", context.Purpose);
        Assert.AreEqual("verify slice S3b", context.Necessity);
        Assert.AreEqual(1, context.FactBasis.Count);
        Assert.AreEqual(1, context.TargetResources.Count);
        Assert.IsFalse(context.IsIrreversibleOperation);
        Assert.IsFalse(context.MayDamageOrDeleteData);
        Assert.AreEqual(identity.WorkspaceId, context.WorkspaceId);
        Assert.AreEqual(identity.SessionId, context.SessionId);
        Assert.AreEqual(identity.AgentInstanceId, context.AgentInstanceId);
        Assert.AreEqual(identity.UserId, context.UserId);
        Assert.IsNotNull(context.RecentTrajectory, "轨迹摘要必须非空。");
        Assert.IsTrue(
            context.RecentTrajectory!.Length <= ClassifierToolApprovalReviewer.MaxTrajectoryChars,
            $"轨迹必须有界（≤ {ClassifierToolApprovalReviewer.MaxTrajectoryChars} 字符），禁止整段历史。");
        Assert.AreEqual(0, context.MatchedRuleSummaries.Count, "适配器没有规则信息，不得伪造规则命中。");
    }

    [TestMethod]
    public async Task C2_NullVerdict_ReturnsDeferredDependency_WithServiceUnavailableCode()
    {
        var classifier = new FakeClassifier(handler: (_, _) => Task.FromResult<ClassificationVerdict>(null!));
        var reviewer = new ClassifierToolApprovalReviewer(classifier);

        var result = await reviewer.ReviewAsync(TicketRequest(), Identity(), Descriptor());

        Assert.AreEqual(ToolApprovalDecision.DeferredDependency, result.Decision);
        Assert.AreEqual(ToolApprovalWire.CodeServiceUnavailable, result.ReasonCode);
    }

    // ---------- 测试基建 ----------

    private sealed class FakeClassifier : IToolCallClassifier
    {
        private readonly ClassificationVerdict? _result;
        private readonly Exception? _exception;
        private readonly Func<ToolCallClassificationContext, CancellationToken, Task<ClassificationVerdict>>? _handler;

        public FakeClassifier(
            ClassificationVerdict? result = null,
            Exception? exception = null,
            Func<ToolCallClassificationContext, CancellationToken, Task<ClassificationVerdict>>? handler = null)
        {
            _result = result;
            _exception = exception;
            _handler = handler;
        }

        public string ClassifierId => "fake-classifier";

        public int CallCount { get; private set; }

        public ToolCallClassificationContext? LastContext { get; private set; }

        public async Task<ClassificationVerdict> ClassifyAsync(ToolCallClassificationContext context, CancellationToken ct = default)
        {
            CallCount++;
            LastContext = context;
            if (ct.IsCancellationRequested)
                throw new OperationCanceledException(ct);
            if (_exception is not null)
                throw _exception;
            if (_handler is not null)
                return await _handler(context, ct);
            return _result ?? throw new InvalidOperationException("fake has no verdict configured");
        }
    }

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

    private static ToolApprovalTicketRequest TicketRequest(
        string argumentsJson = DefaultShellArgs,
        string toolId = "shell",
        string? commandName = null)
        => new()
        {
            ToolId = toolId,
            CommandName = commandName,
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
}
