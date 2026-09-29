namespace PuddingPlatform.Services;

/// <summary>
/// 幂等 schema 阶梯的**修订标记**与判定。阶梯（<c>PuddingApplicationInitializer</c> 里 25 个组）本身
/// 按幂等构造，因此稳态下完全可以跳过；标记用 SQLite 原生 <c>PRAGMA user_version</c>（只读库头，不新增表）。
/// <para>
/// <b>什么时候必须递增 <see cref="Current"/></b>：任何 bootstrapper 改了它建的东西或回填的数据
/// （新增列/表/索引、改回填谓词）都要 +1，否则老库会带着旧结构跳过阶梯。递增后所有库都会跑一次全量阶梯。
/// </para>
/// <para>
/// <b>为什么还要哨兵表</b>：标记只能证明"这个库跑过修订 N"，不能证明"结构没被外部改坏"（手工改库、
/// 半途中断、被旧版本回滚）。所以命中标记时还要确认几张哨兵表在；任一缺失即回落全量阶梯。
/// 判定一律 fail-open：宁可多干活，绝不"假设已完成"。
/// </para>
/// </summary>
public static class PlatformSchemaRevision
{
    /// <summary>当前修订号。改阶梯就 +1。</summary>
    public const long Current = 1;

    /// <summary>
    /// 哨兵表：横跨阶梯早/中/晚三段，覆盖不同 bootstrapper 的产物。名字是编译期常量，
    /// 只用于拼一条 <c>sqlite_master</c> 查询，没有注入面。
    /// </summary>
    public static readonly string[] SentinelTables =
    [
        "AppUsers",              // app-user（早期）
        "message_deliveries",    // message-fabric（中期，含一次历史回填）
        "workspace_tasks",       // workspace-task（中期）
        "HubSkills",             // skill-hub（末期）
    ];

    /// <summary>
    /// 是否需要跑全量阶梯。<paramref name="revision"/> 为 -1 表示"读不到/非 SQLite"，
    /// <paramref name="sentinelTablesFound"/> 为 -1 表示探测失败：两者都按"需要"处理。
    /// </summary>
    public static bool LadderRequired(long revision, int sentinelTablesFound)
        => revision != Current || sentinelTablesFound < SentinelTables.Length;

    /// <summary>
    /// 运维逃生门：<c>PUDDING_SCHEMA_LADDER=full</c> 强制跑全量阶梯（怀疑结构漂移时用）。
    /// 只认这一个取值，其余一律按正常判定。
    /// </summary>
    public static bool ForceFullLadder(string? schemaLadderSetting)
        => string.Equals(schemaLadderSetting, "full", StringComparison.OrdinalIgnoreCase);
}
