using PuddingRuntime.Services.Improvement.Rsi;

namespace PuddingRuntimeTests.Services.Improvement;

/// <summary>
/// RSI S3 增量 B1 契约测试：钉住纯函数装配器的冻结规则（规格 §2.3 / §2.4）。
/// <para>
/// 每条用例都对应一个真实的实现错误（变异即红），最危险的两类是：
/// ①「模仿 ADR-064 把含失败的轨迹整条丢弃」——A3 会红；
/// ②「不按 Sequence 排序而依赖 DB 返回顺序」——A2 会红。
/// </para>
/// </summary>
[TestClass]
public sealed class RsiTrajectoryAssemblerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 21, 10, 0, 0, TimeSpan.Zero);

    // ---------------------------------------------------------------- 构造辅助

    private static RsiEventRow Completed(long sequence, string payload, DateTimeOffset? at = null) => new()
    {
        Type = "tool.call.completed",
        Sequence = sequence,
        Payload = payload,
        OccurredAtUtc = at ?? T0.AddMinutes(sequence),
    };

    private static RsiEventRow Failed(long sequence, string? payload, DateTimeOffset? at = null) => new()
    {
        Type = "tool.call.failed",
        Sequence = sequence,
        Payload = payload,
        OccurredAtUtc = at ?? T0.AddMinutes(sequence),
    };

    private static RsiEventRow Requested(long sequence) => new()
    {
        Type = "tool.call.requested",
        Sequence = sequence,
        Payload = """{"name":"fs.read"}""",
        OccurredAtUtc = T0.AddMinutes(sequence),
    };

    private static RsiTurnSlice Slice(string turnId, params RsiEventRow[] events) => new()
    {
        WorkspaceId = "ws-1",
        AgentInstanceId = "agent-1",
        SessionId = "sess-1",
        TurnId = turnId,
        Events = events,
    };

    // ---------------------------------------------------------------- A1

    /// <summary>A1：一个 turn 两个 completed 步 ⇒ 两个 step、HasOutcomeAnomaly=false（变异：异常判定写反或漏算 ⇒ 红）。</summary>
    [TestMethod]
    public void A1_TwoCompletedSteps_ProducesTwoStepsWithoutAnomaly()
    {
        var trajectory = RsiTrajectoryAssembler.Assemble(
        [
            Slice("turn-a",
                Completed(1, """{"name":"fs.read","exitCode":0}"""),
                Completed(2, """{"name":"fs.write","exitCode":0,"output":"ok"}""")),
        ]);

        Assert.AreEqual(1, trajectory.Count);
        Assert.AreEqual(2, trajectory[0].Steps.Count);
        Assert.IsFalse(trajectory[0].HasOutcomeAnomaly, "全 Completed 的轨迹不得报异常标记。");
    }

    // ---------------------------------------------------------------- A2

    /// <summary>A2：事件顺序打乱 ⇒ 输出仍按 Sequence 升序（变异：不排序、依赖输入顺序 ⇒ 红）。</summary>
    [TestMethod]
    public void A2_OutOfOrderEvents_StepsSortedBySequenceAscending()
    {
        var trajectory = RsiTrajectoryAssembler.Assemble(
        [
            Slice("turn-a",
                Completed(3, """{"name":"c","exitCode":0}"""),
                Completed(1, """{"name":"a","exitCode":0}"""),
                Completed(2, """{"name":"b","exitCode":0}""")),
        ]);

        Assert.AreEqual(1, trajectory.Count);
        CollectionAssert.AreEqual(
            new long[] { 1, 2, 3 },
            trajectory[0].Steps.Select(step => step.Sequence).ToArray(),
            "Sequence 是唯一合法排序键，不得依赖输入顺序或 DB 返回顺序。");
    }

    // ---------------------------------------------------------------- A3

    /// <summary>A3：completed + failed 同一 turn ⇒ 两个 step 都在且 HasOutcomeAnomaly=true（变异：模仿 ADR-064「Any(Failed) ⇒ 整条作废」⇒ 红）。</summary>
    [TestMethod]
    public void A3_FailedStepNotDropped_TrajectoryKeepsBothStepsWithAnomaly()
    {
        var trajectory = RsiTrajectoryAssembler.Assemble(
        [
            Slice("turn-a",
                Completed(1, """{"name":"fs.read","exitCode":0}"""),
                Failed(2, """{"name":"fs.write","exitCode":1,"error":"disk full"}""")),
        ]);

        Assert.AreEqual(1, trajectory.Count, "含失败步的轨迹不得被整条丢弃（ADR-064 是反面教材）。");
        Assert.AreEqual(2, trajectory[0].Steps.Count, "失败步必须保留在 steps 里。");
        Assert.IsTrue(trajectory[0].HasOutcomeAnomaly, "有 Failed 步 ⇒ HasOutcomeAnomaly 必须为 true。");
        Assert.AreEqual(RsiToolOutcome.Failed, trajectory[0].Steps[1].Outcome);
        Assert.AreEqual("disk full", trajectory[0].Steps[1].ErrorPreview, "≤512 范围内 ErrorPreview 保持原文（Error 截断规则的短文本顺带覆盖）。");
    }

    // ---------------------------------------------------------------- A4

    /// <summary>A4：tool.call.failed 即使 payload 无 exitCode/error（Derive 会给 Unknown）⇒ Outcome 仍强制 Failed（变异：透传 Derive 的 Unknown ⇒ 红）。</summary>
    [TestMethod]
    public void A4_FailedEventForcesFailedOutcome_EvenWhenPayloadHasNoSignals()
    {
        var withSilentPayload = RsiTrajectoryAssembler.Assemble(
            [Slice("turn-a", Failed(1, """{"name":"no-signal.tool"}"""))])[0].Steps.Single();
        var withoutPayload = RsiTrajectoryAssembler.Assemble(
            [Slice("turn-b", Failed(1, null))])[0].Steps.Single();

        Assert.AreEqual(RsiToolOutcome.Failed, withSilentPayload.Outcome, "failed 事件语义上就是失败，不得落回 Unknown。");
        Assert.AreEqual(RsiToolOutcome.Failed, withoutPayload.Outcome, "payload 缺失也改变不了 failed 事件的语义。");
        Assert.IsNull(withSilentPayload.ExitCode, "payload 未提供 exitCode ⇒ 保持 null，不得用 0 冒充。");
        Assert.IsNull(withSilentPayload.ErrorPreview);
    }

    // ---------------------------------------------------------------- A5

    /// <summary>A5：只给 tool.call.requested 的 turn ⇒ 不产出轨迹（requested 一律忽略，变异：把 requested 也生成 step ⇒ 红）。</summary>
    [TestMethod]
    public void A5_OnlyRequestedEvents_ProducesNoTrajectory()
    {
        var trajectory = RsiTrajectoryAssembler.Assemble(
        [
            Slice("turn-a", Requested(1), Requested(2)),
        ]);

        Assert.AreEqual(0, trajectory.Count, "requested 不参与 step 生成；零步 turn 不产出轨迹。");
    }

    // ---------------------------------------------------------------- A6

    /// <summary>A6：Events 为空的 turn ⇒ 不产出轨迹（变异：产出空 Steps 的轨迹 ⇒ 红）。</summary>
    [TestMethod]
    public void A6_TurnWithNoEvents_ProducesNoTrajectory()
    {
        var trajectory = RsiTrajectoryAssembler.Assemble([Slice("turn-a")]);

        Assert.AreEqual(0, trajectory.Count, "Steps 为空的 turn 不产出轨迹（零工具调用不携带信号）。");
    }

    // ---------------------------------------------------------------- A7

    /// <summary>A7：payload 缺 name ⇒ ToolName == "(unknown)"（required string 不得留 null，变异：留 null ⇒ 红）。</summary>
    [TestMethod]
    public void A7_PayloadWithoutName_FallsBackToUnknownToolName()
    {
        var completed = RsiTrajectoryAssembler.Assemble(
            [Slice("turn-a", Completed(1, """{"exitCode":0}"""))])[0].Steps.Single();
        var failed = RsiTrajectoryAssembler.Assemble(
            [Slice("turn-b", Failed(1, null))])[0].Steps.Single();

        Assert.AreEqual("(unknown)", completed.ToolName, "completed 缺 name ⇒ 占位常量，不得 null。");
        Assert.AreEqual("(unknown)", failed.ToolName, "failed 缺 name ⇒ 占位常量，不得 null。");
        Assert.AreEqual(RsiToolOutcome.Completed, completed.Outcome, "缺 name 不影响 completed 的结局判定。");
    }

    // ---------------------------------------------------------------- A8

    /// <summary>A8：null / 空 slices ⇒ 空列表且不抛异常（变异：null 时抛 NRE 或返回 null ⇒ 红）。</summary>
    [TestMethod]
    public void A8_NullOrEmptyInput_ReturnsEmptyListWithoutThrowing()
    {
        var fromNull = RsiTrajectoryAssembler.Assemble(null!);
        var fromEmpty = RsiTrajectoryAssembler.Assemble([]);

        Assert.AreEqual(0, fromNull.Count, "null 输入 ⇒ 空列表，不得抛异常。");
        Assert.AreEqual(0, fromEmpty.Count, "空输入 ⇒ 空列表。");
    }

    // ---------------------------------------------------------------- A9

    /// <summary>A9：多 turn 各自独立装配，四元 identity 逐字段透传且产出顺序稳定（变异：identity 统一取第一片 ⇒ 红）。</summary>
    [TestMethod]
    public void A9_MultipleTurns_IdentityPassthroughWithoutCrossContamination()
    {
        var trajectory = RsiTrajectoryAssembler.Assemble(
        [
            new RsiTurnSlice
            {
                WorkspaceId = "ws-1", AgentInstanceId = "agent-1", SessionId = "sess-1", TurnId = "turn-1",
                Events = [Completed(1, """{"name":"a","exitCode":0}""")],
            },
            new RsiTurnSlice
            {
                WorkspaceId = "ws-2", AgentInstanceId = "agent-2", SessionId = "sess-2", TurnId = "turn-2",
                Events = [Completed(1, """{"name":"b","exitCode":0}""")],
            },
        ]);

        Assert.AreEqual(2, trajectory.Count, "每个 slice 恰好产出一条轨迹。");

        // 产出顺序与输入 slices 顺序一致（稳定）
        Assert.AreEqual("turn-1", trajectory[0].TurnId);
        Assert.AreEqual("turn-2", trajectory[1].TurnId);

        Assert.AreEqual("ws-1", trajectory[0].WorkspaceId);
        Assert.AreEqual("agent-1", trajectory[0].AgentInstanceId);
        Assert.AreEqual("sess-1", trajectory[0].SessionId);
        Assert.AreEqual("ws-2", trajectory[1].WorkspaceId);
        Assert.AreEqual("agent-2", trajectory[1].AgentInstanceId);
        Assert.AreEqual("sess-2", trajectory[1].SessionId);
    }

    // ---------------------------------------------------------------- A10

    /// <summary>A10：completed 步字段解析——ExitCode/Error/Output 来自 payload、OccurredAtUtc 取自事件行，缺失字段保持 null（变异：填 0/空串冒充或时间取错 ⇒ 红）。</summary>
    [TestMethod]
    public void A10_CompletedStep_FieldsFromPayloadAndTimestampFromEventRow()
    {
        var occurredAt = T0.AddMinutes(7);
        var trajectory = RsiTrajectoryAssembler.Assemble(
        [
            Slice("turn-a", Completed(5, """{"name":"web.search","output":"result","exitCode":0}""", occurredAt)),
        ]);

        var step = trajectory[0].Steps.Single();
        Assert.AreEqual("web.search", step.ToolName);
        Assert.AreEqual(RsiToolOutcome.Completed, step.Outcome);
        Assert.AreEqual(0, step.ExitCode);
        Assert.IsNull(step.ErrorPreview, "无 error ⇒ null，不得空串冒充。");
        Assert.AreEqual("result", step.OutputPreview, "短输出原样（B4 预览语义）。");
        Assert.AreEqual(6, step.OutputChars, "OutputChars = 截断前 UTF-16 字符数。");
        Assert.AreEqual(6, step.OutputBytes, "OutputBytes = 截断前 UTF-8 字节数（纯 ASCII 时与字符数相同）。");
        Assert.AreEqual(occurredAt, step.OccurredAtUtc, "OccurredAtUtc 必须取自事件行，不得自造时间。");
        Assert.AreEqual("turn-a", step.TurnId);
        Assert.AreEqual(5, step.Sequence);
    }
}

/// <summary>
/// RSI S3 增量 B4 契约测试（任务书 §5 测试矩阵）：截断预览（O1–O5）+ 参数指纹与配对（A1–A6）。
/// <para>每条用例钉住冻结契约的一条；变异方式写在各用例 XML 注释里（红名单见交付报告）。
/// 已知向量由 PowerShell 独立预计算后硬编码（不是被测实现的输出，避免自证）。</para>
/// </summary>
[TestClass]
public sealed class RsiTrajectoryAssemblerB4Tests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);

    // ── 已知向量：SHA-256(UTF-8(arguments 原文)) 小写 hex（实现外部独立计算）──

    private const string Args1 = """{"a":1}""";
    private const string Args1Hash = "015abd7f5cc57a2dd94b7590f04ad8084273905ee33ec5cebeae62276a97f862";

    /// <summary>与 Args1 仅内部空白不同（A5 零归一化守护）。</summary>
    private const string Args2 = """{"a": 1}""";
    private const string Args2Hash = "f9d86028c6e0d64e225186f96acb69338b2c59764df79162107f5c4bb34d1310";

    private const string Args3 = """{"command":"build","target":"Release"}""";
    private const string Args3Hash = "4e9cd6da4bb453c52318f7a13750881ac5da58df37bb275f6013bacb0e4351fd";

    private const string Args4 = """{"path":"C:/tmp/a.txt"}""";
    private const string Args4Hash = "548236c16103a7abf924ae1c57563851049d193a328cbe9282719257b3e958ee";

    // ── 夹具辅助 ──

    private static RsiEventRow Req(long sequence, string payload) => new()
    {
        Type = "tool.call.requested",
        Sequence = sequence,
        Payload = payload,
        OccurredAtUtc = T0.AddMinutes(sequence),
    };

    private static RsiEventRow Done(long sequence, string payload) => new()
    {
        Type = "tool.call.completed",
        Sequence = sequence,
        Payload = payload,
        OccurredAtUtc = T0.AddMinutes(sequence),
    };

    private static RsiTurnSlice Slice(string turnId, params RsiEventRow[] events) => new()
    {
        WorkspaceId = "ws-1",
        AgentInstanceId = "agent-1",
        SessionId = "sess-1",
        TurnId = turnId,
        Events = events,
    };

    private static RsiToolStep SingleStep(params RsiEventRow[] events)
        => RsiTrajectoryAssembler.Assemble([Slice("turn-a", events)]).Single().Steps.Single();

    /// <summary>把参数原文包成 JSON 字符串字面量（含引号与转义），用于拼 requested payload。</summary>
    private static string JsonQuote(string text) => System.Text.Json.JsonSerializer.Serialize(text);

    // ── O1 ──

    /// <summary>
    /// O1：长输出（600 字符 &gt; 512）⇒ OutputPreview.Length == 512、末字符 == …（U+2026）、前 511 字符为原文前缀。
    /// 变异：去掉截断（原文直返）⇒ 长度断言必红。
    /// </summary>
    [TestMethod]
    public void O1_LongOutput_PreviewTruncatedTo512CharsEndingWithEllipsis()
    {
        var payload = "{\"name\":\"t\",\"exitCode\":0,\"output\":\"" + new string('a', 600) + "\"}";
        var step = SingleStep(Done(2, payload));

        Assert.AreEqual(512, step.OutputPreview!.Length, "预览总长恰为 512（511 原文字符 + 1 个省略号字符）。");
        Assert.AreEqual('\u2026', step.OutputPreview[^1], "截断后末字符必须是省略号 U+2026。");
        Assert.AreEqual(new string('a', 511), step.OutputPreview[..511], "截断保留原文前 511 字符。");
    }

    // ── O2 ──

    /// <summary>
    /// O2：短输出（≤ 512）⇒ OutputPreview 原样且不含省略号。
    /// 变异：无条件追加省略号 ⇒ Contains 断言必红。
    /// </summary>
    [TestMethod]
    public void O2_ShortOutput_PreviewVerbatimWithoutEllipsis()
    {
        var step = SingleStep(Done(1, """{"name":"t","exitCode":0,"output":"short output"}"""));

        Assert.AreEqual("short output", step.OutputPreview, "≤512 ⇒ 原样，不加省略号。");
        Assert.IsFalse(step.OutputPreview!.Contains('\u2026'), "短输出不得含省略号。");
    }

    // ── O3 ──

    /// <summary>
    /// O3：计数取截断前原文：600 个中文字符（U+8F93，UTF-8 各 3 字节）⇒ OutputChars == 600（UTF-16）≠ OutputBytes == 1800（UTF-8）。
    /// 变异：改成截断后计数（都变 512 一族）或用 UTF-16 Length（600）冒充字节数 ⇒ 均必红。
    /// </summary>
    [TestMethod]
    public void O3_CountsAreOfOriginalBeforeTruncation_Utf8BytesDifferFromUtf16Chars()
    {
        var payload = "{\"name\":\"t\",\"exitCode\":0,\"output\":\"" + new string('\u8f93', 600) + "\"}";
        var step = SingleStep(Done(2, payload));

        Assert.AreEqual(512, step.OutputPreview!.Length, "（前提）该夹具确实触发了截断。");
        Assert.AreEqual(600, step.OutputChars, "OutputChars = 截断前原文的 UTF-16 字符数。");
        Assert.AreEqual(1800, step.OutputBytes, "OutputBytes = 截断前原文的 UTF-8 字节数（600×3；用 Length 冒充必红）。");
    }

    // ── O4 ──

    /// <summary>
    /// O4：原文缺失（payload 无 output 键）⇒ OutputPreview == null 且 OutputChars == 0 且 OutputBytes == 0。
    /// 变异：把缺失压平成空串 ⇒ IsNull 必红（「没有输出」与「输出为空」不可区分即违规）。
    /// </summary>
    [TestMethod]
    public void O4_MissingOutput_PreviewIsNullAndCountsAreZero()
    {
        var step = SingleStep(Done(1, """{"name":"t","exitCode":0}"""));

        Assert.IsNull(step.OutputPreview, "原文缺失 ⇒ null，不得压平成空串。");
        Assert.AreEqual(0, step.OutputChars);
        Assert.AreEqual(0, step.OutputBytes);
    }

    // ── O5 ──

    /// <summary>
    /// O5：原文是空串（output:""）⇒ OutputPreview 为空串（非 null）且 OutputChars == 0。
    /// 变异：与 O4 互换语义（空串 ⇒ null）⇒ IsNotNull 必红。
    /// </summary>
    [TestMethod]
    public void O5_EmptyStringOutput_PreviewIsEmptyStringNotNull()
    {
        var step = SingleStep(Done(1, """{"name":"t","exitCode":0,"output":""}"""));

        Assert.IsNotNull(step.OutputPreview, "空串原文不是缺失：Preview 必须是空串而非 null。");
        Assert.AreEqual(string.Empty, step.OutputPreview);
        Assert.AreEqual(0, step.OutputChars, "空串的字符数为 0 —— 但身份是空串，不是缺失。");
        Assert.AreEqual(0, step.OutputBytes);
    }

    // ── A1 ──

    /// <summary>
    /// A1：arguments 有值 ⇒ ArgsHash == SHA-256(原文, UTF-8) 小写 hex 64 位，且与独立预计算的已知向量逐字相等。
    /// 变异：改大写输出 / 换 SHA-1 / 截短长度 ⇒ 必红（执行时任取其一）。
    /// </summary>
    [TestMethod]
    public void A1_ArgumentsPresent_HashesToKnownSha256Vector()
    {
        var step = SingleStep(
            Req(1, "{\"name\":\"t\",\"arguments\":" + JsonQuote(Args3) + "}"),
            Done(2, """{"name":"t","exitCode":0}"""));

        Assert.AreEqual(64, step.ArgsHash!.Length, "SHA-256 小写 hex 固定 64 位。");
        Assert.AreEqual(Args3Hash, step.ArgsHash, "必须与独立预计算的已知向量相等（不得自证）。");
        Assert.AreEqual(step.ArgsHash.ToLowerInvariant(), step.ArgsHash, "必须是小写 hex。");
    }

    // ── A2 ──

    /// <summary>
    /// A2：arguments 缺失（无键）或纯空白 ⇒ ArgsHash == null。
    /// 变异：照抄 ConversationSkillEvolutionTrajectorySource.cs:115 的空对象兜底（?? "{}"）⇒ IsNull 必红（任务书 §4.3 的守护）。
    /// </summary>
    [TestMethod]
    public void A2_MissingOrBlankArguments_ArgsHashIsNull_NeverFallbackToEmptyObject()
    {
        var missing = SingleStep(
            Req(1, """{"name":"t"}"""),
            Done(2, """{"name":"t","exitCode":0}"""));
        var blank = SingleStep(
            Req(1, """{"name":"t","arguments":"   "}"""),
            Done(2, """{"name":"t","exitCode":0}"""));

        Assert.IsNull(missing.ArgsHash, "缺失 ⇒ null；不得用空对象字面量兜底（那是把缺失捏造成真实参数值）。");
        Assert.IsNull(blank.ArgsHash, "纯空白视同缺失 ⇒ null。");
    }

    // ── A3 ──

    /// <summary>
    /// A3：两个 requested 交错两个 completed（同名）⇒ 严格 FIFO：completed#1 拿 requested#1 的哈希，completed#2 拿 requested#2 的。
    /// 变异：改成「取最近一条 requested」（LIFO）⇒ 两个哈希互换 ⇒ 必红。
    /// </summary>
    [TestMethod]
    public void A3_InterleavedRequests_PairedFifoNotMostRecent()
    {
        var trajectory = RsiTrajectoryAssembler.Assemble(
        [
            Slice("turn-a",
                Req(1, "{\"name\":\"t\",\"arguments\":" + JsonQuote(Args3) + "}"),
                Req(2, "{\"name\":\"t\",\"arguments\":" + JsonQuote(Args4) + "}"),
                Done(3, """{"name":"t","exitCode":0}"""),
                Done(4, """{"name":"t","exitCode":0}""")),
        ]);

        var steps = trajectory.Single().Steps;
        Assert.AreEqual(2, steps.Count);
        Assert.AreEqual(Args3Hash, steps[0].ArgsHash, "第一个 completed 必须配最早的 requested（FIFO）。");
        Assert.AreEqual(Args4Hash, steps[1].ArgsHash, "第二个 completed 配第二个 requested，而不是最近一条。");
    }

    // ── A4 ──

    /// <summary>
    /// A4：completed 找不到同名未配对 requested ⇒ ArgsHash == null（不得跨名顶替、不得从 completed payload 猜参数）。
    /// 变异：桶不存在时用任意 requested 顶替（跨桶回退）⇒ IsNull 必红。
    /// </summary>
    [TestMethod]
    public void A4_CompletedWithoutMatchingRequest_ArgsHashIsNull()
    {
        var crossName = SingleStep(
            Req(1, "{\"name\":\"other\",\"arguments\":" + JsonQuote(Args3) + "}"),
            Done(2, """{"name":"t","exitCode":0}"""));
        var noRequestAtAll = SingleStep(Done(1, """{"name":"t","exitCode":0}"""));

        Assert.IsNull(crossName.ArgsHash, "只有同名 requested 才配对：不同名 ⇒ null，不得拿别的工具的参数指纹顶替。");
        Assert.IsNull(noRequestAtAll.ArgsHash, "完全没有 requested ⇒ null。");
    }

    // ── A5 ──

    /// <summary>
    /// A5：零归一化：原文 {"a":1} 与 {"a": 1}（仅内部空白不同）⇒ 哈希必须不同，且各自等于已知向量。
    /// 变异：加 trim / 先解析再重序列化（归一化输出）⇒ 哈希相等断言必红。
    /// </summary>
    [TestMethod]
    public void A5_NoNormalization_WhitespaceOnlyDifferenceMustChangeHash()
    {
        var trajectory = RsiTrajectoryAssembler.Assemble(
        [
            Slice("turn-a",
                Req(1, "{\"name\":\"t\",\"arguments\":" + JsonQuote(Args1) + "}"),
                Req(2, "{\"name\":\"t\",\"arguments\":" + JsonQuote(Args2) + "}"),
                Done(3, """{"name":"t","exitCode":0}"""),
                Done(4, """{"name":"t","exitCode":0}""")),
        ]);

        var steps = trajectory.Single().Steps;
        Assert.AreEqual(Args1Hash, steps[0].ArgsHash, "无内部空白的原文哈希必须等于已知向量。");
        Assert.AreEqual(Args2Hash, steps[1].ArgsHash, "带内部空白的原文哈希必须等于已知向量 —— 归一化实现会塌缩成同一个哈希。");
        Assert.AreNotEqual(steps[0].ArgsHash, steps[1].ArgsHash, "实现不得归一化：仅空白不同 ⇒ 哈希必须不同。");
    }

    // ── A6 ──

    /// <summary>
    /// A6：缺失参数的 requested 仍占 FIFO 槽位：requested(无 arguments) → requested(arguments=X) → completed#1 → completed#2
    /// ⇒ #1.ArgsHash == null 且 #2.ArgsHash == sha256(X)。单调用夹具抓不到「跳过占位」错误，必须交错。
    /// 变异：跳过无参数的 requested 不入队 ⇒ 两个哈希互换 ⇒ 双断言必红（任务书 §4.4 末条的守护）。
    /// </summary>
    [TestMethod]
    public void A6_MissingArgumentsRequest_StillOccupiesFifoSlot()
    {
        var trajectory = RsiTrajectoryAssembler.Assemble(
        [
            Slice("turn-a",
                Req(1, """{"name":"t"}"""),
                Req(2, "{\"name\":\"t\",\"arguments\":" + JsonQuote(Args3) + "}"),
                Done(3, """{"name":"t","exitCode":0}"""),
                Done(4, """{"name":"t","exitCode":0}""")),
        ]);

        var steps = trajectory.Single().Steps;
        Assert.AreEqual(2, steps.Count);
        Assert.IsNull(steps[0].ArgsHash, "completed#1 配的是无参数 requested ⇒ null（占位纪律）。");
        Assert.AreEqual(Args3Hash, steps[1].ArgsHash, "completed#2 配带参数的 requested ⇒ 其哈希；跳过占位会让两者互换。");
    }

    // ── A7（父代理验收补：§4.4 冻结① 的守护）──

    /// <summary>
    /// A7：配对必须按 <b>Sequence 升序</b>进行，而不是按事件数组的<b>下标顺序</b> ——
    /// 数组里把 completed(Sequence=2) 排在它的 requested(Sequence=1) <b>之前</b>，completed 仍须配到那个哈希。
    /// 变异：删掉配对前的 OrderBy（直接吃输入顺序）⇒ completed 先到、桶为空 ⇒ ArgsHash=null ⇒ 本用例必红。
    /// <para>为什么必须单独补：B1-A2 断言的是<b>输出</b>顺序，而装配器末尾还有一次 <c>steps.Sort</c> ——
    /// 父代理实测：删掉配对前排序后整个装配器套件 21/21 <b>全绿</b>，即该冻结规则在补本条之前
    /// <b>无任何断言保护</b>（尾部排序把上游那道防线掩盖了）。</para>
    /// </summary>
    [TestMethod]
    public void A7_CompletedListedBeforeItsRequest_StillPairsBySequence()
    {
        // 数组顺序故意「倒着放」：completed 在前、它的 requested 在后（与 Sequence 相反）。
        var step = SingleStep(
            Done(2, """{"name":"t","exitCode":0}"""),
            Req(1, "{\"name\":\"t\",\"arguments\":" + JsonQuote(Args3) + "}"));

        Assert.AreEqual(Args3Hash, step.ArgsHash, "配对前必须按 Sequence 升序排序：requested 虽在数组里靠后，completed 仍须配到它。");
    }
}
