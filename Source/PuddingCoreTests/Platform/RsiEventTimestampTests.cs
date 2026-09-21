using PuddingCode.Platform;

namespace PuddingCoreTests.Platform;

/// <summary>
/// RSI S3 §2.7 时间戳解析契约测试（B2；解析器是纯静态函数，无需数据库）。
/// <para>
/// 每条用例都对应一个真实的实现错误（规格 §4「每条都必须能红」；父代理将亲自做变异验证）。
/// </para>
/// </summary>
[TestClass]
public sealed class RsiEventTimestampTests
{
    /// <summary>
    /// S13（§2.7 换算）：+08:00 与<b>无偏移</b>串均须得到 15:00Z（瞬时不变、偏移 0）；生产磁盘 "O" 形态原样还原。
    /// <para>
    /// 变红条件（任一改动即红）：
    /// ① 改用 <c>DateTimeStyles.None</c> ⇒ 无偏移串按本机时区（本机 UTC+8）解释成 07:00Z，UtcTicks 断言必红；
    /// ② 省略 <c>ToUniversalTime()</c> ⇒ +08:00 串保留非零偏移，Offset 断言必红；
    /// ③ 换用 CurrentCulture ⇒ 非公历文化下显式日期断言漂移必红。
    /// </para>
    /// </summary>
    [TestMethod]
    public void S13_OffsetAndNaiveTimestamps_BothResolveToSameUtcInstant()
    {
        var expectedTicks = new DateTimeOffset(2026, 9, 21, 15, 0, 0, TimeSpan.Zero).UtcTicks;

        // 带 +08:00 偏移：换算到 UTC 瞬时 15:00Z。
        var withOffset = RsiEventTimestamp.ParseUtc("2026-09-21T23:00:00+08:00", "turn-s13");
        Assert.AreEqual(expectedTicks, withOffset.UtcTicks, "+08:00 串须换算到 UTC 瞬时 15:00Z。");
        Assert.AreEqual(TimeSpan.Zero, withOffset.Offset, "OccurredAtUtc 偏移必须为 0（ToUniversalTime 不得省略）。");

        // 无偏移串：必须按 UTC 解释得 15:00Z（若误用 None，本机 UTC+8 会错成 07:00Z）。
        var naive = RsiEventTimestamp.ParseUtc("2026-09-21T15:00:00", "turn-s13");
        Assert.AreEqual(expectedTicks, naive.UtcTicks, "无偏移串必须按 UTC（AssumeUniversal）解释为 15:00Z。");
        Assert.AreEqual(TimeSpan.Zero, naive.Offset, "无偏移串解析后偏移也必须归零。");

        // 生产磁盘形态（"O" 带 +00:00，规格 §2.7 写入约定）：往返还原不变。
        var diskForm = RsiEventTimestamp.ParseUtc("2026-09-21T15:00:00.0000000+00:00", "turn-s13");
        Assert.AreEqual(expectedTicks, diskForm.UtcTicks, "生产 \"O\" 形态须原样还原到同一瞬时。");
        Assert.AreEqual(TimeSpan.Zero, diskForm.Offset);
    }

    /// <summary>
    /// S14（§2.7 fail-closed）：不可解析串 ⇒ <b>抛异常</b>，且消息同时含 turnId 与原始串片段。
    /// <para>
    /// 变红条件（任一改动即红）：
    /// ① 改为返回 null / <c>DateTimeOffset.MinValue</c> / 丢弃该行 ⇒ 不再抛出，ThrowsException 必红 ——
    ///    「不是 null、不是 MinValue」正是由这条「必须抛出」的机制钉死的，返回任何哨兵值都过不了本用例；
    /// ② 异常消息丢掉 turnId 或原始串片段 ⇒ Contains 断言必红（不可定位的解析失败等于没有失败报告）；
    /// ③ 抛错异常类型（如 FormatException / KeyNotFoundException）⇒ 类型断言必红。
    /// </para>
    /// </summary>
    [TestMethod]
    public void S14_UnparsableTimestamp_ThrowsFailClosed_WithTurnIdAndRawSnippet()
    {
        const string turnId = "turn-s14-locator";
        const string raw = "not-a-timestamp";

        var ex = Assert.ThrowsExactly<InvalidOperationException>(
            () => RsiEventTimestamp.ParseUtc(raw, turnId));

        StringAssert.Contains(ex.Message, turnId, "异常消息必须含 turn_id，否则无法定位坏行。");
        StringAssert.Contains(ex.Message, raw, "异常消息必须含原始串片段，否则无法定位坏格式。");
    }
}
