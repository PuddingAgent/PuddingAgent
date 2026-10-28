using System.Globalization;
using System.Text;

namespace PuddingPlatform.Data.Diagnostics;

/// <summary>
/// 把 <see cref="SqlStatementMetrics"/> 的快照渲染成可 grep 的多行文本，供定时日志输出。
/// </summary>
/// <remarks>
/// <para>
/// 为什么是纯函数：报告格式是"人肉诊断的唯一入口"。一旦它自己出错（排序错、单位错、
/// 行被截断到看不出是哪条语句），拿到报告的人会得出错误结论。纯函数才能用普通单测钉住。
/// </para>
/// <para>
/// 输出刻意保持单行一条语句：日志系统按行切分，跨行的记录会被切成互不相干的碎片。
/// </para>
/// </remarks>
public static class SqlStatementMetricsReport
{
    /// <summary>单条 SQL 在报告里最多显示多少字符；超出截断并标注原长。</summary>
    internal const int MaxSqlDisplayChars = 160;

    /// <summary>报告里一条语句的渲染。</summary>
    /// <param name="Rank">从 1 开始的排名。</param>
    /// <param name="Snapshot">该语句的统计。</param>
    /// <returns>单行文本（不含换行符）。</returns>
    internal static string FormatRow(int rank, SqlStatementSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var sb = new StringBuilder(256);
        sb.Append("  #").Append(rank.ToString(CultureInfo.InvariantCulture));
        sb.Append(" calls=").Append(snapshot.Calls.ToString(CultureInfo.InvariantCulture));

        // tick 是 100ns；换成毫秒读起来才有意义。
        sb.Append(" avg=").Append(ToMilliseconds(snapshot.AverageTicks).ToString("0.###", CultureInfo.InvariantCulture)).Append("ms");
        sb.Append(" max=").Append(ToMilliseconds(snapshot.MaxTicks).ToString("0.###", CultureInfo.InvariantCulture)).Append("ms");

        if (snapshot.Rows is { } rows)
            sb.Append(" rows=").Append(rows.ToString(CultureInfo.InvariantCulture));
        else
            sb.Append(" rows=unknown");   // 查询类不猜行数，明确写 unknown

        sb.Append("  ").Append(Truncate(snapshot.Key));
        return sb.ToString();
    }

    /// <summary>
    /// 渲染整份报告。
    /// </summary>
    /// <param name="metrics">统计容器（内部取快照）。</param>
    /// <param name="top">最多输出多少条；必须为正。</param>
    /// <param name="windowLabel">窗口描述，例如 <c>60s</c>。</param>
    /// <returns>多行文本，行间用 <see cref="Environment.NewLine"/> 分隔。</returns>
    public static string Format(SqlStatementMetrics metrics, int top, string windowLabel)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        if (top <= 0)
            throw new ArgumentOutOfRangeException(nameof(top), top, "必须为正；否则报告永远是空的。");

        var snapshot = metrics.Snapshot();
        var sb = new StringBuilder(1024);
        sb.Append("[SqlMetrics] window=").Append(windowLabel ?? "?")
          .Append(" tracked=").Append(metrics.TrackedStatementCount.ToString(CultureInfo.InvariantCulture))
          .Append(" overflowed=").Append(metrics.Overflowed ? "True" : "False")
          .Append(" total=").Append(snapshot.Count.ToString(CultureInfo.InvariantCulture));

        var emitted = 0;
        foreach (var s in snapshot)
        {
            if (emitted >= top)
                break;
            sb.Append(Environment.NewLine).Append(FormatRow(emitted + 1, s));
            emitted++;
        }

        if (snapshot.Count == 0)
            sb.Append(Environment.NewLine).Append("  <no statements recorded>");

        return sb.ToString();
    }

    private static double ToMilliseconds(long ticks)
        => ticks / (double)TimeSpan.TicksPerMillisecond;

    private static string Truncate(string key)
    {
        if (key.Length <= MaxSqlDisplayChars)
            return key;

        return key.Substring(0, MaxSqlDisplayChars)
            + "…(len=" + key.Length.ToString(CultureInfo.InvariantCulture) + ")";
    }
}
