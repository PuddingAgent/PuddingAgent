using System.Globalization;

namespace PuddingRuntime.Services;

/// <summary>
/// 工具输出里的时间渲染：<b>切到本地时区</b>，且<b>必须带日期</b>。
/// <para>
/// 缺陷现场（2026-11-25 同一轮内对照出来的）：同一个子会话 <c>sub-31461569</c>，
/// <c>pool_action="list"</c> 显示 <c>2026-11-25 13:36</c>，而 <c>query_sub_agents</c>
/// 显示 <c>创建: 05:36:13</c> —— <b>差 8 小时，且后者没有日期</b>。
/// 两个工具给出两个答案时，用户没有任何办法判断哪个对。
/// </para>
/// <para>
/// <b>为什么要转换而不是直接格式化：</b>这些值在库里是 <c>DateTimeOffset.UtcNow</c> 写下的
/// <c>…+00:00</c> 文本，<c>DateTimeOffset</c> 按<b>自身的 offset</b> 格式化，
/// 所以 <c>HH:mm:ss</c> 打印的是 UTC 墙钟。转换对旧值也安全：带 <c>+08:00</c> 的历史行
/// 经 <c>ToLocalTime</c> 后墙钟不变，不会把已正确的时间再挪一次。
/// </para>
/// <para>
/// <b>为什么要带日期：</b>只给时分秒时，「四天前」与「今天凌晨」在产物上完全一致 ——
/// 而产物是模型唯一的信息来源。这类错只在跨天/跨时区时才显形，所以能长期不被发现。
/// </para>
/// </summary>
internal static class ToolTimeRendering
{
    /// <summary>概览类列表的精度：到分钟。秒表精度在几十行列表里只是噪声。</summary>
    internal const string MinuteFormat = "yyyy-MM-dd HH:mm";

    /// <summary>单条详情（<c>status</c>）的精度：到秒。查单个条目时秒有意义。</summary>
    internal const string SecondFormat = "yyyy-MM-dd HH:mm:ss";

    /// <summary><c>yyyy-MM-dd HH:mm</c>（本地时区）；<c>null</c> 或 <c>default</c> 渲染为 <c>-</c>。</summary>
    /// <param name="toLocal">
    /// UTC → 本地时区的转换。默认取机器本地时区；显式参数是为了让用例能注入确定转换 ——
    /// 依赖机器时区的断言等于用实际值证明实际值。
    /// </param>
    internal static string RenderMinute(DateTimeOffset? value, Func<DateTimeOffset, DateTimeOffset>? toLocal = null)
        => Render(value, MinuteFormat, toLocal);

    /// <summary><c>yyyy-MM-dd HH:mm:ss</c>（本地时区）；<c>null</c> 或 <c>default</c> 渲染为 <c>-</c>。</summary>
    internal static string RenderSecond(DateTimeOffset? value, Func<DateTimeOffset, DateTimeOffset>? toLocal = null)
        => Render(value, SecondFormat, toLocal);

    private static string Render(
        DateTimeOffset? value,
        string format,
        Func<DateTimeOffset, DateTimeOffset>? toLocal)
    {
        // default(DateTimeOffset) 是 0001-01-01，它是「没有值」而不是「公元元年那一刻」。
        if (value is null || value.Value == default)
            return "-";

        // 注意：`??` 的右操作数必须括号包裹 —— `toLocal ?? static t => …` 会被解析成
        // `(toLocal ?? static) t`，报 CS0106 + CS1001。
        var convert = toLocal ?? (static t => t.ToLocalTime());
        // InvariantCulture 不是风格洁癖：某些区域（如 th-TH）的 `yyyy` 是佛历年份，
        // 用当前区域格式化会把 2026 输出成 2569。
        return convert(value.Value).ToString(format, CultureInfo.InvariantCulture);
    }
}
