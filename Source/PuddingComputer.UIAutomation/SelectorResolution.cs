namespace PuddingComputer.UIAutomation;

/// <summary>选择器解析的分类结果。</summary>
public enum ResolutionOutcome
{
    /// <summary>唯一命中，可以安全操作。</summary>
    Ok,

    /// <summary>遍历完整且确实没有命中。</summary>
    NotFound,

    /// <summary>命中多个，拒绝替调用方挑第一个。</summary>
    Ambiguous,

    /// <summary>遍历被截断（子树读取失败或属性读不到），因此既不能断言唯一、也不能断言不存在。</summary>
    Partial,
}

/// <summary>
/// 把「命中数 + 遍历是否被截断」判定成解析结论。
/// <para>
/// 抽成纯函数的原因：这条判据决定「能不能动手改别人的界面」，
/// 但它的输入来自不可靠的 provider（子树随时可能读失败）。只有把它与 IO 分离，
/// 才能用穷举用例证明每一种组合的结论都是对的。
/// </para>
/// <para>
/// 一条纪律：**遍历被截断时不得给出 Ok 或 NotFound**。
/// 截断后只看到一个命中，可能只是其余命中还没遍历到；截断后一个都没看到，
/// 也可能只是没走到。把这两种情况报成「唯一」或「不存在」，是最容易写错地方的一条路。
/// </para>
/// </summary>
public static class SelectorResolution
{
    /// <summary>判定解析结论。</summary>
    /// <param name="hitCount">已命中的元素数。</param>
    /// <param name="traversalTruncated">遍历是否被截断。</param>
    public static ResolutionOutcome Decide(int hitCount, bool traversalTruncated)
    {
        if (hitCount > 1)
            return ResolutionOutcome.Ambiguous;

        if (traversalTruncated)
            return ResolutionOutcome.Partial;

        return hitCount == 1 ? ResolutionOutcome.Ok : ResolutionOutcome.NotFound;
    }
}
