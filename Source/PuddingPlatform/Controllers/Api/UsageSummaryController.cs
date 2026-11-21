using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Services;

namespace PuddingPlatform.Controllers.Api;

/// <summary>
/// LLM 用量报表 API（可观测性 P0-c）。<b>只读</b>：只有 SELECT，不写库、不建表、不改 schema。
/// <para>
/// 聚合全部在 SQL 侧完成（见 <see cref="UsageSummaryQuery"/>），本控制器只负责
/// 参数解析、错误契约与响应信封。
/// </para>
/// <para>
/// 时间语义：所有入参与出参都是 <b>UTC</b>，区间为 <c>[fromUtc, toUtc)</c> 半开。
/// 纯日期形式的 <c>toUtc</c>（yyyy-MM-dd）按「含当天」解释，即自动折算成次日 00:00 的上界；
/// 这样 <c>?fromUtc=2026-11-01&amp;toUtc=2026-11-21</c> 表示 11-01 至 11-21 一整天都包含。
/// </para>
/// </summary>
[Authorize]
[ApiController]
[Route("api/admin/usage")]
public sealed class UsageSummaryController(
    IDbContextFactory<PlatformDbContext> dbFactory) : ControllerBase
{
    /// <summary>纯日期上界的判定格式（其余一律按日期时间解释）。</summary>
    private const string DateOnlyFormat = "yyyy-MM-dd";

    /// <summary>
    /// GET /api/admin/usage/summary?fromUtc=&amp;toUtc=&amp;groupBy=model|subagent|day
    /// <para>
    /// 返回按分组维度聚合的 tokens / 加权缓存命中率 / 成本。
    /// 空结果返回 200 + 空 <c>rows</c>（不是 404）；分组数超上限时 <c>truncated=true</c> 且
    /// <c>total</c> 仍是真实分组总数。
    /// </para>
    /// </summary>
    /// <param name="fromUtc">下界（含），ISO 日期或日期时间，UTC；缺省表示不限。</param>
    /// <param name="toUtc">上界（不含），ISO 日期或日期时间，UTC；缺省表示不限。</param>
    /// <param name="groupBy">model（默认）/ subagent / day。</param>
    /// <param name="ct">取消令牌。</param>
    [HttpGet("summary")]
    public async Task<IActionResult> GetSummary(
        [FromQuery] string? fromUtc = null,
        [FromQuery] string? toUtc = null,
        [FromQuery] string? groupBy = null,
        CancellationToken ct = default)
    {
        if (!TryParseGrouping(groupBy, out var grouping))
        {
            return BadRequest(new
            {
                code = "usage.invalid_group_by",
                message = $"groupBy 只接受 model / subagent / day，收到 '{groupBy}'。",
            });
        }

        if (!TryParseBoundary(fromUtc, isUpperBound: false, out var from))
        {
            return BadRequest(new
            {
                code = "usage.invalid_from_utc",
                message = $"fromUtc 不是合法的 ISO 日期/日期时间：'{fromUtc}'（按 UTC 解释）。",
            });
        }

        if (!TryParseBoundary(toUtc, isUpperBound: true, out var to))
        {
            return BadRequest(new
            {
                code = "usage.invalid_to_utc",
                message = $"toUtc 不是合法的 ISO 日期/日期时间：'{toUtc}'（按 UTC 解释）。",
            });
        }

        if (from.HasValue && to.HasValue && from.Value >= to.Value)
        {
            return BadRequest(new
            {
                code = "usage.invalid_range",
                message = $"时间区间为空：fromUtc={from:O} 不早于 toUtc（上界不含）。",
            });
        }

        var result = await UsageSummaryQuery.ExecuteAsync(
            dbFactory,
            grouping,
            from,
            to,
            UsageSummaryQuery.DefaultRowLimit,
            ct);

        return Ok(new UsageSummaryResponse(
            GroupBy: ToWireName(grouping),
            FromUtc: from,
            ToUtc: to,
            Limit: UsageSummaryQuery.DefaultRowLimit,
            Returned: result.Rows.Count,
            Total: result.Total,
            Truncated: result.Truncated,
            Rows: result.Rows));
    }

    /// <summary>解析分组维度；空值按默认 model 处理，未知值判为无效（不静默回退）。</summary>
    private static bool TryParseGrouping(string? raw, out UsageSummaryQuery.Grouping grouping)
    {
        grouping = UsageSummaryQuery.Grouping.Model;
        if (string.IsNullOrWhiteSpace(raw))
            return true;

        switch (raw.Trim().ToLowerInvariant())
        {
            case "model":
                grouping = UsageSummaryQuery.Grouping.Model;
                return true;
            case "subagent":
                grouping = UsageSummaryQuery.Grouping.Subagent;
                return true;
            case "day":
                grouping = UsageSummaryQuery.Grouping.Day;
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// 解析时间边界。缺省/空白返回 true 且 value=null。
    /// 无偏移的输入按 UTC 解释；纯日期上界折算为次日 00:00（半开区间的「含当天」）。
    /// </summary>
    private static bool TryParseBoundary(string? raw, bool isUpperBound, out DateTimeOffset? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(raw))
            return true;

        var text = raw.Trim();
        if (!DateTimeOffset.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return false;
        }

        if (isUpperBound && DateOnly.TryParseExact(text, DateOnlyFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            // 纯日期上界：含当天 ⇒ 次日 00:00:00Z 作为开区间上界。
            // 显式构造 DateTimeOffset(DateTime, TimeSpan.Zero)，不用隐式转换——
            // DateTime(Unspecified) 隐式转 DateTimeOffset 会套上机器本地时区。
            parsed = new DateTimeOffset(parsed.UtcDateTime.Date.AddDays(1), TimeSpan.Zero);
        }

        value = parsed;
        return true;
    }

    /// <summary>线上契约用的分组名（与查询参数逐字一致）。</summary>
    private static string ToWireName(UsageSummaryQuery.Grouping grouping) => grouping switch
    {
        UsageSummaryQuery.Grouping.Subagent => "subagent",
        UsageSummaryQuery.Grouping.Day => "day",
        _ => "model",
    };
}
