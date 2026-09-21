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
        Assert.AreEqual("disk full", trajectory[0].Steps[1].Error);
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
        Assert.IsNull(withSilentPayload.Error);
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
        Assert.IsNull(step.Error, "无 error ⇒ null，不得空串冒充。");
        Assert.AreEqual("result", step.Output);
        Assert.AreEqual(occurredAt, step.OccurredAtUtc, "OccurredAtUtc 必须取自事件行，不得自造时间。");
        Assert.AreEqual("turn-a", step.TurnId);
        Assert.AreEqual(5, step.Sequence);
    }
}
