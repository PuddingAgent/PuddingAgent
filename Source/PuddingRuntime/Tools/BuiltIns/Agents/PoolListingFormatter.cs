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

    /// <summary>渲染池状态表。<paramref name="agents"/> 为空时由调用方先说清「本会话没有成员」。</summary>
    internal static string Format(IReadOnlyList<PooledSubAgent> agents)
    {
        var listing = new StringBuilder();
        listing.AppendLine($"## 子代理池状态 ({agents.Count} 个)\n");
        listing.AppendLine("| 名称 | 状态 | 角色 | 任务数 | 最后使用 | SubSessionId |");
        listing.AppendLine("|------|------|------|--------|----------|-------------|");
        foreach (var entry in agents)
        {
            listing.AppendLine(
                $"| {entry.Name} | {entry.Status} | {entry.Role ?? "-"} | {entry.TaskCount} | " +
                $"{entry.LastUsedAt:HH:mm:ss} | {RenderSubSessionId(entry.SubSessionId)} |");
        }

        return listing.ToString();
    }
}
