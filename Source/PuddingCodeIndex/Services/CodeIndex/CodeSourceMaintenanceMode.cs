namespace PuddingCodeIndex.Services.CodeIndex;

/// <summary>
/// 变更施用链路的**开关**（D4，2026-10-02）。
/// <para>
/// <see cref="Legacy"/>：既有逐文件路径（`ICodeIndexFileUpdater.IndexFileAsync` 逐文件，
/// 索引器不认某个文件就升级为整个 scope 重跑）。
/// </para>
/// <para>
/// <see cref="Coordinator"/>：新的源维护链（完整清单校准 → 真实变更集 → 更新计划 → 语言批量接缝 →
/// 稳定读指纹 → 原子替换 → 账本提交）。单路径失败按退避重试，**不升级整仓**；
/// watcher 观察到的消失只作为提示，删除必须由完整且根可用的扫描确认。
/// </para>
/// <para>
/// 默认 <see cref="Legacy"/>：切换是行为变化，必须显式打开（并由外部控制器重启后复核）。
/// 打开但零件未装配齐时会告警并退回 <see cref="Legacy"/>，绝不假装新链路在跑。
/// </para>
/// </summary>
public enum CodeSourceMaintenanceMode
{
    /// <summary>既有逐文件路径（默认）。</summary>
    Legacy = 0,

    /// <summary>新的源维护链（协调器）。</summary>
    Coordinator = 1,
}

/// <summary>索引维护驱动的选项（可由组合根/宿主覆盖）。</summary>
/// <param name="SourceMaintenance">变更施用链路；默认 <see cref="CodeSourceMaintenanceMode.Legacy"/>。</param>
public sealed record CodeIndexMaintenanceOptions(
    CodeSourceMaintenanceMode SourceMaintenance = CodeSourceMaintenanceMode.Legacy);
