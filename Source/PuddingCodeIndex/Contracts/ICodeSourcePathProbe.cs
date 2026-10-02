using PuddingCodeIndex.Contracts;

namespace PuddingCodeIndex.Services.CodeIndex;

/// <summary>
/// **按路径观测**的可选能力端口（D4，2026-10-02）：只给一批已知路径（watcher 提示）取元数据，
/// **不遍历整棵目录树**。
/// <para>
/// 为什么必须有它：完整清单校准（全树枚举）是**周期性**动作；若每一次「保存一个文件」都触发全树枚举，
/// 那么新链路反而比旧的逐文件路径更费磁盘 —— 正好与被修复的放大同款。
/// 以提示驱动的批次只做按路径观测，其结论**必然不完整**（<c>Complete=false</c>），
/// 因此永远不能得出「某路径已删除」：删除仍只能由周期性完整扫描确认。
/// </para>
/// <para>
/// 与 <see cref="ICodeSourceScanner"/> 分开是刻意的：给共享端口加成员会破坏每一个实现者（含组件外替身），
/// 而这里只需要「能按路径观测」的扫描器实现它。
/// </para>
/// </summary>
public interface ICodeSourcePathProbe
{
    /// <summary>只观测给定路径（文件取元数据；目录则枚举其子树），不做全树遍历。</summary>
    /// <param name="absolutePaths">要观测的绝对路径（调用方已按需忽略过滤）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<CodeSourceScanOutcome> ObserveAsync(
        IReadOnlyCollection<string> absolutePaths,
        CancellationToken cancellationToken = default);
}
