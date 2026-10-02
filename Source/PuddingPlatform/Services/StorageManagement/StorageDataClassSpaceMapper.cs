namespace PuddingPlatform.Services.StorageManagement;

/// <summary>一个**语义数据类**的占用（存储管理页"显示不同数据的占比"直接消费这个形状）。</summary>
/// <param name="TargetId">稳定语义 ID（目录里的 ID；未归类为 <see cref="StorageDataClassSpaceMapper.UnclassifiedTargetId"/>）。</param>
/// <param name="DisplayName">展示名。</param>
/// <param name="SafetyLevel">安全级别（原样透传目录值，不在这一层发明规则）。</param>
/// <param name="ManualCleanupAllowed">目录是否允许人工清理（**只是目录声明**，是否可勾选由界面按安全级别决定）。</param>
/// <param name="Bytes">占用字节（表 + 其索引）。</param>
/// <param name="Pages">页数。</param>
/// <param name="Tables">命中的物理表（含索引归属后的表名）。</param>
public sealed record StorageDataClassSpace(
    string TargetId,
    string DisplayName,
    string SafetyLevel,
    bool ManualCleanupAllowed,
    long Bytes,
    long Pages,
    IReadOnlyList<string> Tables);

/// <summary>
/// 把**按表占用**归入 <see cref="StorageDataClassCatalog"/> 的语义数据类。
///
/// <para>
/// 为什么需要这一层：dbstat 给的是物理 B 树（表 + 索引各自一行），而用户与清理策略认的是
/// **语义数据类**（"会话事件证据""遥测"…）。目录里已经有"数据类 → 物理表"的白名单映射
/// （ADR-076），因此这里只做归并，不重新发明分类。
/// </para>
/// <para>
/// 归并要点：dbstat 的每一行已经带 <c>OwnerTable</c>（由探针经 <c>sqlite_master</c> 解析，
/// 索引归到它所属的表）⇒ **索引占用计入其表所在的数据类**，否则"索引特别多的表"会被明显低估。
/// 不在任何数据类里的表进 <see cref="UnclassifiedTargetId"/> 桶 —— 如实展示目录没覆盖的部分，
/// 而不是把它偷偷摊进别的类。
/// </para>
/// </summary>
public static class StorageDataClassSpaceMapper
{
    /// <summary>未归类桶的稳定 ID。</summary>
    public const string UnclassifiedTargetId = "unclassified";

    /// <summary>未归类桶的展示名。</summary>
    public const string UnclassifiedDisplayName = "未归类";

    /// <summary>
    /// 按数据类归并（结果按占用降序；未归类始终排在最后，即使它最大 —— 它是"待补目录"的信号）。
    /// </summary>
    /// <param name="tables">探针给出的按表明细（应含 <c>OwnerTable</c>）。</param>
    /// <param name="definitions">数据类目录；默认用 <see cref="StorageDataClassCatalog.Definitions"/>。</param>
    public static IReadOnlyList<StorageDataClassSpace> Map(
        IReadOnlyList<DatabaseTableSpace> tables,
        IReadOnlyList<StorageDataClassCatalog.StorageDataClassDefinition>? definitions = null)
    {
        ArgumentNullException.ThrowIfNull(tables);

        var owners = BuildOwnerIndex(definitions ?? StorageDataClassCatalog.Definitions);
        var buckets = new Dictionary<string, Bucket>(StringComparer.Ordinal);
        var order = new List<string>();

        foreach (var table in tables)
        {
            // 归属键：解析出的所属表优先，拿不到就退回 B 树自身的名字（索引名也得有归宿）。
            var ownerKey = string.IsNullOrWhiteSpace(table.OwnerTable) ? table.Table : table.OwnerTable!;
            var hasDefinition = owners.TryGetValue(ownerKey, out var definition);

            var targetId = hasDefinition ? definition!.TargetId : UnclassifiedTargetId;
            if (!buckets.TryGetValue(targetId, out var bucket))
            {
                bucket = new Bucket(
                    TargetId: targetId,
                    DisplayName: hasDefinition ? definition!.DisplayName : UnclassifiedDisplayName,
                    SafetyLevel: hasDefinition ? definition!.SafetyLevel.ToString() : string.Empty,
                    ManualCleanupAllowed: hasDefinition && definition!.ManualCleanupAllowed,
                    Tables: new List<string>());
                buckets[targetId] = bucket;
                order.Add(targetId);
            }

            bucket.Bytes += table.Bytes;
            bucket.Pages += table.Pages;
            if (!bucket.Tables.Contains(ownerKey, StringComparer.OrdinalIgnoreCase))
            {
                bucket.Tables.Add(ownerKey);
            }
        }

        return order
            .Select(id => buckets[id])
            // 降序展示；未归类恒排最后（它是"目录没覆盖"的提示，不该因为体积大就抢头条）。
            .OrderBy(space => space.TargetId == UnclassifiedTargetId ? 1 : 0)
            .ThenByDescending(space => space.Bytes)
            .Select(space => new StorageDataClassSpace(
                space.TargetId,
                space.DisplayName,
                space.SafetyLevel,
                space.ManualCleanupAllowed,
                space.Bytes,
                space.Pages,
                space.Tables))
            .ToArray();
    }

    private static Dictionary<string, StorageDataClassCatalog.StorageDataClassDefinition> BuildOwnerIndex(
        IReadOnlyList<StorageDataClassCatalog.StorageDataClassDefinition> definitions)
    {
        var owners = new Dictionary<string, StorageDataClassCatalog.StorageDataClassDefinition>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var definition in definitions)
        {
            foreach (var table in definition.Tables)
            {
                // 同一张表被两个类声明是目录错误；这里以**先声明者**为准并不抛，
                // 因为存储页属于运维面，宁可少报一类也不能因为目录笔误整页打不开。
                owners.TryAdd(table.Table, definition);
            }
        }

        return owners;
    }

    private sealed class Bucket(
        string TargetId,
        string DisplayName,
        string SafetyLevel,
        bool ManualCleanupAllowed,
        List<string> Tables)
    {
        public string TargetId { get; } = TargetId;

        public string DisplayName { get; } = DisplayName;

        public string SafetyLevel { get; } = SafetyLevel;

        public bool ManualCleanupAllowed { get; } = ManualCleanupAllowed;

        public List<string> Tables { get; } = Tables;

        public long Bytes { get; set; }

        public long Pages { get; set; }
    }
}
