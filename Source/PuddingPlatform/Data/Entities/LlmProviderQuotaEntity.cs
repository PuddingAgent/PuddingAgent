using System.ComponentModel.DataAnnotations;

namespace PuddingPlatform.Data.Entities;

/// <summary>
/// Provider 配额管理（每天/每月 token 限额）。
/// 一个 Provider 对应一条配额记录（1:1，独立表方便热更新）。
/// </summary>
public class LlmProviderQuotaEntity
{
    [Key]
    public int Id { get; set; }

    /// <summary>所属 Provider（1:1）</summary>
    public int ProviderId { get; set; }

    /// <summary>每日最大 token 数（null=不限制）</summary>
    public long? DailyTokenLimit { get; set; }

    /// <summary>每月最大 token 数（null=不限制）</summary>
    public long? MonthlyTokenLimit { get; set; }

    /// <summary>今日已用 token</summary>
    public long DailyTokensUsed { get; set; } = 0;

    /// <summary>本月已用 token</summary>
    public long MonthlyTokensUsed { get; set; } = 0;

    /// <summary>是否因超额被暂停</summary>
    public bool IsSuspended { get; set; } = false;

    /// <summary>今日配额最后重置时间（UTC）</summary>
    public DateTimeOffset? DailyResetAt { get; set; }

    /// <summary>本月配额最后重置时间（UTC）</summary>
    public DateTimeOffset? MonthlyResetAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    // 导航属性
    public LlmProviderEntity Provider { get; set; } = null!;
}
