namespace PuddingComputer.UIAutomation;

/// <summary>
/// 把「读到了什么」折算成 <see cref="UiaAvailability"/>（纯函数，可单测）。
/// <para>
/// 阈值不是拍脑袋：下面每个数字都对应一次实测或一条明确的设计取舍。
/// </para>
/// </summary>
public static class UiaAvailabilityClassifier
{
    /// <summary>
    /// 可操作节点少于这个数就判 <see cref="UiaAvailability.Sparse"/>。
    /// <para>
    /// 取 3 的理由：一个可用的界面至少要「一个输入目标 + 一个确认动作 + 一个返回/取消」，
    /// 少于三个几乎肯定读不到真实界面结构。Chromium 只暴露 7 个 framework Pane
    /// 且没有任何可操作节点的情形，正好落在这一档。
    /// </para>
    /// </summary>
    public const int SparseThreshold = 3;

    /// <summary>
    /// 分类。
    /// </summary>
    /// <param name="totalNodes">枚举到的节点总数（含不可操作）。</param>
    /// <param name="actionableNodes">其中可操作的节点数。</param>
    /// <param name="timedOut">是否枚举超时。</param>
    /// <param name="error">错误信息；非空表示读取失败。</param>
    /// <returns>可用性四态。</returns>
    public static UiaAvailability Classify(
        int totalNodes,
        int actionableNodes,
        bool timedOut,
        string? error)
    {
        // 超时优先：它是"没读完"，不是"没有"。
        // 若先判 error 或 0 节点，会把一个慢应用误报成"不支持无障碍"。
        if (timedOut)
            return UiaAvailability.TimedOut;

        if (!string.IsNullOrEmpty(error))
            return UiaAvailability.Unavailable;

        if (totalNodes <= 0)
            return UiaAvailability.Unavailable;

        // 读到了树但可操作节点太少 —— 需要视觉兜底，但不能算失败。
        if (actionableNodes < SparseThreshold)
            return UiaAvailability.Sparse;

        return UiaAvailability.Supported;
    }

    /// <summary>直接从快照计数得出结论。</summary>
    public static UiaAvailability Classify(UiaSnapshot snapshot, bool timedOut, string? error)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return Classify(snapshot.Nodes.Count, snapshot.ActionableCount, timedOut, error);
    }

    /// <summary>
    /// 该可用性下是否应当继续信任无障碍结果。
    /// <para>
    /// 调用方据此决定要不要走视觉/OCR 兜底：<c>Supported</c> 与 <c>Sparse</c>
    /// 仍有部分信息可用，<c>Unavailable</c> 与 <c>TimedOut</c> 则必须换渠道。
    /// </para>
    /// </summary>
    public static bool ProvidesUsableTargets(this UiaAvailability availability) =>
        availability is UiaAvailability.Supported or UiaAvailability.Sparse;
}
