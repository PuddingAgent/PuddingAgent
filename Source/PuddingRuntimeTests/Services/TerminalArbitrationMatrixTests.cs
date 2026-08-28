using PuddingCode.Platform;
using PuddingRuntime.Services.AgentLoop;

namespace PuddingRuntimeTests.Services;

/// <summary>
/// P0 子代理终态事务 Phase 2：终态仲裁组合矩阵。
///
/// 覆盖两组真实策略的组合语义（不重构产品代码）：
///  1. <see cref="CompletionPolicy"/>——轮内信号优先级表（Cancelled &gt; Failed &gt; Waiting &gt; Done &gt; Continue）；
///  2. <see cref="AgentExecutionOutcomePolicy"/>——DONE/WAIT 名义成功后的 failure-only 回复降级。
///
/// 组合链路对齐 AgentExecutionService.Buffered.cs 的生产顺序：
///   verdict = CompletionPolicy.Evaluate(...) → verdict→execState 映射 →
///   executeIsSuccess = Completed|WaitingEvent → OutcomePolicy 降级 → 最终终态。
/// 映射表在此按规范复述（spec-lock：产品侧改映射时本矩阵必须同步更新）。
/// BudgetExhausted 不经由 CompletionPolicy 产生（来自 loop 耗尽 + grace 判定），
/// 其终态字符串矩阵由 PuddingPlatformTests.SubAgentManagerTerminalStatusMatrixTests 覆盖。
/// </summary>
[TestClass]
public sealed class TerminalArbitrationMatrixTests
{
    private const string CanonicalContract = CanonicalWorkReport.ExpectedOutputContract;

    private const string CanonicalReport = """
        SUMMARY:
        The delegated verification completed and produced a concrete result for the parent agent.
        CHANGES:
        none because the delegated task was read-only.
        EVIDENCE:
        The regression run shows two FAILED probes before the retry succeeded for file_read.
        RISKS:
        Runtime arbitration can change when the completion pipeline is modified.
        BLOCKERS:
        none because every required source artifact was available.
        """;

    private const string FailureOnlyReply =
        "执行失败：file_read 工具被权限策略拒绝，无法读取目标文件，任务未能产出任何交付物。";

    private const string NeutralReply =
        "任务完成：两处断言已按权威组件语义改写，定向测试全部通过。";

    // ─────────────────── ① CompletionPolicy 优先级表（含冻结/取消互斥行） ───────────────────

    [DataTestMethod]
    [DataRow("DONE", false, false, "Completed")]
    [DataRow("DONE", true, false, "Cancelled")]
    [DataRow("DONE", false, true, "Cancelled")]
    [DataRow("DONE", true, true, "Cancelled")]
    [DataRow("WAIT", false, false, "Waiting")]
    [DataRow("WAIT", true, false, "Cancelled")]
    [DataRow("WAIT", false, true, "Cancelled")]
    [DataRow("FAILED", false, false, "Failed")]
    [DataRow("FAILED", true, false, "Cancelled")]
    [DataRow("FAILED", false, true, "Cancelled")]
    [DataRow("CONTINUE", false, false, "Continue")]
    [DataRow("CONTINUE", true, false, "Cancelled")]
    [DataRow("CONTINUE", false, true, "Cancelled")]
    [DataRow("done", false, false, "Completed")]
    [DataRow("Wait", false, false, "Waiting")]
    [DataRow("failed", false, false, "Failed")]
    public void CompletionPolicy_Evaluate_PriorityMatrix(
        string status,
        bool isCancelled,
        bool isFrozen,
        string expectedVerdict)
    {
        var verdict = new CompletionPolicy().Evaluate(
            CreateContext(),
            new AgentLoopResponse { Status = status, Message = "matrix probe" },
            turns: [],
            isCancelled: isCancelled,
            isFrozen: isFrozen);

        Assert.AreEqual(
            Enum.Parse<CompletionVerdict>(expectedVerdict),
            verdict,
            $"status={status} cancelled={isCancelled} frozen={isFrozen}");
    }

    // ─────────────── ② CompletionPolicy × OutcomePolicy 全组合仲裁矩阵 ───────────────

    [DataTestMethod]
    [DataRow("DONE", false, false, 0, "canonical", "Completed")]
    [DataRow("DONE", false, false, 1, "canonical", "Completed")]   // 事故2 形态1：可恢复工具失败 + canonical report
    [DataRow("DONE", false, false, 3, "canonical", "Completed")]
    [DataRow("DONE", false, false, 1, "failure-only", "Failed")]   // 事故2 形态2：failure-only 回复降级
    [DataRow("DONE", false, false, 0, "failure-only", "Completed")] // 无工具失败不降级
    [DataRow("DONE", false, false, 1, "neutral", "Completed")]
    [DataRow("WAIT", false, false, 0, "canonical", "WaitingEvent")]
    [DataRow("WAIT", false, false, 1, "canonical", "WaitingEvent")]
    [DataRow("WAIT", false, false, 1, "failure-only", "Failed")]   // Waiting 名义成功同样可被降级
    [DataRow("FAILED", false, false, 1, "canonical", "Failed")]
    [DataRow("FAILED", false, false, 0, "failure-only", "Failed")]
    [DataRow("CONTINUE", false, false, 1, "neutral", "Running")]   // 非终态 verdict 原样透传
    [DataRow("DONE", true, false, 1, "failure-only", "Cancelled")] // 取消最高优先，不进入降级分支
    [DataRow("DONE", true, false, 0, "canonical", "Cancelled")]
    [DataRow("DONE", false, true, 1, "failure-only", "Cancelled")] // 冻结与取消同级
    public void ArbitrationMatrix_CompletionPolicy_x_OutcomePolicy(
        string status,
        bool isCancelled,
        bool isFrozen,
        int toolFailureCount,
        string replyKind,
        string expectedState)
    {
        var finalReply = SelectReply(replyKind);

        var execState = ResolveTerminalOutcome(
            status, isCancelled, isFrozen, toolFailureCount, finalReply, CanonicalContract);

        Assert.AreEqual(
            Enum.Parse<AgentExecutionState>(expectedState),
            execState,
            $"status={status} cancelled={isCancelled} frozen={isFrozen} toolFailures={toolFailureCount} reply={replyKind}");
    }

    // ─────────────────── ③ 事故2 机制形态回归（显式命名） ───────────────────

    [TestMethod]
    public void Incident2_DoneWithEarlyRecoverableFileReadFailures_AndCanonicalReport_StaysCompleted()
    {
        // 事故2 原始形态：子代理前几轮 file_read 连续被拒（toolFailureCount>0），但最终
        // 交付了结构化 canonical 五段报告（EVIDENCE 中合法出现 "FAILED" 字样）——终态必须是 Completed。
        var execState = ResolveTerminalOutcome(
            status: "DONE",
            isCancelled: false,
            isFrozen: false,
            toolFailureCount: 3,
            finalReply: CanonicalReport,
            expectedOutputContract: CanonicalContract);

        Assert.AreEqual(AgentExecutionState.Completed, execState);
    }

    [TestMethod]
    public void Incident2_DoneWithFailureOnlyReply_DowngradesToFailed()
    {
        // 事故2 原始形态：DONE 信号 + 只有失败解释的最终回复（无 canonical 报告）——
        // 名义成功必须降级为 Failed，父代理不得将其误判为成功交付。
        var execState = ResolveTerminalOutcome(
            status: "DONE",
            isCancelled: false,
            isFrozen: false,
            toolFailureCount: 1,
            finalReply: FailureOnlyReply,
            expectedOutputContract: CanonicalContract);

        Assert.AreEqual(AgentExecutionState.Failed, execState);
    }

    [TestMethod]
    public void Incident2_CancelSignal_DominatesFailureOnlyReply()
    {
        // 取消信号优先于降级判定：Cancelled 不是 "executeIsSuccess"，永远不进入降级分支。
        var execState = ResolveTerminalOutcome(
            status: "DONE",
            isCancelled: true,
            isFrozen: false,
            toolFailureCount: 1,
            finalReply: FailureOnlyReply,
            expectedOutputContract: CanonicalContract);

        Assert.AreEqual(AgentExecutionState.Cancelled, execState);
    }

    // ─────────────────────────── 组合仲裁助手（规范复述） ───────────────────────────

    /// <summary>
    /// 按生产顺序组合两组真实策略。verdict→execState 映射复述自
    /// AgentExecutionService.Buffered.cs 的 DONE/WAIT/FAILED/CANCELLED 分支。
    /// </summary>
    private static AgentExecutionState ResolveTerminalOutcome(
        string status,
        bool isCancelled,
        bool isFrozen,
        int toolFailureCount,
        string? finalReply,
        string? expectedOutputContract)
    {
        var verdict = new CompletionPolicy().Evaluate(
            CreateContext(),
            new AgentLoopResponse { Status = status, Message = finalReply },
            turns: [],
            isCancelled: isCancelled,
            isFrozen: isFrozen);

        var execState = verdict switch
        {
            CompletionVerdict.Completed => AgentExecutionState.Completed,
            CompletionVerdict.Waiting => AgentExecutionState.WaitingEvent,
            CompletionVerdict.Failed => AgentExecutionState.Failed,
            CompletionVerdict.Cancelled => AgentExecutionState.Cancelled,
            _ => AgentExecutionState.Running,
        };

        var executeIsSuccess = execState is AgentExecutionState.Completed or AgentExecutionState.WaitingEvent;
        if (AgentExecutionOutcomePolicy.ShouldDowngradeSuccessfulExecution(
                executeIsSuccess,
                toolFailureCount,
                finalReply,
                expectedOutputContract))
        {
            execState = AgentExecutionState.Failed;
        }

        return execState;
    }

    private static string SelectReply(string replyKind) => replyKind switch
    {
        "canonical" => CanonicalReport,
        "failure-only" => FailureOnlyReply,
        _ => NeutralReply,
    };

    private static AgentLoopContext CreateContext() => new()
    {
        SessionId = "session-arbitration-matrix",
        AgentInstanceId = "agent-arbitration-matrix",
        WorkspaceId = "workspace-1",
        AgentTemplateId = "template-arbitration-matrix",
        UserMessage = "arbitration matrix probe",
        MaxRounds = 10,
    };
}
