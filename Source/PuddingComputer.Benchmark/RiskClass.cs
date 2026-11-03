namespace PuddingComputer.Benchmark;

/// <summary>
/// 动作的风险等级。决定"失败或不确定时还能不能再试"。
/// <para>
/// 这个区分不是形式主义：盲目重试一次「发送消息」是补救，盲目重试一次「删除文件」
/// 或「付款」是灾难。所以风险必须先于重试策略声明，且由任务静态给定，不能运行时降级。
/// </para>
/// </summary>
public enum RiskClass
{
    /// <summary>只读：观察、截图、读取值。重试无副作用。</summary>
    ReadOnly,

    /// <summary>可逆：输入文字、打开菜单、切换标签。可安全重试，必要时手动回滚。</summary>
    Reversible,

    /// <summary>
    /// 非幂等：发送消息、提交表单。重复执行会改变结果，不能盲目重试。
    /// </summary>
    NonIdempotent,

    /// <summary>
    /// 破坏性或敏感：删除、覆盖、付款、改权限。
    /// 除"不盲目重试"外，还应在执行前要求澄清。
    /// </summary>
    DestructiveOrSensitive,
}

/// <summary>风险是否需要额外保护（不盲重试 + 执行前澄清）。</summary>
public static class RiskClassExtensions
{
    /// <summary>
    /// 该风险等级下，<c>Unknown</c> 结果是否禁止继续尝试。
    /// <para>
    /// 只读与可逆动作允许在不确定时再试一次；非幂等与破坏性动作不允许 ——
    /// 因为"上一步可能已经生效"正是它们最危险的失败形态。
    /// </para>
    /// </summary>
    public static bool ForbidsRetryOnUnknown(this RiskClass risk) =>
        risk is RiskClass.NonIdempotent or RiskClass.DestructiveOrSensitive;

    /// <summary>执行前是否必须先向用户澄清。</summary>
    public static bool RequiresClarificationBeforeActing(this RiskClass risk) =>
        risk is RiskClass.DestructiveOrSensitive;
}
