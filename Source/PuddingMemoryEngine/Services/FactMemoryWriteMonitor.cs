namespace PuddingMemoryEngine.Services;

/// <summary>事实记忆写入的健康状态。</summary>
public enum FactMemoryWriteHealth
{
    /// <summary>近期有写入，正常。</summary>
    Healthy = 0,
    /// <summary>开关已开、但库里始终 0 行 —— 写入路径没工作。</summary>
    NeverWritten = 1,
    /// <summary>曾经写过，但已超过阈值没有新写入 —— 可能悄悄断了。</summary>
    Stalled = 2,
    /// <summary>写入开关没开，不适用（不算异常）。</summary>
    Disabled = 3,
}

/// <summary>一次健康判定的输入。</summary>
public sealed record FactMemoryWriteSnapshot(
    bool WriteEnabled,
    long FactCount,
    DateTimeOffset? LastWrittenAt,
    DateTimeOffset Now,
    int StaleDays = 7);

/// <summary>
/// 事实记忆写入的**静默失效检测**（纯函数）。
/// <para>
/// 存在的理由：`MemoryFacts` 长期 0 行这件事，是我今天翻库时才偶然发现的 ——
/// 服务注册了、测试全绿、就是没人调用。**静默失效不该靠人偶然发现**，
/// 它必须是一个能被定时检查、能报警的状态。
/// </para>
/// <para>
/// 判定刻意保守：开关关着时报 <see cref="FactMemoryWriteHealth.Disabled"/> 而不是异常，
/// 因为"没开启"是预期状态，把它报成故障会让告警失去意义。
/// </para>
/// </summary>
public static class FactMemoryWriteMonitor
{
    public static FactMemoryWriteHealth Evaluate(FactMemoryWriteSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (!snapshot.WriteEnabled)
            return FactMemoryWriteHealth.Disabled;

        if (snapshot.FactCount <= 0)
            return FactMemoryWriteHealth.NeverWritten;

        if (snapshot.LastWrittenAt is not { } last)
            return FactMemoryWriteHealth.Stalled;

        var staleAfter = TimeSpan.FromDays(Math.Max(1, snapshot.StaleDays));
        return snapshot.Now - last > staleAfter
            ? FactMemoryWriteHealth.Stalled
            : FactMemoryWriteHealth.Healthy;
    }

    /// <summary>需要人工/告警介入的状态（Disabled 与 Healthy 都不算）。</summary>
    public static bool NeedsAttention(FactMemoryWriteHealth health) =>
        health is FactMemoryWriteHealth.NeverWritten or FactMemoryWriteHealth.Stalled;

    /// <summary>一句话说明，便于写日志与告警文本。</summary>
    public static string Describe(FactMemoryWriteHealth health) => health switch
    {
        FactMemoryWriteHealth.Healthy => "事实记忆写入正常。",
        FactMemoryWriteHealth.NeverWritten => "写入开关已开但 MemoryFacts 仍为 0 行：写入路径没有工作。",
        FactMemoryWriteHealth.Stalled => "事实记忆超过阈值没有新写入：可能已静默中断。",
        FactMemoryWriteHealth.Disabled => "事实记忆写入未开启（预期状态）。",
        _ => "未知状态。",
    };
}
