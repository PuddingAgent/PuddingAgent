using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

/// <summary>
/// <see cref="ZhihuSearchShared.FormatEditTime"/> 的时区语义。
/// <para>
/// 缺陷现场（2026-11-25）：源值是 UTC 纪元秒，旧实现走 <c>UtcDateTime.ToString("yyyy-MM-dd HH:mm")</c>
/// ⇒ 把 UTC 墙钟当本地时间印出来，而表头只写 <c>Edited</c>，东八区读的人差 8 小时且没有任何线索。
/// 这正是当天修掉的 <c>query_sub_agents</c> 同一类缺陷的最后一处。
/// </para>
/// <para>
/// <b>为什么必须注入 <c>toLocal</c>：</b>用机器本地时区断言等于「用实际值证明实际值」——
/// 在 UTC 的 CI 上，一个完全不转换的实现也能通过。
/// </para>
/// </summary>
[TestClass]
public sealed class ZhihuEditTimeRenderingTests
{
    /// <summary>2026-11-25T04:26:17Z —— 与池列表样本同一时刻，便于人眼对照。</summary>
    private static readonly long UtcSeconds =
        new DateTimeOffset(2026, 11, 25, 4, 26, 17, TimeSpan.Zero).ToUnixTimeSeconds();

    private static readonly Func<DateTimeOffset, DateTimeOffset> Plus8 =
        static t => t.ToOffset(TimeSpan.FromHours(8));

    private static readonly Func<DateTimeOffset, DateTimeOffset> Minus5 =
        static t => t.ToOffset(TimeSpan.FromHours(-5));

    [TestMethod]
    public void FormatEditTime_Converts_Utc_Epoch_To_Local_With_Date_And_Minute()
    {
        // 04:26:17Z +8 → 12:26，且必须保留日期（只给时分秒时，「四天前」与「今天凌晨」在产物上一致）。
        Assert.AreEqual("2026-11-25 12:26", ZhihuSearchShared.FormatEditTime(UtcSeconds, Plus8));
    }

    [TestMethod]
    public void FormatEditTime_Crosses_Date_Boundary_When_Local_Zone_Is_Behind_Utc()
    {
        // 04:26:17Z -5 → 前一日 23:26。钉死 UTC 的实现会在这里给出 04:26，日期也错一天。
        Assert.AreEqual("2026-11-24 23:26", ZhihuSearchShared.FormatEditTime(UtcSeconds, Minus5));
    }

    [TestMethod]
    public void FormatEditTime_Renders_Absent_Or_NonPositive_As_Dash()
    {
        Assert.AreEqual("-", ZhihuSearchShared.FormatEditTime(null, Plus8));
        Assert.AreEqual("-", ZhihuSearchShared.FormatEditTime(0, Plus8));
        Assert.AreEqual("-", ZhihuSearchShared.FormatEditTime(-1, Plus8));
    }

    [TestMethod]
    public void FormatEditTime_Renders_Out_Of_Range_As_Dash()
    {
        // FromUnixTimeSeconds 对超出 DateTimeOffset 范围的秒数抛 ArgumentOutOfRangeException；
        // 这里要的是「不抛到工具层」——一处脏数据不该让整次搜索结果失败。
        Assert.AreEqual("-", ZhihuSearchShared.FormatEditTime(long.MaxValue, Plus8));
    }
}
