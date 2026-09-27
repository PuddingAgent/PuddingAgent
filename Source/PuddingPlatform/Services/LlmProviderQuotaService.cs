using PuddingCode.Configuration;
using PuddingPlatform.Data.Dtos;

namespace PuddingPlatform.Services;

/// <summary>
/// Provider 级 token 配额（自设预算）。
///
/// 单一事实来源：
/// · 限额与计数窗口起点保存在 data/config/llm.providers.json 的 provider.quota 下；
/// · 已用 token 从 token 账本实时推导（llm_gateway_usage_events + TokenUsageEvents），
///   不在这里重复记账，也不写回文件。
///
/// 因此 reset-daily 只是把日窗口起点推进到“现在”，不会删除或改写任何账本数据；
/// 自然日/自然月切换仍然照常生效（窗口起点早于自然周期起点时按自然周期起点计算）。
/// </summary>
public sealed class LlmProviderQuotaService(
    LlmProviderFileService providers,
    TokenUsageDailyAggregateService usage)
{
    public async Task<LlmProviderQuotaDto?> TryGetAsync(string providerId, CancellationToken ct = default)
    {
        var provider = (await providers.LoadAsync(ct)).Providers.FirstOrDefault(candidate =>
            string.Equals(candidate.ProviderId, providerId, StringComparison.OrdinalIgnoreCase));
        return provider is null ? null : await BuildAsync(provider, ct);
    }

    public async Task<LlmProviderQuotaDto> UpsertAsync(string providerId, UpdateQuotaRequest request, CancellationToken ct = default)
    {
        Validate(request);
        await providers.UpdateQuotaAsync(providerId, quota => new PuddingLlmProviderQuotaConfig
        {
            // 限额是本次编辑的目标状态；窗口起点保留，重置只由 ResetDailyAsync 推进。
            DailyTokenLimit = request.DailyTokenLimit,
            MonthlyTokenLimit = request.MonthlyTokenLimit,
            DailyResetAt = quota?.DailyResetAt,
            MonthlyResetAt = quota?.MonthlyResetAt,
        }, ct);
        return await TryGetAsync(providerId, ct)
            ?? throw new KeyNotFoundException($"Provider '{providerId}' 不存在");
    }

    public async Task<LlmProviderQuotaDto> ResetDailyAsync(string providerId, CancellationToken ct = default)
    {
        await providers.UpdateQuotaAsync(providerId, quota => new PuddingLlmProviderQuotaConfig
        {
            DailyTokenLimit = quota?.DailyTokenLimit,
            MonthlyTokenLimit = quota?.MonthlyTokenLimit,
            DailyResetAt = DateTimeOffset.UtcNow,
            MonthlyResetAt = quota?.MonthlyResetAt,
        }, ct);
        return await TryGetAsync(providerId, ct)
            ?? throw new KeyNotFoundException($"Provider '{providerId}' 不存在");
    }

    /// <summary>不存在的 Provider 直接拒绝，避免出现“保存成功但没写入”的假成功。</summary>
    public static void Validate(UpdateQuotaRequest request)
    {
        if (request.DailyTokenLimit is <= 0) throw new ArgumentException("每日 token 限额必须大于 0，或留空表示不限制。");
        if (request.MonthlyTokenLimit is <= 0) throw new ArgumentException("每月 token 限额必须大于 0，或留空表示不限制。");
        if (request is { DailyTokenLimit: { } daily, MonthlyTokenLimit: { } monthly } && daily > monthly)
            throw new ArgumentException("每日 token 限额不能大于每月 token 限额。");
    }

    private async Task<LlmProviderQuotaDto> BuildAsync(PuddingLlmProviderConfig provider, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var today = now.UtcDateTime.Date;
        var tomorrow = today.AddDays(1);
        var quota = provider.Quota;

        var dailyStart = WindowStart(today, quota?.DailyResetAt, now);
        var monthlyStart = WindowStart(new DateTime(today.Year, today.Month, 1, 0, 0, 0, DateTimeKind.Utc), quota?.MonthlyResetAt, now);

        var dailyTokens = SumForProvider(await usage.GetRangeAsync(dailyStart, tomorrow, ct), provider.ProviderId);
        var monthlyTokens = dailyStart == monthlyStart
            ? dailyTokens
            : SumForProvider(await usage.GetRangeAsync(monthlyStart, tomorrow, ct), provider.ProviderId);

        var suspended = (quota?.DailyTokenLimit is { } dailyLimit && dailyTokens >= dailyLimit)
            || (quota?.MonthlyTokenLimit is { } monthlyLimit && monthlyTokens >= monthlyLimit);

        return new LlmProviderQuotaDto(
            quota?.DailyTokenLimit,
            quota?.MonthlyTokenLimit,
            dailyTokens,
            monthlyTokens,
            suspended,
            quota?.DailyResetAt,
            quota?.MonthlyResetAt,
            quota?.UpdatedAt ?? now);
    }

    /// <summary>窗口起点 = max(自然周期起点, 已保存的重置时间)，且不得超过“现在”。</summary>
    public static DateTime WindowStart(DateTime naturalStart, DateTimeOffset? resetAt, DateTimeOffset now)
    {
        if (resetAt is null || resetAt.Value.UtcDateTime <= naturalStart) return naturalStart;
        var candidate = resetAt.Value.UtcDateTime;
        return candidate > now.UtcDateTime ? now.UtcDateTime : candidate;
    }

    private static long SumForProvider(IReadOnlyList<LlmUsageDailyAggregateRow> rows, string providerId) =>
        rows.Where(row => string.Equals(row.ProviderId, providerId, StringComparison.OrdinalIgnoreCase))
            .Sum(row => row.PromptTokens + row.CompletionTokens);
}
