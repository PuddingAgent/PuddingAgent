namespace PuddingContextPruning;

/// <summary>
/// 确定性剪枝的配置快照（不可变）。所有 <c>*Chars</c> 都是 UTF-16 字符数，<c>*Tokens</c> 是估算 token 数。
/// <para>
/// 默认值表达一条取舍：小于 8000 字符的工具结果不值得为它打断前缀缓存；剪枝至少要省回 2000 token
/// 且不少于原体积的 5%，否则整份回退（迟滞，见 <c>DeterministicPayloadCompaction</c> 的准入地板）。
/// </para>
/// </summary>
public sealed record DeterministicPruneOptions
{
    /// <summary>总开关。关闭时 <c>TryApply</c> 原样返回入参列表实例，不读任何消息。</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>尾部受保护消息条数：最近 N 条永不修改（模型前缀稳定性的最后防线）。</summary>
    public int ProtectedTailMessages { get; init; } = 8;

    /// <summary>工具结果参与「首尾摘要」的最小字符数。</summary>
    public int MinToolResultChars { get; init; } = 8000;

    /// <summary>摘要保留的头部字符数。</summary>
    public int ToolResultHeadChars { get; init; } = 1600;

    /// <summary>摘要保留的尾部字符数。</summary>
    public int ToolResultTailChars { get; init; } = 400;

    /// <summary>工具结果参与「内容完全相同去重」的最小字符数。</summary>
    public int MinDuplicateChars { get; init; } = 512;

    /// <summary>工具参数参与「截断」的最小字符数。</summary>
    public int MinToolArgumentChars { get; init; } = 4000;

    /// <summary>参数截断保留的头部字符数。</summary>
    public int ToolArgumentHeadChars { get; init; } = 512;

    /// <summary>准入地板：回收 token 数低于此值则整份回退。</summary>
    public int MinReclaimTokens { get; init; } = 2000;

    /// <summary>准入地板：回收比例（回收量 / 剪枝前总量）低于此值则整份回退。</summary>
    public double MinReclaimRatio { get; init; } = 0.05;

    /// <summary>配置期 fail-fast：非法组合必须在启动阶段就炸，而不是在压缩路径上静默降级。</summary>
    public void Validate()
    {
        if (ProtectedTailMessages < 0)
            throw new ArgumentOutOfRangeException(
                nameof(ProtectedTailMessages),
                ProtectedTailMessages,
                "ProtectedTailMessages 必须 ≥ 0：受保护尾部长度不能为负。");

        if (MinToolResultChars <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(MinToolResultChars),
                MinToolResultChars,
                "MinToolResultChars 必须 > 0：否则任何工具结果都会被摘要。");

        if (ToolResultHeadChars < 0)
            throw new ArgumentOutOfRangeException(
                nameof(ToolResultHeadChars),
                ToolResultHeadChars,
                "ToolResultHeadChars 必须 ≥ 0。");

        if (ToolResultTailChars < 0)
            throw new ArgumentOutOfRangeException(
                nameof(ToolResultTailChars),
                ToolResultTailChars,
                "ToolResultTailChars 必须 ≥ 0。");

        // 用 long 求和：两个 int 相加可能溢出成负数，从而绕过这条检查。
        if ((long)ToolResultHeadChars + ToolResultTailChars >= MinToolResultChars)
            throw new InvalidOperationException(
                "ToolResultHeadChars + ToolResultTailChars 必须小于 MinToolResultChars："
                + "否则「摘要」并不比原文短。");

        if (MinDuplicateChars <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(MinDuplicateChars),
                MinDuplicateChars,
                "MinDuplicateChars 必须 > 0。");

        if (ToolArgumentHeadChars < 0)
            throw new ArgumentOutOfRangeException(
                nameof(ToolArgumentHeadChars),
                ToolArgumentHeadChars,
                "ToolArgumentHeadChars 必须 ≥ 0。");

        if (MinToolArgumentChars <= ToolArgumentHeadChars)
            throw new InvalidOperationException(
                "MinToolArgumentChars 必须大于 ToolArgumentHeadChars："
                + "否则截断后的参数不会比原文更短。");

        if (MinReclaimTokens < 0)
            throw new ArgumentOutOfRangeException(
                nameof(MinReclaimTokens),
                MinReclaimTokens,
                "MinReclaimTokens 必须 ≥ 0。");

        // 写成「不在区间内」而不是「小于或大于」，这样 NaN 也会被拒。
        if (!(MinReclaimRatio >= 0 && MinReclaimRatio <= 1))
            throw new ArgumentOutOfRangeException(
                nameof(MinReclaimRatio),
                MinReclaimRatio,
                "MinReclaimRatio 必须落在 [0, 1] 区间内。");
    }
}
