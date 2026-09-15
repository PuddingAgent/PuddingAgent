namespace PuddingCode.Goals;

/// <summary>
/// ADR-092 §5.3（G92-1）：版本化检查定义。检查必须由受控执行器运行，
/// 不接受模型自由文本的 shell 字符串，也不允许在 verifier 内执行任意命令。
/// </summary>
public sealed record GoalCheckSpec
{
    public required string CheckId { get; init; }

    public required string CriterionId { get; init; }

    /// <summary>被检查条件的版本；版本变化必须重跑，旧报告失效。</summary>
    public int CriterionRevision { get; init; } = 1;

    /// <summary>检查类型，取值见 <see cref="GoalVerificationSpecKinds"/>。</summary>
    public required string Kind { get; init; }

    /// <summary>版本化检查定义引用（如 checks/regression.md#L12）。</summary>
    public string? DefinitionRef { get; init; }

    public string? DefinitionHash { get; init; }

    /// <summary>检查输入（文件/产物/提交），用于指纹与失效判断。</summary>
    public IReadOnlyList<string> InputRefs { get; init; } = [];

    /// <summary>执行检查时的工作树/输入指纹（覆盖工作树内容，不能只用 HEAD）。</summary>
    public string InputFingerprint { get; init; } = string.Empty;

    /// <summary>执行者角色：core / external_controller / human。</summary>
    public string ExecutorRole { get; init; } = "core";

    /// <summary>该检查期望产生的最小证据说明（人类可读，用于裁决展示）。</summary>
    public string? ExpectedEvidence { get; init; }

    /// <summary>仅 test 类检查使用：声明必须被真实执行的测试用例数（0 tests 不得通过）。</summary>
    public int? ExpectedTestCount { get; init; }
}

/// <summary>ADR-092 §5（G92-1）：受控检查的实际报告。verifier 只读它，不重新执行检查。</summary>
public sealed record GoalCheckReport
{
    public required string CheckId { get; init; }

    public required string CriterionId { get; init; }

    public int CriterionRevision { get; init; } = 1;

    /// <summary>取值见 <see cref="GoalCriterionResultStatuses"/>。</summary>
    public required string Status { get; init; }

    public IReadOnlyList<string> EvidenceRefs { get; init; } = [];

    /// <summary>执行本次检查时的输入指纹；与 spec 不一致即视为旧绿灯。</summary>
    public string? InputFingerprint { get; init; }

    /// <summary>执行本次检查时使用的定义 hash；与 spec 不一致即视为定义已变化。</summary>
    public string? DefinitionHash { get; init; }

    /// <summary>本次检查新生成的运行报告引用（build/test 必须非空，禁止复用旧报告）。</summary>
    public string? ReportRef { get; init; }

    /// <summary>进程退出码；build/test 期望 0。</summary>
    public int? ExitCode { get; init; }

    /// <summary>实际执行的测试用例数（由报告解析，不接受自我声明）。</summary>
    public int? ExecutedTestCount { get; init; }

    /// <summary>实际通过的测试用例数。</summary>
    public int? PassedTestCount { get; init; }

    /// <summary>实际失败的测试用例数。</summary>
    public int? FailedTestCount { get; init; }

    /// <summary>本次检查是否留下了未结束的后台进程。</summary>
    public bool? HasUnfinishedBackgroundProcess { get; init; }

    /// <summary>
    /// 产生该报告的受控执行器标识（如 goal-check-runner）。空值表示来源不可信，不得据此通过。
    /// </summary>
    public string? RunnerId { get; init; }

    /// <summary>
    /// 本次执行的 canonical 调用引用（InvocationId/RunId），用于回溯实际执行记录；
    /// 仅一个非空 ReportRef 字符串不足以证明执行过。
    /// </summary>
    public string? InvocationId { get; init; }

    public DateTimeOffset? ReportedAtUtc { get; init; }

    public string? FailureCode { get; init; }

    /// <summary>人类可读的失败/等待说明；不参与判定。</summary>
    public string? Message { get; init; }
}

/// <summary>受控检查执行上下文（只读事实，不含权限提升）。</summary>
public sealed record GoalCheckContext
{
    public required string GoalRunId { get; init; }

    public required int ActivationEpoch { get; init; }

    public required string WorkspaceId { get; init; }

    public required string AgentInstanceId { get; init; }

    public string? SessionId { get; init; }

    public string? WorkingDirectory { get; init; }

    /// <summary>单次检查的 deadline；到期应产生 waiting 报告而不是失败。</summary>
    public int? TimeoutSeconds { get; init; }
}

/// <summary>ADR-092 §6.2：verification/check 持久工作项的生命周期状态。</summary>
public static class GoalCheckRecordStatuses
{
    public const string Pending = "pending";

    /// <summary>已被某个执行器持租；租约过期后可被重新认领。</summary>
    public const string Leased = "leased";

    /// <summary>已产生真实运行报告；只有带 ReportJson 的 finished 记录才是可信证据。</summary>
    public const string Finished = "finished";
}

/// <summary>
/// ADR-092 §5.3（G92-1）：实际执行受控检查的执行器。
/// 约束：只执行传入的 <see cref="GoalCheckSpec"/>；经既有工具准入（ADR-091）执行；
/// 不写 Goal/Task 终态；不扩大权限；失败/等待必须如实报告，不得伪造通过。
/// </summary>
public interface IGoalCheckRunner
{
    Task<IReadOnlyList<GoalCheckReport>> RunAsync(
        IReadOnlyList<GoalCheckSpec> checks,
        GoalCheckContext context,
        CancellationToken ct = default);
}

public static class GoalCheckReportExtensions
{
    /// <summary>把检查报告投影为逐项条件结果；未知状态按 pending 保守处理。</summary>
    public static IReadOnlyList<GoalCriterionResult> ToCriterionResults(
        this IReadOnlyList<GoalCheckReport>? reports)
    {
        if (reports is null || reports.Count == 0)
            return [];

        return reports
            .Select(report => new GoalCriterionResult
            {
                CriterionId = report.CriterionId,
                CriterionRevision = report.CriterionRevision,
                Status = string.IsNullOrWhiteSpace(report.Status)
                    ? GoalCriterionResultStatuses.Pending
                    : report.Status,
                EvidenceRefs = report.EvidenceRefs,
                InputFingerprint = report.InputFingerprint,
                FailureCode = report.FailureCode,
            })
            .ToList();
    }

    /// <summary>是否仍有未完成的检查（pending/waiting）——用于登记证据等待而不是失败。</summary>
    public static bool HasPendingChecks(this IReadOnlyList<GoalCheckReport>? reports)
        => reports is not null && reports.Any(report =>
            string.Equals(report.Status, GoalCriterionResultStatuses.Pending, StringComparison.Ordinal)
            || string.Equals(report.Status, GoalCriterionResultStatuses.Waiting, StringComparison.Ordinal));

    /// <summary>是否存在失败检查（需要修复而不是等待）。</summary>
    public static bool HasFailedChecks(this IReadOnlyList<GoalCheckReport>? reports)
        => reports is not null && reports.Any(report =>
            string.Equals(report.Status, GoalCriterionResultStatuses.Failed, StringComparison.Ordinal));
}
