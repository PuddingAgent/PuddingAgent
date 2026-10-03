using PuddingCode.Configuration;

namespace PuddingPlatform.Services.StorageManagement;

/// <summary>单个数据库文件的按数据类占用。</summary>
/// <param name="Key">稳定键（platform / code-index / memory / controller）。</param>
/// <param name="DisplayName">展示名。</param>
/// <param name="DatabaseFile">主文件绝对路径。</param>
/// <param name="Exists">库文件是否存在（不存在时其余字段为 0/空 —— 与"空库"区分）。</param>
/// <param name="FileBytes">主文件 + WAL + SHM 字节数。</param>
/// <param name="PageSize">SQLite 页大小。</param>
/// <param name="PageCount">SQLite 页数。</param>
/// <param name="PerTableAvailable">是否拿到 **dbstat** 的按表明细。</param>
/// <param name="SpaceSource">
/// 这些数字**从哪来**（机器可读，便于界面与排障判断可信度）：
/// <c>dbstat</c>（按 B 树精确）、<c>rowcount-sample</c>（精确行数 × 抽样平均行长，见
/// <see cref="DatabaseTableEstimator"/> 的已知偏差）、<c>unavailable</c>（库不存在）。
/// </param>
/// <param name="DataClasses">按数据类归并的占用（降序；「未归类」恒排最后）。</param>
public sealed record StorageDatabaseSpaceDto(
    string Key,
    string DisplayName,
    string DatabaseFile,
    bool Exists,
    long FileBytes,
    long PageSize,
    long PageCount,
    bool PerTableAvailable,
    string SpaceSource,
    IReadOnlyList<StorageDataClassSpace> DataClasses);

/// <summary>
/// "数据库按数据类占用"的只读汇总（ADR-076 存储管理页"显示不同数据的占比"的数据源）。
///
/// <para>
/// 与 <c>StorageInventorySampler</c> 的分工：采样器在**后台**按节拍维护缓存快照，overview 只读缓存
/// （它的注释明确写着"不触发扫描、COUNT(*)、dbstat 或目录遍历"）。本服务是**显式动作**（用户点"查看数据库占用"）
/// 时才有的一次只读测量。
/// </para>
/// <para>
/// **两种数据源，同一种形状**：dbstat 可用就用它（精确到 B 树页，含索引归属）；不可用则退回
/// 「每表精确行数 × 抽样平均行长」的估算（本仓库的 SQLite **没有 dbstat**，2026-10-03 实测确认）。
/// 归并器因此不需要知道数据来源。
/// </para>
/// <para>
/// 数据库清单与采样器保持一致（platform / code-index / memory / controller）：
/// 这四项目前在两处各写一份字面量，若将来要抽成共享清单，应同时改这两处。
/// </para>
/// </summary>
public sealed class StorageDatabaseSpaceService(
    PuddingDataPaths paths,
    DatabaseSpaceProbe probe,
    DatabaseTableEstimator estimator)
{
    /// <summary>数据来源：dbstat 按 B 树精确测量。</summary>
    public const string SpaceSourceDbstat = "dbstat";

    /// <summary>数据来源：精确行数 × 抽样平均行长（含已知偏差）。</summary>
    public const string SpaceSourceRowCountSample = "rowcount-sample";

    /// <summary>数据来源：库不存在，没有数字。</summary>
    public const string SpaceSourceUnavailable = "unavailable";

    /// <summary>被测量的数据库（键 + 展示名 + 相对 DatabasesRoot 的文件名）。</summary>
    private static readonly (string Key, string DisplayName, string RelativeFile)[] Databases =
    [
        ("platform", "Pudding 平台数据库", StorageDataClassCatalog.PlatformDatabaseFile),
        ("code-index", "代码索引数据库", StorageDataClassCatalog.CodeIndexDatabaseFile),
        ("memory", "记忆数据库", "pudding_memory.db"),
        ("controller", "控制面数据库", "pudding_controller.db"),
    ];

    /// <summary>测量全部数据库并按数据类归并（库不存在时如实标 <c>Exists=false</c>）。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<IReadOnlyList<StorageDatabaseSpaceDto>> MeasureAsync(
        CancellationToken cancellationToken = default)
    {
        var results = new List<StorageDatabaseSpaceDto>(Databases.Length);

        foreach (var (key, displayName, relativeFile) in Databases)
        {
            var fullPath = Path.Combine(paths.DatabasesRoot, relativeFile);
            var report = await probe.MeasureAsync(fullPath, cancellationToken).ConfigureAwait(false);

            if (report is null)
            {
                results.Add(new StorageDatabaseSpaceDto(
                    key, displayName, Path.GetFullPath(fullPath),
                    Exists: false, FileBytes: 0, PageSize: 0, PageCount: 0,
                    PerTableAvailable: false, SpaceSource: SpaceSourceUnavailable, DataClasses: []));
                continue;
            }

            // 只把**属于这个库**的数据类参与归并：目录里每个数据类都带 DatabaseFile。
            var definitions = StorageDataClassCatalog.Definitions
                .Where(definition => definition.DatabaseFile is not null
                    && PathEquals(definition.DatabaseFile!, relativeFile))
                .ToArray();

            IReadOnlyList<DatabaseTableSpace> tableSpaces;
            string spaceSource;
            if (report.PerTableAvailable)
            {
                tableSpaces = report.Tables;
                spaceSource = SpaceSourceDbstat;
            }
            else
            {
                var estimates = await estimator
                    .EstimateAsync(report.DatabaseFile, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                tableSpaces = DatabaseTableEstimator.ToTableSpaces(estimates);
                spaceSource = SpaceSourceRowCountSample;
            }

            results.Add(new StorageDatabaseSpaceDto(
                key,
                displayName,
                report.DatabaseFile,
                Exists: true,
                FileBytes: report.FileBytes,
                PageSize: report.PageSize,
                PageCount: report.PageCount,
                PerTableAvailable: report.PerTableAvailable,
                SpaceSource: spaceSource,
                DataClasses: StorageDataClassSpaceMapper.Map(tableSpaces, definitions)));
        }

        return results;
    }

    private static bool PathEquals(string left, string right)
        => string.Equals(
            left.Replace('\\', '/').TrimStart('/'),
            right.Replace('\\', '/').TrimStart('/'),
            StringComparison.OrdinalIgnoreCase);
}
