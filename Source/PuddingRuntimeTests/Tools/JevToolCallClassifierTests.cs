using System.Text;
using System.Text.Json.Nodes;
using PuddingCode.Abstractions;
using PuddingCode.Classification;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

/// <summary>
/// <see cref="JevToolCallClassifier"/> 的离线单测（零网络，Jev 端口全部用 fake，切片 S3d）。
/// <para>
/// 覆盖映射：四选一解析与逐分类可信度（01–04）、无法解析 fail-closed 绝不放行（05）、
/// 依赖异常不冒泡（06）、空/空串/超长垃圾返回（07）、永久类缺可信度防御性降级（08）、
/// 单次调用只请求一次（09）、外层取消传播（10）、Prompt 形态——背景字段 + 四选一关键字 +
/// 超长轨迹截断（11）、自身 deadline（12）、配置禁用 fail-closed（13）、
/// LatencyMs 注入时钟计量（14）。全部用例离线、不接触真实模型与 API key。
/// </para>
/// </summary>
[TestClass]
public sealed class JevToolCallClassifierTests
{
    private const string KAllowOnce = "allow_once";
    private const string KAllowPermanent = "allow_permanent";
    private const string KDenyOnce = "deny_once";
    private const string KDenyPermanent = "deny_permanent";

    // —— 01 allow_once 解析 ⇒ AllowOnce + 四键可信度 + 完整溯源 ——

    [TestMethod]
    public async Task ClassifyAsync_AllowOnceChoice_ReturnsAllowOnceWithFourConfidences()
    {
        var classifier = CreateClassifier(new FakeJevDecisionService(JevResult(choice: KAllowOnce)));

        var verdict = await classifier.ClassifyAsync(ToolContext());

        Assert.AreEqual(ClassificationOutcome.AllowOnce, verdict.Outcome);
        Assert.IsNotNull(verdict.PerOutcomeConfidence, "成功裁决必须携带逐分类可信度。");
        Assert.AreEqual(0.9, verdict.PerOutcomeConfidence![KAllowOnce], 1e-9);
        Assert.AreEqual(0.2, verdict.PerOutcomeConfidence![KAllowPermanent], 1e-9);
        Assert.AreEqual(0.1, verdict.PerOutcomeConfidence![KDenyOnce], 1e-9);
        Assert.AreEqual(0.05, verdict.PerOutcomeConfidence![KDenyPermanent], 1e-9);
        Assert.AreEqual(JevToolCallClassifier.ReasonCodeVerdict, verdict.ReasonCode);
        Assert.AreEqual("jev", verdict.ClassifierId);
        Assert.AreEqual("jev-test-1.0", verdict.ClassifierModel, "ClassifierModel 必须取 Jev 回填的实际模型名。");
        Assert.IsFalse(string.IsNullOrWhiteSpace(verdict.Reason), "Reason 不得为空。");
        Assert.IsTrue(verdict.LatencyMs.HasValue, "默认实现应产出耗时计量。");
    }

    // —— 02 allow_permanent + 高可信度 ⇒ AllowPermanent ——

    [TestMethod]
    public async Task ClassifyAsync_AllowPermanentWithHighConfidence_ReturnsAllowPermanent()
    {
        var classifier = CreateClassifier(new FakeJevDecisionService(
            JevResult(choice: KAllowPermanent, confAllowPermanent: 0.95)));

        var verdict = await classifier.ClassifyAsync(ToolContext());

        Assert.AreEqual(ClassificationOutcome.AllowPermanent, verdict.Outcome);
        Assert.AreEqual(0.95, verdict.PerOutcomeConfidence![KAllowPermanent], 1e-9);
        Assert.AreEqual(JevToolCallClassifier.ReasonCodeVerdict, verdict.ReasonCode);
    }

    // —— 03 deny_once 解析 ⇒ DenyOnce ——

    [TestMethod]
    public async Task ClassifyAsync_DenyOnceChoice_ReturnsDenyOnce()
    {
        var classifier = CreateClassifier(new FakeJevDecisionService(
            JevResult(choice: KDenyOnce, confDenyOnce: 0.7)));

        var verdict = await classifier.ClassifyAsync(ToolContext());

        Assert.AreEqual(ClassificationOutcome.DenyOnce, verdict.Outcome);
        Assert.AreNotEqual(ClassificationOutcome.DenyPermanent, verdict.Outcome);
        Assert.AreEqual(0.7, verdict.PerOutcomeConfidence![KDenyOnce], 1e-9);
    }

    // —— 04 deny_permanent + 高可信度 ⇒ DenyPermanent ——

    [TestMethod]
    public async Task ClassifyAsync_DenyPermanentWithHighConfidence_ReturnsDenyPermanent()
    {
        var classifier = CreateClassifier(new FakeJevDecisionService(
            JevResult(choice: KDenyPermanent, confDenyPermanent: 0.92)));

        var verdict = await classifier.ClassifyAsync(ToolContext());

        Assert.AreEqual(ClassificationOutcome.DenyPermanent, verdict.Outcome);
        Assert.AreEqual(0.92, verdict.PerOutcomeConfidence![KDenyPermanent], 1e-9);
    }

    // —— 05 无法解析的 choice ⇒ Unknown（绝不是 Allow/Deny 任何一项） ——

    [TestMethod]
    public async Task ClassifyAsync_UnparseableChoice_ReturnsUnknown_NeverAllowOrDeny()
    {
        var classifier = CreateClassifier(new FakeJevDecisionService(JevResult(choice: "maybe")));

        var verdict = await classifier.ClassifyAsync(ToolContext());

        Assert.AreEqual(ClassificationOutcome.Unknown, verdict.Outcome, "无法解析的结论必须是 Unknown。");
        Assert.AreNotEqual(ClassificationOutcome.AllowOnce, verdict.Outcome, "未知绝不允许当放行。");
        Assert.AreNotEqual(ClassificationOutcome.AllowPermanent, verdict.Outcome);
        Assert.AreNotEqual(ClassificationOutcome.DenyOnce, verdict.Outcome);
        Assert.AreNotEqual(ClassificationOutcome.DenyPermanent, verdict.Outcome);
        Assert.AreEqual(JevToolCallClassifier.ReasonCodeUnparsed, verdict.ReasonCode);
        Assert.IsFalse(string.IsNullOrWhiteSpace(verdict.Reason), "降级场景也必须写明原因。");
    }

    // —— 06 服务抛异常 ⇒ Unknown + 不可用原因码，绝不冒泡 ——

    [TestMethod]
    public async Task ClassifyAsync_JevThrows_ReturnsUnknownUnavailable_WithoutBubble()
    {
        var fromJevEx = await CreateClassifier(new FakeJevDecisionService(
            exceptionToThrow: new JevDecisionException(JevDecisionCodes.NotConfigured, "endpoint not configured")))
            .ClassifyAsync(ToolContext());

        Assert.AreEqual(ClassificationOutcome.Unknown, fromJevEx.Outcome, "Jev 异常必须折叠为 Unknown。");
        Assert.AreEqual(JevToolCallClassifier.ReasonCodeUnavailable, fromJevEx.ReasonCode);
        Assert.AreNotEqual(ClassificationOutcome.AllowOnce, fromJevEx.Outcome, "依赖失败不得放行。");
        Assert.AreNotEqual(ClassificationOutcome.DenyOnce, fromJevEx.Outcome);
        Assert.IsFalse(string.IsNullOrWhiteSpace(fromJevEx.Reason));

        var fromGeneric = await CreateClassifier(new FakeJevDecisionService(
            exceptionToThrow: new InvalidOperationException("boom")))
            .ClassifyAsync(ToolContext());

        Assert.AreEqual(ClassificationOutcome.Unknown, fromGeneric.Outcome, "任意非取消异常同样折叠为 Unknown。");
        Assert.AreEqual(JevToolCallClassifier.ReasonCodeUnavailable, fromGeneric.ReasonCode);
    }

    // —— 07 空 / 空串 / 超长垃圾返回 ⇒ Unknown（不得乐观放行） ——

    [TestMethod]
    public async Task ClassifyAsync_EmptyOrOversizedGarbage_ReturnsUnknown()
    {
        var missing = await CreateClassifier(new FakeJevDecisionService(JevResult(choice: null)))
            .ClassifyAsync(ToolContext());
        Assert.AreEqual(ClassificationOutcome.Unknown, missing.Outcome, "缺 outcome 答案必须 Unknown。");
        Assert.AreEqual(JevToolCallClassifier.ReasonCodeUnparsed, missing.ReasonCode);

        var empty = await CreateClassifier(new FakeJevDecisionService(JevResult(choice: string.Empty)))
            .ClassifyAsync(ToolContext());
        Assert.AreEqual(ClassificationOutcome.Unknown, empty.Outcome, "空 choice 必须 Unknown。");

        var oversized = await CreateClassifier(new FakeJevDecisionService(JevResult(choice: new string('x', 5000))))
            .ClassifyAsync(ToolContext());
        Assert.AreEqual(ClassificationOutcome.Unknown, oversized.Outcome, "超长垃圾 choice 必须 Unknown。");
        Assert.IsFalse(string.IsNullOrWhiteSpace(oversized.Reason));
    }

    // —— 08 永久类缺对应逐分类可信度 ⇒ 防御性降级为单次类（Reason 写明降级原因） ——

    [TestMethod]
    public async Task ClassifyAsync_PermanentMissingOwnConfidence_DowngradesToOnceWithReason()
    {
        var allowDowngraded = await CreateClassifier(new FakeJevDecisionService(
            JevResult(choice: KAllowPermanent, confAllowPermanent: null)))
            .ClassifyAsync(ToolContext());
        Assert.AreEqual(
            ClassificationOutcome.AllowOnce,
            allowDowngraded.Outcome,
            "allow_permanent 缺自身可信度必须降级为 allow_once。");
        Assert.IsTrue(allowDowngraded.Reason.Contains(KAllowPermanent), "Reason 必须写明降级来源分类。");
        Assert.IsTrue(allowDowngraded.Reason.Contains("降级"), "Reason 必须写明降级原因。");

        var denyDowngraded = await CreateClassifier(new FakeJevDecisionService(
            JevResult(choice: KDenyPermanent, confDenyPermanent: null)))
            .ClassifyAsync(ToolContext());
        Assert.AreEqual(
            ClassificationOutcome.DenyOnce,
            denyDowngraded.Outcome,
            "deny_permanent 缺自身可信度必须降级为 deny_once。");
        Assert.IsTrue(denyDowngraded.Reason.Contains(KDenyPermanent));
        Assert.IsTrue(denyDowngraded.Reason.Contains("降级"));
    }

    // —— 09 每次 ClassifyAsync 只请求 Jev 一次（无重试风暴） ——

    [TestMethod]
    public async Task ClassifyAsync_CallsJevExactlyOncePerDecision()
    {
        var jev = new FakeJevDecisionService(JevResult());
        var classifier = CreateClassifier(jev);

        await classifier.ClassifyAsync(ToolContext());
        Assert.AreEqual(1, jev.CallCount, "一次 ClassifyAsync 必须只请求 Jev 一次。");

        await classifier.ClassifyAsync(ToolContext());
        Assert.AreEqual(2, jev.CallCount, "第二次 ClassifyAsync 再请求一次（无重试风暴）。");
    }

    // —— 10 外层取消 ⇒ OperationCanceledException 照常传播 ——

    [TestMethod]
    public async Task ClassifyAsync_Cancellation_PropagatesOperationCanceledException()
    {
        var jev = new FakeJevDecisionService(JevResult());
        var classifier = CreateClassifier(jev);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => classifier.ClassifyAsync(ToolContext(), new CancellationToken(canceled: true)),
            "外层取消必须让 OperationCanceledException 照常传播，不得折叠成 Unknown。");
        Assert.AreEqual(0, jev.CallCount, "取消发生在 Jev IO 之前，不得发起请求。");
    }

    // —— 11 Prompt 形态：四选一关键字 + 背景字段 + 超长轨迹截断 ——

    [TestMethod]
    public async Task ClassifyAsync_PromptCarriesQuestionKeysBoundedBackgroundAndTruncatedTrajectory()
    {
        var jev = new FakeJevDecisionService(JevResult());
        var classifier = CreateClassifier(jev);
        var longTrajectory = new string('轨', 10_000); // UTF-8 ≈ 30_000 字节 > StateTruncateBytes(8192)

        await classifier.ClassifyAsync(ToolContext(recentTrajectory: longTrajectory));

        var request = jev.LastRequest;
        Assert.IsNotNull(request, "分类器必须把请求交给 Jev 端口。");
        Assert.IsNotNull(request.State);

        // 四选一裁决问题：choice criteria 必须覆盖四个分类关键字。
        var outcomeQuestion = request.Questions.Single(static question => question.Name == "outcome");
        Assert.AreEqual(JevQuestionType.Choice, outcomeQuestion.Type);
        var criteriaKeys = outcomeQuestion.ChoiceCriteria!.Keys;
        Assert.IsTrue(criteriaKeys.Contains(KAllowOnce), "问题必须包含 allow_once 关键字。");
        Assert.IsTrue(criteriaKeys.Contains(KAllowPermanent), "问题必须包含 allow_permanent 关键字。");
        Assert.IsTrue(criteriaKeys.Contains(KDenyOnce), "问题必须包含 deny_once 关键字。");
        Assert.IsTrue(criteriaKeys.Contains(KDenyPermanent), "问题必须包含 deny_permanent 关键字。");

        // 四个逐分类可信度问题（noul）。
        foreach (var key in new[] { KAllowOnce, KAllowPermanent, KDenyOnce, KDenyPermanent })
        {
            var confidenceQuestion = request.Questions.Single(question => question.Name == $"confidence.{key}");
            Assert.AreEqual(JevQuestionType.Noul, confidenceQuestion.Type, $"{key} 的可信度问题必须是 noul。");
        }

        // 背景字段：工作目录 / 工具 / 参数 / shell / 意图 / 必要性 / 不可逆与破坏标记 / 轨迹。
        var stateText = request.State.ToJsonString();
        foreach (var field in new[]
                 {
                     "toolId", "argumentsJson", "workingDirectory", "shell", "purpose", "necessity",
                     "isIrreversibleOperation", "mayDamageOrDeleteData", "recentTrajectory",
                 })
        {
            Assert.IsTrue(stateText.Contains($"\"{field}\""), $"State 必须携带背景字段 {field}。");
        }

        // 超长轨迹必须被有界截断，不得整段历史塞进请求。
        // 容差 +3：字节级截断可能切断 UTF-8 序列，GetString 以 U+FFFD（3 字节）替换，与既有
        // JevToolApprovalReviewer.TruncateUtf8 语义一致，重编码最多膨胀 3 字节。
        var trajectoryNode = ((JsonObject)request.State)["recentTrajectory"] as JsonValue;
        Assert.IsNotNull(trajectoryNode);
        var trajectory = trajectoryNode.GetValue<string>();
        Assert.IsTrue(
            Encoding.UTF8.GetByteCount(trajectory) <= 8192 + 3,
            "轨迹必须按 StateTruncateBytes(8192) 截断（允许 U+FFFD 替换膨胀 ≤3 字节）。");
        Assert.IsTrue(trajectory.Length < longTrajectory.Length, "超长轨迹必须被截断，不得原样整段进入请求。");
    }

    // —— 12 自身 deadline 到期 ⇒ Unknown + unavailable（调用者令牌不取消） ——

    [TestMethod]
    public async Task ClassifyAsync_DeadlineExceeded_ReturnsUnknownUnavailable()
    {
        var jev = new FakeJevDecisionService(handler: async (_, innerCt) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, innerCt);
            return null!;
        });
        var classifier = CreateClassifier(jev, options => options.ReviewTimeoutSeconds = 1);

        var verdict = await classifier.ClassifyAsync(ToolContext(), CancellationToken.None);

        Assert.AreEqual(ClassificationOutcome.Unknown, verdict.Outcome, "deadline 到期必须 fail-closed。");
        Assert.AreEqual(JevToolCallClassifier.ReasonCodeUnavailable, verdict.ReasonCode);
        Assert.AreNotEqual(ClassificationOutcome.AllowOnce, verdict.Outcome, "超时不得放行。");
    }

    // —— 13 配置禁用 ⇒ Unknown + disabled，不调用 Jev ——

    [TestMethod]
    public async Task ClassifyAsync_DisabledByOptions_ReturnsUnknownDisabled_WithoutCallingJev()
    {
        var jev = new FakeJevDecisionService(JevResult());
        var classifier = CreateClassifier(jev, options => options.Enabled = false);

        var verdict = await classifier.ClassifyAsync(ToolContext());

        Assert.AreEqual(ClassificationOutcome.Unknown, verdict.Outcome, "禁用必须 fail-closed Unknown。");
        Assert.AreEqual(JevToolCallClassifier.ReasonCodeDisabled, verdict.ReasonCode);
        Assert.AreEqual(0, jev.CallCount);
    }

    // —— 14 LatencyMs 用注入的 TimeProvider 计量 ——

    [TestMethod]
    public async Task ClassifyAsync_LatencyMs_MeasuredViaInjectedTimeProvider()
    {
        var clock = new FakeClock(); // 每次 GetTimestamp 推进 100ms
        var classifier = CreateClassifier(new FakeJevDecisionService(JevResult()), clock: clock);

        var verdict = await classifier.ClassifyAsync(ToolContext());

        Assert.IsTrue(verdict.LatencyMs.HasValue, "LatencyMs 必须有值。");
        Assert.AreEqual(100d, verdict.LatencyMs!.Value, 1.0, "LatencyMs 应基于注入时钟计量。");
    }

    // —— 15 每个问题都必须带非空 Instructions（离线可发现的真链路契约，由 S3c-2 探针暴露） ——

    [TestMethod]
    public async Task BuildDecisionRequest_EveryQuestion_CarriesNonEmptyInstructions()
    {
        // 真链路口径（JevDecisionLiveTests 是唯一被联网验证过的形状）：**每个**问题都带 Instructions。
        // 缺 Instructions 的 Choice 问题在真实端点上无法推进，而离线桩不校验该字段
        // —— 本断言是唯一能在离线发现该类缺陷的护栏（此前本文件对 Instructions 零引用）。
        var jev = new FakeJevDecisionService(JevResult());
        var classifier = CreateClassifier(jev);

        await classifier.ClassifyAsync(ToolContext());

        var request = jev.LastRequest;
        Assert.IsNotNull(request, "必须发出决策请求。");
        Assert.IsTrue(
            request!.Questions.Count >= 5,
            $"五问（1 四选一 + 4 逐分类可信度）必须全部发出，实际 {request.Questions.Count}。");
        foreach (var question in request.Questions)
        {
            Assert.IsFalse(
                string.IsNullOrWhiteSpace(question.Instructions),
                $"问题 '{question.Name}'（{question.Type}）必须带非空 Instructions：真实 Jev 端点靠 instructions 理解问题意图。");
        }
    }

    // —— 测试辅助 ——

    private static JevToolCallClassifier CreateClassifier(
        FakeJevDecisionService jev,
        Action<ToolApprovalJevOptions>? configure = null,
        TimeProvider? clock = null)
    {
        var options = new ToolApprovalJevOptions();
        configure?.Invoke(options);
        return new JevToolCallClassifier(jev, options, clock);
    }

    private static ToolCallClassificationContext ToolContext(string? recentTrajectory = null)
        => new()
        {
            ToolId = "terminal_execute",
            CommandName = "dotnet build",
            ArgumentsJson = """{"command":"dotnet build"}""",
            WorkingDirectory = "E:/repo",
            Shell = "pwsh",
            OperationContext = "构建验证切片修改",
            Purpose = "验证 S3d 切片交付",
            Necessity = "分类器管线仲裁位硬前置",
            FactBasis = ["git status 仅本切片文件被修改"],
            TargetResources = ["Source/PuddingRuntime/Tools/Approval/JevToolCallClassifier.cs"],
            IsIrreversibleOperation = false,
            MayDamageOrDeleteData = false,
            WorkspaceId = "ws-test",
            SessionId = "session-test",
            AgentInstanceId = "agent-test",
            UserId = "user-test",
            RecentTrajectory = recentTrajectory,
        };

    /// <summary>构造 JevDecisionResult：outcome(choice) + 四个逐分类可信度(noul)，缺省值任意键传 null 即缺答案。</summary>
    private static JevDecisionResult JevResult(
        string? choice = KAllowOnce,
        double? confAllowOnce = 0.9,
        double? confAllowPermanent = 0.2,
        double? confDenyOnce = 0.1,
        double? confDenyPermanent = 0.05,
        string model = "jev-test-1.0")
    {
        var answers = new Dictionary<string, JevAnswer>();
        if (choice is not null)
        {
            answers["outcome"] = new JevAnswer { Name = "outcome", Type = "choice", Choice = choice };
        }

        void AddConfidence(string key, double? value)
        {
            if (value.HasValue)
            {
                answers[$"confidence.{key}"] = new JevAnswer
                {
                    Name = $"confidence.{key}",
                    Type = "noul",
                    Noul = value.Value,
                };
            }
        }

        AddConfidence(KAllowOnce, confAllowOnce);
        AddConfidence(KAllowPermanent, confAllowPermanent);
        AddConfidence(KDenyOnce, confDenyOnce);
        AddConfidence(KDenyPermanent, confDenyPermanent);

        return new JevDecisionResult { Model = model, Answers = answers };
    }

    /// <summary>Jev 端口假实现：计数每次调用、记录最后请求；可注入固定结果 / 异常 / 自定义 handler。</summary>
    private sealed class FakeJevDecisionService : IJevDecisionService
    {
        private readonly JevDecisionResult? _result;
        private readonly Exception? _exceptionToThrow;
        private readonly Func<JevDecisionRequest, CancellationToken, Task<JevDecisionResult>>? _handler;
        private int _callCount;

        public FakeJevDecisionService(
            JevDecisionResult? result = null,
            Exception? exceptionToThrow = null,
            Func<JevDecisionRequest, CancellationToken, Task<JevDecisionResult>>? handler = null)
        {
            _result = result;
            _exceptionToThrow = exceptionToThrow;
            _handler = handler;
        }

        public int CallCount => Volatile.Read(ref _callCount);

        public JevDecisionRequest? LastRequest { get; private set; }

        public Task<JevDecisionResult> DecideAsync(JevDecisionRequest request, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _callCount);
            LastRequest = request;
            if (_handler is not null)
            {
                return _handler(request, ct);
            }

            if (_exceptionToThrow is not null)
            {
                return Task.FromException<JevDecisionResult>(_exceptionToThrow);
            }

            ct.ThrowIfCancellationRequested();
            return Task.FromResult(
                _result ?? throw new InvalidOperationException("fake 未配置决策结果。"));
        }
    }

    /// <summary>假时钟：每次读取 GetTimestamp 固定推进 100ms（验证 LatencyMs 基于注入时钟计量）。</summary>
    private sealed class FakeClock : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp()
        {
            var current = _timestamp;
            _timestamp += TimeSpan.TicksPerSecond / 10;
            return current;
        }
    }
}
