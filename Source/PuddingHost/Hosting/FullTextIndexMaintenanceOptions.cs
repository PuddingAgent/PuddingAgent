using PuddingFullTextIndex;
using PuddingFullTextIndex.Contracts;
using PuddingFullTextIndex.Infrastructure.Maintenance;

namespace PuddingHost.Hosting;

/// <summary>
/// S5b（2026-09-27）：全文索引「局部维护循环」的**宿主侧薄壳** —— 只提供两件东西：
/// ① 配置节名（<c>system.json:FullTextIndex:Maintenance</c>）；
/// ② 「宿主配置 → 组件维护选项」的**单一真源**纯函数映射。
/// <para>
/// ⚠️ 本类型**不是**第二份选项：字段全部来自组件的 <see cref="MaintenanceOptions"/>
/// （维护节直接绑定它）与既有 <see cref="FullTextIndexSupplyOptions"/> /
/// <see cref="FullTextIndexOptions"/>（供给与查询侧同源）。宿主**不允许**再写一份 scope 列表、
/// 再写一个预算字面量或再写一个索引根 —— 两处真源迟早会对「要维护哪些目录、预算多大」给出不同答案。
/// </para>
/// <para>
/// 为什么合并要点是 <b>Scopes / WorkspaceRoot / MaxIndexBytes / IndexRootDirectory</b> 四项：
/// 它们是「维护哪些语料根、相对 scope 以谁为基准、预算多大、checkpoint 落在哪个索引根」的唯一真源，
/// 组件把它们当身份与硬限使用（<see cref="FullTextMaintenanceScope.ScopeKey"/> 即由语料根推导）。
/// 其余旋钮（<c>QueueCapacity</c> / <c>RecoveryScanInterval</c> / <c>Debounce</c> …）只属于维护节，
/// 由绑定结果原样保留。
/// </para>
/// </summary>
public static class FullTextIndexMaintenanceOptions
{
    /// <summary>
    /// 配置节名（**唯一**）：Data 目录 <c>system.json</c> 里 <c>FullTextIndex</c> 下的
    /// <c>Maintenance</c> 子节。默认（无该子节）= <c>Enabled=false</c> ⇒ 零副作用。
    /// </summary>
    public const string SectionName = "FullTextIndex:Maintenance";

    /// <summary>
    /// 把「维护节绑定的组件选项」与「供给 / 查询侧同源配置」合并成本次**生效**的维护选项。
    /// <para>
    /// 口径（D1，逐条可测）：
    /// <list type="bullet">
    /// <item><c>Scopes</c> ← <see cref="FullTextIndexSupplyOptions.Scopes"/>（供给侧同一份清单）；</item>
    /// <item><c>WorkspaceRoot</c> ← <see cref="FullTextIndexSupplyOptions.WorkspaceRoot"/>（相对 scope 的解析基准同源）；</item>
    /// <item><c>MaxIndexBytes</c> ← <see cref="FullTextIndexSupplyOptions.MaxIndexBytes"/>（预算**不写第二遍字面量**）；</item>
    /// <item><c>IndexRootDirectory</c> ← <see cref="FullTextIndexOptions.IndexRootDirectory"/>
    /// （与查询侧同一个索引根实例，checkpoint 只能落在它下面）。</item>
    /// </list>
    /// 其余字段（含 <c>Enabled</c> 与全部数值旋钮）逐字保留绑定值 —— 本函数**不做**任何默认值兜底，
    /// 也**不修改**入参（<see cref="MaintenanceOptions"/> 是 <c>record</c>，返回 <c>with</c> 出来的新实例）。
    /// </para>
    /// </summary>
    /// <param name="maintenance">维护节绑定的组件选项（唯一开关与旋钮来源）。</param>
    /// <param name="supply">供给参数（scope 清单 / 基准 / 预算的唯一真源）。</param>
    /// <param name="indexOptions">索引存储选项（索引根的唯一真源）。</param>
    public static MaintenanceOptions ApplySingleSource(
        MaintenanceOptions maintenance,
        FullTextIndexSupplyOptions supply,
        FullTextIndexOptions indexOptions)
    {
        ArgumentNullException.ThrowIfNull(maintenance);
        ArgumentNullException.ThrowIfNull(supply);
        ArgumentNullException.ThrowIfNull(indexOptions);

        return maintenance with
        {
            Scopes = supply.Scopes ?? [],
            WorkspaceRoot = supply.WorkspaceRoot,
            MaxIndexBytes = supply.MaxIndexBytes,
            IndexRootDirectory = indexOptions.IndexRootDirectory,
        };
    }

    /// <summary>
    /// 把**已由供给解析器校验通过**的绝对语料根映射成组件的维护 scope 记录。
    /// <para>
    /// ⚠️ 规范键走组件的公开单一真源 <c>FullTextChangeCoalescer.NormalizeComparisonKey</c>
    /// （与供给侧 <c>SupplyScopeNormalizer.ToScopeKey</c> 是同一实现：分隔符统一 <c>\</c> +
    /// 不变文化小写 + 保盘根裁剪）—— 宿主**不复刻**这套规则，否则维护与供给会对同一目录给出不同键，
    /// 进而推出不同租约文件 / 不同 checkpoint（同一语料根上直写与整目录替换不互斥）。
    /// </para>
    /// <para>
    /// ⚠️ <see cref="FullTextMaintenanceScope.IndexDirectory"/> 一律留 <c>null</c>：命名哈希由维护器
    /// 经同一真源自行推导，宿主不复刻（D5）。
    /// </para>
    /// </summary>
    /// <param name="acceptedScopes">供给解析器接受的绝对目录（顺序保留）。</param>
    /// <param name="effectiveOptions">生效的维护选项（预算取其 <c>MaxIndexBytes</c>）。</param>
    public static IReadOnlyList<FullTextMaintenanceScope> BuildScopes(
        IReadOnlyList<string> acceptedScopes,
        MaintenanceOptions effectiveOptions)
    {
        ArgumentNullException.ThrowIfNull(acceptedScopes);
        ArgumentNullException.ThrowIfNull(effectiveOptions);

        var scopes = new List<FullTextMaintenanceScope>(acceptedScopes.Count);

        foreach (var rootPath in acceptedScopes)
        {
            scopes.Add(new FullTextMaintenanceScope(
                RootPath: rootPath,
                ScopeKey: FullTextChangeCoalescer.NormalizeComparisonKey(rootPath),
                MaxIndexBytes: effectiveOptions.MaxIndexBytes));
        }

        return scopes;
    }
}
