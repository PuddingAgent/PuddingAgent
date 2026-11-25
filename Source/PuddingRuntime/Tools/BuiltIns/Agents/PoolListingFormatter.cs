using System.Text;
using PuddingCode.Abstractions;

namespace PuddingRuntime.Services.Skills;

/// <summary>
/// <c>pool_action="list"</c> 的纯渲染层：条目进，Markdown 表格出。
/// <para>
/// 抽成纯函数是为了可测。这段渲染原先是 <see cref="SubAgentTool"/> 里的一行内联插值，
/// 「SubSessionId 只取前 8 位」这类缺陷只能靠人眼看产物才发现，测试永远抓不到 ——
/// 而产物恰好是模型唯一的信息来源。
/// </para>
/// </summary>
internal static class PoolListingFormatter
{
    /// <summary>
    /// SubSessionId 的渲染值：<b>原样完整输出</b>。
    /// <para>
    /// 池条目的子会话 id 形如 <c>&lt;父会话 32 位&gt;-sub-&lt;8 位&gt;</c>，同一会话的所有成员共享那 32 位前缀。
    /// 只截前缀会让整列五行完全相同：既分不出成员，也复制不出 <c>cleanup</c> 需要的
    /// <c>sub_agent_id</c>（工具文档要求用 list 查它）。截断不是「省地方」，是把这一列变成装饰。
    /// </para>
    /// </summary>
    internal static string RenderSubSessionId(string? subSessionId)
        => string.IsNullOrWhiteSpace(subSessionId) ? "-" : subSessionId.Trim();

    /// <summary>
    /// 「最后使用」列的渲染值：<b>本地时区的绝对时间</b>，格式 <c>yyyy-MM-dd HH:mm</c>；未使用过则 <c>-</c>。
    /// </summary>
    /// <remarks>
    /// 原始值来自 <c>DateTimeOffset.UtcNow</c>，库里存的是 <c>2026-11-21T04:26:17+00:00</c> 这种 UTC 文本。
    /// 直接按 <c>HH:mm:ss</c> 格式化会同时错两件事：<b>丢掉日期</b>（四天前的记录看起来像今天凌晨），
    /// <b>把 UTC 时刻当成本地时刻</b>（差 8 小时）。两个错都只在跨天/跨时区时才显形，所以能长期不被发现 ——
    /// 现场五个成员里 <c>frontend-ui</c> 的 last_used 是 11-21，而 list 显示「04:26:17」。
    /// <para>
    /// <c>InvariantCulture</c> 不是风格洁癖：某些区域（如 <c>th-TH</c>）的 <c>yyyy</c> 是佛历年份，
    /// 用当前区域格式化会把 2026 输出成 2569。
    /// </para>
    /// </remarks>
    internal static string RenderLastUsed(DateTimeOffset lastUsedAt, Func<DateTimeOffset, DateTimeOffset> toLocal)
        // 格式与「默认转本地」「InvariantCulture」三条不变量都在 ToolTimeRendering 里，
        // 这里只指定精度 —— 两处各写一遍格式串，同一个时刻迟早会渲染出两种字样。
        => ToolTimeRendering.RenderMinute(lastUsedAt, toLocal);

    /// <summary>
    /// 渲染池状态表。<paramref name="agents"/> 为空时由调用方先说清「本会话没有成员」。
    /// </summary>
    /// <param name="toLocal">
    /// UTC → 本地时区的转换。默认 <see cref="TimeZoneInfo.ConvertTime(DateTimeOffset, TimeZoneInfo)"/> 意义上的本地时区；
    /// 显式参数是为了让用例能注入确定转换 —— 依赖机器时区的断言等于用实际值证明实际值。
    /// </param>
    internal static string Format(
        IReadOnlyList<PooledSubAgent> agents,
        Func<DateTimeOffset, DateTimeOffset>? toLocal = null)
    {
        // 注意：`??` 的右操作数必须括号包裹 —— `toLocal ?? static t => ...` 会被解析为 (toLocal ?? static) t，
        // 报 CS0106「修饰符 static 对该项无效」+ CS1001。
        var convert = toLocal ?? (static t => t.ToLocalTime());
        var listing = new StringBuilder();
        listing.AppendLine($"## 子代理池状态 ({agents.Count} 个)\n");
        // 表头点明「本地」：不写的话读者无法判断 04:26 是哪个时区。
        listing.AppendLine("| 名称 | 状态 | 角色 | 任务数 | 最后使用(本地) | SubSessionId |");
        listing.AppendLine("|------|------|------|--------|----------|-------------|");
        foreach (var entry in agents)
        {
            listing.AppendLine(
                $"| {entry.Name} | {entry.Status} | {entry.Role ?? "-"} | {entry.TaskCount} | " +
                $"{RenderLastUsed(entry.LastUsedAt, convert)} | {RenderSubSessionId(entry.SubSessionId)} |");
        }

        return listing.ToString();
    }
}
