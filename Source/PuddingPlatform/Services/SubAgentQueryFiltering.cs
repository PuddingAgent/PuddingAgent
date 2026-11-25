using PuddingCode.Abstractions;

namespace PuddingPlatform.Services;

/// <summary>
/// 子代理查询 / 清理的过滤器语义，抽成纯函数以便独立验证。
/// <para>
/// <b>为什么必须抽出来：</b>此前 <c>SubAgentManager.GetSubAgentsAsync</c> 收了
/// <see cref="SubAgentQueryFilter"/> 却直接忽略，返回父会话下的全部子代理。于是
/// <c>CleanupAsync</c> 传下去的 <c>Status</c> 与 <c>MaxResults</c> 全部失效——而调用方
/// （<c>SubAgentTool</c> 的 <c>pool_action=cleanup</c>）以为自己只清一条
/// （<c>MaxCount=1</c>、目标是某个具体成员），实际会把同一父会话下所有非运行中的
/// 子代理运行记录一起删掉。「参数被静默忽略」比「参数写错」危险得多：写错会报错，
/// 忽略只会让人以为约束生效了。
/// </para>
/// <para>
/// <b>为什么是纯函数而不是在 Manager 内部就地过滤：</b>Manager 的构造需要六个运行时依赖，
/// 为「过滤器有没有生效」去搭一整套假件，测试成本会高到没人愿意补；而这些判定本身
/// 只依赖入参与结果集合，没有任何副作用。
/// </para>
/// </summary>
internal static class SubAgentQueryFiltering
{
    /// <summary>
    /// 把 <see cref="SubAgentQueryFilter"/> 真正应用到查询结果上。
    /// <para>
    /// <c>Status</c> 为 <c>null</c> / 空白 / <c>"all"</c> 时视为不过滤：<c>"all"</c> 是
    /// <c>SubAgentTool</c> 的默认值，若把它当成字面状态去比较，默认行为会从「全部」变成「零条」。
    /// </para>
    /// </summary>
    internal static IReadOnlyList<SubAgentStatus> Apply(
        IReadOnlyList<SubAgentStatus> all,
        SubAgentQueryFilter? filter)
    {
        ArgumentNullException.ThrowIfNull(all);
        if (filter is null)
            return all;

        IEnumerable<SubAgentStatus> query = all;

        if (HasStatusFilter(filter.Status))
        {
            query = query.Where(s =>
                string.Equals(s.Status, filter.Status, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(filter.TemplateId))
        {
            query = query.Where(s =>
                string.Equals(s.TemplateId, filter.TemplateId, StringComparison.OrdinalIgnoreCase));
        }

        if (filter.MaxResults is > 0)
            query = query.Take(filter.MaxResults.Value);

        return query.ToList();
    }

    /// <summary>
    /// 按清理过滤器选出「可以删记录」的条目。运行中的一律排除：清理不得把正在跑的
    /// 子代理的运行记录删掉，那条记录是它收尾时唯一的落点。
    /// </summary>
    /// <remarks>
    /// <c>Take(MaxCount)</c> 必须在所有筛选<b>之后</b>：先截断再筛时，被截掉的可能是
    /// 不匹配的条目，于是「最多清 1 条」变成「最多清 0 条」——正是目标条目被清不掉的成因。
    /// </remarks>
    internal static IReadOnlyList<SubAgentStatus> SelectForCleanup(
        IReadOnlyList<SubAgentStatus> all,
        SubAgentCleanupFilter filter)
    {
        ArgumentNullException.ThrowIfNull(all);
        ArgumentNullException.ThrowIfNull(filter);

        IEnumerable<SubAgentStatus> query = all;

        // 指定成员时按 SubSessionId 精确匹配：名字可以改，会话 ID 不会。
        if (!string.IsNullOrWhiteSpace(filter.SubSessionId))
        {
            query = query.Where(s =>
                string.Equals(s.SubSessionId, filter.SubSessionId, StringComparison.Ordinal));
        }

        if (HasStatusFilter(filter.Status))
        {
            query = query.Where(s =>
                string.Equals(s.Status, filter.Status, StringComparison.OrdinalIgnoreCase));
        }

        if (filter.OlderThanDays is > 0)
        {
            var cutoff = DateTimeOffset.UtcNow.AddDays(-filter.OlderThanDays.Value);
            query = query.Where(s => s.SpawnedAt < cutoff);
        }

        query = query.Where(s => !IsRunning(s.Status));

        var maxCount = filter.MaxCount > 0 ? filter.MaxCount : 100;
        return query.Take(maxCount).ToList();
    }

    private static bool IsRunning(string? status)
        => string.Equals(status, "running", StringComparison.OrdinalIgnoreCase);

    private static bool HasStatusFilter(string? status)
        => !string.IsNullOrWhiteSpace(status)
           && !string.Equals(status, "all", StringComparison.OrdinalIgnoreCase);
}
