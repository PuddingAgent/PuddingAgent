namespace PuddingFullTextIndex.Contracts;

/// <summary>
/// 租约持有者的**角色**：同一进程内可能同时存在多条会写同一索引目录的路径，
/// 角色让它们的身份在**编译期**就必须被显式区分（没有无参默认值可以蒙混过关）。
/// <para>
/// 动机（缺陷 ②「同进程默认身份下两条路径不互斥」）：文件租约以 <c>OwnerId</c> 字符串判「是不是自己人」，
/// 判为自己人就直接<b>重入</b>并覆盖 job 归属。若同进程内两条路径用同一个 <c>OwnerId</c>，
/// 第二条会判成自己人而重入 ⇒「维护直写」与「供给整目录替换」在同一进程内<b>并不互斥</b>
/// （只有跨进程才互斥）。把角色并入 <see cref="SupplyLeaseOwner.OwnerId"/> 后，
/// 两条路径的默认身份天然不同 ⇒ 第二条被如实拒绝（<c>Busy</c>）。
/// </para>
/// <para>
/// ⚠️ **必须保持**的既有语义：**同一角色在同一进程内跨批次仍可重入**
/// （维护路径「每个合并批次取一次租约」依赖它；破坏它会让同一 scope 被自己永久判 <c>Busy</c>）。
/// </para>
/// </summary>
public enum SupplyLeaseRole
{
    /// <summary>供给 / 构建路径（整目录替换 + staging 原子切换）。</summary>
    Supply = 0,

    /// <summary>维护路径（对 live 索引的渐进直写）。</summary>
    Maintenance = 1,
}
