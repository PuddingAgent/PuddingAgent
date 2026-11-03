using Pudding.Contracts.Computer;

namespace PuddingComputer.UIAutomation;

/// <summary>
/// 把「任务怎么描述要找的元素」（<see cref="ElementSelector"/>，来自共享契约）
/// 与「观察到了什么」（<see cref="UiaNode"/>）接起来。
/// <para>
/// 这一跳是整个链路里第一次能真正判定"目标找到了没有"：
/// 在此之前，任务定义和观察结果各自存在，谁也看不懂谁。
/// </para>
/// </summary>
public static class UiaNodeMatching
{
    /// <summary>
    /// 该节点是否满足选择器。
    /// <para>
    /// 所有给出的字段都必须匹配（AND）。空选择器一律返回 <c>false</c> ——
    /// 让它匹配一切是最危险的默认值：调用方漏填空字段时会静默选中第一个节点。
    /// </para>
    /// <para>
    /// 大小写策略是有区别的：<see cref="ElementSelector.AutomationId"/> 与
    /// <see cref="ElementSelector.Name"/> 按**精确**比较（它们是应用给的值，
    /// 大小写本身有语义）；<see cref="ElementSelector.ControlType"/> 与
    /// <see cref="ElementSelector.ClassName"/> 忽略大小写（它们是 UIA / Win32
    /// 的类型标识，各家输出习惯不一致）。
    /// </para>
    /// </summary>
    public static bool Matches(this UiaNode node, ElementSelector selector)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(selector);

        if (!selector.HasAny)
            return false;

        if (!string.IsNullOrWhiteSpace(selector.AutomationId))
        {
            if (!string.Equals(node.AutomationId, selector.AutomationId, StringComparison.Ordinal))
                return false;
        }

        if (!string.IsNullOrWhiteSpace(selector.Name))
        {
            if (!string.Equals(node.Name, selector.Name, StringComparison.Ordinal))
                return false;
        }

        if (!string.IsNullOrWhiteSpace(selector.ControlType))
        {
            if (!string.Equals(node.ControlType, selector.ControlType, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        if (!string.IsNullOrWhiteSpace(selector.ClassName))
        {
            if (!string.Equals(node.ClassName, selector.ClassName, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    /// <summary>
    /// 找出快照中所有满足选择器的节点。
    /// <para>
    /// 返回多个时**不替调用方挑**：选择器写的条件如果命中多个，
    /// 说明它不够精确，应当由上层决定是收紧条件还是要求澄清。
    /// 静默取第一个会让"条件写松了"永远不被发现。
    /// </para>
    /// </summary>
    public static IReadOnlyList<UiaNode> FindAll(this UiaSnapshot snapshot, ElementSelector selector)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(selector);

        if (!selector.HasAny)
            return [];

        return snapshot.Nodes.Where(n => n.Matches(selector)).ToList();
    }

    /// <summary>
    /// 找出唯一匹配的节点。
    /// </summary>
    /// <param name="snapshot">被搜索的快照。</param>
    /// <param name="selector">定位条件。</param>
    /// <param name="match">命中且唯一时为该节点，否则为 null。</param>
    /// <param name="reason">未命中或不唯一时的可读原因。</param>
    /// <returns>是否得到唯一匹配。</returns>
    public static bool TryFindUnique(
        this UiaSnapshot snapshot,
        ElementSelector selector,
        out UiaNode? match,
        out string? reason)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(selector);

        match = null;
        reason = null;

        if (!selector.HasAny)
        {
            reason = "选择器为空，无法定位";
            return false;
        }

        var hits = snapshot.FindAll(selector);

        if (hits.Count == 0)
        {
            reason = $"未找到匹配 [{selector}] 的节点（快照共 {snapshot.Nodes.Count} 个）";
            return false;
        }

        if (hits.Count > 1)
        {
            reason = $"选择器 [{selector}] 命中 {hits.Count} 个节点，不唯一";
            return false;
        }

        match = hits[0];
        return true;
    }
}
