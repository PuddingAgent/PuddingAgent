namespace PuddingPlatform.Data.Dtos;

/// <summary>
/// 用量汇总的一行（可观测性 P0-c，管理端「用量报表」）。
/// <para>
/// 时间语义：本 DTO 里的所有时间与「日」均为 <b>UTC</b>；<see cref="Key"/> 在
/// <c>groupBy=day</c> 时是 UTC 日期（yyyy-MM-dd）。
/// </para>
/// <para>
/// 成本语义：<see cref="TotalCost"/> 为 0 不等于「免费」。当该分组存在 token 但成本全为 0
/// （模型价格登记缺失）时 <see cref="CostKnown"/> 为 <c>false</c>，界面必须显示「成本未登记」；
/// <see cref="UnknownCostRequestCount"/> 记录该分组内「成本为 0 且 token &gt; 0」的请求条数。
/// </para>
/// </summary>
/// <param name="Key">分组键：model → "provider/model"；subagent → SubAgentId；day → UTC 日期。</param>
/// <param name="ProviderId">服务商（仅 groupBy=model 有值，其余为 null）。</param>
/// <param name="ModelId">模型（仅 groupBy=model 有值，其余为 null）。</param>
/// <param name="RequestCount">该分组的事件条数（等价于 LLM 调用次数）。</param>
/// <param name="PromptTokens">输入 token 合计。</param>
/// <param name="CompletionTokens">输出 token 合计。</param>
/// <param name="TotalTokens">输入 + 输出 token 合计。</param>
/// <param name="CacheHitTokens">命中前缀缓存的 token 合计。</param>
/// <param name="CacheMissTokens">未命中的 token 合计。</param>
/// <param name="CacheHitRate">
/// 加权缓存命中率 = sum(CacheHitTokens) / sum(CacheEligibleTokens)。
/// 分母为 0 时返回 <c>null</c>（「算不出」），<b>不是</b> 0。
/// </param>
/// <param name="InputCost">输入 token 成本。</param>
/// <param name="OutputCost">输出 token 成本。</param>
/// <param name="CacheHitCost">缓存命中 token 成本。</param>
/// <param name="TotalCost">总成本；为 0 时须结合 <see cref="CostKnown"/> 判读。</param>
/// <param name="CostKnown">该分组成本是否可信；false 表示「成本未登记」而不是「免费」。</param>
/// <param name="UnknownCostRequestCount">成本为 0 且 token &gt; 0 的请求条数（真正的「缺失」）。</param>
public record UsageSummaryRowDto(
    string Key,
    string? ProviderId,
    string? ModelId,
    long RequestCount,
    long PromptTokens,
    long CompletionTokens,
    long TotalTokens,
    long CacheHitTokens,
    long CacheMissTokens,
    double? CacheHitRate,
    decimal InputCost,
    decimal OutputCost,
    decimal CacheHitCost,
    decimal TotalCost,
    bool CostKnown,
    long UnknownCostRequestCount);

/// <summary>
/// 用量汇总响应信封。
/// <para>
/// 之所以不是裸数组：需求要求响应携带 <c>truncated</c> 与 <c>total</c>（分组总数），
/// 裸数组承载不了这两个元数据。行数据一律在 <see cref="Rows"/> 里，
/// 每行字段契约见 <see cref="UsageSummaryRowDto"/>。
/// </para>
/// </summary>
/// <param name="GroupBy">实际生效的分组维度：model / subagent / day。</param>
/// <param name="FromUtc">下界（含），UTC；请求未指定时为 null。</param>
/// <param name="ToUtc">上界（<b>不含</b>，半开区间），UTC；请求未指定时为 null。</param>
/// <param name="Limit">行数上限（默认 200）。</param>
/// <param name="Returned">本次实际返回的行数。</param>
/// <param name="Total">满足条件的分组总数（不受 limit 截断影响）。</param>
/// <param name="Truncated">是否因 limit 被截断（true 时 total &gt; returned）。</param>
/// <param name="Rows">按用量降序（day 分组按日期升序）排列的汇总行。</param>
public record UsageSummaryResponse(
    string GroupBy,
    DateTimeOffset? FromUtc,
    DateTimeOffset? ToUtc,
    int Limit,
    int Returned,
    long Total,
    bool Truncated,
    IReadOnlyList<UsageSummaryRowDto> Rows);
