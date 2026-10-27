namespace PuddingPlatform.Services.StorageManagement;

/// <summary>
/// 自动清理的调度选项。
///
/// 为什么要单独成类型（2026-10-27，S1 第四部分）：
/// 原先评估周期是协调器里的 <c>private static readonly ... FromHours(6)</c>，
/// 既不能配、也不能在测试里缩短，于是「tick 到底有没有按时触发」这层只能靠等真实日志。
/// 把它变成可注入的选项后，测试可以用毫秒级间隔真实驱动循环。
/// </summary>
public sealed class StorageAutomaticCleanupOptions
{
    /// <summary>
    /// 试运行评估周期。默认 6 小时：评估要对每张表做候选计数探测，不宜频繁。
    /// 非正值会被 <see cref="StorageAutomaticCleanupLoop"/> 夹到 1 分钟，避免热自旋。
    /// </summary>
    public TimeSpan EvaluationInterval { get; set; } = TimeSpan.FromHours(6);
}
