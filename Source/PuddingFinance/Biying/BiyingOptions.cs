namespace PuddingFinance.Biying;

/// <summary>调用必盈 API 的配置。凭据来自数据根配置文件，**不进仓库**。</summary>
public sealed record BiyingOptions
{
    /// <summary>证书（licence）。它同时是 URL 的末段路径，必须脱敏后才能出现在日志/上下文里。</summary>
    public required string Licence { get; init; }

    /// <summary>日调用上限。付费档官方口径 10,000/天；免费档 100/天。</summary>
    public int DailyQuota { get; init; } = 10_000;

    /// <summary>用到该比例开始回执告警（0~1）。</summary>
    public double QuotaWarnRatio { get; init; } = 0.8;

    /// <summary>实时行情缓存时长。官方 3 秒/次刷新，这里取 10 秒——多缓存几秒不会让结论变错。</summary>
    public TimeSpan RealtimeCacheTtl { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>日频数据（K 线等）缓存时长。</summary>
    public TimeSpan DailyCacheTtl { get; init; } = TimeSpan.FromHours(1);

    /// <summary>静态数据（列表、板块树、三表、指标）缓存时长。</summary>
    public TimeSpan StaticCacheTtl { get; init; } = TimeSpan.FromHours(24);

    /// <summary>单次响应字节硬上限。财务接口实测单只股票可达 229 KB，给足余量但仍要封顶。</summary>
    public int MaxResponseBytes { get; init; } = 8 * 1024 * 1024;

    /// <summary>HTTP 超时。</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(20);
}

/// <summary>可注入时钟：配额按 UTC 日切分、缓存按 TTL 过期，两者都必须可被测试控制。</summary>
public interface IBiyingClock
{
    DateTimeOffset UtcNow { get; }
}

internal sealed class SystemBiyingClock : IBiyingClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
