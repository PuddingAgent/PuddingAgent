using PuddingCode.Storage;

namespace PuddingPlatform.Services.StorageManagement;

/// <summary>
/// 给存储分析响应附上两个**目录派生**的结构化映射：「表 → 数据类」与「表 → 保护原因」（纯增量）。
///
/// <para>
/// 为什么单独成型：这两个映射不依赖任何运行时测量（不读文件、不开 SQLite），因此可以在没有数据库、
/// 没有宿主的情况下被独立测试 —— 分析实现只负责在返回前调用一次 <see cref="Apply"/>。
/// 若把它们埋进分析实现里，"映射对不对"就只能靠读代码判断（那一层没有任何单元测试）。
/// </para>
/// <para>
/// **纯增量**的保证点：<see cref="StorageDatabaseAnalysis"/> 的两个新字段是不带 <c>required</c>
/// 的 <c>init</c> 属性，旧调用方不赋值仍然能构造；本方法只覆写这两个字段，其余字段
/// （<c>CapturedAt</c>/<c>TotalBytes</c>/<c>Databases</c>/<c>Items</c>/<c>Warnings</c>）原样保留。
/// </para>
/// </summary>
public static class StorageAnalysisEnrichment
{
    /// <summary>
    /// 返回附带「表 → 数据类」与「表 → 保护原因」的分析，既有字段原样不动。
    /// </summary>
    /// <param name="analysis">分析实现刚构造出来的响应。</param>
    /// <returns>同一个记录的副本，仅两个纯增量字段被填充。</returns>
    public static StorageDatabaseAnalysis Apply(StorageDatabaseAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        return analysis with
        {
            TableToDataClasses = StorageDataClassCatalog.TableDataClasses,
            TableProtectionReasons = StorageDataClassCatalog.ProtectedTableReasons,
        };
    }
}
