using PuddingCode.Classification;
using PuddingCode.Operators;
using PuddingRuntime.Operators.Adapters;

namespace PuddingRuntimeTests.Operators;

/// <summary>
/// S1b 交付物 3：工具审批<b>适配器等价性测试</b>（本切片最重要的证据）。
/// <para>
/// 断言口径：对同一批既有输入，「经新端口的适配器路径」与「直接调用既有分类器路径」的结果<b>语义等价</b>，
/// 且适配器<b>不</b>改写既有裁决的任何一个字段（不补值、不归一化、不伪造分数或阈值）。
/// </para>
/// <para>逐条覆盖规格 §4 的五点：① 主标签规范化键；② 分布逐键逐值；③ 理由 / 原因码 / 算子 id / 模型 id 逐位；
/// ④ <c>Score</c> / <c>Threshold</c> 为 null；⑤ <c>InputDigest</c> 确定性。</para>
/// </summary>
[TestClass]
public sealed class ToolApprovalOperatorAdapterEquivalenceTests
{
    // ---------- ①–④ 代表性用例逐条等价 ----------

    [TestMethod]
    public async Task Equivalence_EveryRepresentativeCase_MapsVerdictWithoutDrift()
    {
        Assert.IsTrue(ToolApprovalAdapterTestData.Cases.Count >= 5, "规格要求至少覆盖 5 类代表性用例。");

        foreach (var testCase in ToolApprovalAdapterTestData.Cases)
        {
            var inner = new StubToolCallClassifier(testCase.Verdict);
            var adapter = new ToolApprovalOperatorAdapter(inner, new FixedClock(OperatorTestData.Origin));
            var context = ToolApprovalOperatorContext.Create(testCase.Context);

            var result = await adapter.ClassifyAsync(context);

            // 被包装者只被转发一次，且收到的是**同一个**既有上下文实例（不重造、不裁剪）。
            Assert.AreEqual(1, inner.Calls, $"[{testCase.Name}] 适配器必须只转发一次。");
            Assert.AreSame(testCase.Context, inner.LastContext, $"[{testCase.Name}] 必须原样转发既有上下文实例。");

            // ① 主标签 == 文档化规范键（字面量断言，独立于生产常量）+ 与纯映射函数一致
            Assert.AreEqual(testCase.ExpectedLabel, result.PrimaryLabel, $"[{testCase.Name}] 主标签必须是规范化小写键。");
            Assert.AreEqual(
                testCase.ExpectedLabel,
                ToolApprovalOperatorAdapter.NormalizeLabel(testCase.Verdict.Outcome),
                $"[{testCase.Name}] 纯映射函数与字面量规范键必须一致。");
            Assert.IsTrue(
                ToolApprovalOperatorAdapter.CanonicalLabels.Contains(result.PrimaryLabel!),
                $"[{testCase.Name}] 主标签必须属于规范键全集。");

            // ② 分布与既有 PerOutcomeConfidence 逐键逐值相等（null ⇒ 空字典，不得伪造）
            if (testCase.Verdict.PerOutcomeConfidence is null)
            {
                Assert.IsEmpty(result.Distribution, $"[{testCase.Name}] 既有分布为 null ⇒ 必须为空字典。");
            }
            else
            {
                Assert.AreSame(
                    testCase.Verdict.PerOutcomeConfidence,
                    result.Distribution,
                    $"[{testCase.Name}] 分布必须原样承载（零拷贝），不得重造或补键。");
                Assert.AreEqual(
                    testCase.Verdict.PerOutcomeConfidence.Count,
                    result.Distribution.Count,
                    $"[{testCase.Name}] 分布键数必须相等。");
                foreach (var pair in testCase.Verdict.PerOutcomeConfidence)
                {
                    Assert.IsTrue(
                        result.Distribution.TryGetValue(pair.Key, out var actual),
                        $"[{testCase.Name}] 分布缺少键 {pair.Key}。");
                    Assert.AreEqual(pair.Value, actual, $"[{testCase.Name}] 分布键 {pair.Key} 的值必须逐位相等。");
                }
            }

            // ③ 理由 / 原因码 / 算子 id / 模型 id 逐位相等
            Assert.AreEqual(testCase.Verdict.Reason, result.Envelope.Reason, $"[{testCase.Name}] Reason 必须逐位相等。");
            Assert.AreEqual(testCase.Verdict.ReasonCode, result.Envelope.ReasonCode, $"[{testCase.Name}] ReasonCode 必须逐位相等。");
            Assert.AreEqual(testCase.Verdict.ClassifierId, result.Envelope.OperatorId, $"[{testCase.Name}] 算子 id 必须取自被包装分类器。");
            Assert.AreEqual(inner.ClassifierId, adapter.OperatorId, $"[{testCase.Name}] 适配器不得自造算子身份。");
            Assert.AreEqual(testCase.Verdict.ClassifierModel, result.Envelope.ModelId, $"[{testCase.Name}] 模型 id 必须逐位相等。");

            // ④ 审批裁决无单一分数、阈值尚未策略化 ⇒ 必须留空（等价性测试在此证明「没有被伪造」）
            Assert.IsNull(result.Envelope.Score, $"[{testCase.Name}] Score 必须为 null（不得伪造）。");
            Assert.IsNull(result.Envelope.ScoreScale, $"[{testCase.Name}] ScoreScale 必须为 null（不得伪造）。");
            Assert.IsNull(result.Envelope.Threshold, $"[{testCase.Name}] Threshold 必须为 null（审批阈值尚未策略化）。");
            Assert.IsNull(result.Envelope.Outcome, $"[{testCase.Name}] 分类投影不得填判断结论。");

            // 置信度：取主标签对应的自报值；键缺失 ⇒ null；种类恒为「模型自报」（未校准）
            Assert.AreEqual(testCase.ExpectedConfidence, result.Envelope.Confidence, $"[{testCase.Name}] 置信度取值必须与自报分布一致。");
            Assert.AreEqual(ConfidenceKind.ModelSelfReported, result.Envelope.ConfidenceKind, $"[{testCase.Name}] 置信种类必须是模型自报。");
            Assert.AreNotEqual(ConfidenceKind.Calibrated, result.Envelope.ConfidenceKind, $"[{testCase.Name}] 不得标成已校准。");

            // 证据：仅当 AppliedRuleId 存在时产出 rule 证据
            if (testCase.ExpectedRuleReference is null)
            {
                Assert.IsEmpty(result.Envelope.Evidence, $"[{testCase.Name}] AppliedRuleId 为 null ⇒ 不得产出证据。");
            }
            else
            {
                Assert.AreEqual(1, result.Envelope.Evidence.Count, $"[{testCase.Name}] 必须恰好一条规则证据。");
                Assert.AreEqual("rule", result.Envelope.Evidence[0].Kind, $"[{testCase.Name}] 证据种类必须是 rule。");
                Assert.AreEqual(testCase.ExpectedRuleReference, result.Envelope.Evidence[0].Reference, $"[{testCase.Name}] 证据引用必须是规则 id。");
                Assert.IsNull(result.Envelope.Evidence[0].Note, $"[{testCase.Name}] 证据无补充说明时不得编造。");
            }

            // 信封身份与可复现性字段
            Assert.AreEqual(ToolApprovalOperatorAdapter.SceneKeyValue, result.Envelope.SceneKey);
            Assert.AreEqual(context.InputDigest, result.Envelope.InputDigest);
            Assert.AreEqual(testCase.Verdict.LatencyMs, result.Envelope.LatencyMs, $"[{testCase.Name}] 耗时必须取被包装裁决的耗时。");
            Assert.IsFalse(result.Envelope.Cached, $"[{testCase.Name}] 适配器不参与判定缓存 ⇒ 恒为 false。");
            Assert.IsNull(result.Envelope.SourceEventIds, $"[{testCase.Name}] SourceEventIds 留空（不编造）。");
            Assert.IsNull(result.Envelope.SourceSha, $"[{testCase.Name}] SourceSha 留空（不编造）。");
            Assert.IsNull(result.Envelope.InstructionVersion, $"[{testCase.Name}] 包装器无指令 ⇒ 指令版本留空。");
            Assert.AreEqual(JudgementEnvelope.CurrentSchemaVersion, result.Envelope.SchemaVersion);
            Assert.AreEqual(OperatorTestData.Origin, result.Envelope.CreatedAtUtc, $"[{testCase.Name}] 时间必须来自注入时钟。");

            // 身份四元组由既有上下文提升，逐位一致
            Assert.IsNotNull(result.Envelope.Identity, $"[{testCase.Name}] 身份四元组必须被填充。");
            Assert.AreEqual(testCase.Context.WorkspaceId, result.Envelope.Identity!.WorkspaceId);
            Assert.AreEqual(testCase.Context.SessionId, result.Envelope.Identity!.SessionId);
            Assert.AreEqual(testCase.Context.AgentInstanceId, result.Envelope.Identity!.AgentInstanceId);
            Assert.AreEqual(testCase.Context.UserId, result.Envelope.Identity!.UserId);
        }
    }

    // ---------- ① 规范化标签与既有文档化规范键的一致 ----------

    [TestMethod]
    public void Equivalence_NormalizeLabel_CoversAllOutcomes_WithDocumentedKeys()
    {
        // 字面量期望：与既有 PerOutcomeConfidence 的文档化规范键逐字一致（不得另造命名）。
        Assert.AreEqual("allow_once", ToolApprovalOperatorAdapter.NormalizeLabel(ClassificationOutcome.AllowOnce));
        Assert.AreEqual("allow_permanent", ToolApprovalOperatorAdapter.NormalizeLabel(ClassificationOutcome.AllowPermanent));
        Assert.AreEqual("deny_once", ToolApprovalOperatorAdapter.NormalizeLabel(ClassificationOutcome.DenyOnce));
        Assert.AreEqual("deny_permanent", ToolApprovalOperatorAdapter.NormalizeLabel(ClassificationOutcome.DenyPermanent));
        Assert.AreEqual("unknown", ToolApprovalOperatorAdapter.NormalizeLabel(ClassificationOutcome.Unknown));

        // 全覆盖：每个既有枚举值都映射进规范键全集（新增成员若未登记会被此断言暴露）
        foreach (var outcome in Enum.GetValues<ClassificationOutcome>())
        {
            var label = ToolApprovalOperatorAdapter.NormalizeLabel(outcome);
            Assert.IsTrue(
                ToolApprovalOperatorAdapter.CanonicalLabels.Contains(label),
                $"既有结论 {outcome} 的规范化标签 {label} 必须在规范键全集内。");
        }

        // 规范键全集恰好这五个（不多不少；多出来的键会让消费者按不存在的键查置信度）
        CollectionAssert.AreEquivalent(
            new[] { "allow_once", "allow_permanent", "deny_once", "deny_permanent", "unknown" },
            ToolApprovalOperatorAdapter.CanonicalLabels.ToArray());
    }

    // ---------- ⑤ InputDigest 确定性 ----------

    [TestMethod]
    public void InputDigest_SameInputSameDigest_AndDifferentInputDifferentDigest()
    {
        var context = ToolApprovalAdapterTestData.Context();

        Assert.AreEqual(
            ToolApprovalOperatorContext.ComputeInputDigest(context),
            ToolApprovalOperatorContext.ComputeInputDigest(context),
            "同一输入两次计算必须得到同一指纹。");
        Assert.AreEqual(
            ToolApprovalOperatorContext.Create(context).InputDigest,
            ToolApprovalOperatorContext.Create(context).InputDigest,
            "规范构造路径两次也必须得到同一指纹。");

        // 形状：算法前缀 + 64 位小写十六进制（自描述，且断言不被随手改成别的摘要算法）
        var digest = ToolApprovalOperatorContext.ComputeInputDigest(context);
        Assert.StartsWith("sha256:", digest);
        Assert.AreEqual(7 + 64, digest.Length, "SHA-256 十六进制表示必须是 64 位。");
        Assert.AreEqual(digest, digest.ToLowerInvariant(), "十六进制必须统一小写（大小写不一致会撞出两个键）。");

        // 规范渲染被**独立实现**复算过（2026-09-21：按同样字段顺序 / 长度前缀在进程外用 SHA-256 重算，
        // 结果与本实现逐字一致）⇒ 固定字面量。若此处失败，说明指纹的渲染格式 / 字段集 / 顺序发生了
        // **破坏性变更**（缓存键与去重会整体失效），必须当成显式决策处理，而不是顺手改期望值。
        Assert.AreEqual(
            "sha256:1dd4402779aa86df2fa809b9fdbd5c5cf884eafb2b594a99e3cfca42e7bf0bd0",
            digest,
            "InputDigest 的规范渲染被固定为字段顺序 + 长度前缀；实际值=" + digest);

        // 规格 §3.2 列出的每个稳定字段，改动其一必须改变指纹
        (string Name, ToolCallClassificationContext Changed)[] distinctInputs =
        [
            ("ToolId", context with { ToolId = "file_write" }),
            ("CommandName", context with { CommandName = "dotnet build" }),
            ("ArgumentsJson", context with { ArgumentsJson = """{"command":"dotnet build"}""" }),
            ("WorkingDirectory", context with { WorkingDirectory = @"E:\other" }),
            ("Shell", context with { Shell = "wsl" }),
            ("WorkspaceId", context with { WorkspaceId = "ws-2" }),
            ("SessionId", context with { SessionId = "sess-2" }),
            ("AgentInstanceId", context with { AgentInstanceId = "agent-2" }),
            ("UserId", context with { UserId = "user-2" }),
        ];

        foreach (var (name, changed) in distinctInputs)
        {
            Assert.AreNotEqual(
                digest,
                ToolApprovalOperatorContext.ComputeInputDigest(changed),
                $"稳定字段 {name} 改变后指纹必须改变。");
        }

        // 人类撰写的说明文本与影响面标记**不参与**指纹（规格 §3.2 固定字段列表）：
        // 它们可在不改变实际调用的前提下被重写，纳入会让同一裁决的重复请求绕过去重与缓存。
        ToolCallClassificationContext[] descriptionOnlyChanges =
        [
            context with { OperationContext = "重写的操作背景" },
            context with { Purpose = "重写的目的" },
            context with { Necessity = "重写的必要性" },
            context with { FactBasis = ["另一条依据"] },
            context with { TargetResources = ["另一路径"] },
            context with { RecentTrajectory = "另一段轨迹" },
            context with { MatchedRuleSummaries = ["另一条规则摘要"] },
            context with { IsIrreversibleOperation = true },
            context with { MayDamageOrDeleteData = true },
        ];

        foreach (var changed in descriptionOnlyChanges)
        {
            Assert.AreEqual(
                digest,
                ToolApprovalOperatorContext.ComputeInputDigest(changed),
                "说明性 / 影响面字段不参与指纹：重写描述不得改变指纹。");
        }

        // 缺省值参与计算：null 与空串归一为同一取值（不跳过字段，避免「缺省」与「空」产生歧义）
        var nullish = context with { CommandName = null, ArgumentsJson = null, WorkingDirectory = null, Shell = null };
        var emptyish = context with { CommandName = string.Empty, ArgumentsJson = string.Empty, WorkingDirectory = string.Empty, Shell = string.Empty };
        Assert.AreEqual(
            ToolApprovalOperatorContext.ComputeInputDigest(nullish),
            ToolApprovalOperatorContext.ComputeInputDigest(emptyish),
            "null 与空串必须归一（缺省值参与计算）。");
        Assert.AreEqual(
            ToolApprovalOperatorContext.ComputeInputDigest(nullish),
            ToolApprovalOperatorContext.ComputeInputDigest(
                context with { CommandName = null, ArgumentsJson = null, WorkingDirectory = null, Shell = null }),
            "同一缺省输入两次必须同指纹。");
    }

    [TestMethod]
    public async Task InputDigest_AndJudgementId_AreStableAcrossRepeatedAdapterCalls()
    {
        var testCase = ToolApprovalAdapterTestData.Cases[0];
        var adapter = new ToolApprovalOperatorAdapter(
            new StubToolCallClassifier(testCase.Verdict),
            new FixedClock(OperatorTestData.Origin));

        var first = await adapter.ClassifyApprovalAsync(testCase.Context);
        var second = await adapter.ClassifyApprovalAsync(testCase.Context);

        Assert.AreEqual(first.Envelope.InputDigest, second.Envelope.InputDigest, "同输入两次调用必须同指纹。");
        Assert.AreEqual(first.Envelope.JudgementId, second.Envelope.JudgementId, "同输入两次调用必须同判定 id（可复现 / 可去重）。");

        var other = await adapter.ClassifyApprovalAsync(ToolApprovalAdapterTestData.Context(command: "another command"));
        Assert.AreNotEqual(first.Envelope.InputDigest, other.Envelope.InputDigest, "不同输入必须得到不同指纹。");
        Assert.AreNotEqual(first.Envelope.JudgementId, other.Envelope.JudgementId, "不同输入必须得到不同判定 id。");
    }

    [TestMethod]
    public async Task Equivalence_ConvenienceEntry_UsesSameCanonicalPathAsPortEntry()
    {
        var testCase = ToolApprovalAdapterTestData.Cases[0];
        var adapter = new ToolApprovalOperatorAdapter(
            new StubToolCallClassifier(testCase.Verdict),
            new FixedClock(OperatorTestData.Origin));

        var viaConvenience = await adapter.ClassifyApprovalAsync(testCase.Context);
        var viaPort = await adapter.ClassifyAsync(ToolApprovalOperatorContext.Create(testCase.Context));

        Assert.AreEqual(viaPort.PrimaryLabel, viaConvenience.PrimaryLabel);
        Assert.AreEqual(viaPort.Envelope.InputDigest, viaConvenience.Envelope.InputDigest);
        Assert.AreEqual(viaPort.Envelope.JudgementId, viaConvenience.Envelope.JudgementId);
        Assert.AreEqual(viaPort.Envelope.Reason, viaConvenience.Envelope.Reason);
    }

    // ---------- 降级契约：不冒泡异常 ----------

    [TestMethod]
    public async Task Degradation_InnerThrows_DoesNotBubble_AndUsesStableReasonCode()
    {
        var context = ToolApprovalOperatorContext.Create(ToolApprovalAdapterTestData.Context());
        var adapter = new ToolApprovalOperatorAdapter(
            StubToolCallClassifier.Throwing(new InvalidOperationException("inner down")),
            new FixedClock(OperatorTestData.Origin));

        var result = await adapter.ClassifyAsync(context);

        AssertDegraded(result, OperatorReasonCodes.CoreFailure, context.InputDigest);
        StringAssert.Contains(result.Envelope.Reason, nameof(InvalidOperationException), "理由必须写明异常类型，便于定位。");
        StringAssert.Contains(result.Envelope.Reason, "inner down", "理由必须保留异常消息。");
    }

    [TestMethod]
    public async Task Degradation_CancelledToken_UsesCancelledReasonCode()
    {
        var context = ToolApprovalOperatorContext.Create(ToolApprovalAdapterTestData.Context());
        var adapter = new ToolApprovalOperatorAdapter(
            new StubToolCallClassifier(ToolApprovalAdapterTestData.Cases[0].Verdict),
            new FixedClock(OperatorTestData.Origin));

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await adapter.ClassifyAsync(context, cts.Token);

        AssertDegraded(result, OperatorReasonCodes.Cancelled, context.InputDigest);
    }

    [TestMethod]
    public async Task Degradation_InnerReturnsNull_UsesCoreFailureReasonCode()
    {
        var context = ToolApprovalOperatorContext.Create(ToolApprovalAdapterTestData.Context());
        var adapter = new ToolApprovalOperatorAdapter(
            StubToolCallClassifier.ReturningNull(),
            new FixedClock(OperatorTestData.Origin));

        var result = await adapter.ClassifyAsync(context);

        AssertDegraded(result, OperatorReasonCodes.CoreFailure, context.InputDigest);
        StringAssert.Contains(result.Envelope.Reason, "null");
    }

    [TestMethod]
    public async Task Degradation_ForeignOrNullContext_UsesContextMismatch_WithoutThrowing()
    {
        var adapter = new ToolApprovalOperatorAdapter(
            new StubToolCallClassifier(ToolApprovalAdapterTestData.Cases[0].Verdict),
            new FixedClock(OperatorTestData.Origin));

        var foreign = await adapter.ClassifyAsync(new ForeignContext());
        AssertDegraded(foreign, OperatorReasonCodes.ContextMismatch, "foreign-digest");

        // 端口契约要求「上下文缺失 ⇒ 降级」，不是抛异常（与 S1a 基类入口语义一致）
        var missing = await adapter.ClassifyAsync(null!);
        AssertDegraded(missing, OperatorReasonCodes.ContextMismatch, string.Empty);
    }

    [TestMethod]
    public async Task Degradation_EmptyReason_IsReplacedByPlaceholder_WithoutTouchingOtherFields()
    {
        var testCase = ToolApprovalAdapterTestData.Cases[0];
        var verdict = testCase.Verdict with { Reason = "   " };
        var adapter = new ToolApprovalOperatorAdapter(
            new StubToolCallClassifier(verdict),
            new FixedClock(OperatorTestData.Origin));

        var result = await adapter.ClassifyAsync(ToolApprovalOperatorContext.Create(testCase.Context));

        Assert.AreEqual(ToolApprovalOperatorAdapter.MissingReasonPlaceholder, result.Envelope.Reason);
        Assert.AreEqual(testCase.ExpectedLabel, result.PrimaryLabel, "占位理由不得影响裁决结果字段。");
        Assert.AreEqual(testCase.ExpectedConfidence, result.Envelope.Confidence);
        Assert.AreEqual(testCase.Verdict.ReasonCode, result.Envelope.ReasonCode, "占位路径不得改动原因码。");
        Assert.AreEqual(testCase.Verdict.ClassifierId, result.Envelope.OperatorId);
        Assert.AreEqual(testCase.Verdict.ClassifierModel, result.Envelope.ModelId);
    }

    /// <summary>降级形状断言：主标签 null、分布空、置信 null、种类 Unknown、证据空、不冒泡异常。</summary>
    private static void AssertDegraded(ClassificationResult result, string expectedReasonCode, string expectedDigest)
    {
        Assert.IsNull(result.PrimaryLabel, "降级 ⇒ 主标签必须为 null（不得给出标签）。");
        Assert.IsEmpty(result.Distribution, "降级 ⇒ 分布必须为空字典（不得为 null，避免消费方空引用）。");
        Assert.AreEqual(expectedReasonCode, result.Envelope.ReasonCode, "降级必须携带稳定原因码。");
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.Envelope.Reason), "降级理由禁止留空。");
        Assert.IsNull(result.Envelope.Score);
        Assert.IsNull(result.Envelope.ScoreScale);
        Assert.IsNull(result.Envelope.Threshold);
        Assert.IsNull(result.Envelope.Confidence);
        Assert.AreEqual(ConfidenceKind.Unknown, result.Envelope.ConfidenceKind, "降级路径没有自报值可标 ⇒ Unknown（绝不 Calibrated）。");
        Assert.IsEmpty(result.Envelope.Evidence);
        Assert.AreEqual(ToolApprovalOperatorAdapter.SceneKeyValue, result.Envelope.SceneKey);
        Assert.AreEqual(JudgementEnvelope.CurrentSchemaVersion, result.Envelope.SchemaVersion);
        Assert.IsNull(result.Envelope.LatencyMs, "降级路径没有真实裁决耗时可报 ⇒ 留空。");
        Assert.AreEqual(expectedDigest, result.Envelope.InputDigest, "降级路径必须尽力保留可用指纹（不伪造）。");
    }
}
