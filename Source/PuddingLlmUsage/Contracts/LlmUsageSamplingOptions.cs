namespace PuddingLlmUsage.Contracts;

/// <summary>
/// 采样配置。与「谁来调度它」无关 —— 调度器只负责按 <see cref="Interval"/> 唤醒，
/// 具体查哪些 provider、超时多久由这里决定。
/// </summary>
/// <param name="Enabled">总开关。关闭时不发起任何外部请求。</param>
/// <param name="ProviderIds">
/// 要采样的 provider。<b>必须显式列出</b>：配置里有五个 provider 不代表五个都支持
/// <c>/user/balance</c>，盲目全量请求只会制造失败记录。
/// </param>
/// <param name="Interval">采样间隔。默认十分钟。</param>
/// <param name="PerProviderTimeout">
/// 单个 provider 的超时。超时按<b>失败观测</b>处理（不是异常），
/// 因此它不会打断整轮，也不会让缺口消失。
/// </param>
public sealed record LlmUsageSamplingOptions(
    bool Enabled,
    IReadOnlyList<string> ProviderIds,
    TimeSpan Interval,
    TimeSpan PerProviderTimeout)
{
    /// <summary>默认：启用，只采 deepseek，十分钟一次，单 provider 二十秒超时。</summary>
    public static LlmUsageSamplingOptions Default { get; } = new(
        Enabled: true,
        ProviderIds: ["deepseek"],
        Interval: TimeSpan.FromMinutes(10),
        PerProviderTimeout: TimeSpan.FromSeconds(20));

    /// <summary>
    /// 校验并返回<b>规范化的</b>选项：provider 去空白、小写、去重、保持首次出现顺序。
    /// </summary>
    /// <exception cref="ArgumentException">配置不可用。</exception>
    public LlmUsageSamplingOptions Validate()
    {
        if (Interval <= TimeSpan.Zero)
            throw new ArgumentException("采样间隔必须为正。", nameof(Interval));

        if (Interval > TimeSpan.FromHours(24))
            throw new ArgumentException("采样间隔不得超过 24 小时（超过就不是采样了）。", nameof(Interval));

        if (PerProviderTimeout <= TimeSpan.Zero)
            throw new ArgumentException("单 provider 超时必须为正。", nameof(PerProviderTimeout));

        if (PerProviderTimeout >= Interval)
            throw new ArgumentException("单 provider 超时必须小于采样间隔，否则两轮会重叠。", nameof(PerProviderTimeout));

        var normalized = ProviderIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (Enabled && normalized.Length == 0)
            throw new ArgumentException("启用采样时必须至少指定一个 provider。", nameof(ProviderIds));

        return this with { ProviderIds = normalized };
    }
}
