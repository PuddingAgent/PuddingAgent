using System.Globalization;

namespace PuddingCode.Platform;

/// <summary>
/// RSI S3 §2.7 时间戳解析契约（冻结，逐字实现，不得由调用方临场发明）。
/// <para>
/// 事实：conversation_events.occurred_at 是 [Required] <b>string</b> 列（ConversationEventEntity），
/// 所有生产写入者统一 <c>DateTimeOffset.UtcNow.ToString("O")</c> ⇒ 磁盘形态如
/// <c>2026-09-21T15:00:00.0000000+00:00</c>（带偏移，不是 Z）。
/// </para>
/// <para>
/// 契约三要素（⛔ 缺一即返工）：
/// ① <see cref="CultureInfo.InvariantCulture"/> —— 平台既有 7+ 处 DateTimeOffset.Parse 走 CurrentCulture，
///    非公历文化（ar-SA / th-TH）会解析成不同年份，禁止照抄；
/// ② <see cref="DateTimeStyles.AssumeUniversal"/> —— 字符串不含偏移时按 UTC 解释；
///    ⛔ 禁止 <see cref="DateTimeStyles.None"/>（无偏移串会按本机时区解释，本机 UTC+8 ⇒ 恰好错 8 小时）；
/// ③ <see cref="DateTimeOffset.ToUniversalTime"/> —— 字段名是 OccurredAtUtc，类型名不得撒谎。
/// </para>
/// <para>
/// 不可解析 ⇒ <b>fail-closed 抛出</b>：不返回 null（格式漂移不可见）、不丢行（静默丢工具步）、
/// 不返回 MinValue（把「读不懂」伪装成真实时刻）。异常消息必须同时含 turnId 与原始串片段（可定位）。
/// 纯度分界（§2.7）：纯函数层（增量 A / B1）绝不抛异常；边界层（B2，即本类 + EF 实现）对不可解析输入 fail-closed。
/// </para>
/// </summary>
public static class RsiEventTimestamp
{
    /// <summary>异常消息里保留的原始串最大片段长度（规格：截断即可，能定位即可）。</summary>
    private const int MaxSnippetLength = 64;

    /// <summary>
    /// 把 occurred_at 的字符串形态解析为 UTC 瞬时（偏移恒为 0）。
    /// 不可解析（含 null / 空白 / 非 timestamp 串）⇒ 抛 <see cref="InvalidOperationException"/>，
    /// 消息同时含 <paramref name="turnId"/> 与原始串片段（截断至 <see cref="MaxSnippetLength"/>）。
    /// </summary>
    public static DateTimeOffset ParseUtc(string raw, string turnId)
    {
        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return parsed.ToUniversalTime();
        }

        var snippet = raw is null
            ? "<null>"
            : raw.Length <= MaxSnippetLength
                ? raw
                : raw[..MaxSnippetLength];

        throw new InvalidOperationException(
            $"RSI occurred_at 时间戳不可解析（fail-closed，不降级不丢行）：turn_id='{turnId}'，原始片段='{snippet}'。");
    }
}
