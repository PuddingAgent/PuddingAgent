using PuddingFullTextIndex.Contracts;

namespace PuddingFullTextIndex;

/// <summary>
/// 供给协调器的可注入策略（A1：全部有默认值，宿主接线属 A4）。
/// </summary>
public sealed record SupplyCoordinatorOptions
{
    /// <summary>
    /// 索引体积预算的默认值：<b>1 GiB = 2^30 = 1,073,741,824 B</b>（不是十进制 1 GB = 1e9）。
    /// <para>
    /// ⚠️ <b>同值不同层</b>：宿主侧 <c>Source/PuddingHost/Hosting/FullTextIndexSupplyOptions.cs</c> 的
    /// <c>DefaultMaxIndexBytes</c> 也是这个值（那份是配置单一真源，且其注释声明「该字面量只允许出现一处」）。
    /// 本组件不得引用 Host（依赖方向由编译期强制：PuddingFullTextIndex → PuddingPathFiltering 仅此一条），
    /// 因此这里必须再写一次常量，并<b>显式登记这处重复</b>：A4/S5 接线时宿主应把配置值显式传进来
    /// （<c>SupplyScopeRequest.BudgetBytes</c> 或本选项的 <see cref="DefaultBudgetBytes"/>），
    /// 让组件侧的常量退化为「未接线时的兜底」，避免两侧各自漂移。
    /// </para>
    /// </summary>
    public const long DefaultMaxIndexBytes = 1_073_741_824;

    /// <summary>未指定预算时的默认预算（字节）。</summary>
    public long DefaultBudgetBytes { get; init; } = DefaultMaxIndexBytes;

    /// <summary>终态 job 的保留条数（默认 32）；超出后按时间淘汰最旧，避免无界内存。</summary>
    public int MaxRetainedJobs { get; init; } = 32;

    /// <summary>
    /// 最小重建间隔（默认 12h）：同 scope 存在**未过期的成功终态**时，新的供给请求直接合并，
    /// 不重复构建。允许 <see cref="TimeSpan.Zero"/>（= 每次都重建）。
    /// </summary>
    public TimeSpan MinRebuildInterval { get; init; } = TimeSpan.FromHours(12);

    /// <summary>
    /// 租约续期间隔（默认 40s ≈ 默认租约有效期 2 分钟的 1/3）：job 运行期间后台续期，
    /// 避免长构建被别的进程误判为「过期」而接管。
    /// </summary>
    public TimeSpan LeaseRenewInterval { get; init; } = TimeSpan.FromSeconds(40);

    /// <summary>非法状态转换的被拒记录保留条数（默认 32）。</summary>
    public int MaxRetainedTransitionRejections { get; init; } = 32;

    /// <summary>本进程在租约里的 owner 标识（默认 <c>机器名#进程号</c>）。</summary>
    public string OwnerId { get; init; } = SupplyLeaseOwner.ForCurrentProcess().OwnerId;
}
