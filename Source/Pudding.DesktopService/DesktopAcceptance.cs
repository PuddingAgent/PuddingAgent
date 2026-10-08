namespace Pudding.DesktopService;

/// <summary>
/// **两段式验收的判定**（项目强制规则：内部 Agent 不能独立验收承载自身的 Desktop/Core 生命周期）。
///
/// 规则原文落成可执行判定，原因是它最容易被"看起来验证过了"绕过：
/// 内部 Agent 能测当前进程**已经加载**的成品代码与工具，但
/// ① 它不能证明刚改的代码已被当前进程加载（改动可能根本没生效）；
/// ② 它不能独立判定启动/重启/崩溃恢复/退出回收（单实例会把第二次启动转发给旧进程，退出后观察不到子进程回收）。
///
/// 因此把「阶段 + 声明者 + 证据」摆在一起判，并**明确拒绝**不该由该角色作出的结论。
/// </summary>
public static class DesktopAcceptance
{
    /// <summary>验收阶段（顺序即推进方向）。</summary>
    public enum Stage
    {
        /// <summary>内部 Agent 交付：代码已构建、可交给外部控制器部署。</summary>
        ReadyForExternalDeploy,

        /// <summary>在新构建上跑通产品内功能 smoke（仍不含生命周期结论）。</summary>
        InProductFunctionalComplete,

        /// <summary>生命周期结论（启动/重启/崩溃恢复/退出回收）——**只能由外部控制器**判定。</summary>
        ExternalLifecycleVerified,
    }

    /// <summary>谁在声明这个结论。</summary>
    public enum Claimant
    {
        /// <summary>运行在产品内部的 Agent（能测已加载的成品，不能判定自身生命周期）。</summary>
        InternalAgent,

        /// <summary>进程外控制器（负责部署新构建、重启、观察退出回收）。</summary>
        ExternalController,
    }

    /// <summary>一次验收声明所带的事实。</summary>
    public sealed record Evidence(
        Stage Stage,
        Claimant Claimant,
        string? BuildId,
        IReadOnlyList<string>? Checks,
        bool RanOnANewBuild,
        bool ExercisesLifecycle);

    /// <summary>判定是否允许作出该声明；不允许时给出**具体**理由（不含糊其辞）。</summary>
    public static bool CanClaim(Evidence evidence, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        reason = null;

        if (!Enum.IsDefined(evidence.Stage) || !Enum.IsDefined(evidence.Claimant))
        {
            reason = "acceptance stage and claimant must be registered values";
            return false;
        }

        if (string.IsNullOrWhiteSpace(evidence.BuildId))
        {
            // 没有构建标识的结论无法复核（"测的是哪一版"必须能被指出来）。
            reason = "an acceptance claim must name the build it was verified against";
            return false;
        }

        switch (evidence.Stage)
        {
            case Stage.ReadyForExternalDeploy:
                if (evidence.Checks is not { Count: > 0 })
                {
                    reason = "ready-for-external-deploy requires at least one recorded check";
                    return false;
                }

                return true;

            case Stage.InProductFunctionalComplete:
                if (!evidence.RanOnANewBuild)
                {
                    // 这是最容易骗过自己的一条：在**旧进程**上看到功能可用，并不能证明新代码被加载。
                    reason = "in-product functional completion must be verified on a newly started build,"
                        + " not in a process that may still be running older code";
                    return false;
                }

                if (evidence.Checks is not { Count: > 0 })
                {
                    reason = "in-product functional completion requires at least one recorded check";
                    return false;
                }

                return true;

            case Stage.ExternalLifecycleVerified:
                if (evidence.Claimant != Claimant.ExternalController)
                {
                    // 承载自身的进程判定不了自己的生命周期（单实例转发 / 退出后无法观察子进程）。
                    reason = "startup, restart, crash recovery and exit reclamation can only be judged"
                        + " by an external controller";
                    return false;
                }

                if (!evidence.ExercisesLifecycle)
                {
                    reason = "an external lifecycle verdict must actually exercise the lifecycle";
                    return false;
                }

                return true;

            default:
                reason = "unregistered acceptance stage";
                return false;
        }
    }

    /// <summary>阶段线名（与文档/交付物里的用词一致，避免中英混用时对不上）。</summary>
    public static string NameOf(Stage stage) => stage switch
    {
        Stage.ReadyForExternalDeploy => "ready-for-external-deploy",
        Stage.InProductFunctionalComplete => "in-product-functional-complete",
        Stage.ExternalLifecycleVerified => "external-lifecycle-verified",
        _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "Stage is not registered."),
    };
}
