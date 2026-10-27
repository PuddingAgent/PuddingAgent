using PuddingFullTextIndex.Contracts;
using PuddingFullTextIndex.Infrastructure.Supply;

namespace PuddingFullTextIndex.Infrastructure.Maintenance;

/// <summary>
/// 把 <see cref="MaintenanceOptions"/> 的**原始配置**翻译成
/// <see cref="IFullTextIndexMaintenance.StartAsync"/> 需要的 scope 清单。
///
/// <para>
/// 为什么需要它：<c>MaintenanceOptions.Scopes</c> 允许绝对路径，也允许相对
/// <see cref="MaintenanceOptions.WorkspaceRoot"/> 的写法；而
/// <see cref="FullTextMaintenanceScope"/> 要求「已规范化的 RootPath + ScopeKey」。
/// 这套规范化规则（绝对化 / 去尾分隔符 / 分隔符统一 / 不变文化小写 / 重复与嵌套拒绝）
/// 的**唯一真源**是 <see cref="SupplyScopeNormalizer"/>，所以这里只做「相对 → 绝对」的合成，
/// 其余原样交给它，绝不在这里复刻第二份键算法。
/// </para>
///
/// <para>
/// ⚠️ 相对 scope 且 <see cref="MaintenanceOptions.WorkspaceRoot"/> 也不是绝对路径时，
/// 这里**不猜**基准目录，而是把原值原样交下去 —— 由规范化器以「相对路径」拒绝并给出可读原因。
/// 静默丢弃或静默替换基准都会让「配置写错了」表现成「配置没生效」。
/// </para>
///
/// <para>
/// 本类型**零 IO**：不做 <c>Directory.Exists</c>、不推导索引目录。
/// 目录存在性由规范化器判定（它需要结构化的拒绝原因），索引目录留给维护器
/// 用 <c>FullTextIndexPaths</c> 同一真源推导（<c>IndexDirectory: null</c>）。
/// </para>
/// </summary>
public static class FullTextMaintenanceScopePlanner
{
    /// <summary>
    /// 规划结果：可启动的 scope 清单 + 逐条拒绝说明。
    /// <para>两者都返回，调用方不得只看一半 —— 只看 Accepted 会把「全被拒绝」误读成「没有配置」。</para>
    /// </summary>
    /// <param name="Scopes">已规范化的 scope（可能为空，见 <paramref name="Rejections"/>）。</param>
    /// <param name="Rejections">被拒绝的 scope（可读说明，含原始取值与原因）。</param>
    public sealed record Plan(
        IReadOnlyList<FullTextMaintenanceScope> Scopes,
        IReadOnlyList<string> Rejections);

    /// <summary>按配置规划 scope 清单。纯函数：给定同一份选项必然得到同一结果。</summary>
    public static Plan Create(MaintenanceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var candidates = new List<string>(options.Scopes.Count);
        foreach (var raw in options.Scopes)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                // 原样交下去：规范化器会以「空值」拒绝并带上原因。
                candidates.Add(string.Empty);
                continue;
            }

            var trimmed = raw.Trim();
            if (Path.IsPathFullyQualified(trimmed))
            {
                candidates.Add(trimmed);
                continue;
            }

            if (!string.IsNullOrWhiteSpace(options.WorkspaceRoot)
                && Path.IsPathFullyQualified(options.WorkspaceRoot))
            {
                candidates.Add(Path.Combine(options.WorkspaceRoot!, trimmed));
                continue;
            }

            candidates.Add(trimmed);
        }

        var normalized = SupplyScopeNormalizer.Normalize(candidates);

        var scopes = new List<FullTextMaintenanceScope>(normalized.Accepted.Count);
        foreach (var accepted in normalized.Accepted)
        {
            scopes.Add(new FullTextMaintenanceScope(
                accepted.RootPath,
                accepted.ScopeKey,
                options.MaxIndexBytes,
                // null = 由维护器经 FullTextIndexPaths 自行推导，避免在这里复刻命名哈希。
                IndexDirectory: null));
        }

        var rejections = new List<string>(normalized.Rejections.Count);
        foreach (var rejection in normalized.Rejections)
            rejections.Add(rejection.ToString() ?? string.Empty);

        return new Plan(scopes, rejections);
    }
}
